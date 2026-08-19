# Road Elevation Profile Continuity — faceted road surfaces from a slope-discontinuous vertical fit

**Date:** 2026-08-17 · **Status:** work items 1, 2, 4 and 6 built and measured (§17, §19); the road
mesh assembly is now in scope as §18. Tracked as
[sbrett9/carla#29](https://github.com/sbrett9/carla/issues/29), worked on branch
`feature/JNI-347-road-mesh-elevation-profile-continuity`.
**Datum:** ellipsoidal WGS84 (HAE) throughout — `project_datum_decision`. Nothing here changes the
datum or the height source; this is entirely about how an already-sampled height series is turned into
OpenDRIVE records.
**Relates to:** [04_DynamicWorld_DataPipeline.md](04_DynamicWorld_DataPipeline.md) (§2a defines the
record; §4c is where the linear fit was chosen and the cubic deferred),
[06_Elevation_Strategy.md](06_Elevation_Strategy.md) (§10 grade separation — the `Raised` samples this
work must not smear), [02_CARLA_OSM_MapGen.md](02_CARLA_OSM_MapGen.md),
[08_Layer_Architecture.md](08_Layer_Architecture.md).
**Code:** `CarlaNet.Map/OpenDrive/ElevationInjector.cs`, `CarlaNet.Transport/CarlaClient.cs`,
`LibCarla/source/carla/road/{Road,Lane,Map}.cpp`, `LibCarla/source/carla/road/MeshFactory.cpp`.
**Client:** world generation and the visual check run through `CarlaControl/scripts/run_SCTMV.py`
and the `carlacontrol` package, which supersede the monolithic `CarlaNet/python/SCTMV.py`. Build
arguments live in `CarlaControlArgumentParser`, and the OSM-to-world path in `WorldBuilder`.

---

## 1. The defect

Roads in generated digital-twin worlds are visibly **faceted in the vertical direction**: the surface
reads as a chain of flat panels with a crease at every sample boundary rather than a continuous grade.

The cause is neither the road mesh nor the plan-view geometry. It is the `<elevationProfile>` that
`ElevationInjector` writes. Every record is emitted as a straight ramp (`c=0, d=0`), so the vertical
profile is **C0 continuous but C1 discontinuous** — the height is exact at every sample and the slope
steps at every one of them. The mesh is tessellated at `vertex_distance = 2.0 m` against samples `10 m`
apart, so it faithfully reproduces all five facets of each slope break.

Two structural errors compound it, and neither is fixed by a better intra-road fit: short connector
roads inside junctions sample their heights independently of the roads they join, and the two
opposing-direction roads that OSM encodes for a single street are sampled and fitted independently of
each other.

## 2. What the record is, and who reads it

`<elevationProfile>` holds one or more `<elevation s a b c d>` records, each a cubic in the distance
along the road from that record's own start station:

```
z(ds) = a + b·ds + c·ds² + d·ds³        ds = s − record_s
```

Two of the four terms are being discarded.

The profile has exactly one evaluation chokepoint, `Road::GetDirectedPointIn`
(`LibCarla/source/carla/road/Road.cpp:184-204`), which sets both the height and the pitch:

```cpp
const auto elevation_info = GetElevationOn(s);
p.location.z = static_cast<float>(elevation_info.Evaluate(s));
p.pitch      = elevation_info.Tangent(s);
```

Everything downstream inherits from it:

- **Road mesh vertices** — `MeshFactory.cpp:891, 991` build lane geometry from
  `road.GetDirectedPointIn(s_current)`.
- **Waypoint z and pitch** — `Lane::ComputeTransform` (`Lane.cpp:180`, also `:245`) calls the same
  function, so the traffic manager's path, spawn placement, and per-vehicle telemetry truth all follow
  the same curve the mesh does.

That single shared chokepoint is why this is worth fixing at the profile and nowhere else: it is the
only representation both the rendered surface and the driven path read.

> Correction to [04_DynamicWorld_DataPipeline.md](04_DynamicWorld_DataPipeline.md) §2b, which states
> that `MeshFactory` inspects the elevation `c`/`d` coefficients to decide flatness (cited there as
> `MeshFactory.cpp:88-93`). No such check exists in this tree — searching `MeshFactory.cpp` for
> "elevation" returns nothing. The mesh path reaches elevation only through `GetDirectedPointIn`.
> Emitting non-zero `c`/`d` therefore changes vertex heights and nothing else; there is no flatness
> fast-path to fall out of.

## 3. Measured baseline

All figures below were measured on branch tip `f08869292` by parsing the ten already-generated maps in
`Build/sumo-smoketest/*_elevated.xodr` — read-only, no server, no editor. The method is stated with
each table so the numbers can be reproduced or contradicted.

### 3.1 Census of emitted records

Counting `<elevation>` records and their coefficients, roads carrying a profile, and the plan-view
geometry primitives:

| map | records | `c≠0` | `d≠0` | roads | >2 records | last record `b=0` | line/arc/paramPoly3 |
|---|---:|---:|---:|---:|---:|---:|---|
| Arapahoe_I25 | 5,431 | 0 | 0 | 1,053 | 664 | 1,053 | 714/0/1,865 |
| Bellvue_Overpass | 1,195 | 0 | 0 | 43 | 29 | 43 | 49/0/125 |
| East56th | 90 | 0 | 0 | 18 | 12 | 18 | 6/0/12 |
| GalleyRoad | 2,248 | 0 | 0 | 416 | 300 | 416 | 240/0/550 |
| Gardnerville_Centerville_Lane | 1,675 | 0 | 0 | 213 | 158 | 213 | 124/0/337 |
| IRAN | 6,322 | 0 | 0 | 1,795 | 1,218 | 1,795 | 932/0/1,860 |
| Iran_Route_96 | 1,652 | 0 | 0 | 50 | 44 | 50 | 142/0/214 |
| Lakeview_Carson | 3,328 | 0 | 0 | 329 | 245 | 329 | 462/0/1,374 |
| SF_LaurelHeights | 2,239 | 0 | 0 | 498 | 373 | 498 | 247/0/512 |
| wrigley | 10,130 | 0 | 0 | 2,785 | 768 | 2,785 | 1,765/0/1,925 |
| **total** | **34,310** | **0** | **0** | **7,200** | **3,811** | **7,200 (100%)** | **4,681/0/8,774** |

Three things follow directly.

**The quadratic and cubic terms are never used.** Not once in 34,310 records.

**Every road ends dead-flat.** All 7,200 of them. The fitting loop derives `b` from the *next* sample
(`ElevationInjector.cs:199-205`), so the last record has no `i+1` and keeps its initialised `b = 0`.
Each road therefore arrives at its successor carrying zero grade regardless of the grade it was
actually on.

**Plan-view geometry is already smooth and is not part of this defect.** Zero `<arc>` records;
`paramPoly3` outnumbers `line` by 8,774 to 4,681. netconvert's
`--opendrive-output.straight-threshold` default of `1e-08` degrees already emits a parameterised curve
for essentially every bend. There is no horizontal smoothing left on the table.

> These totals corroborate the independent census recorded in issue #29, which covered nine maps
> before `East56th` was generated. Subtracting `East56th` from the table above gives 34,220 records,
> 7,182 roads, 3,799 with more than two records, 7,182 ending `b=0`, and 4,675/0/8,762 plan-view
> primitives — matching the issue exactly on every figure.

### 3.2 Slope discontinuity at internal record boundaries

At each internal boundary, the step between the incoming record's tangent evaluated at the boundary
station and the outgoing record's `b`:

```
|b[i] − (b[i−1] + 2·c[i−1]·h + 3·d[i−1]·h²)|      h = s[i] − s[i−1]
```

which under the present fit reduces to `|b[i] − b[i−1]|`. Slope is dimensionless (rise/run), so 0.02
is a 2 % grade step.

| map | boundaries | median | p90 | p99 | max | fraction > 0.02 |
|---|---:|---:|---:|---:|---:|---:|
| Arapahoe_I25 | 4,378 | 0.0100 | 0.0482 | 0.1447 | 0.345 | 29.8 % |
| Bellvue_Overpass | 1,152 | 0.0073 | 0.0272 | 0.0684 | 0.122 | 17.4 % |
| East56th | 72 | 0.0024 | 0.0122 | 0.0158 | 0.018 | 0.0 % |
| GalleyRoad | 1,832 | 0.0108 | 0.0643 | 0.3794 | 0.684 | 34.8 % |
| Gardnerville_Centerville_Lane | 1,462 | 0.0107 | 0.0355 | 0.0636 | 0.088 | 27.4 % |
| IRAN | 4,527 | 0.0087 | 0.0281 | 0.0813 | 0.717 | 19.6 % |
| Iran_Route_96 | 1,602 | 0.0056 | 0.0287 | 0.3657 | 0.581 | 14.1 % |
| Lakeview_Carson | 2,999 | 0.0116 | 0.0463 | 0.1159 | 0.238 | 31.3 % |
| SF_LaurelHeights | 1,741 | 0.0149 | 0.0575 | 0.1382 | 0.256 | 40.8 % |
| wrigley | 7,345 | 0.0140 | 0.7690 | 2.2115 | 52.61 | 41.0 % |
| **all** | **27,110** | **0.0105** | **0.0754** | **1.2957** | **52.61** | **30.7 %** |

The corresponding **height** step at the same boundaries — `|a[i] − (a[i−1] + b[i−1]·h + …)|` — has a
maximum of `1.8e-15 m` across all ten maps. C0 continuity is exact to floating point. The defect is
purely C1, which is what makes it a fitting problem rather than a sampling or bookkeeping one.

`wrigley` (dense urban Chicago) is the noise-dominated case: 1,111 of its 7,345 boundaries exceed a
0.5 slope step, and those have a **median span of 10.00 m on roads of median length 89.8 m** — they
are ordinary full-step spans, not degenerate stubs. Its extreme tail (52.61) is separately explained
by sub-metre roads: the worst offenders are 0.20 m-long roads carrying two records. Both classes are
real and want different treatment — the first wants filtering (§7), the second wants the short-road
handling in §8 and §9.

### 3.3 Height and slope mismatch across road-to-road links

For every `<link>` `predecessor`/`successor` with `elementType="road"`, each road's fitted profile is
evaluated at its own contact station and compared with the neighbour's at the station named by
`contactPoint`. Travel direction reverses when two roads meet end-to-end or start-to-start, so the
neighbour's tangent is negated in those cases before comparison.

| map | links | \|dz\| median | \|dz\| p90 | \|dz\| max | slope median | slope p90 | slope max | slope > 0.02 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Arapahoe_I25 | 1,464 | 0.0000 | 0.124 | 2.359 | 0.0187 | 0.0501 | 0.323 | 47.6 % |
| Bellvue_Overpass | 54 | 0.0000 | 0.040 | 0.111 | 0.0153 | 0.0370 | 0.095 | 29.6 % |
| East56th | 24 | 0.0068 | 0.087 | 0.183 | 0.0068 | 0.0159 | 0.017 | 0.0 % |
| GalleyRoad | 620 | 0.0001 | 0.143 | 2.906 | 0.0254 | 0.1004 | 0.635 | 55.6 % |
| Gardnerville_Centerville_Lane | 316 | 0.0000 | 0.094 | 0.404 | 0.0125 | 0.0417 | 0.079 | 32.9 % |
| IRAN | 2,526 | 0.0000 | 0.060 | 1.856 | 0.0098 | 0.0294 | 0.468 | 21.0 % |
| Iran_Route_96 | 58 | 0.0000 | 0.029 | 0.191 | 0.0193 | 0.0593 | 0.306 | 48.3 % |
| Lakeview_Carson | 488 | 0.0003 | 0.344 | 1.197 | 0.0534 | 0.1119 | 0.237 | 78.7 % |
| SF_LaurelHeights | 746 | 0.0000 | 0.073 | 0.586 | 0.0160 | 0.0618 | 0.139 | 41.8 % |
| wrigley | 2,956 | 0.0000 | 0.152 | 19.54 | 0.0124 | 0.6185 | 33.33 | 37.1 % |

Heights across links largely agree — both roads sample the same plan position, so the median `dz` is
0.0000 m nearly everywhere — but the tails are real and map-dependent, reaching 1.20 m on
Lakeview_Carson and 2.9 m on GalleyRoad. **Slope is the dominant failure**: mismatched beyond 0.02 on
21-79 % of links on every map with relief.

Read the slope column precisely. Because every road ends `b = 0` (§3.1) and a successor link always
contacts the next road's start, **100 % of these comparisons have one side pinned to zero** — measured
on all ten maps. The figure is a sound measure of *a slope kink exists at this seam*, which is the
defect, but it is not a measure of the two roads disagreeing about the terrain grade: it is
essentially the magnitude of whichever side is not zeroed. What the roads actually disagree about
only becomes measurable once both sides carry real tangents (§17). Seams are counted once each, not
once per direction.

This table reproduces the corresponding measurement in issue #29 to three decimals on both maps it
reported (Iran_Route_96: 58 links, max `dz` 0.191, max slope 0.306, 48 %; Lakeview_Carson: 488 links,
`dz` p90 0.344 / max 1.197, slope p90 0.112 / max 0.238, 79 %), which were measured independently.

### 3.4 Junction connector grades

Grades implied between consecutive records on roads with `junction != -1`:

| map | connector roads | spans | > 15 % | > 25 % | worst | median road length |
|---|---:|---:|---:|---:|---:|---:|
| Arapahoe_I25 | 732 | 1,279 | 6 | 1 | 32.3 % | 10.68 m |
| Bellvue_Overpass | 27 | 54 | 0 | 0 | 9.5 % | 10.13 m |
| East56th | 12 | 18 | 0 | 0 | 1.5 % | 11.92 m |
| GalleyRoad | 310 | 512 | 30 | 13 | 55.1 % | 11.94 m |
| Gardnerville_Centerville_Lane | 158 | 263 | 0 | 0 | 7.9 % | 11.94 m |
| IRAN | 1,263 | 2,096 | 9 | 6 | 67.9 % | 11.99 m |
| Iran_Route_96 | 29 | 62 | 1 | 1 | 30.6 % | 16.74 m |
| Lakeview_Carson | 244 | 417 | 6 | 0 | 23.7 % | 12.02 m |
| SF_LaurelHeights | 373 | 687 | 0 | 0 | 13.6 % | 11.98 m |
| wrigley | 1,478 | 1,713 | 322 | 304 | 763 % | 9.65 m |

These are **sampled terrain heights, not fitting artifacts** — a C1 fit will round the creases either
side of them and leave the ramp itself standing at full height. A 30 % grade over ten metres is not a
road, and 763 % is not a surface at all.

`Iran_Route_96` (62 spans, one above 15 %, worst 30.6 %) and `Lakeview_Carson` (417 spans, six above
15 %, worst 23.7 %) match issue #29 exactly.

### 3.5 Terminal-span geometry

`StationsAlong` (`ElevationInjector.cs:86-93`) yields `0, step, 2·step, …` strictly below the road
length, then **exactly `length`**. The final span is therefore a remainder of whatever the road length
leaves over, not a full step:

| quantity | value |
|---|---|
| roads measured | 7,200 |
| terminal span, median | 4.99 m |
| terminal span, p10 | 0.94 m |
| terminal span, min | 0.0063 m |
| terminal spans under 1 m | 10.3 % |
| terminal spans under 0.1 m | 0.4 % |
| **terminal spans implying a grade above 15 %** | **585 of 7,200 (8.1 %)** |

A full step's worth of sampling noise divided by a centimetre-scale remainder produces an arbitrarily
large apparent grade. Today that grade is discarded (the last record's `b` is forced to 0), so the
error is currently latent — but any fix that starts honouring the terminal tangent inherits it. The
terminal span must be handled explicitly, not merely included in the fit.

## 4. Causes, in impact order

1. **Slope discontinuity at every sample boundary.** `BuildElevationProfile`
   (`ElevationInjector.cs:191-213`) hardcodes `c=0, d=0`. Each span is an independent straight ramp
   with no tangent agreement at either end. Measured at §3.2: 30.7 % of boundaries step more than 2 %
   grade, median step 1.05 %.

2. **Every road ends dead-flat.** 7,200 of 7,200 (§3.1). Guarantees a kink at every junction
   independent of the terrain.

3. **Slope is discontinuous across roads; height mostly is not.** §3.3 — slope mismatched on 21-79 %
   of links, heights agreeing at the median but with a 0.2-2.9 m tail.

4. **Junction connectors sample their heights independently.** §3.4 — two connectors leaving one node
   can disagree by metres over ten metres of length. Short connectors are also structurally immune to
   `RejectOutliers` (`ElevationInjector.cs:281-305`), which needs a valid neighbour on **both** sides
   and so can never bracket anything on a two-record road.

5. **Opposing carriageways of one street are fitted independently.** OSM encodes each direction as its
   own way, so netconvert emits two `<road>` records on a coincident reference line — the reason the
   OSM mesh defaults set `wall_height = 0` (`CarlaNet/python/carlanet/__init__.py:469-475`).
   `ElevationInjector` treats them as unrelated: separate sample series, separate outlier rejection,
   separate fits, on 10 m grids whose stations land at *different physical points* because
   `s_left = length − s_right` is generally not a multiple of the step. On a 39 % grade a 5 m station
   offset is 2 m of height. Issue #29 measured the consequence on `Iran_Route_96` roads 180/199 — two
   2,303.05 m roads whose reference lines are coincident to 0.000 m at all 461 compared stations:
   median `|dz|` 0.008 m, p90 0.091 m, **max 1.201 m**, 6 % of stations above 0.25 m, and **242
   crossovers** where the two halves of one carriageway swap which is on top.

6. **Unfiltered photogrammetry noise.** `RejectOutliers` removes only isolated spikes beyond 4 m from
   both neighbours (default `outlierThresholdMeters = 4.0`, `ElevationInjector.cs:139`). Everything
   below that enters the profile as a genuine-looking slope change. There is no low-pass filter
   anywhere in the chain. §3.2's `wrigley` result — 1,111 boundaries stepping more than 0.5 slope on
   ordinary 10 m spans — is this cause, not the fit.

## 5. Why the existing junction mesh smoothing does not cover this

`Map::GenerateChunkedMesh` (`Map.cpp:1117-1200`) — the path OpenDRIVE world generation actually takes —
already smooths junctions when `smooth_junctions` is set, which the OSM mesh defaults do set
(`carlanet/__init__.py:469-475`). `MeshFactory::MergeAndSmooth` (`MeshFactory.cpp:1098-1131`) runs
**100 iterations of weighted Laplacian z-smoothing (λ = 0.5)** over junction lane-mesh vertices.

Its scope is narrow in two ways that matter here, both read from `GetVertexNeighborhoodAndWeights`
(`MeshFactory.cpp:1050-1096`):

- The r-tree contains **only the junction's own connector lane meshes**. Approach roads are not in the
  neighbourhood, so the smoothing cannot carry slope across the junction boundary.
- Only interior vertices are moved (`i > 2 && i < n-2`); the first and last two vertices of each lane
  mesh are pinned. The junction's boundary heights are held exactly where the profile put them, so a
  connector spanning a 3 m height error still spans it — the error is redistributed along the
  connector, not removed.

There is a further consequence worth stating explicitly, because it is not recorded anywhere else:
**this smoothing moves mesh vertices only, never the profile.** Waypoint z inside a junction still
comes from the raw cubic via `Lane::ComputeTransform` → `GetDirectedPointIn` (§2). Wherever the
Laplacian moves a junction vertex, the rendered surface and the driven path disagree by that amount.
Vehicles crossing a junction are placed against a curve that is not the surface they are drawn on.
Correcting the profile fixes both consumers at once; leaning harder on mesh smoothing would widen the
gap between them. The size of that gap has not been measured — it needs the built mesh, so it is a
runtime measurement, not an offline one.

## 6. Monotone cubic Hermite fit

Add a fit mode that emits real `c` and `d` from a **monotone cubic Hermite interpolant with
Fritsch-Carlson tangent limiting** (PCHIP). Keep `PiecewiseConstant` and `PiecewiseLinear` intact and
selectable; name the new member for what it does.

**Monotone specifically, not a natural cubic spline.** A natural spline solves a global tridiagonal
system for C2 continuity and is free to overshoot between knots; on a noisy real-world height series
it invents humps and dips that were never sampled. On a road surface an invented 30 cm hump reads
worse than the polyline it replaced, and it would also corrupt the bare-earth telemetry truth derived
from the same profile. Fritsch-Carlson limiting guarantees the interpolant stays within the bracketing
sample values on monotone runs, at the cost of dropping to C1 rather than C2 — which is exactly the
continuity class the defect calls for.

Per road, with samples `(s_i, z_i)`, spans `h_i = s_{i+1} − s_i` and secants
`Δ_i = (z_{i+1} − z_i)/h_i`:

1. Initial tangents `m_i` from the weighted three-point difference (one-sided at the ends).
2. Wherever `Δ_i = 0`, force `m_i = m_{i+1} = 0` — a flat run stays flat.
3. Wherever `sign(m_i) ≠ sign(Δ_i)`, force `m_i = 0` — no reversal against the local secant.
4. Limit: with `α = m_i/Δ_i`, `β = m_{i+1}/Δ_i`, if `α² + β² > 9` scale both by `τ = 3/√(α² + β²)`.

Then each span emits one record keyed at `s_i`:

```
a = z_i
b = m_i
c = (3·Δ_i − 2·m_i − m_{i+1}) / h_i
d = (m_i + m_{i+1} − 2·Δ_i) / h_i²
```

This reproduces `z_i` and `z_{i+1}` exactly and matches tangents at both ends, so the slope step at
every internal boundary becomes zero by construction rather than by tolerance. CARLA evaluates with
`ds = s − record_s` (§2), which is the same convention these coefficients are written in, so no
station shifting is required beyond what `CubicPolynomial.Set(a,b,c,d,s)` already does.

Degenerate spans need explicit handling: an `h_i` below a floor (the 6 mm terminal spans of §3.5)
makes `c` and `d` numerically explosive. Merge or drop such a station rather than fitting through it.

## 7. Noise filtering before the fit

Low-pass each road's z series before fitting — Savitzky-Golay, or robust LOESS where the extra cost is
justified. The fit removes creases; it does not remove the noise that created them, and §3.2's
`wrigley` result shows the noise alone is large enough to matter on ordinary spans.

Constraints:

- **Must not smooth across `Raised` runs.** The `Raised` flag marks heights deliberately routed to the
  photoreal surface by `GradeSeparation` (bridge decks — [06](06_Elevation_Strategy.md) §10). Those
  samples are already exempt from outlier rejection (`ElevationInjector.cs:292`) and must be exempt
  here too. Anchor the filter at the boundaries of a raised run and filter each side independently: a
  deck transition is a real step, not noise. Smearing a deck back into the road beneath it is
  precisely the defect the layer routing exists to prevent.
- **Must not smooth across genuine grade breaks at road ends.** A road end is a boundary condition,
  not an interior point.
- **Strength is a parameter with a conservative default**, and the probe (§14) reports what each
  setting does to deviation-from-samples, so the smoothing/fidelity trade-off is visible rather than
  assumed. Filtering necessarily moves the curve off the sampled points; how far is a decision, and it
  needs a number attached.

## 8. Road-end tangents and junction slope continuity

Resolve one height per shared node so incident roads agree, and set endpoint tangents so slope carries
through the junction rather than resetting.

The `b = 0`-on-last-record bug (§3.1, cause 2) is fixed as part of this: the final record needs a real
tangent derived from the incoming grade. §3.5 is the trap — the terminal span is a short remainder,
and 8.1 % of them imply a grade above 15 %. Deriving the terminal tangent from that span alone
substitutes one artificial number for another. Derive it from the road's approach grade over a
distance comparable to the sample step, and treat the terminal station as a point the curve must pass
through rather than a span to take a slope from.

The failure mode is corroborated from an unrelated toolchain: the community CARLA map-building notes
at `thillRobot/carla_simulator/docs/maps.md` describe hitting it via RoadRunner and having to rebuild
maneuver roads by hand. It is a known class of defect in OpenDRIVE map production, worth fixing
explicitly rather than hoping a better intra-road fit conceals it.

## 9. Junction connector height sourcing

A connector inside a junction should take its heights from a surface consistent with the junction it
belongs to and the roads it links, rather than sampling independently. Two connectors leaving one node
must not disagree by metres (§3.4).

Constrain a connector's profile to interpolate between its resolved endpoint heights when its own
samples imply a grade the linked roads do not carry. Note that outlier rejection cannot help here:
`RejectOutliers` needs a valid neighbour on both sides, and a two-record road has no interior point.
Sub-metre connector roads (§3.2, the 0.20 m roads behind `wrigley`'s extreme tail) should carry a
single record at the resolved node height rather than a fitted ramp.

## 10. Paired carriageway height agreement

Detect opposing-direction road pairs — equal length, coincident reference line, headings opposite at
matched stations — and give the pair **one shared height series** rather than two independent ones.
Sampling once and evaluating both roads' profiles from it removes the station-offset error at its
source (cause 5) and costs no extra Cesium round-trips; it halves them for these roads. Each road
keeps its own `<elevation>` records — only the underlying heights are shared.

Detection is cheap and safe to gate on strict agreement: issue #29's measurement found roads 180/199
coincident to 0.000 m at all 461 compared stations, so a tight coincidence tolerance will not produce
false pairs.

## 11. Sample density, and only after the above

With the fit and the filter in place, evaluate reducing `--step`
(`CarlaControl/src/carlacontrol/CarlaControlArgumentParser.py:97`, default 10.0, reaching the
generator as `sample_step_meters` at `CarlaControl/src/carlacontrol/WorldBuilder.py:116` and
defaulting to the same 10.0 in `CarlaNet/python/carlanet/__init__.py:2228`).

Reducing it *before* §6 and §7 makes things strictly worse: more samples under a linear fit means more
noise-driven slope breaks per metre, and §3.2 shows the noise is already the larger term on the worst
maps. Sampling is a Cesium round-trip and dominates build time, so measure the cost against the
measured improvement rather than assuming it helps.

## 12. Netconvert flags — real but secondary

Cheap to test via `OsmConversionOptions.ExtraArgs` with no rebuild; the effective argument list is
built in `CarlaNet/src/CarlaNet.Map/OsmConverter.cs:222-288`, and `WorldBuilder` appends the
drivable-road filter and sets `opts.ExtraArgs`
(`CarlaControl/src/carlacontrol/WorldBuilder.py:38-46`). Small next to §6-§10, but worth trying:

- `--geometry.min-dist 1.0` (default `-1`, off) — drops near-duplicate OSM geometry points that create
  micro-kinks. Most relevant of the set.
- `--junctions.internal-link-detail 10` (default `5`) — smoother s-curves through intersections.
- `--geometry.min-radius.fix` (default off; `min-radius` 9 m) — straightens hairpin artifacts from OSM
  node noise.

Already at useful defaults, leave alone: `--geometry.max-grade.fix` (true), `--junctions.corner-detail`
(5), `--opendrive-output.straight-threshold` (1e-08).

**Do not add `--osm.elevation` or `--osm.layer-elevation`.** Those are OSM-tag and DEM elevation
heuristics that would fight both the Cesium sampling and the `GradeSeparation` layer routing. Our
elevation source is better for this application.

## 13. Out of scope

- **Triangular holes in the generated road mesh** — tracked separately in
  [sbrett9/carla#30](https://github.com/sbrett9/carla/issues/30), and measured there as not
  attributable to the `.xodr`.
- **Removing the wash crossing at `Iran_Route_96` 27.0705587, 55.9590512.** The 5.7 m dip is real
  sampled terrain. A monotone fit rounds its creases and keeps the descent, which is correct unless
  the road actually crosses on a causeway or culvert — a layer-routing question for `GradeSeparation`,
  not a fitting one.
- ~~**Merging a junction into a single mesh.**~~ **Now in scope — see §18.** Held out originally on
  the reasoning that netconvert emits connectors as separate roads and CARLA meshes each separately.
  That is still true, but §18 measures the consequence: once the profile agrees, the remaining visible
  damage at an intersection is the mesh assembly, not the elevation.
- ~~**Changing the junction Laplacian smoothing.**~~ **Now in scope — see §18**, since retriangulating
  a junction supersedes what `MergeAndSmooth` is attempting.
- **Horizontal/plan-view geometry** — measured as already smooth (§3.1).
- **The `CarlaTools` Digital Twin tool.** Its OSM→xodr link is a dead stub
  (`CustomFileDownloader.cpp:72`, `HAS_OSM2ODR` never defined), and it is an editor-time map baker,
  architecturally at odds with headless runtime generation.
- **Changing `vertex_distance` (2.0 m).** The mesh faithfully reproduces a jagged input; coarsening it
  would hide the symptom and lose road-surface fidelity.

## 14. Work order

Later items depend on earlier ones being validated, so the order is load-bearing.

1. **Offline probe** — read-only, no server, no editor; precedent
   `CarlaNet/python/probe_grade_separation.py`. Reads an existing `*_elevated.xodr`, recovers each
   road's `(s, z)` series from the `a` attributes (which are the sampled heights), re-fits with a
   candidate scheme, and reports: slope discontinuity distribution at internal boundaries; max/RMS
   deviation of the fitted curve from the samples; overshoot outside bracketing sample values on
   monotone runs; endpoint height and slope mismatch across links; connector grades against the roads
   they link; paired-carriageway disagreement with crossover count; and a locate-by-lat/lon mode so a
   surface reported from a running session can be tied to road ids without loading the map. The
   measurements in §3 are the control it must reproduce.
2. **Monotone cubic Hermite fit mode** (§6).
3. **Pre-fit low-pass filter** (§7).
4. **Road-end tangents and junction slope continuity** (§8).
5. **Junction connector height sourcing** (§9).
6. **Paired carriageway height agreement** (§10).
7. **Sample density evaluation** (§11).
8. **Netconvert flag trial** (§12).
9. **Offline mesh probe** (§18) — reconstruct the vertices `MeshFactory` emits, from the .xodr, so
   mesh changes are measurable without the engine the way profile changes were.
10. **Weld coincident vertices** within each generated mesh (§18).
11. **Retriangulate junctions as one surface** (§18).

Items 1, 2, 4 and 6 are done. Item 5 is deferred behind §18: once a junction is one retriangulated
surface its interior grade comes from the resolved height field, so connector height sourcing
largely dissolves into it for rendering. It still matters for waypoints, so it stays on the list.

## 15. Acceptance criteria

Each reported with the measured number, not a claim. Baselines are §3.

| # | criterion | baseline |
|---|---|---|
| 1 | Zero `<elevation>` records with both `c=0` and `d=0` on any road carrying more than two samples | 3,811 such roads, 100 % linear |
| 2 | Slope discontinuity at internal boundaries below a stated epsilon, with the before/after distribution; height continuity must not regress | median 0.0105, p90 0.0754, p99 1.2957, max 52.61, 30.7 % of 27,110 boundaries above 0.02; height step already exact at 1.8e-15 m |
| 3 | Zero overshoot outside bracketing sample values on monotone runs | not applicable to a linear fit; this is the new risk the monotone constraint bounds |
| 4 | Slope mismatch across road-to-road links below a stated tolerance, and no road ending with an artificial `b = 0` | 7,200 of 7,200 roads end `b=0`. The 21-79 % of links above 0.02 measures the kink, not a road-to-road disagreement — 100 % of comparisons have one side pinned to zero (§3.3), so the tolerance must be set against the post-fit measurement in §17 |
| 5 | Junction endpoint height agreement within a stated tolerance | max 0.19 m Iran_Route_96, 1.20 m Lakeview_Carson, 2.91 m GalleyRoad |
| 6 | No junction connector carrying a grade the roads it links do not | worst 30.6 % Iran_Route_96, 23.7 % Lakeview_Carson, 55.1 % GalleyRoad |
| 7 | Paired opposing carriageways agree within a stated tolerance at matched stations, crossovers driven to zero | roads 180/199: max 1.201 m, 6 % of stations above 0.25 m, 242 crossovers |
| 8 | Terminal records carry a tangent derived from the approach grade rather than from the remainder span, demonstrated on the roads where the two differ | 585 of 7,200 roads (8.1 %) have a terminal span implying a grade above 15 %; minimum terminal span 6.3 mm |
| 9 | `dotnet test` green, `ElevationInjectorTests` and `GradeSeparationTests` in particular | `GradeSeparationTests.cs:779` pins `PiecewiseLinear` for deck preservation |
| 10 | New unit tests covering the fit mode, the filter, the road-end tangent, connector height sourcing, and carriageway pairing | — |

Criterion 9 additionally requires a **regression test that a bridge deck survives the new fit and the
filtering** — the `Raised` exemption of §7 proven, not asserted.

## 16. Visual verification

Measurements do not close this. The road surface is judged visually by running the SCTMV client,
`CarlaControl/scripts/run_SCTMV.py`:

- **`Iran_Route_96` at 27.0769276, 55.9823149** — junction fork; road 190 forks into 191 and 196
  through connectors 211 and 212, with four slope reversals in twenty metres of travel (issue #29).
- **`Iran_Route_96` at 27.0705587, 55.9590512** — split carriageway over a wash; roads 180 and 199.
- **`Lakeview_Carson`** or **`SF_LaurelHeights`** — real relief, the general check. Lakeview_Carson
  carries the worst link-slope mismatch measured (78.7 %); SF_LaurelHeights the worst
  internal-boundary rate (40.8 %).
- **`Bellvue_Overpass`** and **`Arapahoe_I25`** — grade-separation regression cases. Bridge decks must
  not smear into the roads beneath them.
- **`wrigley`** — the noise-dominated case, and the one that exercises the degenerate sub-metre
  connector handling.

## 17. What the probe measured

`CarlaNet/python/probe_elevation_profile.py` (with `ElevationProfileProbe` and
`ElevationProfileFitter`) implements §14 item 1. Its "before" column reproduces every
table in §3 exactly on all ten maps, and its locate mode reproduces both named
reproducers of §16 — the same five roads within 45 m of the junction fork, the same
closest approaches, and the same `+0.092 / 0.000 / +0.306 / 0.000 / −0.061` slope
sequence through it. That is the control; the results below are what the candidate
monotone fit does against it.

### The fit does what it was specified to do

Re-fitting every road with monotone cubic Hermite drives the slope step at internal
record boundaries to **exactly zero on all ten maps** — median, p90, p99 and max all
0.0000, against a 30.7 % baseline above 0.02. Overshoot is **0 violations across every
monotone span on every map** (4,310 spans on Arapahoe_I25, 1,602 on Iran_Route_96, and
so on), so the Fritsch-Carlson limiting is holding. Height continuity does not regress:
the C0 step stays at machine epsilon, rising from ~1e-15 m to ~1e-14 m purely from the
extra rounding of evaluating a full cubic. Criteria 1, 2 and 3 are met by the fit alone.

### The link-slope baseline measures the kink, not a disagreement between roads

The probe established something that changes how §3.3 and criterion 4 must be read.
**On every one of the ten maps, 100 % of link comparisons have one side pinned to
`b = 0`.** Every road ends flat, and in these files a successor link always contacts the
next road's start while a predecessor link always contacts the previous road's end — so
one of the two tangents being compared is always the artificial zero.

The pre-fix figure of 21-79 % of links above 0.02 is therefore a valid measure of *there
is a slope kink at this seam* — the kink is real, and it is the defect — but it is **not**
a measure of the two roads disagreeing about the terrain grade. It is essentially the
magnitude of whichever side is not zeroed. Cause 3's ranking stands; its explanation
needed this correction.

Once both sides carry real tangents the quantity becomes measurable for the first time,
and it is non-zero. Comparing it against the pre-fix number compares two different
things, so the paired per-seam change is the only honest read:

| map | seams | mean before | mean after | improved | worsened |
|---|---:|---:|---:|---:|---:|
| Arapahoe_I25 | 1,464 | 0.0246 | 0.0260 | 795 | 668 |
| Bellvue_Overpass | 54 | 0.0184 | 0.0214 | 24 | 30 |
| East56th | 24 | 0.0078 | 0.0076 | 12 | 12 |
| GalleyRoad | 620 | 0.0490 | 0.0519 | 328 | 292 |
| Gardnerville_Centerville_Lane | 316 | 0.0170 | 0.0173 | 153 | 163 |
| IRAN | 2,526 | 0.0144 | 0.0145 | 1,312 | 1,213 |
| Iran_Route_96 | 58 | 0.0300 | 0.0264 | 37 | 21 |
| Lakeview_Carson | 488 | 0.0572 | 0.0412 | 309 | 177 |
| SF_LaurelHeights | 746 | 0.0253 | 0.0332 | 314 | 432 |
| wrigley | 2,956 | 0.2133 | 0.3976 | 1,069 | 1,866 |

Most maps split near evenly, which is what swapping one quantity for a different one
looks like. Lakeview_Carson and Iran_Route_96 — the two with real relief and no elevated
structures — improve outright. `wrigley` degrades sharply, but its "real" grades include
the 11 m structure arches of the next subsection, so exposing them is the measurement
working, not the fit failing.

**§8 is load-bearing rather than a refinement**, for this reason rather than the one first
recorded here: the residual disagreement between adjacent roads is only visible after the
fit, it is non-zero on every map, and nothing in §6 addresses it. Endpoint tangents have
to be resolved jointly across the junction. Item 2 should not be landed alone and judged
on link statistics, because the pre-fix statistic it would be judged against does not
measure the same thing.

Seams are counted once each, not once per direction — verified on Iran_Route_96, where 58
directed comparisons cover 58 distinct seams.

Junction connector grades are **unchanged** by the fit — worst 32.3 % on Arapahoe_I25
before and after, 30.6 % on Iran_Route_96 — confirming by measurement what §3.4 argued:
a C1 fit rounds the creases either side of a connector ramp and leaves the ramp standing.
§9 is needed for criterion 6.

Paired carriageways improve but nowhere near enough: roads 180/199 go from max 1.2015 m
and 242 crossovers to max 1.0506 m and 202 crossovers. §10 is needed for criterion 7.

### Sustained departures that outlier rejection cannot reach

The pairing detector finds **533 carriageway pairs on `wrigley`**, of which **45 carry a
sustained departure** — a continuous run, 25 to 45 m long, over which the two halves of
one street disagree by more than a metre, reaching **12.674 m**. Inspected directly, the
two profiles agree to 0.000 m at both shared endpoints and one carriageway arches
smoothly over its twin in between: its samples landed on an overhead structure while the
twin's landed on the ground.

Because the departure is a smooth run rather than an isolated spike, **`RejectOutliers`
cannot reach it by construction** — it only rejects a sample that is far from *both*
neighbours, and every sample along the arch is close to its neighbours. This is a
distinct failure mode from the noise of cause 6, at roughly forty times the magnitude,
and it makes §10 do more than remove station-offset error. It also constrains §10: the
shared series cannot simply be taken from one road of the pair, because on these roads
one of the two is on a structure. Choosing the lower, or the layer-consistent, series is
a decision §10 has to make explicitly.

### The low-pass is not a free win, and must not be applied blind

Sweeping the filter window with the linear fit (whose slope steps are exactly the
sampled grade variation) separates what the filter removes from what it costs:

| window | SF_LaurelHeights above 0.02 | its rms deviation | wrigley above 0.02 | its rms deviation |
|---:|---:|---:|---:|---:|
| 1 (off) | 40.8 % | 0.000 m | 41.0 % | 0.000 m |
| 5 | 30.4 % | 0.066 m | **45.4 %** | 0.633 m |
| 7 | 27.4 % | 0.077 m | **45.7 %** | 0.833 m |
| 9 | 27.7 % | 0.080 m | **46.4 %** | 0.954 m |

On a map whose roughness really is zero-mean noise the trade is good: SF_LaurelHeights
loses a quarter of its grade breaks for 6.6 cm rms. On `wrigley` the filter **makes the
metric worse at every strength** while dragging the curve up to 6.4 m (window 5) from
the sampled heights, because smoothing an 11 m arch spreads one large grade break into
several moderate ones instead of removing it. Worse, a 6.4 m excursion is the deck-smear
failure the `Raised` exemption exists to prevent, reproduced here on a map where the
probe has no `Raised` information at all.

So §7's "conservative default" cannot be a single global number. The filter needs to be
gated on the series actually being noise — or applied only where no sustained departure
is present — and it must be measured per map rather than assumed.

### Probe limitations, stated

- **`Raised` flags are invisible to it.** They live in `ElevationInjector`'s in-memory
  sample list, not in the .xodr, so the probe cannot verify deck preservation. That check
  belongs to `probe_grade_separation.py` and to the C# regression test in criterion 9.
- **A filter window of `order + 1` points or fewer is a mathematical no-op** — the
  polynomial passes through the centre sample exactly. With the default order 2 this
  makes windows of 3 do nothing at all; the tool warns rather than silently reporting
  "no change".
- **One derived figure differs from the issue.** At the split-carriageway reproducer the
  probe measures the nearest approach of both roads' reference lines as 0.74 m where the
  issue reports 1.2 m. The road identification, lengths, profiles and wash figures all
  match exactly, so this is a difference in the distance metric, not in the finding; it
  is unexplained and does not affect anything downstream.


## 18. Single triangulated road surface

Brought into scope 2026-08-18 after the elevation work landed and the road surface was still
visibly shattered at intersections. The measurements below say why: **the profile is no longer the
limiting factor there, the mesh assembly is.**

### The profile at a junction is already agreed

Junction 106 of `Arapahoe_I25` — the intersection three reported picks landed in — carries 14
connector roads within a 30 m radius. Sampling every connector at 1 m and comparing heights wherever
two *different* connectors pass through the same plan position (within 1.5 m):

| | |
|---|---|
| plan-coincident sample pairs | 398 |
| vertical gap, median | **0.020 m** |
| p90 | 0.085 m |
| max | **0.207 m** |
| over 0.25 m | **0.0 %** |

Two centimetres median. No further profile work changes what that intersection looks like.

### What the mesh actually is

- `Map::GenerateChunkedMesh` (`Map.cpp:1117-1200`) emits one mesh per lane section per road, split
  at `max_road_length` (500 m for OSM), plus one merged mesh per junction, then bins everything into
  a grid. On `Arapahoe_I25` that is **183 junction meshes and 321 non-junction roads**.
- Junction 106 alone is assembled from **21 independent lane ribbons** across its 14 connectors.
- `Mesh::operator+=` (`Mesh.cpp:348-373`) appends the vertex, normal, index and UV buffers and
  offsets the indices. **It never welds, never deduplicates, and never shares a vertex.** A junction
  is therefore one mesh *object* but not one *surface*: overlapping quad strips whose edges are
  duplicated, sitting 2-20 cm apart.

At those separations the depth buffer cannot order the surfaces reliably, which is what produces the
hard polygon boundaries, bright slivers and shadow acne that survive a correct elevation profile.
`MergeAndSmooth`'s 100-iteration Laplacian is upstream's attempt to hide exactly this; it is
insufficient, it only moves interior vertices, and it displaces the mesh from the profile the
waypoints still follow (§5).

### There are no lane semantics in the mesh to lose

This is the finding that unblocks stitching. `OpenDriveGenerator.cpp:87-102` spawns each chunk as one
`AProceduralMeshActor` and calls `CreateMeshSection_LinearColor` with **section index 0, empty UVs,
empty vertex colours, empty tangents**, and no semantic tagging. Lane type, lane id and road id never
reach the engine, and sidewalk lanes are already merged into the same actor as driving lanes.

Lane semantics live in the OpenDRIVE `Map` object — waypoints, routing, lane changes and the traffic
manager all read that and never the mesh. **Merging the mesh into one surface costs no driving
semantics.** Where per-triangle attribution is wanted later (labelling, segmentation), the route is
vertex attributes — `CreateMeshSection_LinearColor` already accepts UV0 and VertexColor and both are
currently passed empty — not split geometry. That is strictly more capable than the present state.

### The junction extra width was load-bearing, and is not any more

`additional_width` (0.6 m, added to each *half*-width of a driving lane inside a junction) makes
every connector 1.2 m wider than the roads it joins. That overhang is visible on short connectors —
of the 18 under 3 m long on `Arapahoe_I25`, all are wider than they are long, and the 1.30 m
connector at 39.5955106, −104.8856034 is 4.55 m wide against the 3.35 m lanes either side.

It is nonetheless doing real work. The parameter is a mesh-generation input rather than something
written into the .xodr, so both settings can be measured against one map:

| | `additional_width = 0.6` | `= 0.0` |
|---|---:|---:|
| junctions enclosing a hole > 0.5 m² | 44 of 183 | **60 of 183** |
| total enclosed hole area | 347.0 m² | **668.2 m²** |
| largest single hole | 66.8 m² | 66.8 m² |
| cross-strip duplicate vertices | 49.4 % | 55.3 % |

Removing the overhang **nearly doubles** the enclosed hole area: the overlap it creates is what
closes the seams between adjacent connectors. The largest single hole is identical under both,
confirming that the big holes are unmodelled junction interior rather than seam slack, and that no
setting of this parameter reaches them.

That was measured against the ribbon mesh, and the resolved surface subsumes it exactly as
anticipated: a single surface has no inter-connector seams to bridge, and the asphalt between
turning paths is covered by the enclosure test rather than by overlapping the paths themselves.

Re-measured against the resolved surface, the overhang is no longer neutral — it is harmful:

| | `additional_width = 0.0` | `= 0.6` (default) |
|---|---:|---:|
| known hole and non-hole points classified correctly | **13 of 13** | 12 of 13 — paves the median |
| height layers | **13** | 24 |
| one-cell cracks through the surface | **203** (51 m²) | 228 (57 m²) |
| paved area | 245,429 m² | 248,802 m² |
| worst neighbour step, edge / diagonal | 0.395 / 0.646 m | 0.381 / 0.457 m |

Two costs. Widening a connector far enough across a median makes the median read as enclosed by
junction paving, so the gap filling paves it — the island-and-spike defect returning through a
different door. And the added overlap stacks surfaces that disagree in height, which the layer split
then tears into separate sheets: 24 layers where true lane width gives 13, on 3,373 m² of asphalt
that does not exist in the road network. Against that, it buys a slightly better worst diagonal step.

The resolved-surface path therefore meshes connectors at their true lane width regardless of the
parameter. The parameter is kept, and still applies to the per-lane path, which still needs it —
removing it there would reopen the 668.2 m² measured above.

### Order of work

**Fix the data before welding it.** Welding is a representation change and cannot repair a genuine
disagreement: where two surfaces are centimetres apart it produces a clean single surface, but across
a real step it would either leave the step or bridge it with a near-vertical triangle. §10 therefore
lands first, and did.

1. **Offline mesh probe.** Reconstruct the vertices `MeshFactory` emits directly from the .xodr —
   lane widths and `GetCornerPositions` are all recoverable — and measure duplicate vertices,
   coincident-but-unwelded pairs, T-junctions and overlapping faces. Without this the mesh work is
   judged from screenshots, which is what the elevation work deliberately avoided.
2. **Weld coincident vertices** within each generated mesh, tolerance a few centimetres. Bounded,
   removes the z-fighting, measurable against step 1.
3. **Retriangulate junctions** as one surface: take the union of the connector lane footprints in
   plan, triangulate once, and sample z from the resolved elevation profile.

Constraint carried from §7 and §10: a grade separation passing through a junction footprint must not
be welded or triangulated into the surface beneath it. `Raised` samples mark those, and the mesh
stage has to honour them the same way the fit does.


## 19. What landed, and what it measured

`ElevationFitMode.MonotoneCubicHermite` carries items 2, 4 and 6 together and is selected at the
production call site. `PiecewiseConstant` and `PiecewiseLinear` are untouched and reproduce their
previous output byte for byte, so the whole change is gated on the fit mode.

Measured by re-running the injector over the ten smoketest maps and reading the output with the
probe. On `Arapahoe_I25`, confirmed against a map generated by the running client rather than a
simulation:

| quantity | before | after |
|---|---|---|
| slope kinks inside roads, above 0.02 | 17-41 % of boundaries | **0.0 % on every map** |
| records carrying curvature | 0 of 34,310 | 4,211 of 5,364 on Arapahoe_I25 |
| roads ending on an artificial `b = 0` | 7,200 of 7,200 | only the genuinely flat ones |
| height mismatch where linked roads meet | up to 19.5 m | **exactly 0.000 m on every map** |
| junction seam slope, median | 0.0098-0.0534 | ~0, continuous |
| paired carriageway disagreement, worst | 0.026-13.442 m | 0.002-0.770 m |
| carriageway crossovers | 19-976 per map | roughly halved on every map |

Two things the measurement caught that reasoning had not:

**Item 4 introduced a defect that only item 6 exposed.** The two directions of a street never link to
each other, so the junction pass put their mirrored ends in different node groups and resolved them
to different heights — reopening along the centre line the disagreement the shared height series had
just closed. On `wrigley` that alone was 9.0 m. Tying a merged pair's corners into one node took it
under a metre. A junction resolution that groups only *linked* ends is incomplete: coincident ends
must be grouped too.

**Fitting a shared surface is not the same as sharing a series.** The first implementation evaluated
the merged surface at each road's own stations, which left `Arapahoe_I25` at 0.470 m and *raised* the
crossover count. Both roads must carry records at the same physical stations, so their fitted curves
are reflections of each other rather than two independent fits of one function.


## 20. Holes in the resolved surface

Resolving the network into one surface per height layer left holes a vehicle drops through. They
looked alike in the viewer and had three unrelated causes, which is why fixing one never moved the
others. All three were found by mapping cell coverage around lat/lon points taken from the running
scene, not by reading the code.

### The enclosure test was too strict, then too loose

Paving a gap because paving surrounds it fills medians, the triangular islands between a slip lane
and the road it leaves, and the outside of bends — all have road on every side. Requiring instead
that *every* ray land on junction-connector paving cleared those but opened new holes inside real
intersections, where a ray leaving through an approach arm lands on the through road beyond it.

Measured over both sets of points, the two populations separate cleanly on a majority rather than a
unanimity:

| | reaches junction paving, of 8 directions | nearest junction paving |
|---|---|---|
| gaps genuinely inside an intersection | 5, 5, 6, 8, 8 | 0.5-1.5 m |
| median, island, bulge, two juts, spike | 0, 1, 2, 2, 2 | 6 m to none |

Nothing measured falls between two and five. The test is now: paving within reach along all four
axes, *and* junction paving in more than half of the eight directions. The axis condition is what
still excludes a median — the ray running along it reaches the limit and finds nothing.

### Layer growth was discarding the cells it rejected

A cell was marked claimed when it was *queued*, not when it was placed. The two tests that reject a
cell — the layer already occupies that plan position, or the cell disagrees with a neighbour the
layer holds — then dropped it into nothing: it belonged to no layer, and no later seed could pick it
up because it was already marked. 217 cells map-wide, 54 m2, arranged in one-cell lines along every
boundary where two growth branches meet. The long diagonal crack across the I-25 deck was this.

A cell is now claimed only when placed, and the seed sweep repeats until a pass finds nothing
unclaimed — a single pass only recovers rejects that happen to sit later in iteration order. Every
sampled cell now ends up in a layer: 977,848 of 977,848, against 977,631 before.

### Adjacent lane quads leave a sliver neither one covers

Two lanes derive their shared boundary independently, from different reference lines, so the two
edges disagree by a fraction of a millimetre. A cell centre landing inside that sliver is claimed by
neither quad. 663 cells map-wide, 166 m2, pinched between paving on opposite sides — a crack
narrower than a wheel, running down otherwise solid road.

This is closed morphologically: a cell is paved when paving lies within a metre on two opposite
sides, the two sides agree in height, and the result agrees with all eight neighbours. The height
agreement is what makes it safe to run over the whole network rather than only inside junctions — it
cannot bridge a deck to the road beneath it, and it cannot round off the end of a road.

### Measured together

| quantity | before | after |
|---|---|---|
| known hole and non-hole points classified correctly | 8 of 13 | **13 of 13** |
| cells lost by layer splitting | 217 (54 m2) | **0** |
| one-cell cracks through the surface | 877 (219 m2) | **203 (51 m2)** |
| worst neighbour step, edge / diagonal | 0.395 / 0.646 m | 0.395 / 0.646 m |
| neighbour pairs above 1 m, of 3.8 M | 0 | 0 |
| median neighbour step | - | 6.5 mm (p99 58.8 mm) |
| grade separation preserved | 6.64 m | 7.41 m (true clearance 6.8 m) |

The worst neighbour step is unchanged, but only because of `RelaxLayer`. Before relaxation the
retained cells raise it to 1.099 m: they are exactly the places where overlapping ribbons disagreed,
which is why they failed the growth test in the first place. Relaxation is doing real work here, not
cosmetic smoothing — without it these fixes would trade holes for steps.

The 203 cracks that remain are wider than a metre or have sides that disagree in height, so they are
left open deliberately rather than bridged with invented slope.


## Sources

- OpenDRIVE 1.4 §5.3.5 road elevation (the `<elevation>` cubic form).
- F. N. Fritsch and R. E. Carlson, *Monotone Piecewise Cubic Interpolation*, SIAM Journal on Numerical
  Analysis 17(2), 1980 — the tangent-limiting condition in §6.
- A. Savitzky and M. J. E. Golay, *Smoothing and Differentiation of Data by Simplified Least Squares
  Procedures*, Analytical Chemistry 36(8), 1964.
- SUMO netconvert option reference (geometry and junction options, §12).
- `thillRobot/carla_simulator`, `docs/maps.md` — independent report of the junction continuity failure
  from a RoadRunner toolchain.
