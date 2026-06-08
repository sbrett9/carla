# 07 — Road-Network Filtering: Car-Drivable-Only OSM → OpenDRIVE

**Status:** Research / plan of attack. No code changed; CARLA server not launched.
**Date:** 2026-06-08
**Scope:** Eliminate the "mess of road" in dense urban OSM (Wrigleyville, Chicago) by
making `netconvert` import only car-drivable streets and drop pedestrian, rail, and
parking/service ways.

---

## 1. Root-cause analysis

### 1.1 What the converter does today

`CarlaNet.Map.OsmConverter.BuildArguments` (`carla/CarlaNet/src/CarlaNet.Map/OsmConverter.cs`)
shells out to the bundled `netconvert.exe` (Eclipse SUMO **1.27.0**, staged at
`carla/Build/sumo-install/bin/`) with **no edge-filtering whatsoever**. The full flag set is:

```
--osm-files <in.osm>
--opendrive-output <out.xodr>
--proj <projString>                # tmerc, possibly origin-pinned
--default.lanewidth 3.35
--default.sidewalk-width 2.80
--tls.guess true|false
--geometry.remove
--roundabouts.guess
[--offset.disable-normalization]   # when origin pinned or CenterMap=false
[--junctions.join | --tls.discard-loaded]
<ExtraArgs…>
```

`OsmConversionOptions` exposes only: `NetconvertPath`, `ProjDataDirectory`,
`DefaultLaneWidth`, `DefaultSidewalkWidth`, `ProjString`, `GenerateTrafficLights`,
`CenterMap`, `OriginLatitude/Longitude`, and a raw `ExtraArgs` escape hatch.
**There is no option to restrict which OSM way types become roads.** As a result every
importable OSM way is meshed into an OpenDRIVE `<road>`.

### 1.2 What the Wrigleyville OSM actually contains

Tag census of `carla/Import/Maps/WrigleyVille.osm` (occurrences across nodes **and** ways;
node-only point tags such as `crossing`, `traffic_signals`, `stop`, `bus_stop` are noted):

| `highway=` value | count | drivable by car? | role |
|---|---|---|---|
| footway | 308 | **no** | sidewalks / paths (way) |
| crossing | 263 | no | mostly node points |
| service | 77 | **no** (see §2.3) | alleys, parking aisles, driveways |
| stop | 32 | n/a | node |
| residential | 26 | **yes** | street |
| secondary | 21 | **yes** | street |
| bus_stop | 17 | n/a | node |
| tertiary | 14 | **yes** | street |
| traffic_signals | 13 | n/a | node |
| steps | 6 | **no** | stairs |
| elevator | 2 | no | node |
| pedestrian | 2 | **no** | plaza/footway |
| busway | 1 | (bus only) | edge case |
| turning_circle | 1 | n/a | node |

| `railway=` value | count | role |
|---|---|---|
| subway | 11 | CTA "L" / subway lines (**non-drivable**) |
| switch / crossover / stop / platform / station / facility / abandoned | ~18 | rail infra (non-drivable) |

| `service=` value | count |
|---|---|
| alley | 39 |
| parking_aisle | 9 |
| crossover / drive-through / driveway | ~10 |

| `footway=` sub-value | count |
|---|---|
| sidewalk | 170 |
| crossing | 95 |

**Conclusion:** the drivable street ways number in the low tens
(`residential` 26 + `secondary` 21 + `tertiary` 14 ≈ **60 ways**), while the imported ways
are dominated by **308 footways, 77 service ways, plus 11 subway lines and assorted rail
infrastructure**. netconvert turns all of those into roads, which is exactly the tangle.
This matches the observed bloated build (**2785 roads / 8402 elevation records**); a
drivable-only network for this extent should be on the order of a few hundred roads at most.

### 1.3 Why this happens (the SUMO type map)

netconvert maps each OSM way to an edge **type** named `<key>.<value>` (e.g.
`highway.residential`, `railway.subway`) and assigns each type a set of allowed vehicle
classes (`vClass`) from a **type-map file**. The default map is
`data/typemap/osmNetconvert.typ.xml`.

> **Build note (important):** our staged `sumo-install` ships **only** `netconvert.exe`,
> its DLLs, and PROJ data — there is **no `data/typemap/` directory and no
> `osmNetconvert.typ.xml`**, and no `SUMO_HOME` is set. netconvert therefore falls back to
> its **compiled-in built-in default type map** (SUMO embeds the OSM defaults in the binary;
> this is why the existing conversions already succeed). Any `--keep/remove-edges.by-vclass`
> filtering operates against those built-in defaults, so we do **not** need to ship a typemap
> for the vclass approach to work. (See §3.2 for the trade-off vs. `--type-files`.)

The relevant permissions in the SUMO default OSM type map (verified against
`eclipse-sumo/sumo` `data/typemap/osmNetconvert.typ.xml`):

| edge type | allow / disallow | passenger car? |
|---|---|---|
| highway.footway | `allow="pedestrian"` | **no** |
| highway.path | `allow="pedestrian bicycle"` | **no** |
| highway.steps | `allow="pedestrian"` | **no** |
| highway.cycleway | `allow="bicycle"` | **no** |
| highway.pedestrian | `allow="pedestrian"` | **no** |
| **highway.service** | `allow="delivery pedestrian bicycle"` | **no** ← note! |
| highway.living_street | `disallow="rail … ship"` | yes |
| highway.residential | `disallow="rail … ship"` | yes |
| highway.secondary | `disallow="rail … ship"` | yes |
| highway.tertiary | `disallow="rail … ship"` | yes |
| railway.subway | `allow="subway"` | **no** |
| railway.rail | `allow="rail"` | **no** |
| railway.tram | `allow="tram"` | **no** |

The decisive insight: **every non-drivable way type we want to exclude (footway, path,
steps, cycleway, pedestrian, service, all `railway.*`) is defined in the default type map as
NOT allowing the `passenger` vehicle class**, whereas every normal street
(residential/secondary/tertiary/living_street/primary/…) DOES allow `passenger`. That gives
us a single, robust filter axis: **vehicle class = `passenger`.**

---

## 2. The fix: filter edges by vehicle class

### 2.1 Recommended primary flag

```
--keep-edges.by-vclass passenger
```

`netconvert --help` (this 1.27.0 build) describes it verbatim as:

> `--keep-edges.by-vclass STR[]   Only keep edges which allow one of the vclasses in STR[]`

So an edge survives import iff it permits the `passenger` class. Applied to Wrigleyville
this keeps residential/secondary/tertiary/living_street/primary streets and drops **all**
footways, paths, steps, cycleways, pedestrian ways, `highway.service` (alleys, parking
aisles, driveways, drive-throughs), and every `railway.*` (subway/L, tram, rail, platforms,
switches). It is a **single flag** and is robust to whatever way mix a new OSM extract
contains — no per-type enumeration required.

This neatly subsumes the task's explicit exclusion list:
- `railway=*` → all rail types disallow passenger → dropped.
- `highway=footway/sidewalk/path/steps/cycleway/pedestrian` → pedestrian/bicycle only → dropped.
- `highway=service` + `service=parking_aisle` → service disallows passenger → dropped (§2.3).

### 2.2 Why keep-by-vclass over the alternatives

- **vs. `--remove-edges.by-vclass`** — its semantics are "remove edges which allow **only**
  vclasses from STR[]" (verified in docs). You would have to enumerate the complete set of
  non-car classes (`pedestrian bicycle delivery subway rail rail_urban tram …`) and keep it
  in sync with SUMO; an edge allowing any unlisted class would survive. More fragile, more to
  maintain. Use keep-by-vclass instead.
- **vs. `--keep-edges.by-type highway.residential,…`** — requires hard-coding the exact set
  of `highway.*` street types and will silently drop a legitimate street type you forgot
  (e.g. `highway.unclassified`, `highway.primary` in a different extract). Type filtering is a
  useful *secondary* refinement, not the primary gate.
- **vs. shipping a custom `--type-files` typemap** — heaviest option; needs us to author and
  stage an XML file and keep `SUMO_HOME`/paths correct. Defer unless we need per-type tuning
  (lane counts, speeds) beyond simple drivable/not-drivable.

### 2.3 Service / parking-aisle nuance (decision point)

In the **SUMO default** type map, `highway.service` does *not* allow `passenger`, so
`--keep-edges.by-vclass passenger` already removes **all** service ways — alleys, driveways,
**and** `service=parking_aisle`. For the stated goal ("normal streets, exclude parking/service")
that is the desired behavior and needs no extra work.

> Caveat to record: this also removes **ordinary alleys and driveways**, which in some CARLA
> scenarios are legitimately drivable. If we later decide alleys should be drivable while only
> parking aisles are excluded, the vclass gate alone cannot distinguish `service=alley` from
> `service=parking_aisle` (both import as edge type `highway.service`). That finer split would
> require a custom `--type-files` map (or post-processing) and is **out of scope** for the
> current "clean drivable streets" objective. Note also: CARLA's own `osm2odr`/`OSM2ODRSettings`
> does **not** filter these out either — so this filtering is a deliberate improvement over
> upstream CARLA behavior, not a regression from it.

### 2.4 Recommended companion flags (cleanup of side effects)

Removing edges fragments the graph. Add these to keep the result clean and connected:

```
--remove-edges.isolated         # drop edges with no connection to the rest of the net
--keep-edges.components 1        # keep only the single largest weakly-connected component
```

`netconvert --help` (this build):
> `--remove-edges.isolated      Removes isolated edges`
> `--keep-edges.components INT  Only keep the INT largest weakly connected components`

`--keep-edges.components 1` is the bigger hammer: after pedestrian/rail/service removal,
small street stubs that only connected to the rest of the map *through* a removed footway or
alley become detached islands. Keeping the largest component yields one coherent drivable
network. **Validate its effect** (see §5) — if it discards wanted streets in a legitimately
multi-component extract, relax to `2`/`3` or drop it and rely on `--remove-edges.isolated`
alone.

> Interaction note: the existing `--geometry.remove` (edge joining) and `--junctions.join`
> run on the **already-filtered** graph, which is what we want — fewer, cleaner junctions.
> No ordering change is required; netconvert applies edge-removal during/after loading.

---

## 3. `OsmConversionOptions` additions (C#, `CarlaNet.Map`)

All changes are in `carla/CarlaNet/src/CarlaNet.Map/OsmConverter.cs` (the record
`OsmConversionOptions` and the single-source-of-truth method `OsmConverter.BuildArguments`).

### 3.1 New options (recommended shape)

```csharp
/// <summary>When true, restrict the imported network to car-drivable roads only by
/// passing <c>--keep-edges.by-vclass passenger</c> to netconvert. This drops sidewalks,
/// footpaths, steps, cycleways, pedestrian ways, all railway/subway/tram lines, and
/// service ways (alleys, parking aisles, driveways), because none of those allow the
/// 'passenger' vehicle class in SUMO's default OSM type map. Default: true.</summary>
public bool DrivableOnly { get; init; } = true;

/// <summary>Vehicle classes kept when <see cref="DrivableOnly"/> is true
/// (netconvert <c>--keep-edges.by-vclass</c>). Default: just "passenger".
/// Override to e.g. ["passenger","bus"] to also keep bus-only lanes/busways.</summary>
public IReadOnlyList<string> KeepVehicleClasses { get; init; } = ["passenger"];

/// <summary>Drop isolated edges and keep only the largest weakly-connected component
/// after filtering, so removed pedestrian/rail/service ways don't leave drivable
/// islands (netconvert <c>--remove-edges.isolated</c> + <c>--keep-edges.components 1</c>).
/// Default: true. Set false if a legitimately multi-component extract loses wanted roads.</summary>
public bool PruneDisconnected { get; init; } = true;
```

### 3.2 `BuildArguments` additions

Insert after the existing `--roundabouts.guess` block, **before** `ExtraArgs` is appended
(so an explicit `ExtraArgs` entry can still override during experimentation):

```csharp
if (_options.DrivableOnly && _options.KeepVehicleClasses.Count > 0)
{
    args.Add("--keep-edges.by-vclass");
    args.Add(string.Join(",", _options.KeepVehicleClasses));   // e.g. "passenger"
}

if (_options.PruneDisconnected)
{
    args.Add("--remove-edges.isolated");
    args.Add("--keep-edges.components");
    args.Add("1");
}
```

> `--keep-edges.by-vclass` takes a comma-separated `STR[]`; pass the joined list as a
> **single** argument token after the flag (consistent with how the existing code adds
> `--proj <value>` as two tokens via `ArgumentList`).

No change is needed to PROJ handling, origin pinning, or TLS logic; filtering is orthogonal.

---

## 4. Phased implementation plan

**Phase 0 — Baseline (no code).** Run the current converter on WrigleyVille and record
`<road>`/`<junction>`/`<elevation>` counts. (Already known: ~2785 roads / 8402 elevations.)

**Phase 1 — Prove the flags via `ExtraArgs` (no new options yet).** In `test_osm_world.py
--convert-only`, set
`opts.ExtraArgs = ["--keep-edges.by-vclass", "passenger"]` and re-run. Compare road/junction
counts. Then add `--remove-edges.isolated` and `--keep-edges.components 1`. This validates the
exact flag strings against *this* netconvert build before touching C#.

**Phase 2 — Add the typed options.** Implement `DrivableOnly`, `KeepVehicleClasses`,
`PruneDisconnected` in `OsmConversionOptions` + `BuildArguments` (§3). Keep `ExtraArgs` as the
final override. Default `DrivableOnly=true`, `PruneDisconnected=true`.

**Phase 3 — Wire through callers.** `test_osm_world.py` / `test_digital_twin.py` get the new
defaults for free. Add a `--keep-all-ways` (or `--include-nondrivable`) flag to the test
scripts that sets `DrivableOnly=False` for A/B comparison and debugging.

**Phase 4 — Headless world build.** Run `test_digital_twin.py` end-to-end; confirm the meshed
world is clean and the elevation-sampling/Cesium pipeline still works on the smaller network
(it should be *faster* — far fewer reference-line samples).

**Phase 5 — Unit test.** Add a `BuildArguments` assertion test (CarlaNet has an xUnit suite):
with `DrivableOnly=true` the arg list contains `--keep-edges.by-vclass passenger`; with it
false, it does not. Pure string-list assertion, no netconvert run needed.

---

## 5. Validation steps

1. **Quantitative `.xodr` diff.** For the same OSM + origin, compare before/after:
   - `<road ` count, `<junction ` count, `<elevation ` count
     (`test_osm_world.py --convert-only` already prints roads & junctions; the digital-twin
     test prints roads & elevations). Expect a **large** drop — from ~2785 roads toward the
     low hundreds. A clean Wrigleyville drivable net should be far smaller.
2. **Negative checks (must be ZERO drivable roads from these).** Confirm the dropped ways are
   gone. Quickest signal is the road-count collapse; for spot-checking, a filtered network
   should contain no geometry tracing the CTA Red Line / subway alignment and no
   sidewalk-width strips paralleling streets.
3. **Connectivity sanity.** Inspect netconvert stderr for warnings about removed/disconnected
   edges. Confirm `--keep-edges.components 1` did not amputate a wanted district; if road
   count looks *too* low, relax `PruneDisconnected` and re-compare. Visually (or via spawn
   points), the main Clark/Addison/Sheffield grid around Wrigley Field must remain connected.
4. **Drivability.** In the headless build (`test_digital_twin.py`), spawn a few autopilot
   vehicles (`--traffic N`); they should find valid spawn points and drive on normal streets
   only — no vehicles meshed onto rail/footway geometry.
5. **Regression.** Re-run the existing CarlaNet test suite (the conversion smoke tests) to
   confirm origin-pinning, PROJ, and TLS behavior are unaffected by the added flags.

---

## 6. Exact flag summary (verified against `netconvert.exe --help`, SUMO 1.27.0)

| flag | value | purpose |
|---|---|---|
| `--keep-edges.by-vclass` | `passenger` | **primary** — keep only car-drivable edges; drops footway/path/steps/cycleway/pedestrian, all railway.*, and highway.service |
| `--remove-edges.isolated` | (none) | drop edges left with no connections after filtering |
| `--keep-edges.components` | `1` | keep only the largest weakly-connected drivable component |

Optional, only if finer control is later needed (not in the primary plan):

| flag | value | purpose |
|---|---|---|
| `--remove-edges.by-type` | `highway.service,railway.subway,…` | explicit type blacklist (more fragile than by-vclass) |
| `--keep-edges.by-type` | `highway.residential,highway.secondary,…` | explicit street whitelist |
| `--type-files` | `<custom>.typ.xml` | author a custom permission/lane/speed map (must be staged; `data/typemap` is NOT currently shipped) |

---

## 7. Citations

- `netconvert.exe --help` — local build `carla/Build/sumo-install/bin/netconvert.exe`
  (Eclipse SUMO **1.27.0**); verbatim option descriptions for `--keep-edges.by-vclass`,
  `--remove-edges.by-vclass`, `--keep-edges.by-type`, `--remove-edges.by-type`,
  `--remove-edges.isolated`, `--keep-edges.components`.
- SUMO docs — *Networks/Import/OpenStreetMap*: type naming `<key>.<value>`, default typemap
  `data/typemap/osmNetconvert.typ.xml`, vclass-based filtering for "remove all edges which
  cannot be used by passenger vehicles." https://sumo.dlr.de/docs/Networks/Import/OpenStreetMap.html
- SUMO default OSM type map — `eclipse-sumo/sumo` `data/typemap/osmNetconvert.typ.xml`:
  permission rows confirming footway/path/steps/cycleway/pedestrian = pedestrian/bicycle only,
  `highway.service = allow="delivery pedestrian bicycle"` (no passenger), railway.* = rail
  classes only, residential/secondary/tertiary = passenger allowed.
  https://raw.githubusercontent.com/eclipse-sumo/sumo/main/data/typemap/osmNetconvert.typ.xml
- SUMO docs — *netconvert* option reference (semantics of keep-vs-remove by vclass).
  https://sumo.dlr.de/docs/netconvert.html
- Source: `carla/CarlaNet/src/CarlaNet.Map/OsmConverter.cs` (current flags & options).
- Data: `carla/Import/Maps/WrigleyVille.osm` (tag census in §1.2).
