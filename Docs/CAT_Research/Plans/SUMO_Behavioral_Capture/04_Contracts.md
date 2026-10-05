# 04 — Interface contracts

**Status:** Plan section. Every claim about existing behaviour is read from the working tree
and cited `path:line`, or **measured** by inspecting an artifact (the measurement is described where it
is used), or explicitly labelled an inference. No code was changed, no build was run.
**Date:** 2026-09-18
**Scope:** Every interface between two components of the SUMO-driven behavioural-capture system: the
artifact's name and location, its format, its complete field table, a worked example, the validation
rules, the failure mode when a rule is violated, and the versioning rule. Ten contracts, `C1`–`C10`.
**Audience:** an engineer implementing one side of one of these interfaces who has not read the
conversation that produced this plan.

**Binds to:** [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3 (decisions not to re-litigate), §3a (the
simulated-time-of-day requirement), **§3b (the scope boundary — this pipeline labels; it never scores)**,
**§3c (the live exercise is a primary use case, and it is generic past our boundary)**, **§3d (cyclic
generation is driven from outside, and we do not judge our own runs)** and §4 (standing rules).
**Depends on:** [`03_CoSimulation_Runtime.md`](03_CoSimulation_Runtime.md) (owns the tick and
solar-clock *mechanism*; this section states the *guarantee*),
[`06_Truth_And_Annotation.md`](06_Truth_And_Annotation.md) (owns the annotation payload; this section
owns where it is carried and how it is joined),
[`08_Collection_And_EPoL.md`](08_Collection_And_EPoL.md) (owns the EPoL design; this section owns the
boundary shape), [`10_Scale_And_Performance.md`](10_Scale_And_Performance.md) (owns every numeric limit;
this section states which limits must exist, never their values),
[`11_Time_And_Illumination.md`](11_Time_And_Illumination.md) (owns what a civil time *means* here and
why a policy is chosen; this section owns the wire shape and the validation — §11.13 states exactly
what it needs from `11`), [`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) (owns how
an operator *expresses* a policy; this section owns how the expressed policy is *carried, bound and
checked*).

**Change history.**

| Rev | Change |
|---|---|
| 27 | 2026-10-05. `C3`: every interval in the supervision plan carries `anchor` — the events of its participant that commit its start and end, resolved against the route file the package carries, or null when the interval is declared in civil time — and an interval over a `duration` stop declares its length and no start (§5.2; [`06`](06_Truth_And_Annotation.md) §3.3, D6.4). The route index a phase anchor names is the package's own, so a plan and the route file it was compiled with stay bound by `routes_digest` |
| 26 | 2026-10-02. `C10`: the world truth track ([`06`](06_Truth_And_Annotation.md) §8.3) joins §12.7's artifacts. It is written under W2, the header first and every row flushed as one line, so a track cut off anywhere is the rows before the cut; its summary is written under W1, and its `ended`, written as the session ends, is the closing statement. A capture run always writes it, under its capture directory's `truth/` |
| 25 | 2026-10-02. `C9`: every capture's `<_solar>` and `carla:solar` carry `illumination_band`, from the achieved sun and never the declared time, and `illumination_band_elevation`, the elevation it was cut from — refraction-corrected wherever the block carries it (§11.8.3). The manifest's `captures_missing_solar_block` is stated, zero in a healthy run; until the manifest exists each recorder counts it and the closeout gates it at zero. `C8`: the observation writer strips both band fields and V8.7 refuses them, so the frozen field set is unchanged (§10.4a) |
| 24 | 2026-10-02. `C3`: the supervision plan carries `additional_digest`, the lane closures' additional file's SHA-256 as the lock's `files` records it, null where a scenario closes no lane, beside the routes', configuration's and network's (§5.2, §5.3). The lock's `files` table names `additional` where there is one. The plan's other completions — checked parameters, projected `hard_negative_for`, resolved exemplars, an absence's site — are [`06`](06_Truth_And_Annotation.md) §8.1's |
| 23 | 2026-10-02. `C4`: a `sensor_id` is the camera's name, and every camera has one (§6.1, §6.3). Every capture is named after it, `<sensor_id>_<local capture time>`, where every capture was `SCTMV_<local capture time>`, and the platform track's callsign is it, where it defaulted to `OVERWATCH` for every camera given none; a camera given none is `CARLA-SENSOR-<actor id>`. Its grammar loses `:`, which no Windows file name holds, and a name is used as given or refused, never rewritten. It is unique within a process and, spawned as the camera's `role_name`, refused where another camera in the world holds it, case aside. The platform track's uid stays `CARLA-SENSOR-<actor id>` |
| 22 | 2026-10-02. `C1`: the truth record's `special_type` is the catalogue's `cot_special_type`, as the owner ruled ([`06`](06_Truth_And_Annotation.md) D6.18): a vehicle whose body's blueprint a class draws carries that class's kind, empty where the class curates none, whatever the blueprint declares, and a blueprint no class draws keeps its own. A drive session hands its catalogue's table to the client it drives through, so the capture sidecar and the live pull of that process report it; the standalone producer writes it when given the catalogue (§3.4.2, §3.4.3) |
| 21 | 2026-10-02. `C2`: two optional performance controls, off by default and recommended for no scenario (§4.2, D4.44, D4.45). A limit on which vehicles get a body -- a circle, the cameras' footprints or a capacity, chosen by the run -- adds `in_limit` to the admission predicate, two eviction rows (E5, E6) under new numbers and the reason `outside_limit`; a vehicle outside it is simulated, has no body and no imagery-side truth, and is counted. A draw distance changes no admission: every vehicle keeps its body and its truth, and each camera's sidecar marks the vehicles beyond it, which are not observed by that camera (§4.5). The participant guarantee holds with no limit, the default (§4.4). E2, E4, V2.3 and V2.4 stay withdrawn |
| 20 | 2026-10-02. `C1`: the catalogue carries each body's width without its mirrors (`body_width_m`, §3.2b), measured from the mesh in the editor, beside the box's full extent; SUMO is given the body width (D4.3). `C3`: Bahonar is recompiled with 3 s lane changes and runs as before (§5.2a). `C7`: a body's heading is its own path's, its truth velocity the path's, and SUMO's angle is recorded beside them (§9.1, D4.13) |
| 19 | 2026-10-01. `C3`: a lane change takes 3 s for every vehicle — the compiler writes `lanechange.duration` 3 into every configuration and the lock records it (§5.2a, D4.42). Measured on the three shipped scenarios; Bahonar deadlocks behind a body wider than its lanes and is not recompiled with it |
| 18 | 2026-10-01. `C4`: every pooled body is spawned with `role_name` `sumo` (D4.9, as built). `capture:sumo_id` is not stamped, because a pooled body draws a succession of vehicles; the vehicle a body draws is named per frame instead, on the world-observer snapshot for every reader (§6.4; [`03`](03_CoSimulation_Runtime.md) D3.39) |
| 17 | 2026-10-01. `C3`: a scenario that declares lane closures carries a fifth file, the rerouter `.add.xml`, named by its configuration and digested in its lock |
| 16 | 2026-10-01. `C2`: a vehicle SUMO inserts is drawn from the frame SUMO first reports it in, at that position and moving, and never before SUMO inserted it, and its admission instant is that frame (§4.2, §4.3). A vehicle SUMO has when rendering begins is drawn on the first rendered frame |
| 15 | 2026-09-30. `C2` draws every vehicle SUMO has in a capture window. The render cap (128, hard 192) was never measured — M2 never ran — and the scenario is the arbiter of population, so a heavier scenario runs slower, never thinner. The input is `capture_windows[]` and `prewarm_s`; the region, priority and capacity gates, E2, E4, V2.3, V2.4, the reasons `outside_region` and `capped`, the rendered-fraction gate and open question 3 are withdrawn; `D4.6` holds by construction |
| 14 | `C1`: the registry carries corrected class metadata and the European HGV; the content's unregistered blueprints inventoried; the Fuso Rosa is a bus; the default class set stated |
| 13 | `C3` is the directory of loose files the scenario compiler writes, bound by its lock; the clipped OSM is not carried, and each validation rule states where it is enforced |
| 12 | `C9`'s package-build rules carried out by the scenario compiler with the session's own readers |
| 11 | `C9`: `set_solar_epoch` writes the declared offset as the sun's zone; no client-side conversion; one advance mechanism |
| 10 | `C5` built at world build: resolved table in the world package, SUMO lane positions and intervals, V5.12 compares two implementations; the engine RPC pair specified |
| 9 | `C6`: the session writes an advancing sun every tick, engine advance off; residual tolerances rate-independent |
| 8 | The OpenSCENARIO catalogue projection leaves this plan; `C1` has one serialisation |
| 7 | Two-wheelers are outside the vehicle mapping contract; `C1` refuses them rather than substituting |
| 6 | `C3` carries the annotation vocabulary and binds it by digest |
| 5 | `C10` records facts, not a verdict; per-artifact crash safety; the contract for observing a live run |
| 4 | `C8` gains a live delivery mode; `C10` added; the external side left unspecified on purpose |
| 3 | `C8` becomes a corpus handover; three artifact roots become two; nothing scores a model |
| 2 | `C9` added — the simulated-time epoch and illumination policy — and bound into `C3`, `C6`, `C7` |
| 1 | `C1`–`C8` |

### What this section does not cover

- **Sizing.** Every cap, radius, window, budget and tolerance below is named and typed; not one is given
  a value. Values belong to [`10_Scale_And_Performance.md`](10_Scale_And_Performance.md).
- **The annotation vocabulary.** `C4` carries `instance_id` and says how it joins; the term list, the
  `PatternInstance` payload and the migration from `.labels.json` are
  [`06_Truth_And_Annotation.md`](06_Truth_And_Annotation.md)'s.
- **How SUMO and CARLA are stepped.** `C6` states what must be true at a tick boundary and what every
  participant is forbidden to do. The loop, the subscription set and the failure recovery are
  [`03_CoSimulation_Runtime.md`](03_CoSimulation_Runtime.md)'s.
- **Whether the .NET client can do these things today.** That audit is
  [`05_CarlaNet_Capability_Audit.md`](05_CarlaNet_Capability_Audit.md)'s. Where a contract needs an
  RPC, a shim method or a C# type that this document could not find, it says so in place.
- **What a civil time means, and why one illumination policy is preferred over another.** `C9` fixes
  the field set, the arithmetic that has to be reproducible, the validation and the failure modes. The
  semantics — what "the site's civil time" is for a scenario, how a window is chosen against the sun,
  whether illumination is a corpus stratifier, and the rationale for a headlight threshold — are
  [`11_Time_And_Illumination.md`](11_Time_And_Illumination.md)'s.
- **The operator's surface over any of it.** Which flags exist, how a run selects or overrides a
  policy, and what an operator sees is
  [`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md)'s. `C9` says only what the
  resulting choice must look like on the wire and what the manifest must record about it.
- **Anything a model does with what we emit, and anything about the external chain itself.** Running a
  detector, a tracker or a model service, associating their output to truth, and any metric, harness or
  comparison over them are outside this effort entirely
  ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b). So — per §3c — are **a detector's input schema, a track
  format, a report schema, a fusion or normalisation stage, and any latency, retry or error semantics
  past our emission**. `C8` specifies what we emit, in what form, with what timing and identity
  guarantees, and how an arbitrary consumer attaches and detaches; it specifies nothing that consumes
  it, and `D4.34`'s substitution test is how a reader checks that claim (§10.11).
- **How a run is invoked, attended or otherwise.** The flags, the configuration layering and its
  resolution, the exit-status set, the live monitor and the closeout rendering are
  [`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md)'s. `C10` (§12) owns the **record**
  of what a run produced and what was checked, and §12.8 states what an external process can observe
  while a run is alive. **When a run starts and when it stops are the caller's, not ours**
  ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d): there is no scheduler, no cadence, no run-length policy, no
  training loop and no model lifecycle anywhere in this plan.
- **Anything OpenSCENARIO.** This system is SUMO-driven; the storyboard path is a separate effort on
  its own branch, and the executor it uses stays
  ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3 item 6). No contract here produces, consumes or validates an
  OpenSCENARIO document, and `C1` emits one serialisation of the vehicle catalogue rather than a
  projection of it. [`Findings/20`](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)
  §5.6's two requirements on a vehicle catalogue — generated from a running server rather than
  hand-maintained, versioned and shipped with the distribution — are both met by `C1`; its preference
  for the OpenSCENARIO serialisation belongs to whoever builds the storyboard path, and §3.2 records
  the one measurement that path would have to add, namely axle geometry the sweep has no source for.
- **Pedestrians**, out of scope by [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3.5.

---

## 1. How to read a contract

Each contract below has the same eight parts, so two people implementing the two sides can work from
the same headings.

| Part | What it fixes |
|---|---|
| **Artifact** | The file or message, its name, its location, and who writes it |
| **Format** | The encoding, and the exact grammar where the encoding does not fix it |
| **Fields** | Name, type, unit, required/optional, meaning — every field, no "and so on" |
| **Example** | A complete, valid instance, built from measured values wherever measured values exist |
| **Validation** | The rules a conforming artifact satisfies, each one checkable by a program |
| **Failure** | What the system does when a rule is violated — which side detects it, and when |
| **Versioning** | How the artifact changes over time and how a consumer refuses one it cannot read |
| **What breaks** | The concrete damage if the contract is ignored. This is why it is written down |

**Normative words.** *Must* is a rule a validator enforces and whose violation stops the run. *Should*
is a rule a validator warns on. *May* is permitted and unchecked.

**Units.** Every length is metres, every time is seconds, every angle is degrees. Simulated time is
distinguished from wall-clock time in every field name (`_s` is simulated seconds unless the name says
`wall`). Coordinates are named for their frame: `carla_x`/`carla_y`/`carla_z` (CARLA-local metres),
`sumo_x`/`sumo_y` (the SUMO network's projected metres), `latitude`/`longitude`/`hae_m` (WGS84
ellipsoidal, the project datum).

**Three clocks, three names, never interchangeable.** They are named here once and used consistently
below; `C9` exists because nothing in the tree distinguishes them.

| Clock | Field-name convention | Meaning |
|---|---|---|
| **Simulated** | `_s`, and bare `t` | Seconds since the simulation's own zero. SUMO's `<begin>`, every capture window, every interval. Carries no date and no zone |
| **Civil** | `_civil`, or a full ISO-8601 string with an explicit offset | Wall-clock time at the site, of the form `2026-03-21T23:00:00+03:30`. What a human means by "the night shift". **A civil time without an explicit offset is not a civil time** and is rejected (`C9` V9.2) |
| **Sun-clock** | `solar_time`, `_solar_hours` | The number `CesiumSunSky.SolarTime` holds: hours in the sun's *own* time zone, which is derived from the map longitude and is **not** the civil offset (`C9` §11.4). A conversion sits between civil and sun-clock and it is not the identity |

Wall-clock time appears in five places and nowhere else: `generated_at_utc` on build artifacts;
`sumo_step_timeout_wall_s`; the live delivery envelope's `emitted_wall_utc` and the stream header's two
stamps (§10.9.2); the transcript's `received_wall_utc` (§10.10); and the run record's
`started_wall_utc`, `stopped_wall_utc` and `ended_wall_utc` (§12.2, §12.3, §12.4). The last three are
**delivery and bookkeeping, never data** — nothing
rendered, recorded or joined may depend on any of them, and `C8`'s guarantee L3 says so explicitly for
the one case where somebody might be tempted, which is pairing frames by their arrival time.

**Digest.** Wherever this document says *digest*, it means the lowercase hex SHA-256 of the artifact's
bytes, or — for a JSON document that carries its own digest field — of the document serialised with
that field set to the empty string, with sorted keys, two-space indent, UTF-8, no BOM, `\n` line
endings. The existing world package already computes digests this way in spirit
(`CarlaNet.Transport/CarlaClient.cs:1252-1253`); this document makes the canonicalisation explicit
because two implementations that disagree about whitespace disagree about identity.

**A note on stale citations.** [`Findings/20`](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)
and [`Findings/23`](../../Findings/23_SUMO_Traffic_Integration.md) cite `CarlaNet/python/SCTMV.py:NNN`
in several places. That file no longer exists; the live path is
`carla/CarlaControl/scripts/run_SCTMV.py` over the `carlacontrol` package. Every such claim used below
was **re-resolved against `carla/CarlaControl/`** and is cited at its current location, with the
re-resolution noted.

---

## 2. The contract map

```mermaid
flowchart TB
  subgraph build["Content and world build"]
    CONTENT[("cooked content<br/>VehicleParameters.json")]
    SWEEP["vehicle-catalogue sweep<br/>(spawns one of each, measures)"]
    CAT[/"C1 vehicles.catalogue.json"/]
    WORLDGEN["world build<br/>run_SCTMV.py --build"]
    CWP[/"world package .cwp<br/>world.json · map.xodr · bareearth.bin"/]
    AOI[/"C5 &lt;extract&gt;.aoi.geojson"/]
  end

  subgraph author["Scenario authoring (no CARLA)"]
    BUILDER["SUMO scenario builder<br/>carlacontrol.SumoScenarioBuilder"]
    ANNOT["annotation compiler"]
    EPOCH[/"C9 epoch + illumination policy<br/>(carried in the lock)"/]
    CSP[/"C3 scenario package, loose files<br/>lock · network · routes · sumocfg<br/>· supervision plan and its vocabulary · resolution report"/]
  end

  subgraph run["Capture run"]
    DRIVER["co-simulation driver<br/>C6 owns the clock"]
    CLOCK["solar clock<br/>C9: civil time from the tick,<br/>audits the sun"]
    SUMO["sumo (TraCI over TCP)"]
    RSC["render-set controller<br/>C2"]
    SERVER["CARLA server<br/>C7 authority per actor"]
    SUN["CesiumSunSky<br/>sole sun authority"]
    REC["frame recorder"]
  end

  subgraph out["Capture output"]
    IMG[/"imagery + sidecars<br/>incl. &lt;_solar&gt; and carla:solar"/]
    MAN[/"run manifest<br/>C2 render states · C4 identity table<br/>C9 epoch · policy · solar residual"/]
  end

  subgraph handover["Handover — C8, one contract in two delivery modes"]
    OBSR[/"OBSERVATION records<br/>imagery · collection metadata<br/>· radiometry · solar · epoch"/]
    TRUR[/"TRUTH records<br/>CoT sidecars · labels · supervision<br/>· coverage · manifests"/]
    DEFD["deferred delivery<br/>files under the two roots"]
    LIVED["live delivery<br/>the same records, as produced"]
    TRX[/"transcript — opaque, verbatim<br/>neither root (C8 §10.10)"/]
  end
  RES[/"C10 run record<br/>appended throughout, gates as facts"/]
  EXT["an arbitrary external consumer<br/>API, formats and transport unknown here"]
  AUTO["an external process<br/>drives regeneration, and may<br/>kill any of this at any instant"]

  CONTENT --> SWEEP --> CAT
  WORLDGEN --> CWP
  CAT --> BUILDER
  CWP --> BUILDER
  AOI --> BUILDER
  BUILDER --> CSP
  ANNOT --> CSP
  EPOCH --> CSP
  CSP --> DRIVER
  CWP --> SERVER
  DRIVER <--> SUMO
  DRIVER --> RSC
  DRIVER --> CLOCK
  CLOCK -->|"set_solar_date · set_solar_time"| SUN
  SUN -->|"solar block on every<br/>world-observer snapshot"| CLOCK
  RSC --> SERVER
  SUN --> SERVER
  SERVER --> REC
  REC --> IMG
  DRIVER --> MAN
  RSC --> MAN
  CLOCK --> MAN
  IMG --> OBSR
  IMG --> TRUR
  MAN --> TRUR
  OBSR --> DEFD
  TRUR --> DEFD
  OBSR --> LIVED
  DEFD --> EXT
  LIVED --> EXT
  EXT -. "whatever it returns, if anything" .-> TRX
  MAN --> RES
  RES --> AUTO
  SERVER -. "queried live: rendered set, sim time,<br/>solar state (C10 §12.8)" .-> AUTO
  MAN -. "never enters the<br/>OBSERVATION root (C8 D4.20)" .-x OBSR
```

**The register.** Every artifact, who writes it, who reads it, and which contract governs it.

| Artifact | Written by | Read by | Contract |
|---|---|---|---|
| `vehicles.catalogue.json` | the catalogue sweep, against a running server | scenario builder, assistant author, human author, validator, **and the co-simulation bridge at runtime — the pose conversion needs the measured extent** (`C1` §3.2) | `C1` |
| `<name>.rou.xml` `vType` set | scenario builder, from the catalogue | SUMO, playback bridge | `C1` |
| `capture_windows[]` in the lock, and `prewarm_s` given at run start | scenario author, through the compiler; operator | render-set controller | `C2` |
| `render_states[]` in the run manifest | render-set controller | truth consumers, corpus auditor, corpus builder | `C2` |
| scenario package: `<scenario_id>.lock.json` and the files beside it | scenario compiler | co-simulation session, operator surface | `C3` |
| spawn attributes `capture:*` | playback bridge at spawn | truth producer, recorder log, replayer | `C4` |
| `<extract>.aoi.geojson` | the author, beside the OSM | world build, scenario builder, world actor | `C5` |
| `areas.resolved.json` in the world package | world build | scenario compiler, truth producer, and — for its **authored definitions only** — the observation writer (`C8` §10.4) | `C5` |
| clock parameters: the step in the scenario package, the rest given at run start | scenario compiler; operator | co-simulation driver | `C6` |
| per-actor authority | playback bridge at spawn | every subsystem that touches an actor | `C7` |
| per-actor vehicle light state | playback bridge, every tick | the rendered scene, and nothing else — it is not published as data | `C7` |
| `OBSERVATION` root — imagery plus collection metadata | frame recorder and the collection-metadata writer | an external consumer, at handover — deferred or live (`D4.29`) | `C8` |
| `TRUTH` root — sidecars, labels, supervision, coverage, manifests | truth producer and manifest writer | corpus auditor, corpus builder, and an external consumer at handover | `C8` |
| corpus manifest — contents, versions, declared omissions | corpus builder, at handover | an external consumer | `C8` |
| the live handover streams — the **same** `OBSERVATION` and `TRUTH` records, emitted as produced | the handover emitter | an arbitrary external consumer, about which this document assumes nothing | `C8` §10.9 |
| transcript index and blobs — bytes an external chain returned | the transcript recorder | **nothing in this system** (`D4.32`); a human, or an external team if the transcript is released | `C8` §10.10 |
| `truth/world_truth_track.csv` — every SUMO vehicle at every sampled frame inside the window, drawn or not | the capture session | corpus auditor, corpus builder: the base rate's denominator | `C10` §12.7 for how it is written; [`06`](06_Truth_And_Annotation.md) §8.3 owns the payload |
| `run_record.jsonl` — identity and bindings, the gate record, the stop, what was produced | the component that owns the run manifest, appended from before the first capture | an automated caller; an operator; a corpus builder | `C10` |
| `epoch` block in the scenario package's lock | scenario author, through the compiler | co-simulation driver, solar clock, truth producer, corpus auditor, **and the observation writer** (`C8` §10.4a) | `C9` |
| `illumination` block in the scenario package's lock, and the run override | scenario author, through the compiler; operator at run start | solar clock | `C9` |
| `<_solar>` sidecar element and the `carla:solar` PNG chunk, each with the capture's `illumination_band` | frame recorder — **already written today** (`CotWriter.cs:52-65`, `SolarMetadata.cs:19`), the band since 2026-10-02 (`C9` §11.8.3) | truth consumers, corpus auditor, observation writer | `C9` |
| `epoch`, `illumination_in_force`, `solar_achieved[]`, `solar_residual` in the run manifest | the solar clock, closed at run end | corpus auditor, corpus stratification | `C9` |

---

## 3. C1 — The vehicle catalogue

The headline contract. A user hands the assistant authoring a SUMO scenario a catalogue of vehicles to
choose from, with real dimensions and colour; the playback bridge maps what the scenario chose back
onto CARLA blueprints.

### 3.1 The defect this replaces, measured

`BlueprintChooser.Choose` takes the whole blueprint catalogue, filters to `vehicle.*`, and returns the
**first** entry whose identifier contains a preferred substring for the entity's category
(`CarlaNet.Scenario/BlueprintChooser.cs:43-63`). One category therefore resolves to exactly one
blueprint, which is why a five-car convoy is five identical cars. The preference table is
`BlueprintChooser.cs:16-25`.

These measurements make the defect sharper than doc 20 §5.6 records.

**Measurement 1 — the content build's vehicle registry is hand-edited, and was measured wrong.** The
vehicle blueprint set is loaded at runtime from a JSON file
(`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Actor/Factory/VehicleActorFactory.cpp:19-26`), read
here at `Unreal/CarlaUnreal/Content/Carla/Config/VehicleParameters.json`, plus an optional per-map
`Config/<map>/Vehicles.json` appended to it (`:22-24`). Parsed with `json` when the published
catalogue was swept:

| Property | Measured |
|---|---|
| Vehicle entries | **17** |
| `BaseType` values | `bus` ×7, `car` ×6, `truck` ×3, empty ×1 |
| `SpecialType` values | **empty on all 17** |
| `NumberOfWheels` | `4` ×16, `3` ×1 (`vehicle.Lincoln.Mkz`) |
| Entries with at least one `RecommendedColors` entry | **17 of 17** |
| Motorcycle or bicycle blueprints | **none** |

Six of the seven `bus` declarations were plainly cars — `ue4.ford.mustang`, `ue4.ford.crown`,
`ue4.bmw.grantourer`, `ue4.audi.tt`, `ue4.mercedes.ccc`, `ue4.chevrolet.impala`. The seventh,
`fuso.mitsubishi`, is the Mitsubishi Fuso Rosa light bus, and upstream CARLA publishes it as a bus.
`sprinter.mercedes` had no `BaseType` at all, so the truth producer's fallback reported it as `car`
(`CarlaNet.Recording/VehicleTelemetryService.cs:100-102`). Every `special_type` was empty, so
[doc 09 §4](../../Findings/09_Telemetry_CoT_Contract.md)'s `special_type=emergency` mapping had nothing
to fire on even for the ambulance, the police Charger and the fire truck. The Lincoln's blueprint wires
four wheels, not three.

The registry now declares the corrected values — `car` on the six saloons, `van` on the Sprinter and the
ambulance, `emergency` on the ambulance, the police Charger and the fire appliance, `taxi` on the taxi,
four wheels on the Lincoln — and two new entries, `vehicle.carlamotors.european_hgv` and
`vehicle.jeep.wrangler_rubicon`. The catalogue
still derives each blueprint's kind from its measurement (§3.2) rather than reading these, because the
file is hand-edited and nothing checks it against the bodies.

**Measurement 1a — the content holds more vehicle blueprints than it registers, and most of the rest
cannot spawn.** `Content/Carla/Blueprints/Vehicles/` holds thirty vehicle blueprint folders and a
`2Wheeled` folder, read offline from each blueprint's own name table and the packages it references:

| Blueprints | Registered | Why |
|---|---|---|
| The seventeen of the table above, and `EuropeanHGV` | globally, 18 entries | `EuropeanHGV` derives from `BaseVehiclePawnNW` and wires six wheel blueprints on three axles; one skeletal mesh, no trailer |
| `JeepWranglerRubicon` | globally, the nineteenth entry | its four wheel setups were wired in the editor (below); a generation-1 car with no door or lamp components |
| `MiningTruck` | only on `Mine_01`, as `vehicle.miningtruck.miningtruck` in `Config/Mine_01/Vehicles.json` | an off-highway haul truck the content scopes to its map; registering it globally would declare the id twice there |
| `2Wheeled/` — `CrossBike`, `Harley`, `KawasakiNinja`, `LeisureBike`, `RoadBike`, `Vespa`, `Yamaha` | no | two-wheelers, outside this contract (`D4.40`) |
| `AudiA2`, `AudiETron`, `BmwIsetta`, `CitroenC3`, `Cybertruck`, `NissanMicra`, `SeatLeon`, `Tesla`, `ToyotaPrius`, `VolkswagenT2` | no | no wheel is wired in |

The last row's folders each hold four wheel blueprints, but no vehicle blueprint references one: none
serialises `WheelSetups`, `BaseVehiclePawn` sets none, and `UChaosWheeledVehicleMovementComponent`
starts with none. `ACarlaWheeledVehicle::BeginPlay` passes the vehicle to
`FAckermannController::UpdateVehiclePhysics`, which calls `GetMaximumSteerAngle`, which asserts
`check(Wheels.Num() > 0)` (`Vehicle/CarlaWheeledVehicle.cpp:164`, `:285-289`;
`Vehicle/AckermannController.cpp:217-218`).
Spawning one would assert, so registering one would put a server-stopping body into every client's
random draw. The `Cybertruck` also references two glass meshes that are not in the content. Wiring the
wheel setups in the editor is what makes these registrable; until then an author asking for a
Volkswagen T2 is refused (§3.10), not given the nearest body.

The Jeep Wrangler was the first so wired. Its movement component now binds `Wheel_Front_Left`,
`Wheel_Front_Right`, `Wheel_Rear_Left` and `Wheel_Rear_Right` to its own four wheel blueprints; the
bone names are the Patrol's. Those wheel blueprints declared a 32 cm radius against a measured
41.6 cm tyre (wheel bone at z 40.9 cm, mesh floor at −0.7 cm), so each now declares 41 cm. Its other
physics are the Chaos defaults, left as they were: SUMO-driven vehicles are posed, not driven
through physics.

**Measurement 2 — half the preference table matches nothing.** Definition ids are lowercased
(`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Actor/ActorBlueprintFunctionLibrary.cpp:203-206`), so
`vehicle.UE4.audi.tt` becomes `vehicle.ue4.audi.tt`. Against the measured 17, the `motorbike` and
`bicycle` preference lists (`harley`, `yamaha`, `kawasaki`, `crossbike`, `omafiets`, `diamondback`)
match nothing, so those categories fall through to `vehicles[0]` — a car
(`BlueprintChooser.cs:62`). `mercedes` matches both `vehicle.sprinter.mercedes` and
`vehicle.ue4.mercedes.ccc`, resolved by enumeration order rather than by intent. Note also that doc 09
and doc 20 use `vehicle.audi.tt` and `vehicle.audi.a2` as examples; **neither id exists** in this
content build.

**Measurement 3 — the shipped SUMO type sets declare only vehicle kinds this content build holds.**
The two authored type sets are `AMBIENT_VEHICLE_TYPES`
(`CarlaControl/src/carlacontrol/SumoScenarioBuilder.py:402-424`, five types over `passenger`,
`delivery` and `truck`) and `VEHICLE_TYPES`
(`CarlaControl/scripts/make_arapahoe_scenario.py:100-138`, six over the same three classes). Every
type in both is a four-wheeled road vehicle, which is what Measurement 1 says the content build can
render. Nothing in either set reaches the chooser's unmatched fall-through of Measurement 2.

#### Scope boundary — two-wheelers are outside this contract

> **D4.40 — motorcycles, mopeds and bicycles are outside the vehicle mapping contract. No catalogue
> class names one, no `vType` declares one, and an author asking for one is refused rather than given
> something else.**

Two independent reasons, and either alone settles it:

- **A two-wheeler carries a rider, and riders are not rendered.** Pedestrians are out of scope
  ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3.5), so a motorcycle in this corpus would be a riderless body
  moving down a road at motorcycle speed — an object that exists nowhere outside the imagery, which is
  a worse thing to train a detector on than an absent vehicle class.
- **The content build registers none.** Measurement 1: `VehicleParameters.json` holds 18 vehicles and
  not one of them is a two-wheeler — the seven two-wheeled blueprints in the content are deliberately
  left unregistered (Measurement 1a) — and the two-wheeler identifiers the client carries elsewhere —
  `harley`, `kawasaki`, `yamaha`, `vespa`, `omafiets`, `crossbike`, `diamondback`, `gazelle`
  (`CarlaControl/src/carlacontrol/TrafficController.py:36-48`) — match nothing the server returns
  (Measurement 2). Nothing is registered, so the sweep has nothing to spawn and nothing to measure.

This is a boundary on what the catalogue covers, not a defect in it. The `cot_base_type` vocabulary of
§3.4.3 still lists `motorcycle` and `bicycle`, because that vocabulary is the CoT contract's and this
contract does not get to edit it; what this boundary fixes is that no class in a catalogue may take
those values, because no member blueprint could carry them. The consequence for an author is §3.10's
refusal row, and it is a refusal precisely so the substitution §3.12 describes cannot happen quietly.

#### Scope boundary — articulated vehicles are outside this contract

> **D4.41 — an articulated vehicle is outside the vehicle mapping contract until its trailer's pose
> can be produced. SUMO does not report one, and a rigid body of the full length is wrong in exactly
> the situation the corpus is made of.**

**SUMO reports one position, one angle and one declared length per vehicle, and nothing else.**
Measured: nothing in TraCI's vehicle surface or its constant table names a trailer, a hitch, a
kingpin or an articulation angle (`Build/sumo-install/tools/traci/_vehicle.py`,
`constants.py`). Where `sumo-gui` draws a semi bending through a turn, that is a drawing convention
applied to a shape name; the simulated body is rigid and the bend is not state anyone can read.

So a tractor and its trailer cannot be placed from what the bridge receives. The trailer does not
follow the cab's *current* heading — it follows where the cab has **been**, and its yaw lag through a
turn is set by the path and the wheelbase. Reconstructing it means carrying a history of the cab's
track and seating the trailer along it, which is a different mechanism from the pose conversion of
[`03`](03_CoSimulation_Runtime.md) §6 and would have to be built rather than configured.

**Placing one rigid body of the articulated length instead is wrong precisely where it matters.** On a
straight it is indistinguishable; through a turn it sweeps a path no articulated vehicle sweeps, and
turns are where behaviour is legible — a corpus is made of the manoeuvres, not the straights. A
plausible-looking lorry that corners impossibly is worse than an absent vehicle class, for the same
reason a riderless motorcycle is.

The content also registers none: the sweep found no articulated body among the 17, the largest bodies
it measured — the 8.00 m lorry and the 10.17 m bus — are rigid, and the European HGV the registry adds
is a rigid three-axle lorry with no trailer. So the boundary costs nothing today. It is recorded
because the first attempt to add a semi will otherwise reach for the nearest long body and discover
the problem in the imagery rather than in the contract.

### 3.2 Where a blueprint's dimensions come from — measured, not assumed

The brief asks whether dimensions are available *before* spawning. They are not. Four readings, in the
order the data would have to travel:

1. **The parameter struct carries no dimension.** `FVehicleParameters`
   (`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Actor/VehicleParameters.h`) has `Make`, `Model`,
   `Class`, `NumberOfWheels`, `Generation`, `ObjectType`, `BaseType`, `SpecialType`,
   `HasDynamicDoors`, `HasLights`, `RecommendedColors`, `SupportedDrivers` — and nothing else.
2. **The definition builder emits no dimension.** `MakeVehicleDefinition`
   (`ActorBlueprintFunctionLibrary.cpp:839-930`) emits the variations `role_name`, `ros_name`, `color`
   (only when `RecommendedColors` is non-empty, `:850-861`), `driver_id`, `sticky_control`,
   `terramechanics`, and the attributes `object_type`, `base_type`, `special_type`,
   `number_of_wheels`, `generation`, `has_dynamic_doors`, `has_lights`. No length, no width, no height,
   no bounding box.
3. **The RPC type has no slot for one.** `carla::rpc::ActorDefinition` is
   `MSGPACK_DEFINE_ARRAY(uid, id, tags, attributes)`
   (`LibCarla/source/carla/rpc/ActorDefinition.h:49`), mirrored in C# as
   `ActorDefinition(Uid, Id, Tags, Attributes)`
   (`CarlaNet/src/CarlaNet.Types/Rpc/Actors/ActorDefinition.cs`). `GetActorDefinitionsAsync`
   (`CarlaNet.Transport/CarlaClient.cs:467`) calls `get_actor_definitions`
   (`Unreal/.../Carla/Server/CarlaServer.cpp:1177`) and returns exactly that.
4. **The bounding box first exists on a spawned actor.** `carla::rpc::Actor` carries
   `bounding_box` as key 3 (`CarlaNet.Types/Rpc/Actors/Actor.cs`), filled from `FActorInfo::BoundingBox`
   (`Unreal/.../Carla/Actor/ActorInfo.h:25`), which is computed at registration by
   `UBoundingBoxCalculator::GetActorBoundingBox(&Actor)` over the **already-spawned `AActor`**
   (`Unreal/.../Carla/Actor/ActorRegistry.cpp:163,173`). The only bounding-box RPCs on the server are
   `get_all_level_BBs` (`CarlaServer.cpp:1194`), which returns static level geometry by semantic tag,
   and `get_light_boxes`. There is no RPC keyed by blueprint id.

The truth record's `length_m`/`width_m`/`height_m` are consequently `2 × bounding_box.extent` of the
spawned actor (`CarlaNet.Recording/VehicleTelemetryService.cs:103,110`), which is why the storyboards
in `carla/Import/` — all three of which declare `<Dimensions width="1.9" length="4.6" height="1.45"/>`
for every car (`Import/Gardnerville_CentervilleEast.xosc:18`) — disagree with truth and nothing
notices.

> **D4.1 — the catalogue is generated by a build-time spawn-and-measure sweep against a running
> server. It is not, and cannot be, a projection of the blueprint library.** There is no cheaper source
> of dimensions, because the server does not hold one.

This is also why upstream ships a static `vtypes.json` rather than deriving one
([doc 23 §6.8](../../Findings/23_SUMO_Traffic_Integration.md)): upstream measured its blueprint set once
and froze the result. Our blueprint set differs — 18 entries, none of them upstream's default Audi set
— so ours is new work rather than a file to copy, and freezing it is exactly what §3.11's digests exist
to make safe.

**Measurement 4 — the sweep's output already exists in this tree, incidentally.** Fifty-four recorded
truth sidecars in `carla/Build/SCTMV_recordings/*.xml` were parsed for distinct
`<_carla type_id="vehicle.*">` elements. They cover **all 17** blueprints the registry held when they
were recorded, and carry exactly the measurement a sweep would produce:

| blueprint | length_m | width_m | height_m | `base_type` reported | recommended colours |
|---|---|---|---|---|---|
| `vehicle.ambulance.ford` | 6.36 | 2.35 | 2.43 | truck | 1 |
| `vehicle.carlacola.actors` | 8.00 | 2.91 | 4.05 | truck | 1 |
| `vehicle.dodge.charger` | 5.01 | 1.88 | 1.54 | car | 7 |
| `vehicle.dodgecop.charger` | 5.24 | 1.92 | 1.64 | car | 1 |
| `vehicle.firetruck.actors` | 8.58 | 2.90 | 3.83 | truck | 1 |
| `vehicle.fuso.mitsubishi` | 10.17 | 3.93 | 4.24 | bus (a light bus) | 6 |
| `vehicle.lincoln.mkz` | 4.89 | 1.84 | 1.52 | car | 1 |
| `vehicle.mini.cooper` | 4.55 | 2.10 | 1.77 | car | 4 |
| `vehicle.nissan.patrol` | 5.59 | 2.15 | 2.06 | car | 6 |
| `vehicle.sprinter.mercedes` | 5.92 | 1.99 | 2.73 | car (by fallback) | 5 |
| `vehicle.taxi.ford` | 5.35 | 1.79 | 1.58 | car | 1 |
| `vehicle.ue4.audi.tt` | 4.18 | 1.99 | 1.39 | **bus** (wrong) | 5 |
| `vehicle.ue4.bmw.grantourer` | 4.61 | 2.24 | 1.67 | **bus** (wrong) | 5 |
| `vehicle.ue4.chevrolet.impala` | 5.36 | 2.03 | 1.41 | **bus** (wrong) | 5 |
| `vehicle.ue4.ford.crown` | 5.37 | 1.80 | 1.57 | **bus** (wrong) | 5 |
| `vehicle.ue4.ford.mustang` | 4.72 | 1.89 | 1.30 | **bus** (wrong) | 5 |
| `vehicle.ue4.mercedes.ccc` | 4.67 | 1.81 | 1.44 | **bus** (wrong) | 5 |

Every number in the worked examples below is one of these, so the example is a real catalogue entry
rather than an invented one. Two consequences for the sweep's specification fall straight out: it must
**record the measured dimensions and disregard `base_type`**, because `base_type` is hand-edited and
was measured wrong on seven of this content build's entries; and it must carry a **curated** class
assignment in the catalogue, so a wrong declared value never reaches SUMO's `vClass`.

#### The sweep tool, specified

- **Name and location:** `carla/CarlaControl/scripts/make_vehicle_catalogue.py`, a thin CLI over
  `carla/CarlaControl/src/carlacontrol/VehicleCatalogueBuilder.py` (repo convention: one public class
  per file, file named for the class — `carla/AGENTS.md`). Windows/Linux parity applies to any
  `Scripts/*` wrapper, not to this CLI, which is platform-neutral Python.
- **When it runs:** once per content build, as a build step, against a headless server with any map
  loaded. It needs no world package, no Cesium imagery and no OSM — only a server.
- **What it spawns:** every definition returned by `get_actor_definitions`
  (`CarlaNet.Transport/CarlaClient.cs:467`) whose `id` starts with `vehicle.`, **one at a time**, never
  concurrently. One at a time matters: concurrent spawns can collide and return
  `EActorSpawnResultStatus::Collision` (`Unreal/.../Carla/Actor/Factory/VehicleActorFactory.cpp:43-47`),
  which the sweep would then have to distinguish from a genuinely unspawnable blueprint.
- **Where it spawns:** at a single fixed transform well clear of geometry and of any other actor — by
  default `z = 300 m` above the map's first recommended spawn point, with gravity and physics
  irrelevant because the actor is destroyed before it can fall anywhere. The bounding box is
  **actor-local** (`FActorInfo::BoundingBox`, `Unreal/.../Carla/Actor/ActorInfo.h:25`, computed by
  `UBoundingBoxCalculator::GetActorBoundingBox` at `ActorRegistry.cpp:163`), so the pose does not
  affect the measurement; height is chosen only to guarantee a clear spawn.
- **How it measures:** read the `rpc::Actor` returned by the spawn — it carries `bounding_box` as key 3
  (`CarlaNet.Types/Rpc/Actors/Actor.cs`) — and record `2 × extent.x`, `2 × extent.y`, `2 × extent.z` as
  `length_m`, `width_m`, `height_m`, and `bounding_box.location` as `bbox_centre_m`. This is exactly
  what the truth producer does (`CarlaNet.Recording/VehicleTelemetryService.cs:103,110`), so the
  catalogue and the truth record agree by construction rather than by coincidence. It also records
  every declared variation with its type, `restrict_to_recommended` flag and recommended values, from
  the definition rather than from the actor.
- **How it tears down:** destroy the actor, and **confirm the destroy**, before the next spawn. A
  sweep that leaks actors turns its own later spawns into collisions and its measurements into
  failures. At the end it asserts the world's vehicle actor count is what it was at the start, and
  refuses to emit a catalogue if it is not — a leaked actor means some measurement may have been taken
  against a colliding spawn.
- **Colour applicability:** the sweep spawns each blueprint a second time with a distinctive `color`
  and scans the server log for the known `MaterialNotFound` warning attributable to that spawn. That
  warning is `BaseVehiclePawn.ApplyColor` failing to find the `Bodywork_Mat` slot; it is cosmetic and
  benign in itself, but it is exactly the signal that a requested colour did **not** reach the body.
  The sweep records `colour_applied` as `true`, `false`, or `unknown` when the log was unavailable.
  It must never record `true` by assumption.
- **Lamp applicability:** a third pass, specified in §3.2a, which measures *optically* which lamps a
  blueprint actually lights. It is a separate pass because it is the only measurement in the sweep that
  needs a camera and a night sun, and because a sweep that cannot get one must still emit a catalogue
  (with every lamp recorded `unknown`) rather than emit a guess.
- **How far its geometry reaches: the bounding box, and no further.** The only geometry a spawned
  actor hands back is `bounding_box` (`CarlaNet.Types/Rpc/Actors/Actor.cs`, key 3), so the sweep
  measures no wheel diameter, no track width and no axle position — it has no source for any of them.
  Anything that needs axle geometry, such as a conformant OpenSCENARIO `<Vehicle>` with its required
  `<Axles>` element, cannot be produced from this measurement, and deriving plausible axle numbers
  from the box would put fabricated values into a file a foreign reader takes as measured. Closing
  that gap means reading wheel geometry off the actor, which is an engine change nothing here needs.
- **What it emits:** `vehicles.catalogue.json` (§3.3), plus a one-page
  human-readable report listing every blueprint, its measured dimensions, and every discrepancy
  between the blueprint's own declared `base_type` and `special_type` and the catalogue's derived and
  curated ones — which, on the registry the published catalogue measured, was six wrong `base_type`
  values, one empty one and seventeen empty `special_type` values. It says nothing about wheel counts:
  the sweep has no source for one. It also writes `VehicleParameters.corrected.json`, the registry with
  those two fields set from the catalogue, which is what makes Measurement 1's defect fixable by the
  person who owns the content.
- **What it must not do:** infer a dimension, fall back to a default, or skip a blueprint that failed
  to spawn. A blueprint that will not spawn is written with `"measurement": "failed"` and a reason, and
  the catalogue is still emitted — a partial catalogue that says which entries are missing is more
  useful than no catalogue, and the scenario compiler refuses a scenario that references a failed
  entry ([`07`](07_Scenario_Authoring.md) check 14).
- **Dependency on [`05_CarlaNet_Capability_Audit.md`](05_CarlaNet_Capability_Audit.md):** the sweep
  needs the Python shim to expose a spawned actor's bounding box. That the RPC carries it is measured
  above; that the shim surfaces it is not verified here. If it does not, the sweep is a C# tool over
  `CarlaClient` instead, which changes its language and nothing else in this contract.

### 3.2a Lamp capability — measured, because `has_lights` says nothing

A night capture that assumes every blueprint has the same lamps will be
wrong, and — as with colour — it will be wrong *invisibly*, because the client is told the command
succeeded.

**Measurement 5 — `HasLights` is `true` on all 17 blueprints.** Parsed from
`Unreal/CarlaUnreal/Content/Carla/Config/VehicleParameters.json` with `json`, the same file Measurement 1
used: `HasLights` is `true` for every one of the seventeen entries, with no per-lamp breakdown anywhere
in the file. It is emitted as the `has_lights` attribute by `MakeVehicleDefinition`
(`ActorBlueprintFunctionLibrary.cpp:839-930`), so it reaches a client with no spawn — and it carries
exactly as much information as `SpecialType`, which Measurement 1 found empty on all 17. A blanket
value is not a measurement.

**Three readings that show why a set-and-read-back probe cannot substitute for an optical one.**

1. **The read-back returns the command, not the vehicle.** `ACarlaWheeledVehicle::GetVehicleLightState`
   is `return InputControl.LightState;`
   (`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Vehicle/CarlaWheeledVehicle.cpp:486-489`), and
   `SetVehicleLightState` stores the incoming bitmask into that same field whenever any bit differs
   (`:684-701`). So `get_light_state` after `set_light_state` returns what was asked for, whatever the
   vehicle did with it.
2. **Whether a lamp illuminates is implemented per blueprint, in Blueprint.** The only thing the C++
   does with the stored value is raise `RefreshLightState`, which is a
   `UFUNCTION(BlueprintImplementableEvent)`
   (`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Vehicle/CarlaWheeledVehicle.h:310-311`, raised at
   `CarlaWheeledVehicle.cpp:699`). A blueprint whose graph handles four of the eleven bits is
   indistinguishable, over the RPC, from one that handles all eleven.
3. **This is the same shape as the measured colour trap.** §3.2's `colour_applied` exists because
   `ApplyColor` can fail to find the `Bodywork_Mat` slot and report nothing to the caller. Lamps are
   that failure with no log line at all.

> **D4.23 — lamp capability is measured optically, per blueprint, per lamp, and carried in the
> catalogue. `has_lights` is recorded verbatim and never used to decide anything** — it is `true` on
> all 17 and therefore discriminates nothing.

**The lamp pass, specified.** Run as part of the catalogue sweep, after the dimension pass:

- Set the sun to a night instant and disable advancement, so the only light in frame is the vehicle's:
  `set_solar_date` then `set_solar_time` at a declared sun-clock hour, and `set_time_advance(false)`
  (`carlanet/__init__.py:1506`, `:1500`, `:1535`). The hour and date used are recorded in the catalogue
  header as `lamp_probe.solar_time_hours` / `lamp_probe.solar_date`, together with the sun elevation
  the world reported back, because a measurement whose lighting is not recorded is not repeatable. The
  pass refuses to report at all if that elevation is above the horizon, since it would then be
  measuring a lamp against daylight. This is the one place `C1` depends on `C9`'s mechanism, and it is
  a build-time dependency only.
- Spawn one blueprint at the sweep's fixed transform and place a camera at each of **two** declared
  relative poses, ahead of the body looking back along it and behind it looking forward. Two, because
  front and rear lamps are different meshes and one camera cannot see both; a pixel count is taken from
  whichever camera sees the larger change, so the camera that cannot see a lamp does not dilute the one
  that can.
- Capture with `VehicleLightState.NONE`, then one frame per lamp bit with exactly that bit set,
  restoring `NONE` between bits — and **capture the restored `NONE` state as well**. Each lamp is
  compared against the off state before it and the off state after it, and the difference between those
  two off states is the run's own noise floor. A single early reference is not enough: **measured on
  this content build**, a pass that compared every bit against one reference taken 40 steps after the
  spawn reported all eleven lamps lit on `vehicle.ambulance.ford` with 3,281 pixels of gain, against
  2,464 pixels between two captures of the *same* state — the vehicle's arrival moves the scene's
  exposure, and exposure drift produces the same positive difference a lamp does. Comparing each bit
  against the off state beside it reduced the same blueprint's readings to 0–1 pixels.
- A lamp is `lit` when it gains more pixels than both the measured floor by a declared margin and a
  declared minimum, above a declared luminance threshold in a declared region; `unlit` when it does
  not; `unknown` when the pass could not run — no camera, no sun in the world, or a capture that
  failed. The threshold, the region, the margin, the minimum and the camera poses are catalogue-header
  fields, not constants in code, for the same reason the solar instant is.
- **A positive control runs once per pass**: the sun is moved to a declared daylight instant and back,
  and the same metric must respond. A metric that cannot see a vehicle go from night to day could not
  have seen a lamp either, so if it does not respond the pass records `ran: false` and every verdict
  `unknown`, rather than publishing seventeen `unlit` verdicts it was never in a position to make.
- The pass must never write `lit` by assumption, and must never infer one lamp from another. Front and
  rear are separate bits and separate meshes.
- **The camera takes the clock.** Stepped synchronously, a camera delivers exactly one frame per step,
  each rendered after the lamp state the pass wrote; free-running, it delivers whatever frame the
  world happened to render. So the pass switches the world to synchronous stepping for its duration
  and restores the caller's settings afterwards.

**What is *not* measured, and must not be.** Whether a lamp is bright enough to be *detectable* at a
given range by a given sensor is a collection question, not a content property; it belongs to
[`08_Collection_And_EPoL.md`](08_Collection_And_EPoL.md) and to whatever occlusion and detectability
work follows [doc 17](../../Findings/17_Photoreal_Occlusion_Metric.md). `C1` records only that the lamp
changes the rendered image at all.

**The confounder this opens, and the rule that closes it.** If the blueprints that light up are not
distributed alike across marked and unmarked vehicles, lamp capability becomes a night-time appearance
separator in exactly the way `vType@color` was a daytime one (§3.7.1). The distribution is a property
of the class members, so the check is the same shape as V1.11 and is written as V1.19.

#### The catalogue is a runtime dependency of the bridge, not only an authoring aid

This is the consequence that makes `C1` load-bearing at playback and not merely at build.

SUMO's reference point is the **front bumper centre**; CARLA's actor transform is the body's origin,
and the body's geometric centre is offset from that origin by the bounding box's own centre. So the
pose conversion of [doc 23 §6.7](../../Findings/23_SUMO_Traffic_Integration.md) — the third of the
three conventions, and the one upstream's `BridgeHelper` needs the vehicle extent for — **cannot be
computed without the catalogue.** SUMO knows a declared `length`; only the catalogue knows the rendered
body's extent and where its centre sits relative to the actor origin.

Written out, so two implementations agree. Given SUMO position `(sx, sy)` (front bumper centre, in the
network's projected metres), SUMO angle `a` (degrees clockwise from north), catalogue `length_m = L`
and `bbox_centre_m = (cx, cy, cz)`:

```
ψ   = a − 90                                  # CARLA yaw, degrees
ux  =  cos ψ ,  uy = sin ψ                    # heading unit vector, CARLA frame
bx  =  sx − (L / 2) · ux                      # body centre, CARLA frame
by  = −sy − (L / 2) · uy                      # note the Y negation: carla_y = −sumo_y
                                              # actor origin = body centre minus the local
                                              # bbox centre rotated into the world frame
ax  = bx − (cx · cos ψ − cy · sin ψ)
ay  = by − (cx · sin ψ + cy · cos ψ)
az  = drape_ground_z(ax, ay) + seat_offset    # C7 — never SUMO's z, which is zero everywhere
```

For every blueprint measured here `cy` is expected to be ~0, so the shift collapses to
`−(L / 2 + cx) · (ux, uy)`; the general form is written down because a blueprint whose mesh is not
centred on its origin would otherwise be silently mis-seated laterally.

> **D4.17 — the co-simulation bridge loads the catalogue at run start and uses it for the pose
> conversion, not only for blueprint selection. A vehicle whose extent is unknown is not rendered.**

**What the bridge does when an extent is unknown.** Three cases, one response:

| Case | Response |
|---|---|
| The realised vType carries no `carla:blueprint` param | Not rendered. `render_state = simulated_only`, `reason = "no_blueprint"` |
| It names a catalogue entry whose `measurement` is `failed` | Not rendered. `reason = "unknown_extent"` |
| It names an entry that is not in the catalogue at all | Not rendered. `reason = "unknown_extent"` |

In every case the bridge emits **one** warning per distinct vType, never per vehicle, and the run
continues with the vehicle present in behavioural truth and absent from the imagery — which is exactly
what `C2` §4.5's accounting exists to record.

**It must not fall back to SUMO's declared `length`.** That is the substitution this whole contract
exists to prevent: it would place a body of unknown size at a pose computed from a length nobody
measured, and the error is invisible in every artifact, because both sides remain internally
consistent. All three cases are unreachable in a package that passed `C1` V1.5, V1.8 and V1.9 at build
time; the runtime behaviour exists so that a validator bug degrades into a recorded absence rather than
into a silently wrong corpus.

### 3.2b The body's width without its mirrors — measured, because SUMO's width is the body's

The box the sweep reads is the spawned actor's bounding box, which spans the whole mesh, wing mirrors
included. That is the right box for the truth record and for seating -- it is what the imagery shows --
and the wrong width for SUMO, whose width is the body's: it is the room the vehicle takes in its lane
and, under a lane change spread over time, decides which neighbouring lanes it overlaps. Measured with
its mirrors, `vehicle.fuso.mitsubishi` is **3.93 m** wide, wider than every 3.35 m lane on the shipped
networks; under three-second lane changes the Bahonar pattern of life deadlocked behind it at the first
junction it had to change lanes for (§5.2a). Its body is **3.23 m**.

A server cannot see vertices, so the body width is measured from the mesh in the editor, and the
catalogue carries it as a measured input. **Method** (2026-10-02, UE 5.7.4 editor, all nineteen
blueprints): each blueprint's own skeletal mesh is exported as ASCII FBX at LOD0; its vertices are
binned along the vehicle's length in 5 cm bins, each side's widest vertex from the centre line kept per
bin; a side's body half-width is the largest half-width held over at least 0.6 m of length -- a
morphological opening that removes mirrors and any other protrusion shorter than that -- and the body
width is the two sides' sum (`CarlaControl/scripts/measure_vehicle_body_widths.py`, the export step
documented there). The plain vertex extent across equals the sweep's `width_m` to 0.1 mm on every
blueprint, which is how the table is held to the catalogue's meshes: a table whose full width disagrees
is refused as another mesh's. The table, `CarlaControl/catalogue/vehicle_body_widths.json`, is merged
by the sweep, or into the catalogue in the tree by `apply_vehicle_body_widths.py` with no server.

| Blueprint | Box width (`width_m`) | Body width (`body_width_m`) |
|---|---|---|
| `vehicle.fuso.mitsubishi` | 3.928 | 3.233 |
| `vehicle.ue4.chevrolet.impala` | 2.033 | 1.779 |
| `vehicle.carlacola.actors` | 2.912 | 2.787 |
| `vehicle.ue4.bmw.grantourer` | 2.242 | 2.144 |
| `vehicle.carlamotors.european_hgv` | 2.865 | 2.787 |
| `vehicle.dodgecop.charger` | 1.924 | 1.854 |
| `vehicle.ambulance.ford` | 2.351 | 2.286 |
| the other twelve | within 0.06 m of their boxes | `vehicle.nissan.patrol` unchanged |

> **D4.43 — SUMO is given each body's width without its mirrors; the truth box and the seating keep
> the full extent.** The body width is a measurement with its method and date in the catalogue's
> header (`body_width`), never a guess or a scale factor applied to the box: no model is rescaled. A
> catalogue with a measured blueprint that has no body width is not written, and a scenario class that
> draws a body the catalogue has none for is refused under check 14.

### 3.3 Artifact and format

> **D4.2 — the catalogue has one serialisation, `vehicles.catalogue.json`, and it is authoritative.
> The sweep emits no second projection of it.**

The catalogue's readers are the SUMO scenario builder, the playback bridge, the validator and an
assistant author, and every one of them reads JSON at lower cost than any alternative. The payload is
also mostly outside what any vehicle-description standard carries natively: `vClass`, `guiShape`,
`sigma`, `speedDev`, class membership, the colour palette, `lamp_capability` and the `lamp_probe`
conditions have no home except vendor extensions, so a second serialisation would be a vendor
document wearing a standard's file extension, plus a second thing to keep in step.

**Location.** The catalogue is a property of a content build, so it ships with the distribution:

```
<distribution root>/catalogue/vehicles.catalogue.json
```

and every scenario package binds it by `catalogue_digest` and `blueprint_set_digest` in its lock, and by
`carla:catalogue_digest` on every vehicle type it emits (`C3` §5.3), so a scenario always names the
catalogue it was compiled against.

**Encoding.** UTF-8, no BOM, `\n` line endings, two-space indent, object keys sorted, for the digest
rule of §1 to be well defined.

### 3.4 Fields

#### 3.4.1 Document header

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `catalogue_version` | integer | — | yes | Schema shape of this document. `1` for the shape below |
| `catalogue_id` | string | — | yes | Human-readable name, e.g. `carla-0.10.0-win64-development` |
| `catalogue_digest` | string | — | yes | Digest of this document per §1. Empty while computing |
| `content_build_id` | string | — | yes | Identifies the cooked content measured. The distribution's own version string |
| `blueprint_set_digest` | string | — | yes | Digest over the sorted list of `"<id>\|<attr_id>=<attr_value>"` for every blueprint and every declared attribute returned by `get_actor_definitions`. The **only** part of the catalogue a running server can independently reproduce |
| `generated_at_utc` | string | — | yes | ISO-8601 UTC, millisecond precision |
| `generator` | string | — | yes | `carlacontrol.VehicleCatalogueBuilder` and its version |
| `server_version` | string | — | yes | The server the sweep measured against |
| `body_width` | object | — | yes | How the body widths were measured: `{ method, measured, source }` -- the editor-side method, its date and the table merged (§3.2b) |
| `lamp_probe` | object | — | yes | The lamp pass's own conditions, so the measurement is repeatable: `{ ran, solar_date, solar_time_hours, sun_elevation_deg, camera_poses, image_size, luminance_threshold, region, minimum_lit_pixels, drift_margin, average_frames, positive_control_pixels }`. `camera_poses` is a list because front and rear lamps need two of them (§3.2a). `ran: false` with a reason when the pass could not run, in which case every `lamp_capability` value is `unknown` |
| `vehicles` | array | — | yes | §3.4.2 |
| `classes` | array | — | yes | §3.4.3 |

#### 3.4.2 `vehicles[]` — one entry per blueprint, all measured

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `blueprint_id` | string | — | yes | The definition id exactly as the server returns it, lowercase (`ActorBlueprintFunctionLibrary.cpp:205`) |
| `uid` | integer | — | yes | The definition's `uid`, carried so a spawn description can be built without a second lookup |
| `tags` | string | — | yes | The definition's `tags`, verbatim |
| `measurement` | string | — | yes | `measured` or `failed` |
| `measurement_note` | string | — | no | Present only when `measurement == "failed"` |
| `length_m` | number | m | yes if measured | `2 × bounding_box.extent.x` |
| `width_m` | number | m | yes if measured | `2 × bounding_box.extent.y`: the whole mesh, wing mirrors included; the truth box's width |
| `body_width_m` | number | m | yes if measured | The body's width without its mirrors, measured from the mesh in the editor (§3.2b); SUMO's `width` |
| `height_m` | number | m | yes if measured | `2 × bounding_box.extent.z` |
| `bbox_centre_m` | `[x,y,z]` | m | yes if measured | `bounding_box.location`, actor-local. Needed for the bumper-shift of `C7` and for any projected box |
| `declared_base_type` | string | — | yes | The blueprint's own `base_type` attribute, **verbatim and untrusted** — a hand-edited value, measured wrong or absent for 7 of 17 when first swept (§3.1) |
| `declared_special_type` | string | — | yes | The blueprint's own `special_type` attribute, verbatim. Measured empty for all 17 when first swept. Carried as data: truth gives a blueprint a class draws its class's `cot_special_type` instead (§3.4.3) |
| `number_of_wheels` | integer | — | yes | Verbatim. `3` for `vehicle.lincoln.mkz` when first swept, which was wrong; `6` for `vehicle.carlamotors.european_hgv`. Carried as data, never used to classify |
| `generation` | integer | — | yes | Verbatim |
| `settable_attributes` | array of object | — | yes | `{ id, type, restrict_to_recommended, recommended_values[] }` for every *variation* the definition declares |
| `colour_settable` | boolean | — | yes | True when a `color` variation is declared. Measured true for all 17 |
| `colour_palette` | array of string | — | yes | The `color` variation's recommended values, each `"R,G,B"` with integer components 0–255 |
| `colour_applied` | string | — | yes | `true` \| `false` \| `unknown` — whether a set colour reaches the body (§3.2) |
| `declared_has_lights` | boolean | — | yes | The blueprint's own `has_lights` attribute, **verbatim and untrusted** — measured `true` on all 17 (§3.2a Measurement 5). Carried as data, never used to decide |
| `lamp_capability` | object | — | yes | One entry per `VehicleLightState` bit, each `lit` \| `unlit` \| `unknown`. The eleven keys are the bit names in snake case — `position`, `low_beam`, `high_beam`, `brake`, `right_blinker`, `left_blinker`, `reverse`, `fog`, `interior`, `special_1`, `special_2`, mapping one-to-one onto `Position`…`Special2` (`carlanet/__init__.py:2666-2676`; `NONE` and `All` are not bits). No key may be absent; `unknown` is the value for "not measured" |

#### 3.4.3 `classes[]` — the authoring unit

A class is what a scenario author asks for. It becomes a SUMO `<vTypeDistribution>`; its members
become SUMO `<vType>`s.

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `class_id` | string | — | yes | The name an author writes. Matches `[a-z][a-z0-9_]{0,31}`. Becomes the `vTypeDistribution` id |
| `description` | string | — | yes | One human sentence. This is what an assistant author reads to choose |
| `sumo_vclass` | string | — | yes | A SUMO vehicle class, e.g. `passenger`, `truck`, `bus`, `delivery`, `taxi`, `authority`, `army`. **Curated, never taken from `declared_base_type`** |
| `cot_base_type` | string | — | yes | The truth record's `base_type` for members of this class — `car`, `truck`, `van`, `bus`, `motorcycle`, `bicycle`. Curated, and it **overrides** the blueprint's declared `base_type` in truth |
| `cot_special_type` | string | — | no | The truth record's `special_type` for members of this class, e.g. `emergency`, `taxi`; absent is the empty kind. Curated, because the content build declared none when first swept, and **truth takes it from here** by the owner's ruling ([`06`](06_Truth_And_Annotation.md) D6.18): a vehicle of a member blueprint carries this kind, an empty one included, whatever the blueprint declares, in the capture sidecar and the live pull of the process running the drive (`VehicleTelemetryService.cs:159-161`) and in the standalone producer's XML and CSV (`SumoCotBridge.py:469-489`) |
| `members` | array of object | — | yes | `{ blueprint_id, weight }`; `weight` is a positive number, normalised across the class |
| `max_speed_mps` | number | m/s | yes | SUMO `maxSpeed` |
| `accel_mps2` | number | m/s² | yes | SUMO `accel`. Explicit, never defaulted (§3.6) |
| `decel_mps2` | number | m/s² | yes | SUMO `decel`. Explicit |
| `sigma` | number | — | yes | SUMO driver imperfection, 0–1. Explicit |
| `speed_factor_mean` | number | — | yes | SUMO `speedFactor` mean |
| `speed_factor_dev` | number | — | yes | SUMO `speedDev`. **Explicit and mandatory** (§3.6) |
| `min_gap_m` | number | m | yes | SUMO `minGap`. Explicit |
| `gui_shape` | string | — | yes | SUMO `guiShape`, for `sumo-gui` only |
| `gui_colour` | string | — | yes | `#RRGGBB`. **For `sumo-gui` only. Never rendered** (§3.7) |
| `render_colour_policy` | string | — | yes | `palette` (draw from the member blueprint's `colour_palette`) or `fixed` (§3.7) |
| `render_colour` | string | — | only if `fixed` | `"R,G,B"` integers 0–255 |
| `lamps_expected` | array of string | — | no | Lamp names this class is expected to show **in addition to** the ones `C7` §9.4 commands for every vehicle — a beacon on an emergency class, for example. Checked against `lamp_capability` by V1.18. Absent means "the common set only" |

#### 3.4.4 The default classes

The sweep's own curation (`VehicleClassAssignment.DEFAULT_CLASSES`) files every registered blueprint in
exactly one class, and refuses to emit a catalogue in which a measured blueprint reaches none or a class
names a blueprint the sweep did not measure:

| `class_id` | `sumo_vclass` | `cot_base_type` / `cot_special_type` | Members |
|---|---|---|---|
| `civ_car` | `passenger` | `car` | the six `ue4.*` saloons, `dodge.charger`, `lincoln.mkz`, `mini.cooper`, `nissan.patrol` |
| `offroad` | `passenger` | `car` | `jeep.wrangler_rubicon` — an open-topped short-wheelbase jeep |
| `civ_van` | `delivery` | `van` | `sprinter.mercedes` |
| `civ_truck` | `truck` | `truck` | `carlacola.actors` — a two-axle rigid box lorry |
| `heavy_truck` | `truck` | `truck` | `carlamotors.european_hgv` — a three-axle rigid lorry |
| `bus` | `bus` | `bus` | `fuso.mitsubishi` — the Fuso Rosa, curated to `bus` because no box tells a bus from a lorry |
| `taxi` | `taxi` | `car` / `taxi` | `taxi.ford` |
| `police` | `authority` | `car` / `emergency` | `dodgecop.charger` |
| `ambulance` | `emergency` | `van` / `emergency` | `ambulance.ford` |
| `fire_appliance` | `emergency` | `truck` / `emergency` | `firetruck.actors` |

No class is defined for a body the content cannot yet spawn (Measurement 1a), because a class with no
measured member is refused (V1.7). The Jeep Wrangler, the first of them wired, has its `offroad` class,
distinct from the Patrol's sport utility in `civ_car`. When the rest are wired and registered they
belong as follows: the Volkswagen T2 in `civ_van`; the A2, Isetta, C3, Micra, Leon and Prius in `civ_car`, with
the Tesla Model 3 and the e-tron beside them unless the truth record is to carry upstream's
`special_type=electric`, which would need a class of its own; the Cybertruck wherever its measured box
and a curated reason place it.

### 3.5 Worked example

Catalogue fragment, with measured dimensions and measured palettes:

```json
{
  "catalogue_version": 1,
  "catalogue_id": "carla-0.10.0-win64-development",
  "catalogue_digest": "8f2c…",
  "content_build_id": "Carla-0.10.0-Win64-Development",
  "blueprint_set_digest": "41ad…",
  "generated_at_utc": "2026-09-17T09:14:22.108Z",
  "generator": "carlacontrol.VehicleCatalogueBuilder/1.0.0",
  "server_version": "0.10.0",
  "vehicles": [
    {
      "blueprint_id": "vehicle.mini.cooper",
      "uid": 26,
      "tags": "vehicle,mini,cooper",
      "measurement": "measured",
      "length_m": 4.55, "width_m": 2.10, "height_m": 1.77,
      "bbox_centre_m": [0.02, 0.00, 0.72],
      "declared_base_type": "car",
      "declared_special_type": "",
      "number_of_wheels": 4,
      "generation": 3,
      "settable_attributes": [
        { "id": "color", "type": "RGBColor", "restrict_to_recommended": false,
          "recommended_values": ["0,0,0", "104,4,8", "27,54,118", "26,62,29"] },
        { "id": "role_name", "type": "String", "restrict_to_recommended": false,
          "recommended_values": ["autopilot", "scenario", "ego_vehicle"] }
      ],
      "colour_settable": true,
      "colour_palette": ["0,0,0", "104,4,8", "27,54,118", "26,62,29"],
      "colour_applied": "false",
      "declared_has_lights": true,
      "lamp_capability": {
        "position": "lit", "low_beam": "lit", "high_beam": "lit", "brake": "lit",
        "right_blinker": "lit", "left_blinker": "lit", "reverse": "unlit", "fog": "unlit",
        "interior": "unlit", "special_1": "unlit", "special_2": "unlit"
      }
    },
    {
      "blueprint_id": "vehicle.carlacola.actors",
      "uid": 31,
      "tags": "vehicle,carlacola,actors",
      "measurement": "measured",
      "length_m": 8.00, "width_m": 2.91, "height_m": 4.05,
      "bbox_centre_m": [0.10, 0.00, 1.86],
      "declared_base_type": "truck",
      "declared_special_type": "",
      "number_of_wheels": 4,
      "generation": 3,
      "settable_attributes": [
        { "id": "color", "type": "RGBColor", "restrict_to_recommended": false,
          "recommended_values": ["149,0,14"] }
      ],
      "colour_settable": true,
      "colour_palette": ["149,0,14"],
      "colour_applied": "unknown",
      "declared_has_lights": true,
      "lamp_capability": {
        "position": "unknown", "low_beam": "unknown", "high_beam": "unknown", "brake": "unknown",
        "right_blinker": "unknown", "left_blinker": "unknown", "reverse": "unknown",
        "fog": "unknown", "interior": "unknown", "special_1": "unknown", "special_2": "unknown"
      }
    }
  ],
  "classes": [
    {
      "class_id": "civ_car",
      "description": "Ordinary civilian passenger cars. The default for background traffic.",
      "sumo_vclass": "passenger",
      "cot_base_type": "car",
      "members": [
        { "blueprint_id": "vehicle.mini.cooper",          "weight": 1.0 },
        { "blueprint_id": "vehicle.dodge.charger",        "weight": 1.0 },
        { "blueprint_id": "vehicle.lincoln.mkz",          "weight": 1.0 },
        { "blueprint_id": "vehicle.nissan.patrol",        "weight": 0.8 },
        { "blueprint_id": "vehicle.ue4.audi.tt",          "weight": 0.6 },
        { "blueprint_id": "vehicle.ue4.bmw.grantourer",   "weight": 0.8 },
        { "blueprint_id": "vehicle.ue4.chevrolet.impala", "weight": 0.8 },
        { "blueprint_id": "vehicle.ue4.ford.crown",       "weight": 0.8 },
        { "blueprint_id": "vehicle.ue4.ford.mustang",     "weight": 0.6 },
        { "blueprint_id": "vehicle.ue4.mercedes.ccc",     "weight": 0.6 }
      ],
      "max_speed_mps": 35.0,
      "accel_mps2": 2.6, "decel_mps2": 4.5, "sigma": 0.5,
      "speed_factor_mean": 1.00, "speed_factor_dev": 0.10,
      "min_gap_m": 2.5,
      "gui_shape": "passenger",
      "gui_colour": "#CCCCD1",
      "render_colour_policy": "palette"
    }
  ]
}
```

**Provenance of the numbers above.** The example predates the sweep, and the sweep has since run: the
catalogue it produced is in the tree at `CarlaControl/catalogue/vehicles.catalogue.json`, and that file
rather than this fragment is where a real value is read from. Against it:

- `length_m`, `width_m`, `height_m`, `colour_palette` and the `recommended_values` are **measured**
  (§3.2, Measurement 1 and Measurement 4), and the swept values agree with Measurement 4's to within a
  centimetre. Two `colour_palette` counts here do not survive the sweep: `vehicle.ue4.ford.crown`
  declares **one** recommended colour rather than five, and `vehicle.ue4.mercedes.ccc` **four** rather
  than five.
- `bbox_centre_m` was **illustrative** when this was written, because the recorded sidecars carry
  dimensions but not the box centre (`CarlaNet.Recording/CotWriter.cs` writes
  `length_m`/`width_m`/`height_m` and no centre). It is now **measured**, and it is not zero: the
  forward component runs from **−0.493 m** (`vehicle.fuso.mitsubishi`) to **+0.185 m**
  (`vehicle.ue4.ford.crown`), so ignoring it biases the bumper shift by up to half a metre. The
  lateral component is zero to a millimetre on sixteen of the seventeen and **−0.092 m** on
  `vehicle.fuso.mitsubishi`, which is the off-centre mesh §3.2's general form exists for.
- `declared_has_lights` is **measured** (§3.2a, Measurement 5: `true` on all 17).
- `lamp_capability` was **illustrative**. It is now **measured**, and it is very nearly uniform:
  **sixteen of the seventeen blueprints change no pixel when any of their eleven lamps is
  commanded.** The seventeenth, `vehicle.firetruck.actors`, lights exactly one — its `high_beam`,
  at 369 gained pixels against a same-state control of 48 and a bar of 192. Every other blueprint's
  strongest reading across all eleven bits is between 0 and 20 pixels, at or below its own noise
  floor, in a pass whose positive control moved by six figures of pixels. The example's row of `lit`
  verdicts is therefore a shape rather than a value: it shows what an entry looks like when lamps do
  light, which one blueprint and one bit in this content build do.
- **The consequence for `V1.19` is immediate.** Lamp capability is currently a perfect separator of
  one class: at the probe's sun instant, the only vehicle in this content build that shows a lit lamp
  is the fire appliance, and `fire_appliance` is a single-member class. Any night window containing it
  alongside anything else has an illumination covariate that is not behavioural, and `V1.18` will warn
  on every other class for every conspicuity lamp the policy commands.

The `.rou.xml` the scenario builder emits from that class — **generated, never hand-written**:

```xml
<!-- Generated from vehicles.catalogue.json; edit the catalogue, not this. -->
<vType id="vehicle.mini.cooper" vClass="passenger"
       length="4.55" width="2.10" height="1.77" minGap="2.5"
       maxSpeed="35.0" accel="2.6" decel="4.5" sigma="0.5"
       speedFactor="1.00" speedDev="0.10"
       guiShape="passenger" color="#CCCCD1">
  <param key="carla:blueprint"        value="vehicle.mini.cooper"/>
  <param key="carla:class_id"         value="civ_car"/>
  <param key="carla:catalogue_digest" value="8f2c…"/>
</vType>

<vType id="vehicle.dodge.charger" vClass="passenger"
       length="5.01" width="1.88" height="1.54" minGap="2.5"
       maxSpeed="35.0" accel="2.6" decel="4.5" sigma="0.5"
       speedFactor="1.00" speedDev="0.10"
       guiShape="passenger" color="#CCCCD1">
  <param key="carla:blueprint"        value="vehicle.dodge.charger"/>
  <param key="carla:class_id"         value="civ_car"/>
  <param key="carla:catalogue_digest" value="8f2c…"/>
</vType>

<vTypeDistribution id="civ_car"
                   vTypes="vehicle.mini.cooper vehicle.dodge.charger …"
                   probabilities="1.0 1.0 …"/>

<flow id="corridor_east_morning" type="civ_car" begin="0" end="21600"
      vehsPerHour="220" from="26417705#0" to="26401454#6"
      departLane="free" departSpeed="max"/>
```

**`<param>` children of `<vType>` are schema-valid.** Measured in the vendored SUMO schema:
`Build/sumo-src/data/xsd/types/route.xsd`, `vTypeType` opens with
`<xsd:element name="param" type="paramType" minOccurs="0" maxOccurs="unbounded"/>`, and
`SUMORouteHandler::myStartElement` handles `SUMO_TAG_PARAM`
(`Build/sumo-src/src/utils/vehicle/SUMORouteHandler.cpp:200-201`).

### 3.6 The two-way mapping, and why it needs no tolerance

The brief's question — does the vType derive from the blueprint, or is the blueprint chosen to fit the
vType? — has a third answer that dissolves the tolerance problem entirely.

> **D4.3 — one `vType` per catalogue blueprint, with `length` and `height` copied verbatim from the
> measurement and `width` the body's without its mirrors (§3.2b, D4.43); one `vTypeDistribution` per
> catalogue class. The author asks for a class; SUMO
> draws the member; the member *is* the blueprint.**

Neither pure direction works:

- *vType derives from the blueprint, one per class* would make a flow of 200 `civ_car` into 200
  identical cars — the appearance confounder
  [doc 20 §2.6](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md) and decision 12
  prohibit.
- *blueprint chosen to fit the vType* lets an author declare a vType no blueprint matches. A
  nearest-match rule would then render a 2.2 m two-wheeler as a 4.55 m car, silently — the substitution
  §3.1's scope boundary and `D4.40` exist to refuse, and the one the chooser performs today for any
  category its preference table misses (Measurement 2).

The distribution form resolves both, and it is already how the largest authored scenario works.
Measured in `BahonarPatternOfLife.zip`, path
`BahonarPatternOfLife/scenario/Shahid_Bahonar_Port_PatternOfLife.rou.xml`: **14 `<vType>`, 3
`<vTypeDistribution>`, 245 `<flow>`, 0 `<vehicle>`**, and flows reference the distribution
(`type="civ_mix"`), not a type. The playback bridge therefore reads the **realised** type per vehicle
via `traci.vehicle.getTypeID`, exactly as the existing SUMO telemetry bridge already does
(`CarlaControl/src/carlacontrol/SumoCotBridge.py:300`).

**The mapping, both directions:**

| Direction | Rule |
|---|---|
| catalogue entry → `vType` | The generator writes one `<vType id="<blueprint_id>">` per member, `length`/`width`/`height` copied from the measurement to two decimal places, behaviour parameters from the class, and `<param key="carla:blueprint">` carrying the blueprint id again |
| `vType` → blueprint, at playback | Read `<param key="carla:blueprint">`. **Never parse the id.** The id is human sugar; the param is the binding |
| SUMO vehicle → blueprint | `traci.vehicle.getTypeID(v)` → that vType → its `carla:blueprint` param. One hop, no matching, no nearest neighbour |

**Tolerance.** Because the generator copies the measured value, the only difference that can arise is
decimal rounding. The validator's tolerance is therefore **0.01 m** on each of `length`, `width`,
`height`, and its purpose is to absorb serialisation, not to permit substitution.

**Why the tolerance is not cosmetic.** Two independent effects, both measurable:

1. **Car-following gaps.** SUMO's Krauss model computes the space a vehicle occupies as
   `length + minGap`, and lane occupancy and junction blocking from `width`. A vType 2 m longer than
   the body being rendered makes SUMO reserve road the imagery shows as empty.
2. **Pose.** SUMO's reference point is the front bumper centre, CARLA's is the body centre
   ([doc 23 §6.7](../../Findings/23_SUMO_Traffic_Integration.md)), so every pose is shifted by
   `length / 2` along the heading. A length mismatch of `Δ` puts the rendered body `Δ/2` from where
   SUMO believes it is. Worked from measured values: Bahonar's `civ_truck` declares `length="10.0"`;
   the nearest CARLA lorry, `vehicle.carlacola.actors`, measures **8.00 m**. Binding one to the other
   would place every such lorry **1.00 m** out along its own heading, in every frame, for the whole
   capture — a systematic bias exactly in the axis a detector's along-track error is measured in.

**Failure when nothing fits.** An author asks for a class the catalogue does not define, or a class
whose `members` list is empty, or a member whose `measurement` is `failed`. All three are **errors at
scenario package build**, never at playback, and the message must list the classes the catalogue does
offer. This is the loud failure that replaces today's silent substitution.

**A hand-written `vType`.** Permitted, and checked: any `<vType>` in the routes file must carry
`carla:blueprint` naming an entry whose `measurement` is `measured`, and its `length`, `width` and
`height` must equal that entry's within 0.01 m. A `vType` without the param, or outside tolerance, is
an **error**. There is no warning tier here, because the consequence is a behavioural simulation
computed for a body that was never rendered.

### 3.7 Colour

**Two formats, and a measured trap in each.**

*CARLA.* The `color` attribute is exactly three comma-separated integers 0–255. `ActorAttributeToColor`
(`ActorBlueprintFunctionLibrary.cpp:1222-1262`) splits on `,`, requires exactly three channels, parses
each with `Atoi`, and rejects anything outside `[0, 255]`. On any failure it logs an error **on the
server** and returns the default — the client is told nothing. So a malformed colour is a **silent
no-op from the caller's side**. The attribute exists only when the blueprint declares
`RecommendedColors` (`:850-861`), measured present on all 17, with `bRestrictToRecommended = false`
(`:855`), so any legal triple is accepted, not only the recommended ones.

*SUMO.* `RGBColor::parseColor` (`Build/sumo-src/src/utils/common/RGBColor.cpp:279-323`) first parses a
comma triple as integers; **if all components are ≤ 1 it throws and re-parses them as fractions
× 255**. So `"1,0,0"` is red, not near-black, and `"1,1,1"` is white, not near-black. `#RRGGBB` and
`#RRGGBBAA` are parsed unambiguously (`:285-298`).

> **D4.4 — colours are written as `#RRGGBB` in every SUMO artifact and as `"R,G,B"` integers 0–255 in
> every CARLA artifact. The generator never emits a SUMO comma triple.**

That removes the fraction ambiguity by construction. It is a migration from the shipped artifacts,
which use fractions: measured in the Bahonar route file, all 14 vTypes carry `color="0.80,0.80,0.82"`
and similar, and the existing bridge converts them to CARLA's byte form through TraCI
(`SumoCotBridge.py:305,326` → measured sample row `color="230,204,51"` from
`color="0.90,0.80,0.20"`, in `BahonarPatternOfLife/samples/bahonar_cot_sample.csv`).

#### Which vType fields bind to the blueprint, and which do not

This is the rule the integration lead asked for, and the measurement supports it exactly.

| `vType` field | Binds to the rendered vehicle? | Why |
|---|---|---|
| `length`, `width`, `height` | **Yes, exactly** | They change SUMO's gaps and the bumper-shift. `C1` makes them equal by construction (§3.6) |
| `vClass` | Yes, as `cot_base_type` / `cot_special_type` in truth | Curated in the catalogue, because the blueprint's own `base_type` is measurably wrong |
| `maxSpeed`, `accel`, `decel`, `sigma`, `speedFactor`, `speedDev`, `minGap` | No — they are SUMO's driving model | CARLA does not simulate the driver in this mode (`C7`) |
| `guiShape` | **No** | `sumo-gui` only |
| `color` | **No. Deliberately ignored at render time** | §3.7.1 |

##### 3.7.1 `vType` colour is a `sumo-gui` property and is never rendered

**Measured confounder.** In the Bahonar route file the four anomaly types carry conspicuous colours —
`anomaly_probe` `1.00,0.45,0.00` (orange), `anomaly_escort` `1.00,0.10,0.10` (red), `anomaly_shadow`
`1.00,0.20,0.60` (magenta), `anomaly_staybehind` `1.00,0.30,0.00` (orange) — while every civilian and
port type is a muted grey or brown: `civ_car` `0.80,0.80,0.82`, `civ_pickup` `0.55,0.58,0.60`,
`civ_truck` `0.60,0.50,0.35`, `port_vehicle` `0.55,0.70,0.85`. Nine vehicle ids are listed in
`marked_ids` and every one of them uses one of those four types. Carrying `vType` colour through to
the blueprint would therefore make **colour a perfect linear separator of the positive class**, which
is precisely the appearance confounder
[doc 20 §2.6](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md) and decision 12
prohibit.

> **D4.5 — `vType@color` is read only by `sumo-gui`. The rendered colour is drawn from the member
> blueprint's own `colour_palette` by the seeded rule of §3.8, and is identical in distribution for
> marked and unmarked vehicles of the same class.**

This keeps everything everyone needs:

- **SUMO still has a colour**, so `sumo-gui` renders normally and nothing in SUMO changes.
- **The author still sees the marked vehicles** in `sumo-gui`, in whatever conspicuous colour they
  like, and are *encouraged* to make them conspicuous — it is now free of consequence, because it
  reaches nothing downstream. The existing Bahonar colouring is therefore correct as authored and
  needs no change; only its interpretation at render time does.
- **The corpus is not poisoned**, because the imagery's colour distribution is a property of the class,
  not of the supervision state.

**Two escape hatches, both loud.**

1. `render_colour_policy = "fixed"` on a class renders every member in one colour. Legitimate — a taxi
   fleet, a livery. The validator requires that the class contains **both** marked and unmarked
   vehicles in the scenario, or the run is rejected as appearance-confounded.
2. `carla:colour_override` as a `<param>` on an individual `<vehicle>` or `<trip>` forces one colour.
   Legitimate when the colour *is* the phenomenon. The validator requires the same override value to
   appear on at least one vehicle whose supervision is `unlabelled` or `nominal`, drawn from the same
   class — so the colour is never a separator. An override on a blueprint whose `colour_applied` is
   `false` is a **warning**: the request will be honoured by the attribute and ignored by the material.

A package-level switch `render_uses_vtype_colour` exists for diagnostic runs where an operator wants
the `sumo-gui` colours on screen. Setting it `true` stamps `appearance_confounded: true` into the run
manifest and marks the run **not corpus-eligible**. It is a debugging aid with a recorded consequence,
not a configuration choice.

### 3.8 One class, many blueprints, one reproducible draw

Two draws happen, and only one of them is ours.

**The blueprint draw is SUMO's.** When SUMO instantiates a flow vehicle from a `vTypeDistribution` it
picks a member using its own seeded RNG, fixed by `<seed>` in the `.sumocfg` (measured:
`<seed value="42"/>` in the Bahonar config). The choice is therefore recorded inside the behavioural
simulation, reproducible from the SUMO seed alone, visible in SUMO's own outputs, and **identical for
annotated and ambient vehicles because they draw from the same distribution**. The bridge performs no
blueprint selection at all. This is the strongest available form of doc 20 decision 12: not "selection
is reproducible from the run seed" but "selection is not made at playback".

**The colour draw is ours**, because SUMO has no concept of a CARLA colour palette. It must be
deterministic, stable per SUMO vehicle id, and re-derivable by any implementation. Specified exactly:

```
palette  = catalogue.vehicles[blueprint_id].colour_palette          # ordered, as written
material = appearance_seed_decimal + "|" + sumo_vehicle_id          # ASCII, no separator padding
h        = SHA-256(UTF-8 bytes of material)
index    = int.from_bytes(h[0:8], byteorder="big") % len(palette)
colour   = palette[index]
```

- `appearance_seed` is a 64-bit unsigned integer, **defaulting to the SUMO seed**, rendered in decimal
  with no sign and no padding. A separate field so a deliberate appearance re-roll is expressible without
  re-running the behaviour. Not declared in the scenario package as built: SUMO's own seeded
  `vTypeDistribution` draw chooses the body, and nothing reads a separate seed yet
  ([`07`](07_Scenario_Authoring.md) D7.11).
- `sumo_vehicle_id` is the exact SUMO id, including a flow's `.N` suffix, so two vehicles of one flow
  differ.
- An empty palette (impossible in the measured content build, but expressible) means no `color`
  attribute is set at all.

**Worked draw.** `appearance_seed = 42`, vehicle `corridor_east_morning.17`, blueprint
`vehicle.mini.cooper` with the measured 4-entry palette. `material = "42|corridor_east_morning.17"`,
`index = <first 8 bytes of SHA-256> mod 4`, colour = that palette entry. Re-running with the same
scenario package and the same seeds yields the same car in the same colour, which is what makes a
counterfactual pair ([doc 20 §11 question 7](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md))
cheap.

### 3.9 Validation

Checked by the catalogue generator (`G`), the scenario package builder (`S`), or the co-simulation
driver at run start (`R`).

| # | Rule | Where | Response |
|---|---|---|---|
| V1.1 | `blueprint_id` unique; matches `vehicle\.[a-z0-9_.]+` | G | refuse |
| V1.2 | Every `measured` entry has all three dimensions, each > 0.2 m and < 30 m, **and a `bbox_centre_m`** — the pose conversion needs it (§3.2) | G | refuse |
| V1.2a | The sweep's vehicle-actor count at the end equals its count at the start | G | refuse — a leaked actor may have turned a later spawn into a collision |
| V1.3 | `colour_palette` entries are three integers 0–255 | G | refuse |
| V1.4 | `colour_applied` is one of `true`/`false`/`unknown`; never absent | G | refuse |
| V1.5 | Every `classes[].members[].blueprint_id` exists in `vehicles[]` | G, S | refuse |
| V1.6 | Every class declares `accel`, `decel`, `sigma`, `speed_factor_mean`, `speed_factor_dev`, `min_gap_m` explicitly | G | refuse — §3.10 |
| V1.7 | No class has an empty `members` list | G, S | refuse |
| V1.8 | Every `vType` in the routes file carries `carla:blueprint` | S | refuse |
| V1.9 | Every such `vType`'s `length`/`width`/`height` equals the catalogue entry within 0.01 m | S | refuse |
| V1.10 | Every `flow`/`trip`/`vehicle` `type=` names a declared `vType` or `vTypeDistribution` | S | refuse |
| V1.11 | No `vType` or `vTypeDistribution` is referenced **only** by vehicles carrying supervision `annotated`, unless the class declares `appearance_is_the_phenomenon: true` | S | refuse — the confounder of §3.7.1 |
| V1.12 | Every `carla:colour_override` value also appears on a vehicle with supervision `unlabelled` or `nominal` in the same class | S | refuse |
| V1.13 | A `carla:colour_override` on a blueprint with `colour_applied == "false"` | S | warn |
| V1.14 | `blueprint_set_digest` recomputed from the live server equals the catalogue's | R | refuse — §3.10 |
| V1.14a | The bridge loaded the catalogue and every class member has `length_m` and `bbox_centre_m` | R | refuse to start — without them the pose conversion is undefined (§3.2) |
| V1.15 | SUMO colours are `#RRGGBB`; CARLA colours are `"R,G,B"` 0–255 | G, S | refuse |
| V1.16 | A referenced class's `sumo_vclass` is permitted on every edge its flows route over | S | refuse, naming the vClass and the first offending edge |
| V1.17 | Every `measured` entry carries all eleven `lamp_capability` keys, each `lit`/`unlit`/`unknown`; and `lamp_probe` is present with `ran` set | G | refuse — an absent key is indistinguishable from an unmeasured one, and that is the ambiguity this field exists to remove |
| V1.18 | For a scenario with any capture window whose civil span includes an hour at which `C7` §9.4 commands a conspicuity lamp (`C9` decides which; the check is "the policy would command lamp L"), every class used in that window has `lamp_capability[L] == "lit"` for every member | S | **warn**, naming the class, the member and the lamp, and record it in the run manifest as `lamp_gaps[]`. Not a refusal: a blueprint without a working headlight is a content fact, not an authoring error, and the honest response is to record it rather than to forbid the capture |
| V1.18a | The same check where `lamp_capability[L] == "unknown"` | S | **warn**, distinctly from V1.18 — "not measured" and "measured absent" must never collapse into one message |
| V1.19 | Within any class used in a night window, `lamp_capability` for the policy-commanded lamps is identical across all members, **or** the class contains both marked and unmarked vehicles in the scenario | S | refuse — otherwise lamp capability is a night-time appearance separator of the positive class, exactly as `vType@color` was a daytime one (§3.7.1, V1.11) |
| V1.20 | No class declares a `sumo_vclass` of `motorcycle`, `moped` or `bicycle`, and no class declares a `cot_base_type` of `motorcycle` or `bicycle`; no `vType` in the routes file carries those `vClass` values | G, S | refuse, naming `D4.40` — the scope boundary of §3.1, checkable because both vocabularies are closed lists |

### 3.10 Failure modes

| Violation | Detected | Behaviour |
|---|---|---|
| Catalogue references a blueprint the running content does not have | run start, V1.14 | **Refuse to start.** Name every blueprint that moved. Do not substitute |
| `vType` length disagrees with the blueprint | scenario build, V1.9 | **Refuse to build.** Report the pose bias `Δ/2` it would have caused, in metres |
| Author asks for a class that does not exist | scenario build | **Refuse**, listing the classes the catalogue offers |
| Author asks for a two-wheeler — motorcycle, moped or bicycle | scenario build | **Refuse**, naming `D4.40`: two-wheelers are outside this contract, the content build registers none, and a rider would not be rendered even if it did. Never fall through to a car |
| Author asks for an articulated vehicle — a semi, a tractor unit, a drawbar trailer | scenario build | **Refuse**, naming `D4.41`: SUMO reports one pose for the whole vehicle and no articulation, so a trailer cannot be placed. Never fall through to the nearest long rigid body |
| Author asks for any other vehicle kind the content lacks | scenario build | **Refuse**, and say plainly that this content build has none. Never substitute |
| A `vType` reaches playback without `carla:blueprint` (should be unreachable) | playback | Vehicle is **not rendered**; `render_state = simulated_only`, reason `no_blueprint`; one warning per distinct vType, not per vehicle; the run continues and the manifest records it |
| Colour string malformed | server, silently | Nothing is reported to the client (`ActorBlueprintFunctionLibrary.cpp:1241-1252`). V1.15 exists precisely because this failure is invisible at runtime |
| A lamp the illumination policy commands is `unlit` on a rendered blueprint | scenario build V1.18; and again at run start | **Warn, and record.** The vehicle renders dark where it should be lit; `lamp_gaps[]` in the run manifest names every (class, blueprint, lamp) so a corpus auditor can find every affected frame. Never substitute another lamp |
| A night window authored against a catalogue whose `lamp_probe.ran` is `false` | scenario build, V1.18a | **Warn**, and stamp `lamp_capability: "unmeasured"` into the run manifest. The capture is legitimate; the claim "the vehicles had headlights" is not available from it |
| Class omits `speed_factor_dev` | catalogue build, V1.6 | **Refuse.** A scalar `speedFactor` leaves the vClass default deviation in place — measured 0.1 for `passenger` (`Build/sumo-src/src/utils/vehicle/SUMOVTypeParameter.cpp:283`), 0.05 for `taxi` (`:287`), `truck` (`:170`), `delivery` (`:266`) — and `Distribution_Parameterized::parse` overwrites only the mean (`Build/sumo-src/src/utils/distribution/Distribution_Parameterized.cpp:64-77`). A catalogue that does not state the deviation is not reproducible |

**Why every SUMO parameter is explicit.** The catalogue must not rely on SUMO's vClass defaults,
because they are large, class-dependent and invisible in the artifact. Measured from
`Build/sumo-src/src/utils/vehicle/SUMOVTypeParameter.cpp` and
`Build/sumo-src/src/utils/common/SUMOVehicleClass.cpp`:

| vClass | default length (m) | default width (m) | default height (m) | default speedDev |
|---|---|---|---|---|
| `passenger`, `authority`, `army`, unlisted | 5.0 (`SUMOVehicleClass.cpp:604`) | 1.8 (`:175`) | 1.5 (`:176`) | 0.1 for `passenger` (`SUMOVTypeParameter.cpp:283`); **0.0** for `army`/`authority`, which the switch does not name |
| `taxi` | 5.0 | 1.8 | 1.5 | 0.05 (`:287`) |
| `truck` | 7.1 (`SUMOVehicleClass.cpp:575`) | 2.4 (`SUMOVTypeParameter.cpp:162`) | 2.4 (`:163`) | 0.05 (`:170`) |
| `bus` | 12.0 (`:579`) | 2.5 (`SUMOVTypeParameter.cpp:186`) | 3.4 (`:187`) | — |
| `delivery`, `emergency` | 6.5 (`:593-594`) | 2.16 (`SUMOVTypeParameter.cpp:265`) | 2.86 (`:266`) | 0.05 for `delivery` |
| `motorcycle` | 2.2 (`:573`) | 0.9 (`SUMOVTypeParameter.cpp:152`) | 1.5 (`:153`) | 0.1 |

The cost of leaving these implicit is measurable in the shipped scenario: of Bahonar's 14 vTypes,
**width is declared on 5 and height on none**, so every one of them reports a defaulted height, and
nine of them a defaulted width. The sample CSV confirms it —
`civ_taxi` rows carry `length_m=4.50` (declared) with `width_m=1.80` and `height_m=1.50` (both
defaults). Under `C1` those numbers become the blueprint's real 4.55 × 2.10 × 1.77.

### 3.11 Versioning and binding

- `catalogue_version` is an integer. A consumer that does not implement the version it reads **must
  refuse**, not best-effort.
- `catalogue_digest` identifies the exact catalogue. A scenario package records it (`C3`).
- `blueprint_set_digest` is the part a running server can independently reproduce, from
  `get_actor_definitions` alone, with no spawning. The driver recomputes it at run start (V1.14).
- `content_build_id` covers what the set digest cannot: a mesh change alters a bounding box without
  altering any definition. A mismatch here with all digests matching is a **warning** naming the two
  build ids, because it is the one case where the catalogue may be stale in a way nothing can detect
  cheaply.

**Binding tiers at run start:**

| Condition | Response |
|---|---|
| `blueprint_set_digest` differs | **refuse** |
| set digest matches, `catalogue_digest` differs | **warn**, naming every entry whose fields moved |
| everything matches, `content_build_id` differs | **warn** |

### 3.12 What breaks if C1 is violated

- **A behavioural simulation computed for a body nobody rendered.** SUMO reserves space by `length`
  and `width`; the imagery shows a different body. Every gap, every follow distance and every junction
  occupancy in the corpus is then a claim about a vehicle that does not appear, and the truth record
  and the imagery disagree about the same object. Nothing downstream can detect this, because both
  sides are internally consistent.
- **A systematic along-track pose bias.** `Δlength / 2` in every frame, in exactly the axis along which
  the label and the pixels have to agree for the label to mean anything. Measured worst case in the
  shipped artifacts: 1.00 m.
- **The pose conversion cannot be computed at all.** The front-bumper-centre to actor-origin shift
  needs the measured extent and box centre, and SUMO has neither (`D4.17`). Without the catalogue at
  runtime the bridge either does not render or renders at a guessed offset — and the guess is
  invisible, because both the imagery and the truth record agree with each other while both are wrong.
- **Colour becomes the label.** Measured: the four Bahonar anomaly types are the only conspicuous
  colours in the file and cover all nine marked vehicles. A corpus built that way carries its own
  supervision in a covariate — "orange means anomaly" is readable straight off the pixels — and nothing
  in the corpus records that it does.
- **One category, one car.** The present state (`BlueprintChooser.cs:43-63`) produces convoys of
  identical vehicles, which
  [doc 18 §8.4](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md) already records as worse
  than arbitrary for detector training.
- **Silent substitution.** An author asks for a vehicle kind the content lacks, gets a car, and is
  told nothing. Every subsequent conclusion about that kind is a conclusion about cars. `D4.40` and
  V1.20 make the two-wheeler case a refusal for exactly this reason; the general case is §3.10's
  second refusal row.
- **A night corpus of dark vehicles, asserted to be lit.** `has_lights` is `true` on all 17
  (Measurement 5) and the light-state read-back returns the command rather than the vehicle
  (`CarlaWheeledVehicle.cpp:486-489`), so every layer above reports success while the imagery shows an
  unlit body. The corpus then asserts a lit vehicle where the imagery holds a dark one, and records no
  trace of which of the seventeen blueprints actually lit up — a label that contradicts its own frame.
- **Lamp capability becomes the night-time label.** If the blueprints that light up cluster in the
  classes the marked vehicles use, the positive class is separable on illumination rather than on
  behaviour — the same defect as `vType@color`, at a different time of day, and V1.19 is the check.

---

## 4. C2 — The render set

When CARLA draws a SUMO vehicle, when the body appears, when it is released, and what the truth record
says about a vehicle that SUMO simulated and CARLA did not draw.

The sizing case is seven simulated days at one-second steps with 245 flows
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §5, re-measured here as 245 `<flow>` and 0 `<vehicle>`). Over that
span SUMO creates far more distinct vehicles than are alive at any one time, and most of the span lies
outside every capture window. What `C2` fixes is that every vehicle alive in a capture window is drawn,
the instant it is admitted and the instant it is released, and what the truth record says about one
SUMO simulated and CARLA did not draw. By default nothing limits how many are drawn at once: the
scenario decides the population, and a heavier population makes a synchronous run slower on the wall
clock, never different in content ([`10`](10_Scale_And_Performance.md) §4.3). Two optional performance
controls trade fidelity for speed where a run chooses them, and neither is a default: a limit on which
vehicles get a body (D4.44) and a draw distance (D4.45).

### 4.1 Artifact

Two halves, both named:

- **Input** — `capture_windows[]` and `prewarm_s`, typed in §4.2. The authored candidate windows are
  carried in the lock (`C3` §5.3), and the operator chooses which a run captures
  ([`12`](12_Operator_Control_Surface.md) D12.4). `prewarm_s` is given at run start and valued in
  [`10`](10_Scale_And_Performance.md) §8. By default nothing else is input and no parameter chooses
  which vehicles are drawn. A run may choose an optional limit -- `render_set` `circle` with its
  region, `cameras`, and a capacity under either or under every vehicle -- and an optional draw
  distance; both are given at run start, recorded in the run's effective configuration, and absent
  unless chosen ([`12`](12_Operator_Control_Surface.md) §5.2).
- **Output** — a `render_states[]` array in the run manifest, written by the render-set controller as
  append-only rows: an admission row when a vehicle is admitted, a release row when it is released. A
  vehicle still rendered when the run stops has an admission row and no release row, which is exactly
  what a reader needs to know (`D4.36`, `C10` §12.7). The manifest's closing row states that the run
  closed; it is not what makes the manifest readable.

### 4.2 The admission predicate

Evaluated once per admission pass, for every SUMO vehicle currently in the simulation. An admission
pass runs at most once per SUMO step.

```
admit(v, t) ⟺  in_window(t)
           ∧  alive(v, t)
           ∧  ¬ rendered(v)
           ∧  measured_body(v)
           ∧  in_limit(v, t)
```

with the terms defined as:

| Term | Definition | Parameter (typed here, valued in [`10_Scale_And_Performance.md`](10_Scale_And_Performance.md)) |
|---|---|---|
| **`in_window`** | `t` lies in one of the run's `capture_windows[]`, each `{ begin_s, end_s }` in simulated seconds, or within `prewarm_s` before one | `capture_windows[]`, `prewarm_s` |
| **`alive`** | SUMO holds `v` at `t`, moving or parked | — |
| **`rendered`** | `v` already has a body | — |
| **`measured_body`** | `v`'s vType names a catalogue blueprint whose extent the catalogue measured (`C1` §3.2, `D4.17`) | — |
| **`in_limit`** | True for every vehicle unless the run chose an optional limit (D4.44). Under one: `v` lies inside the circle's admit radius, or inside or within its lead of a registered camera's ground footprint, and ranks within the capacity by the limit's order | `render_set`, its region and settings, the capacity: chosen by the run, valued nowhere in this plan as a recommendation |

With no limit, the default, every vehicle SUMO has in a window is drawn, wherever it is and whether or
not any sensor can see it; there is no region, no priority order and no capacity. A vehicle SUMO inserts is drawn from the frame
SUMO first reports it in: where SUMO inserted it, moving from then, and never on a frame before SUMO
inserted it ([`03`](03_CoSimulation_Runtime.md) D3.6). A vehicle SUMO already has when the session
starts rendering is drawn on the first rendered frame. No vehicle waits for a body: bodies
are spawned or reused as the population needs, with no ceiling ([`03`](03_CoSimulation_Runtime.md)
§8.2), and what a larger population costs is wall-clock time ([`10`](10_Scale_And_Performance.md)
§4.3).

**An optional limit leaves vehicles out, and says so** (D4.44). Under a circle, the cameras or a
capacity, a vehicle `in_limit` rejects is still simulated by SUMO -- its behaviour is the scenario's --
and has no body, so it is in no frame and no imagery-side truth record, and it is counted (§4.5). One
inside the limit is drawn exactly as with none: every vehicle stays subscribed, so a vehicle the limit
admits part-way through its drive is drawn from that admission at its interpolated position, and one
SUMO inserts inside it from the frame SUMO first reports it in ([`03`](03_CoSimulation_Runtime.md)
§8.3.2). **A draw distance does not enter the predicate** (D4.45): every vehicle it applies to keeps
its body, pose and truth, and a camera simply does not draw one farther than the distance from it, so
it is a fact about each camera's view, marked in that camera's sidecar (§4.5).

**`measured_body` is the one exclusion with no limit, and it is per type, not per vehicle.** A vType that names no
blueprint, or names one the catalogue holds no measurement for, is refused the first time it is seen
and the answer is kept for the run: its vehicles are simulated and their behavioural truth recorded,
and they are never placed at a guessed size (`C1` §3.2). The scenario compiler refuses to write a
package with such a type ([`07`](07_Scenario_Authoring.md) check 14), and `run_capture` re-runs that
check against the live server ([`12`](12_Operator_Control_Surface.md) check 25), so inside a compiled
package the exclusion is never met.

### 4.3 The eviction rule

A rendered vehicle is released when any of:

| # | Condition | Notes |
|---|---|---|
| E1 | SUMO removed the vehicle (arrival, `remove`, collision removal) | SUMO is the authority on existence |
| E2 | *Withdrawn 2026-09-30* with the render region: there is no region for a vehicle to leave | — |
| E3 | The capture window closed and no window opens within `prewarm_s` | |
| E4 | *Withdrawn 2026-09-30* with the render cap: there is no capacity to exceed | — |
| E5 | Under an optional limit only: the circle or every registered camera's reach stopped admitting the vehicle, beyond its hysteresis and, under the cameras, for the release lag | reason `left_the_region`; never met with no limit. A new number: E2 stays withdrawn |
| E6 | Under an optional capacity only: more vehicles passed the limit than the capacity allows and this one ranked out | reason `capacity`; never met with no capacity. A new number: E4 stays withdrawn |

A vehicle still drawn when the run stops is not released; §4.1 says what its rows then show.

**A release is abrupt, and the instant is recorded.** Per-actor opacity fade is demoted and is not to be
designed around ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md), *Vehicle fade is demoted*): an admitted vehicle
appears at full opacity and a released one disappears. What replaces the fade-derived notion of a
vehicle having "arrived" is the **recorded admission and release instant** — `rendered_spans[]` in §4.5
— which is a fact about the capture rather than a visual transition. The admission instant is the
frame SUMO first reports the vehicle in, or the first rendered frame for one SUMO already had, and it
is the first frame the vehicle is drawn on (§4.2): no frame before it shows the vehicle.

Two consequences, both already true in the tree. The arrival gate degrades to inert rather than
breaking: `CarlaClient.IsActorEstablished` returns true for any actor nobody has faded
(`CarlaNet.Transport/CarlaClient.cs:1571`), the truth producer's gate is documented as inert in exactly
that case (`CarlaNet.Recording/VehicleTelemetryService.cs:66-73`), and `GetActorOpacity` returns 1.0 for
an unfaded actor (`CarlaClient.cs:1562`), so `VehicleTelemetry.Opacity`
(`VehicleTelemetryService.cs:112`) is a constant 1.0 under this mode. And the release path stays a
single path: `C2` is the *only* subsystem that destroys a vehicle here, because the .NET traffic manager
is locked out ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3.4) and no storyboard executor is running, so this
mode does not add a fourth destroyer
([issue #18](https://github.com/sbrett9/carla/issues/18)). Whether the actor is destroyed or returned to
a pool is [`03_CoSimulation_Runtime.md`](03_CoSimulation_Runtime.md)'s; `C2` fixes only that the release
instant is recorded and that exactly one component decides it.

### 4.4 The participant guarantee

> **D4.6 — a vehicle participating in an open annotated or nominal interval is drawn throughout it:
> from its departure, or from `window.begin_s − prewarm_s` if it departed earlier, until SUMO removes
> it (E1) or the window closes (E3). This holds by construction with no limit, the default — every
> vehicle SUMO has in a window is drawn (§4.2), so nothing can displace a participant.**

**Under an optional limit the guarantee does not hold, and nothing pretends it does.** A run that
chooses a circle, the cameras or a capacity (D4.44) may leave a participant without a body for part
or all of its interval. That is recorded rather than prevented: the participant's `render_states[]`
entry carries `outside_limit` for the spans the limit left out (§4.5), V2.6 marks a run invalid that
ends orderly with a participant undrawn in an open interval, exactly as for any other cause, and
`run_capture`'s launch echo has already said that a vehicle outside the limit is not in CARLA. A draw
distance never displaces a participant: it keeps its body, and only a camera beyond the distance does
not show it (§4.5).

The reasoning is the whole point of the capture: an authored subject that was never rendered produced
no imagery, so the run has no evidence for the very thing it was built to produce, and a run that
*silently* drops its subject looks exactly like a run whose model missed it. V2.5 puts every
participant's interval inside a capture window, and within the window a run captures the admission
rule alone draws every participant. The one way a participant could have no body is a vehicle type
with no measured body, and that is refused before a run starts: by the scenario compiler
([`07`](07_Scenario_Authoring.md) check 14) and again by `run_capture` against the live server
([`12`](12_Operator_Control_Surface.md) check 25). A participant undrawn inside an open interval all
the same — a spawn the server refused, or a package that bypassed both checks — is written as such in
`render_states[]` (§4.5), and V2.6 and `C10`'s `D4.6` gate row report it (§12.5).

### 4.5 What truth says about a simulated-but-unrendered vehicle

This is doc 20 §2.5's observability accounting, stated as a contract.

> **D4.7 — behavioural truth exists for every SUMO vehicle in a capture window. Imagery-side truth
> exists only for rendered ones. Every SUMO vehicle carries an explicit `render_state`, and the
> absence of a vehicle from a sidecar is never the carrier of that fact.**

`render_states[]` in the run manifest, one entry per SUMO vehicle that existed during any capture
window:

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `sumo_vehicle_id` | string | — | yes | The join key |
| `vtype_id` | string | — | yes | The realised vType, i.e. the blueprint (`C1` §3.6) |
| `class_id` | string | — | yes | The catalogue class |
| `entity_id` | string | — | no | Present for authored vehicles only |
| `render_state` | string | — | yes | `rendered` \| `partially_rendered` \| `simulated_only` |
| `reason` | string | — | yes when not `rendered` | `outside_window` \| `no_blueprint` \| `unknown_extent` \| `spawn_failed` \| `outside_limit`. `outside_window` covers the part of a vehicle's life outside every window CARLA was attached for, a truth-only window included; `no_blueprint` and `unknown_extent` are `C1` §3.2's runtime cases (`D4.17`); `outside_limit` is the part an optional render-set limit left out (D4.44) and is never written with no limit |
| `sumo_span_s` | `[begin, end]` | s | yes | Simulated seconds of the vehicle's whole life in SUMO |
| `rendered_spans` | array of `{ begin_s, end_s, actor_id }` | s | yes | Empty for `simulated_only`. One entry per *rendering* — a vehicle released and re-admitted has two, with two different `actor_id`s (`C4`) |
| `observed_spans` | array of `{ sensor_id, begin_s, end_s }` | s | yes | In-frustum coverage per collection sensor, and drawn by it: a span where the vehicle stood wholly beyond an optional draw distance from that sensor is not observed by it (D4.45). Empty is meaningful and must be written |
| `observed_union_s` | number | s | yes | Total simulated seconds observed by at least one sensor |

**The accounting rules that follow, stated so a consumer cannot get them wrong:**

- A `simulated_only` vehicle contributes to any denominator computed over **behaviour** — how many
  vehicles executed a pattern, base rates of behaviour in the world.
- It contributes **zero** to any denominator computed over **imagery** — per-sensor prevalence, and the
  imagery-side denominator of doc 20 §2.5. A vehicle that was never rendered is not something the
  imagery ever had a chance to contain. *(This is a rule about which of our own numbers is the
  honest one, not about what any consumer would be charged for.)*
- `observed_union_s` and the per-sensor `observed_spans` are the honest denominators. `sumo_span_s` is
  not.
- Inside a window CARLA was attached for and with no limit, a vehicle goes undrawn for exactly two
  causes: its type has no measured body (`no_blueprint`, `unknown_extent`), or the server refused its
  spawn (`spawn_failed`). Neither is a choice about which vehicles to draw, so no consumer has to treat
  the drawn vehicles as a sample of the simulated ones.
- **Under an optional limit there is a third cause, and it is a choice** (`outside_limit`, D4.44).
  The drawn vehicles are then a sample of the simulated ones -- chosen by place under the circle or the
  cameras, by the scenario seed's order under a capacity on every vehicle -- and a consumer must treat
  them so: any count over imagery is over the drawn, and any count over behaviour still takes every
  vehicle from the manifest. The run's effective configuration names the limit, and its record counts
  the vehicle-passes it left out and the releases it caused.
- **An optional draw distance leaves every vehicle drawn and some unseen** (D4.45). A vehicle wholly
  beyond the distance from a sensor is listed in that sensor's sidecar with `beyond_draw_distance`,
  is not in its image and is not observed by it; one partly beyond it is marked `partly`, may be drawn
  without its far parts, and is observed by the usual tests where they find it.
- A vehicle with `render_state = partially_rendered` has a behavioural interval that is only partly
  evidenced; an interval clipped to `rendered_spans ∩ observed_spans` is the supervisable part.

### 4.6 Validation, failure, versioning

| # | Rule | Response |
|---|---|---|
| V2.1 | `capture_windows[]` non-empty, each `begin_s < end_s`, non-overlapping, sorted | refuse at scenario build |
| V2.2 | Every window lies within the SUMO config's `[begin, end]` | refuse |
| V2.3 | *Withdrawn 2026-09-30* with the render region: there is no region to resolve | — |
| V2.4 | *Withdrawn 2026-09-30* with the render cap: there is no cap to compare | — |
| V2.8 | An optional limit, where chosen, is one the session can draw: a circle has a region, and a region is given only to a limit that reads one ([`12`](12_Operator_Control_Surface.md) check 53); a draw distance reaches the point every channel is aimed at (check 52) | refuse at launch. A new number: V2.3 and V2.4 stay withdrawn |
| V2.5 | Every participant's interval lies within some capture window | refuse — an annotated interval nobody could render is an authoring error, not a runtime outcome |
| V2.6 | At an orderly run end, no `render_states[]` entry has `render_state != "rendered"` with an `entity_id` and an open interval. **Evaluated at closeout only**: a run the caller stops leaves intervals open by construction, which is a fact about when it was stopped and not a violation (`C10` §12.5, §12.7) | run is marked invalid |
| V2.7 | `render_states[]` covers every SUMO vehicle that existed in a window | run is marked incomplete |

The input has no version of its own: the windows travel in the lock under its `lock_version` (`C3`
§5.3), and `prewarm_s` is recorded in the run's effective configuration
([`12`](12_Operator_Control_Surface.md) §3.7).

### 4.7 What breaks if C2 is violated

- **Absence becomes ambiguous.** Without `render_states[]`, a vehicle missing from the imagery could be
  one that was rendered and simply not visible, one the renderer never instantiated, or one SUMO never
  simulated. Those three have opposite implications and no consumer can separate them after the fact,
  because the manifest and the imagery are the only artifacts.
- **The corpus's own denominator is wrong by most of its mass.** Any count taken over imagery that uses
  `sumo_span_s` instead of `observed_union_s` is charged with vehicles no sensor ever saw — at the
  sizing case that is most of the simulation — so the corpus misdescribes itself and every consumer
  inherits the error.
- **Prevalence is overstated**, which bites hardest at the low base rates doc 20 §2.6 records, where a
  small error in the denominator moves the reported rate by a large factor.
- **The imagery's traffic stops being the scenario's.** A rule that drew fewer vehicles than SUMO has
  would make what the imagery holds depend on the machine rather than on the scenario, and the first
  vehicle it left out could be the one the run was built for — after which the run looks
  superficially fine. `D4.6` holds because, by default, nothing chooses. This is why a limit is never a
  default (D4.44): one a run chooses is named in its configuration, said at launch and counted in its
  record, so the run cannot look fine while it left its subject out.
- **An abrupt appearance becomes unaccounted for.** Vehicles appear and vanish at full opacity by
  decision, which is acceptable — but only because `rendered_spans[]` records exactly when. Without it,
  a body that pops into frame is indistinguishable from a detection artifact, and the observability
  accounting has no instant to attribute it to.

---

## 5. C3 — The scenario package

A scenario package is a **directory of loose files**, all of them written by the scenario compiler
(`carlacontrol.ScenarioCompiler`, `CarlaControl/scripts/compile_scenario.py`) from one specification and
one world package, and bound to each other and to that world by the digests in its lock
([`07`](07_Scenario_Authoring.md) §5.1). Loose rather than zipped because every reader takes the files as
they are: SUMO loads the `.sumocfg` and the network and route file beside it, the co-simulation session
takes the `.sumocfg` path and resolves the network the way SUMO does
([`03`](03_CoSimulation_Runtime.md) D3.28), and the operator surface reads the lock as its scenario layer
([`12`](12_Operator_Control_Surface.md) §3.6).

The generators it replaces also wrote loose files — `.net.xml`, `.rou.xml`, `.sumocfg`, `.labels.json` —
and bound them to nothing: no digest, no world, no record that `t = 0` was midnight.

### 5.1 What the world package holds, measured

`WorldPackage` writes a `.cwp` zip of STORED entries — `world.json`, `map.xodr`, `map.net.xml` and, for a
draped world, `bareearth.bin` (`CarlaNet/src/CarlaNet.Map/WorldPackage/WorldPackage.cs`, the
`ManifestEntry`, `OpenDriveEntry`, `NetworkEntry` and `GridEntry` constants) — and the world build then
publishes the authoring reference set into the same file: `areas.resolved.json`, `areas.aoi.geojson`,
`places.json` and `solar.json` ([`07`](07_Scenario_Authoring.md) §2.12). Read from
`carla/Build/world-packages/Shahid_Bahonar_Port.cwp` with `zipfile`, its `world.json` records, beyond the
origin, georeference, grid and staging fields: `SourceOsmFileName` and `SourceOsmSha256`,
`OpenDriveSha256`, `NetworkFingerprint`, `NetconvertPath`, `NetconvertVersion` (`Eclipse SUMO netconvert
1.27.0`), `NetconvertExtraArgs`, and `NetconvertArgv` — the complete argument list of the one netconvert
run that wrote both the network and the OpenDRIVE the world was made from.

Three findings from that measurement:

1. **The `.xodr` digest already exists** — `OpenDriveSha256`, written at
   `CarlaNet.Transport/CarlaClient.cs:1253`, and `SourceOsmSha256` at `:1252`. Doc 20 §7.5 and
   [doc 18 §5.5](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md) plan to *introduce* a
   world-binding digest; it is already produced. `C3` consumes it rather than inventing one.
2. **The network travels; the clipped OSM does not need to.** The package carries the SUMO network from
   the same invocation as the OpenDRIVE, and a scenario runs that network byte for byte
   ([`07`](07_Scenario_Authoring.md) D7.32). Rebuilding a network from the clipped OSM would not
   reproduce it — a second conversion with identical flags is a different graph (07 §1.3,
   `WorldPackageReader`) — so the OSM is the world build's input, and neither a scenario nor its package
   reads it (07 §2.2).
3. **The whole netconvert invocation is recorded**, `NetconvertArgv`, and what it produced is
   fingerprinted, `NetworkFingerprint`: canonical graph content, not file bytes (07 D7.15). The
   fingerprint is what binds a scenario to a world; the argument list is provenance, and it names the
   world's type map where the world has one (07 D7.33).

### 5.2 Artifact and format

- **Location:** one directory per scenario, named by the caller (`compile_scenario.py --out-dir`);
  `Build/scenarios/<scenario_id>/` by convention.
- **Files**, each named by the `scenario_id` except the network, which keeps the world's map name:

| File | Content |
|---|---|
| `<scenario_id>.lock.json` | The manifest, §5.3: every binding, digest and declaration a run needs, and the digests of the files below -- four, and a fifth where the scenario closes lanes |
| `<scenario_id>.sumocfg` | The SUMO configuration: the network and route file by relative name, `begin` 0, `end`, `step-length`, the seed and the processing options, the epoch restated in a comment |
| `<scenario_id>.rou.xml` | Vehicle types bound to measured bodies (`C1`), every actor and flow already routed, departure-sorted, in plain seconds, with no supervision |
| `<scenario_id>.add.xml` | Only where the specification declares `lane_closures`: the rerouter that closes each set of lanes for its interval, named by the configuration ([`07`](07_Scenario_Authoring.md) check 55) |
| `<MapName>.net.xml` | The world package's `map.net.xml`, byte for byte |
| `<scenario_id>.supervision.json` | The supervision plan — the annotation set, payload owned by [`06`](06_Truth_And_Annotation.md) §8.1 — carrying the resolved vocabulary it was checked against, import-flattened, with its digest ([`06`](06_Truth_And_Annotation.md) §8.7), and the digests of the files it was compiled against: `routes_digest`, `config_digest` and `additional_digest`, each the SHA-256 the lock's `files` records for that file (`additional_digest` null where there is no additional file), and `network_digest`, the network fingerprint. Every interval carries `anchor`, the events of its participant that commit its start and end, resolved against the route file this package carries — a stop's lane and position, a phase's index in the vehicle's compiled route — or null when it is declared in civil time ([`06`](06_Truth_And_Annotation.md) §3.3) |
| `<scenario_id>.resolution.json`, `.resolution.md` | What the compile resolved, and every finding ([`07`](07_Scenario_Authoring.md) §5.3). Written by every compile; the only files a refused compile writes |

- **Written whole, and only when the compile passes.** Every self-check runs on the files in memory
  before any is written (07 §5.4, the emission stage); a refused compile writes only its resolution
  report.
- **Byte-identical from the same specification, seed and world**: no file carries a timestamp, a machine
  path or the specification's file name (07 D7.30).
- **Referenced, not carried:** the world package, bound by network fingerprint and OpenDRIVE digest; the
  vehicle catalogue, bound by `catalogue_digest` and `blueprint_set_digest` and named on every emitted
  vehicle type by `carla:catalogue_digest`; and the areas of interest, which are the world package's own
  published table (`C5` §7.2) and which the supervision plan names by id.

### 5.2a The processing options, and how long a lane change takes

The compiler writes the SUMO options that decide how the traffic moves into every configuration and
records them in the lock's `traffic.processing`, rather than leaving any to SUMO's default:
`time-to-teleport` −1, `max-depart-delay` 900, `collision.action` warn, and `lanechange.duration` 3.
Why each of the first three is fixed is [`07`](07_Scenario_Authoring.md) §7.1's; the value of the last is this contract's.

> **D4.42 — a lane change takes 3 s, for every vehicle.** SUMO's default `lanechange.duration` is 0
> (`MSFrame.cpp:489`): a vehicle crosses a lane width inside one step, which renders as a sideways jump
> and spikes the reconciliation residual ([`06`](06_Truth_And_Annotation.md) §6.2, which sets it above
> zero and leaves the value here). The value is a physical one: a passenger car takes about 3 to 5 s to
> change lanes, and 3 s is taken for every vehicle until a value per vehicle class is decided.

**What SUMO does with it, read from SUMO 1.27.0 and measured through TraCI.** A change is spread over
time only where the duration is longer than the step (`MSAbstractLaneChangeModel::startLaneChangeManeuver`).
The vehicle moves across at half the two lanes' widths per duration — 1.117 m/s between 3.35 m lanes —
and SUMO keeps reporting the lane it started on until it is past halfway, then the lane it is moving to,
its lateral offset carried over to that lane (`MSLaneChanger::continueChange`). The reported position
includes the offset, and the reported angle turns with the movement (`MSVehicle::computeAngle`). The
lane-change model caps the sideways rate at 1.0 m/s plus the forward speed (the vType's
`lcMaxSpeedLatStanding` and `lcMaxSpeedLatFactor` at their defaults), so a vehicle standing in a queue
still moves across at 1.0 m/s: 5.2 % of Arapahoe's lane-change vehicle-steps are below 0.5 m/s. The
bridge renders the vehicle where SUMO has it ([`03`](03_CoSimulation_Runtime.md) §6.4).

**What it does to the shipped scenarios, measured 2026-10-01** in SUMO 1.27.0 alone over each whole run:

| Scenario | Live vehicles at 0 s: peak / median / p99 | At 3 s |
|---|---|---|
| Arapahoe underpass dwell | 440 / 338 / 427 | 441 / 345 / 431, all 7,433 inserted, none waiting |
| Gardnerville orbit | 50 / 39 / 48 | 49 / 39 / 47 |
| Bahonar pattern of life | 170 / 42 / 159 | **deadlocked** with the box widths: 457 live at 1 h against 54, 1,820 at 6 h, 7,034 vehicles discarded by `max-depart-delay` by day 2. With the body widths (2026-10-02): **170 / 42 / 159**, all 69,245 inserted |

Recompiled with the body widths without mirrors (§3.2b) on 2026-10-02, Arapahoe peaks at 461, median
344, p99 449, every vehicle inserted, none waiting, no collision; Gardnerville at 49, 39, 47.

Over 300 s of Arapahoe's morning peak, SUMO's own position jumps by more than a step's travel plus 1 m
3,667 times at 0 s — the instantaneous changes — and 16 times at 3 s, every one at the step a spread
change switches lanes between two lanes that are not parallel, eight of them inside a junction, the
largest 15.9 m.

**Why Bahonar deadlocks, measured.** The catalogue measures `vehicle.fuso.mitsubishi` at 3.93 m wide,
wider than every lane on the three networks (3.35 m). Under a spread lane change SUMO holds a vehicle on
every lane it overlaps: at 388 s the bus stops at the end of the right-hand lane of edge `168434252`,
needing the left lane for its turn, "blocked by left leader, overlapping", while the head of the left
lane holds the bus as its own leader 0.10 m ahead, and with teleporting forbidden the queue never
clears. Every duration from 1.5 s to 5 s deadlocks it, at steps from 0.05 s to 1 s; the control, the
same run at 3 s with the bus 2.5 m wide, does not (peak 157, 56 live at 1 h). The 3.93 m was the box,
mirrors included; SUMO is now given the body's 3.23 m (D4.43), and Bahonar is recompiled with D4.42 and
runs as it did before lane changes were spread.

### 5.3 Lock fields

`<scenario_id>.lock.json`, as `ScenarioCompiler._lock` writes it:

| Field | Meaning |
|---|---|
| `lock_version` | Schema shape; refused when unimplemented |
| `scenario_id`, `scenario_name` | Stable across runs. `scenario_id` is **the field the recorder accepts and is never given** — §5.6 |
| `spec_version`, `specification`, `specification_sha256` | The specification compiled, by name and digest |
| `compiler` | `name` and `version` |
| `files` | `routes`, `config`, `network`, `supervision`, and `additional` where the scenario closes lanes: each a `path` relative to the lock, and its `sha256`. The supervision plan restates the routes', configuration's and additional file's digests, so the plan and the files it was compiled against are bound both ways |
| **`world`** | `package`, `map_name`, `network_fingerprint`, `netconvert_argv`, `netconvert_version`, `opendrive_sha256`, `source_osm_sha256`, `origin_latitude`, `origin_longitude`, `georeference` — copied from the world package the specification was compiled against |
| **`catalogue`** | `catalogue_id`, `catalogue_digest`, `blueprint_set_digest`, `content_build_id` (`C1` §3.11) |
| **`vocabulary`** | `core_version`, `namespaces` with their versions, and `vocabulary_digest`, the digest of the vocabulary document the supervision plan carries |
| **`traffic`** | `sumo_seed`, `step_length_s`, `end_s`, `processing` (the SUMO options that decide how traffic moves, §5.2a), and `routed_by`: the `duarouter` release that routed, the world's converter, how the two stand by release number and whether a mismatch was accepted ([`07`](07_Scenario_Authoring.md) check 6) |
| `epoch`, `epoch_block_sha256` | The `C9` epoch object verbatim, and its digest canonicalised per §1 |
| `illumination` | The authored `C9` illumination default. An operator may override it at run start (`C9` §11.8); the run manifest records which won |
| `capture_windows` | The authored candidate windows: id, begin and end seconds, civil begin, end and date |
| `ephemeris` | The functions every declared sun was computed with |
| `illumination_label_association` | Check 41's statistic, its band table and its entry count ([`07`](07_Scenario_Authoring.md) §5.6) |

**Fields an earlier form of this contract named, and where they are.** The world's origin height and
staging rectangle are the world package's, which the lock binds. The run's fixed delta and substep count
are the run's (`C6`). `generated_at_utc` is absent on purpose: a timestamp would break byte-identity. The
clipped OSM's digest went with the OSM (§5.1 finding 2). `appearance_seed` is not declared, because SUMO's
own seeded `vTypeDistribution` draw chooses the body (07 D7.11). `area_block_sha256` and
`render_uses_vtype_colour` are not built. There is no `render_set` field: nothing in a scenario chooses
which vehicles are drawn (`C2` §4.1).

### 5.4 Validation and the mismatch tiers

Each rule states where it is enforced today. The compiler's checks are [`07`](07_Scenario_Authoring.md)
§5.2's, by id; the session's refusals are [`03`](03_CoSimulation_Runtime.md)'s.

| # | Condition | Response | Enforced |
|---|---|---|---|
| V3.1 | The world the server has loaded is not the package's — OpenDRIVE digest, origin, bare-earth record | **refuse** | At run start, by the session's loaded-world check ([`03`](03_CoSimulation_Runtime.md) §7.2, D3.26) |
| V3.2 | *Retired.* The clipped OSM's digest differs | — | The OSM is not carried (§5.1 finding 2); the network fingerprint of V3.4 is the binding |
| V3.3 | The network is in another frame: its `convBoundary`, projection or `netOffset` against the package and its OpenDRIVE | **refuse** | At compile, checks 3, 4 and 5; at run start, the session's frame check |
| V3.4 | The network the scenario runs is not the world package's, by canonical fingerprint | **refuse** | At compile, check 1 against the specification, and D7.32 for what is written; at run start, `ScenarioNetworkCheck` on the network the `.sumocfg` loads ([`03`](03_CoSimulation_Runtime.md) D3.28) |
| V3.5 | `blueprint_set_digest` differs | **refuse** | At compile, checks 14 and 15 bind every type to the catalogue's measured body. At run start, not built: the session records the catalogue it loaded and does not compare it with the lock |
| V3.6 | `catalogue_digest` differs, set digest matches | **warn**, naming every moved entry | Not built |
| V3.7 | The areas of interest differ, everything else matches | **warn** | Not built. An area edit republishes the world package's table without a rebuild and without moving the network fingerprint (07 D7.28); a recompile re-resolves every area the supervision names (check 20) |
| V3.8 | `content_build_id` differs, all digests match | **warn** | Not built |
| V3.9 | `sumo_step_s` is not a whole multiple of the run's fixed delta | **refuse** | `C6`: the run's, not the package's |
| V3.10 | A file the lock lists is absent, or its SHA-256 is not the lock's | **refuse** | Not built at run start: the session reads the `.sumocfg` and the network, not the lock |
| V3.11 | The epoch is absent, or `epoch_block_sha256` does not match the epoch as carried | **refuse** | At compile, check 33: no lock is written without an epoch. At run start the session validates the epoch it is handed (`SolarEpoch`) — the lock is a file it can read one from (`run_sumo_drive.py --epoch <scenario_id>.lock.json`) — and does not compare the digest |
| V3.12 | The epoch fails a `C9` V9.* rule | **refuse** | At compile, checks 33 and 34, through the session's own `SolarEpoch`; at run start, `SolarEpoch` again |
| V3.13 | The world reports no sun and `illumination.require_sun` is true | **refuse** | At run start, by the session; `run_sumo_drive.py --no-sun-required` is `require_sun: false` |
| V3.14 | The world's origin longitude differs from the lock's | already **refuse** by V3.1 and V3.3 | Restated because the sun's position is computed from the world's origin (§11.3) |
| V3.15 | The vocabulary document the supervision plan carries does not match `vocabulary_digest` | **refuse** | At compile, the digest is computed over exactly what is published. At run start, not built |

That tiering answers doc 20 §11 question 6 for this plan — **an area-only difference is a warning** —
and the warning is not yet built (V3.7).

**Why the epoch is bound at the refuse tier and not warned about.** The world bindings are refuse-tier
because a different world silently relocates every position (§5.7). A different epoch silently relocates
every *frame in time*: the same windows, the same vehicles, the same behaviour, rendered under a
different sun, with the sidecar faithfully recording the sun it got. Both failures are invisible in
every artifact because both sides stay internally consistent, which is the property that decides the
tier. The epoch is therefore bound as the world is — carried in the lock, digested separately, and
checked before the first tick.

### 5.5 Versioning

`lock_version` is an integer, refused when unimplemented. The network, the routes, the configuration
and the supervision plan with its vocabulary are **carried**; the world package and the catalogue are
**referenced by digest**. A scenario package is therefore reproducible from itself plus the world package
and catalogue it names, and a compile of the same specification against them gives the same bytes
(07 D7.30).

### 5.6 A defect this closes

`scenario_id` is accepted by the recorder (`CarlaNet.Recording/FrameRecorder.cs` constructor parameter,
written to the sidecar container at `CarlaNet.Recording/CotWriter.cs:45`) and **is never supplied**.
Re-resolved to the live path today: `CarlaControl/src/carlacontrol/NativeRecorder.py:96-112` passes
`run_id` and `seed` to `world.start_recording` and does not pass `scenario_id`. (Doc 20 §4.2 cites the
deleted `SCTMV.py:1472-1479` for this; the finding holds at the new location.) Every sidecar recorded
today therefore omits the scenario it was recorded under, and the run manifest joins to captures by
exactly that field. `C3` makes `scenario_id` a required lock field precisely so there is something to
pass.

### 5.7 What breaks if C3 is violated

- **A scenario runs against the wrong world and produces plausible nonsense.** Edge ids are numeric
  OSM way ids; a rebuild from a different extract can reuse them. Routes then resolve, vehicles drive,
  and every position is wrong by an amount nothing reports.
- **The coordinate identity silently stops holding.** A different origin means `sumo(x, y)` no longer
  equals `carla(x, −y)`, and every rendered pose is offset by the difference — uniformly, so it looks
  like a georeferencing error rather than a binding error.
- **A capture cannot be reproduced.** Without the lock's digests, a package re-run a year later depends on
  a world, a catalogue and a vocabulary nobody can show are the ones it was compiled against.
- **Captures cannot be joined to supervision**, because `scenario_id` is absent from the sidecar.
- **Every label in the corpus becomes an opaque string, or a differently-meaning one.** With no
  vocabulary in the package, a consumer reading `bahonar:post_unmanned` a year later has the spelling
  and nothing else — no definition, no `applies_to`, no `broader` parent to roll it up to. With the
  vocabulary carried but not digest-bound, the definitions may have been edited after the annotation
  set was compiled against them; every label still resolves, and nothing in either artifact says that
  what it resolves to has moved ([`06`](06_Truth_And_Annotation.md) §8.7).
- **The same scenario renders under a different sun on every machine.** Without the epoch in the
  package, the civil meaning of `t` lives in the author's head and in trip identifiers, and the sun
  falls back to whatever the world was spawned with — measured as local solar noon and the *host
  system date* (`CesiumHeightSampler.cpp:409`, `WorldBuilder.py:229-230`). Two runs of one package on
  two days then differ in seasonal sun angle, and nothing in either artifact says so.

---

## 6. C4 — Identity

Doc 20 §6.3 defines four identifiers. SUMO adds a fifth, and it is the one the whole behavioural record
is keyed on.

### 6.1 The five identifiers

| Identifier | Assigned by | Lifetime | Stable across runs | Carried in |
|---|---|---|---|---|
| `sumo_vehicle_id` | SUMO, from the `<flow>`/`<trip>`/`<vehicle>` id | SUMO insertion → SUMO removal | **Yes**, given a fixed `sumo_seed` and an unchanged routes file | routes file; spawn attribute `capture:sumo_id`; sidecar `_carla@sumo_id`; the SUMO-side CoT `uid` |
| `actor_id` | The CARLA server at spawn | spawn → destroy | **No** | sidecar `_carla@actor_id`; CoT `uid` as `CARLA-TRUTH-<actor_id>` (`CotWriter.cs:134`) |
| `entity_id` | The author | The scenario | **Yes** | spawn attribute `capture:entity_id`; sidecar `_carla@entity_id` |
| `instance_id` | The annotation compiler, deterministically from `scenario_id` + authored instance name | The scenario | **Yes** | annotation set; sidecar `<_supervision><annotation instance=…>` |
| `sensor_id` | The operator, in the collection configuration; `CARLA-SENSOR-<actor id>` where none is given | The capture session | **Yes, once authored** | the camera's name: every capture's file name begins with it, the platform event's `contact/callsign` in the sidecar and `callsign` in the `carla:sensor` PNG chunk, and the camera's `role_name` on the server |

**`sensor_id` today.** The shim defaults it to `platform_uid or f"CARLA-SENSOR-{camera.id}"`
(`CarlaNet/python/carlanet/__init__.py:1910`; doc 20 §6.3 cites `:1818`, re-resolved here), and a
camera is an actor, so the default is a different identifier in every run. The parameter to supply a
stable one exists and is plumbed — `CarlaControl/src/carlacontrol/NativeRecorder.py:65,106` from
`--platform-uid` (`CarlaControlArgumentParser.py:538`). What is missing is a convention requiring it
and a check. `C4` requires it: a capture session with more than one sensor **must** supply
`platform_uid` for every sensor, and the driver refuses to start otherwise.

**As built (2026-10-02): the `sensor_id` is the camera's name.** It is carried as the camera's name
rather than as `platform_uid`: `run_capture` refuses a session of several channels without one per
channel (check 11, case aside), and every recording entry point takes one — a channel's `sensor_id`,
`--camera-name` on `run_SCTMV.py`, `run_sumo_drive.py` and `orbiting_drone.py`, and `camera_name` on
the shim's `start_recording` and `spawn_camera`. Every capture is written as
`<sensor_id>_<local capture time>` (`CameraName.StillStem`), which was `SCTMV_<local capture time>`
whatever camera took it, and the platform event's callsign is the `sensor_id`, which defaulted to
`OVERWATCH` for every camera given none, so two cameras' telemetry collided on it. A camera given no
name is `CARLA-SENSOR-<actor id>`, unique on its server. The camera is spawned with the name as its
`role_name`, so any client reading the world's actors sees it: a name another camera in the world
holds is refused at spawn, and two recorders in one process cannot hold one name. The platform
event's uid stays `CARLA-SENSOR-<actor id>` unless `platform_uid` is given. CoT keeps the two apart
by design — the uid a machine identity, the callsign the label a person reads — and the actor id is
unique on the server with no client having to agree it with another, while a reader that knows the
platform event by its `CARLA-SENSOR-` prefix keeps working. What the uid does not give is stability
across runs; the name now does, in the callsign and in every file name.

### 6.2 Cardinality, and the one that surprises people

```mermaid
erDiagram
  SCENARIO ||--o{ PATTERN_INSTANCE : declares
  SCENARIO ||--|| CATALOGUE : "bound by catalogue_digest"
  SCENARIO ||--|| WORLD : "bound by world_opendrive_sha256"
  SCENARIO ||--|| EPOCH : "bound by epoch_block_sha256"
  EPOCH ||--o{ CIVIL_INSTANT : "t maps to exactly one"
  CIVIL_INSTANT ||--|| SOLAR_STATE : "one sun, audited to a tolerance"
  PATTERN_INSTANCE ||--|{ PARTICIPANT : has
  PARTICIPANT }o--|| ENTITY : "entity_id"
  ENTITY ||--|| SUMO_VEHICLE : "1:1 for authored vehicles"
  SUMO_VEHICLE }o--|| VTYPE : "realised type"
  VTYPE ||--|| BLUEPRINT : "carla:blueprint"
  SUMO_VEHICLE ||--o{ CARLA_ACTOR : "0..n renderings"
  CARLA_ACTOR ||--o{ TRUTH_EVENT : "per capture tick"
  SENSOR ||--o{ CAPTURE : produces
  CAPTURE ||--|| SOLAR_STATE : "stamped with the tick's sun"
  CAPTURE ||--|{ TRUTH_EVENT : contains
  RUN_MANIFEST ||--|{ RENDER_STATE : records
  RENDER_STATE }o--|| SUMO_VEHICLE : "sumo_vehicle_id"
```

**What this diagram deliberately does not contain.** There is no entity for a consumer's track and no
association edge to `TRUTH_EVENT`. A consumer's track is **not an entity of this identity model**: we
never see one, we form no such relation (`D4.27`), and asserting a cardinality for something outside the
system is exactly the over-specification `D4.34` forbids — it would have to change the moment the
external chain was replaced. The rule by which supervision *would* be carried onto such a track is
documented at `C8` §10.6 and performed nowhere.

> **D4.8 — `sumo_vehicle_id` → `actor_id` is one-to-many, not one-to-one.** A vehicle released by `C2`
> and later re-admitted is a **new CARLA actor with a new `actor_id`** and the **same**
> `sumo_vehicle_id`. Any consumer that assumes one actor per SUMO vehicle will silently merge two
> renderings into one track, or drop the second.

`rendered_spans[]` in `render_states[]` (`C2` §4.5) carries one entry per rendering with its
`actor_id`, so the mapping is always recoverable. This is why `render_states[]` is a required artifact
and not a diagnostic.

**The other cardinalities, stated:** `entity_id` → `sumo_vehicle_id` is 1:1 and exists only for
authored vehicles; an ambient flow vehicle has a `sumo_vehicle_id` and **no** `entity_id`.
`instance_id` → `entity_id` is 1:n. `sensor_id` → capture is 1:n. `actor_id` → truth event is 1:n, one
per capture tick the actor was present.

**Solar state is scene-scoped and has no identity of its own.** There is exactly one solar state per
tick and it is identical for every actor, every sensor and every capture at that tick — it is written
once on the world-observer snapshot (`WorldObserver.cpp:326-339`) and read from the cache by whichever
recorder needs it (`FrameRecorder.cs:160-162`). That is why it is not an identifier and why it cannot
encode anything about a particular object, which is the structural fact `C8` §10.4a's ruling rests on.

### 6.3 Naming rules

| Identifier | Grammar | Why |
|---|---|---|
| `sumo_vehicle_id` | `[A-Za-z0-9_-]+(\.[0-9]+)?` | SUMO appends `.N` to a flow id to name the vehicles it generates (measured: `<flow id="corridor_d0_p0_h0">` yields `corridor_d0_p0_h0.0`). A flow or trip id containing `.` therefore makes the suffix ambiguous, and the existing bridge already splits on the last `.` to recover the flow (`CarlaControl/src/carlacontrol/SumoCotBridge.py:329`). **A flow, trip or vehicle id must not contain `.`** |
| `entity_id` | `[a-z][a-z0-9_]{0,63}` | Referenced by annotations and by name-convention shorthand |
| `instance_id` | `[a-z][a-z0-9_]{0,63}` | Derived deterministically, so no counter and no timestamp |
| `sensor_id` | `[A-Za-z0-9_.-]{1,63}`, not a Windows device name (CON, PRN, AUX, NUL, COM0–9, LPT0–9, alone or before a dot), not ending in `.`, and not `CARLA-SENSOR-<digits>` | Authored; freer because it may follow an external naming scheme. It is the camera's name, so it begins every capture's file name and names the channel's directory: `:` was dropped from the grammar on 2026-10-02 because no Windows file name holds it, and the rest is the rule every camera name meets (`CameraName`), whose default form is reserved for cameras given no name. Unique within a session without regard to case |

### 6.4 Spawn attributes, and the verification of doc 20 §4.5

Doc 20 §4.5 claims custom spawn attributes are accepted unvalidated by the server and preserved
through record and replay. **The claim holds.** Verified at four points:

1. **RPC → engine, unfiltered.** `rpc::ActorDescription`'s conversion to `FActorDescription` copies
   *every* attribute into `Variations` with no lookup against the blueprint's declared variations:
   `for (const auto &item : attributes) { Description.Variations.Emplace(ToFString(item.id), item); }`
   (`LibCarla/source/carla/rpc/ActorDescription.h:47-56`).
2. **Spawn stores the description as received.** `spawn_actor` passes it straight to
   `Episode->SpawnActorWithInfo` (`Unreal/.../Carla/Server/CarlaServer.cpp:1311-1332` — doc 20 §4.5
   cites `:1139-1146`, re-resolved here; the claim itself verifies), and
   `FActorRegistry::MakeActorInfo` does `Info->Description = std::move(Description)` then
   `Info->SerializedData.description = Info->Description`
   (`Unreal/.../Carla/Actor/ActorRegistry.cpp:161,172`). So the custom attribute is returned to
   **every** client by `SerializeActor` (`Unreal/.../Carla/Game/CarlaEpisode.cpp:253-270`), which is how
   the truth producer would see it — it already reads attributes by name
   (`CarlaNet.Recording/VehicleTelemetryService.cs:99-107`).
3. **The recorder log preserves them.** `ACarlaRecorder::CreateRecorderEventAdd` copies **all**
   variations into the actor-add event, skipping only those with an empty id
   (`Unreal/.../Carla/Recorder/CarlaRecorder.cpp:753-770`).
4. **Replay restores them.** `CarlaReplayerHelper::ProcessReplayerEventAdd` rebuilds
   `ActorDesc.Variations` from every logged attribute
   (`Unreal/.../Carla/Recorder/CarlaReplayerHelper.cpp:191-207`).

**One asymmetry, also verified.** The Python shim refuses to set an attribute the blueprint does not
declare: `if name not in self._attrs: raise KeyError(...)`
(`CarlaNet/python/carlanet/__init__.py:610-613`). So custom attributes must be added from C#, where
`BlueprintChooser.Describe` already assembles the attribute list
(`CarlaNet.Scenario/BlueprintChooser.cs:33-39`), or the shim needs a bypass. The playback bridge is the
C# path, so this costs nothing where it is needed.

**The attributes, namespaced `capture:` so they cannot collide with an engine-declared id (all of which
are bare lowercase words):**

| Attribute | Type | Req. | Meaning |
|---|---|---|---|
| `capture:sumo_id` | String | yes | The `sumo_vehicle_id` this actor is a rendering of |
| `capture:class_id` | String | yes | The catalogue class (`C1`) |
| `capture:entity_id` | String | no | Present only for authored vehicles |
| `capture:instance_id` | String | no | Present only for a participant in exactly one instance; multi-instance participation lives in the dynamic registry (doc 20 §7.3), not here |
| `capture:role` | String | no | The participant's role in the instance |
| `role_name` | String | yes | **`sumo`** for SUMO-driven vehicles |

> **D4.9 — `role_name` is a provenance field and carries the authority class.** `autopilot` for .NET
> traffic-manager traffic (`CarlaControl/src/carlacontrol/TrafficController.py:484`), `scenario` for
> storyboard entities, **`sumo`** for SUMO-driven ones. `hero` and `ego` are never used here: they
> carry traffic-manager, ROS2 and replayer behaviour (doc 20 §4.3), so using them would change what the
> simulation does.

`role_name` is a declared variation on every definition with
`bRestrictToRecommended = false` (`ActorBlueprintFunctionLibrary.cpp:203-214`, recommended values
replaced with `{autopilot, scenario, ego_vehicle}` at `:846-847` via `:225-237`), so `sumo` is accepted
by both the C# and the Python paths.

**As built (2026-10-01).** Every body the pool spawns carries `role_name` `sumo`
(`VehicleBodyPool.RoleName`; `CarlaClientWorld.Spawn` sends it in place of the definition's default,
and adds it to a definition that declares none). Before, every body took the default, `autopilot`,
and the truth record named the traffic manager as the driver of every SUMO vehicle — 152 of 152 in a
Bahonar sidecar. The recorded sidecar and the live pull read the attribute from the actor's
description, so both now say `sumo`. The `capture:*` attributes are not stamped, and `capture:sumo_id`
cannot be as the table has it: under the pool ([`03`](03_CoSimulation_Runtime.md) D3.9) one body draws
a succession of SUMO vehicles, and an attribute is fixed at spawn. The vehicle a body draws is named
per frame instead — in the session's render set for the recorder beside it (03 §8.9, D3.37), and on
the world-observer snapshot for every other reader (03 D3.39) — so V4.5 holds for `role_name` and its
`capture:sumo_id` half, with V4.6, is met by the per-frame naming rather than by an attribute.

### 6.5 The identity lifecycle

```mermaid
stateDiagram-v2
  [*] --> Declared : author writes a flow/trip in the routes file
  Declared --> Simulated : SUMO inserts it, assigns sumo_vehicle_id
  Simulated --> Rendered : C2 admits it; server assigns actor_id;\ncapture:sumo_id stamped at spawn
  Simulated --> SimulatedOnly : C2 does not draw it\n(outside_window / no_blueprint / unknown_extent / spawn_failed)
  Rendered --> Observed : projects inside a sensor frustum;\ntruth event written with both ids
  Observed --> HandedOver : C8 — imagery and truth leave this system
  HandedOver --> [*] : what a consumer does with it, including\nsupervision transfer by the documented\nrule (C8 §10.6), happens outside this system
  Observed --> Rendered : leaves frame, still rendered
  Rendered --> Released : C2 eviction E3, the window closed — body released, release instant recorded
  Released --> Rendered : re-admitted — NEW actor_id, SAME sumo_vehicle_id
  Rendered --> Removed : SUMO removes it (E1)
  Released --> Removed : SUMO removes it while released
  SimulatedOnly --> Removed : SUMO removes it
  Removed --> [*] : render_state closed in the manifest
```

`HandedOver` is this system's last state, and a vehicle reaches it identically whether the records were
written to a corpus or emitted live — the state machine has no delivery-mode branch, which is `D4.29`
seen from the identity side. Everything past `HandedOver` belongs to whoever received it
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b, §3c), and `C8` §10.6 documents the transfer rule without
performing it.

### 6.6 Join rules

| From | To | Key | Notes |
|---|---|---|---|
| truth event | SUMO behaviour record | `sumo_vehicle_id` | The primary join. Requires `capture:sumo_id` to reach the sidecar |
| truth event | truth event, across ticks | `actor_id` | Intra-run, within one rendering only |
| truth event | annotation set | `entity_id`, then `instance_id` | Cross-run comparison rests on `entity_id`; `actor_id` cannot |
| capture | capture, across sensors | `tick` | **Not filenames.** The recorder's file stem is the camera's name and local wall-clock time to the millisecond, and two cameras sample on their own phase, so the same simulated instant carries different names in different directories (doc 20 §7.5; the tick is on every capture) |
| an external consumer's track | truth | position and time, **never uid** | The rule `C8` §10.6 *documents* and this pipeline does not perform. [doc 09 §9](../../Findings/09_Telemetry_CoT_Contract.md) fixes it; `C8` §10.8 is why the corpus keeps an identifier join unavailable |
| run manifest | captures | `run_id` + `scenario_id` | Both must be supplied — §5.6 |
| render state | truth | `sumo_vehicle_id`, then `rendered_spans[].actor_id` | The only way to recover which actor was which rendering |

### 6.7 Validation, failure, versioning

| # | Rule | Response |
|---|---|---|
| V4.1 | Every flow/trip/vehicle id matches the grammar and contains no `.` | refuse at scenario build |
| V4.2 | `entity_id` unique across the scenario | refuse |
| V4.3 | Every annotation's `entity_id` names a declared vehicle | refuse |
| V4.4 | `sensor_id` supplied explicitly when more than one collection sensor is configured | refuse at run start |
| V4.5 | Every spawned SUMO-driven actor carries `capture:sumo_id` and `role_name="sumo"` | assertion in the bridge; a spawn without them is a bug, not a condition |
| V4.6 | No two rendered actors carry the same `capture:sumo_id` at the same tick | refuse the second spawn; a duplicate means the release path leaked |

The attribute namespace is versioned by `scenario_package_version`. A new `capture:*` key is additive
and inert to older consumers, because the engine ignores attributes it does not recognise (§6.4).

### 6.8 What breaks if C4 is violated

- **Behavioural truth and imagery truth cannot be joined.** SUMO knows what every vehicle did; CARLA
  knows what every actor looked like. Without `capture:sumo_id` in the sidecar there is no key between
  them, and the entire behavioural annotation is unreachable from the imagery.
- **A re-rendered vehicle becomes two unrelated tracks**, or one track with a gap attributed to
  occlusion.
- **Cross-run comparison fails**, which is the whole point of a parameter sweep: one authored pattern,
  many variants, diffed. `actor_id` is assigned at spawn and differs every run.
- **Multi-camera captures cannot be paired**, if anything is built expecting filenames to correspond.
- **Supervision silently disappears on replay** unless identity is on a spawn attribute — the reason
  §6.4's verification matters. An in-process registry cannot survive a replay; a spawn attribute does.

---

## 7. C5 — Areas of interest

Doc 20 §8 specifies GeoJSON beside the OSM extract, validated at build, held in the world on a
dedicated actor with a Set/Get RPC pair mirroring staging bounds. Restated here as a contract and
extended, because a SUMO scenario author sites behaviour on **edges**, not on metres.

**Built:** the source, its validation at world build, the resolution to CARLA-local metres and to SUMO
lanes, the resolved table in the world package, and readers in Python and C#. The classes are
`carlacontrol.AreaOfInterestSource` (§7.1, V5.1–V5.4), `AreaOfInterestResolver` (§7.2–§7.3,
V5.5–V5.7, V5.12), `AuthoringReferenceSet` (publication), `WorldPackageReader` and
`CarlaNet.Map.WorldPackage` (reading, V5.11). **Not built:** the engine's areas-of-interest actor and
its RPC pair, specified in §7.4 so they can be; the scenario-build rules V5.8–V5.10, which are
stage F's; and the `<userData>` copy in the `.xodr` (§7.6).

### 7.1 Artifact: the source definitions

- **Name and location:** `<extract>.aoi.geojson`, beside the OSM extract given to `--osm`, discovered
  by name; overridden by `--aoi <file>` (`CarlaControl/src/carlacontrol/CarlaControlArgumentParser.py`),
  mirroring how the extract itself is supplied. No file means no areas.
- **Format:** GeoJSON, RFC 7946. A `FeatureCollection`. WGS84 by mandate of the standard, which is the
  datum this project locked end to end.

| Shape | Encoding | Use |
|---|---|---|
| Polygon | `Polygon` / `MultiPolygon` geometry, holes allowed in either | A car park, a block, a compound — anything whose extent matters |
| Centre and radius | `Point` geometry with `properties.radius_m` | A standoff ring, a site whose extent is unknown. A circle is not a GeoJSON primitive, so this is the conventional encoding |

**Per-feature `properties`:**

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `id` | string | — | yes | Stable, referenced by scenarios and annotations. `[a-z][a-z0-9_]{0,63}`. A Feature-level `id` member is not read |
| `name` | string | — | yes | Human; non-empty |
| `kind` | string | — | no | A declared term, for stratification. Carried through unchecked: the vocabulary's term list is not settled ([`13`](13_Work_Breakdown.md) §13.5) |
| `radius_m` | number | m | Point only | Must be > 0. **Refused on a polygon**, which would ignore it |

Any other property is warned about and not carried. A third position component (altitude) is warned
about and ignored. A `crs` member is warned about — RFC 7946 removed it — and positions are read as
WGS84 degrees regardless. Heights are deliberately absent: these are ground footprints and vehicles
are on the ground.

> **The ordering trap.** GeoJSON positions are **`[longitude, latitude]`**. Every internal signature
> here is the opposite: `Geodesy.GeodeticToCarlaLocal` takes a `GeoLocation(Latitude, Longitude,
> Altitude)` (`CarlaNet/src/CarlaNet.Types/Geom/Geodesy.cs:104`, the record at
> `CarlaNet.Types/Geom/GeoLocation.cs`). Every reader must transpose, and §7.5 requires the validator
> to diagnose the transposition by name. The two Python conversions the resolver uses —
> `SumoNetworkQuery.to_sumo` and `GeodeticFrame.to_carla` — take `longitude` and `latitude` as
> keyword-only arguments, so a positional call cannot hide the swap.

### 7.2 Artifact: the resolved table

The source file is geographic. Two consumers need it in other frames, and neither may re-implement a
projection.

- **Name and location:** `areas.resolved.json` in the **world package**, beside `areas.aoi.geojson`,
  the source carried byte for byte ([`07`](07_Scenario_Authoring.md) §2.12). A scenario package does not
  copy them: the compiler reads the world package's table, the supervision plan names areas by id, and
  the lock binds the world package (`C3` §5.2).
- **Written by:** the world build, after `CarlaNet.Map` writes the package, with the network and the
  manifest both in hand (`carlacontrol.AuthoringReferenceSet`); and by
  `CarlaControl/scripts/publish_reference_set.py` into an existing package.
- **Always written.** A world with no areas declared carries a table with an empty `areas` list and an
  empty `source_sha256`, so "no areas" reads differently from "published before areas existed", which
  carries no table at all.
- **Deterministic.** No timestamp; keys sorted; areas in source order; lanes and edges in UTF-8 id
  order. The same GeoJSON against the same world gives the same bytes.

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `resolved_version` | integer | — | yes | Schema shape. `1`; a reader refuses any other |
| `source_file_name` | string | — | yes | The GeoJSON's file name; empty when none was declared |
| `source_sha256` | string | — | yes | Digest of the `.aoi.geojson` bytes this was resolved from; the digest a scenario lock would bind as `area_block_sha256`, which is not built (`C3` §5.3); empty when none was declared |
| `world_map_name` | string | — | yes | `MapName` from the world package |
| `world_georeference` | string | — | yes | The proj string, copied from the world package |
| `world_origin_latitude`, `world_origin_longitude` | number | ° | yes | The origin the geographic frame was held at |
| `network_fingerprint` | string | — | yes | `NetworkFingerprint` of the network the lanes were read from |
| `near_m` | number | m | yes | The distance that made a lane `near` (§7.3) |
| `frame` | object | — | yes | How the areas were placed: `carla_from_sumo` (`"carla(x, y) = sumo(x, -y)"`), `projected_by` (the SUMO release and call), `net_offset_m`, `geodesy_agreement_limit_m`, `geodesy_worst_residual_m` (V5.12) |
| `areas[]` | array | — | yes | One per feature, in source order |
| `areas[].id`, `.name`, `.kind` | string | — | yes | From `properties`; `kind` is `null` when undeclared |
| `areas[].geographic` | object | — | yes | The source geometry verbatim, `[lon, lat]` |
| `areas[].carla_local` | object | m | yes | GeoJSON-shaped, in CARLA-local metres to the millimetre: `{"type": "Polygon", "coordinates": [[[x, y], …], …]}`, the same for `MultiPolygon`, or `{"type": "Circle", "centre": [x, y], "radius_m": r}` |
| `areas[].envelope_carla_m` | array | m | yes | `[min_x, min_y, max_x, max_y]` in CARLA-local metres |
| `areas[].sumo` | object | — | yes | §7.3 |
| `areas[].warnings` | array of string | — | yes | V5.5–V5.7 as they fired for this area, each prefixed with its rule id |

### 7.3 Resolution to SUMO edges and lanes

> **D4.10 — an area resolves to a lane-and-position table, not to a list of edge ids.** An edge id
> alone cannot be turned into a `<stop>`; `laneId`, `startPos` and `endPos` can.

```
areas[].sumo = {
  "edges": [ { "edge_id": "26413459", "containment": "crossing" }, … ],
  "lanes": [ { "lane_id": "26413459_0", "edge_id": "26413459",
               "containment": "crossing", "s_begin_m": 33.9, "s_end_m": 63.94,
               "intervals_m": [[33.9, 63.94]],
               "allowed_vclasses": ["pedestrian", "delivery", "bicycle"] },
             { "lane_id": "-26413425#3_0", "edge_id": "-26413425#3",
               "containment": "near", "distance_m": 37.79,
               "allowed_vclasses": ["pedestrian", "delivery", "bicycle"] }, … ]
}
```

That is Bahonar's guard tower 3 as a 25 m circle: its scenario parks the guard at position 58.9 on
`26413459` (`CarlaControl/scripts/make_bahonar_scenario.py`, `TOWER_POSTS[3]`), and the contained
stretch begins at 58.9 − 25 = 33.9.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `lane_id` | string | — | SUMO lane id, `<edge>_<index>` |
| `edge_id` | string | — | The lane's edge |
| `containment` | string | — | `inside` \| `crossing` \| `near` |
| `s_begin_m`, `s_end_m` | number | m | Bounds of the contained portion, in SUMO lane-position metres — the values a `<stop startPos endPos>` takes. Absent for `near` |
| `intervals_m` | array of `[s0, s1]` | m | Every contained stretch, in lane-position metres. More than one when the lane leaves the area and comes back. Absent for `near` |
| `distance_m` | number | m | For `near` only: the shortest distance from the lane to the area |
| `allowed_vclasses` | array of string | — | The classes SUMO admits on the lane, `traci.lane.getAllowed`'s expansion of the lane's `allow`/`disallow`, in SUMO's order; `["all"]` for a lane declaring neither that SUMO reports as unrestricted |

**The resolution rule, stated so two implementations agree:**

1. Read every non-internal lane's shape polyline from the world's `map.net.xml`. Shapes are in the
   network's projected metres.
2. Convert each area vertex (a circle's centre) into that frame **through SUMO's own projection**:
   `traci.simulation.convertGeo(lon, lat, fromGeo=True)` against a SUMO process holding the world's own
   network (`carlacontrol.SumoNetworkQuery`) — the transform, and the PROJ, that placed every lane. No
   CARLA server is involved; the process never steps. `sumolib`'s offline `convertLonLat2XY` is not
   used: it needs `pyproj`, which is not a dependency here and would bring a second PROJ build. Measured:
   starting SUMO on the three shipped networks takes 0.53–0.68 s, a conversion about 60 µs.
3. CARLA-local metres are the co-simulation bridge's identity, `carla(x, y) = sumo(x, −y)`. An area, a
   lane and a rendered road are then in one frame by construction.
4. Clip each lane's shape against the area's boundary: split every segment where it meets a ring edge
   (or the circle), and keep the pieces whose midpoint is inside, even-odd over all rings of a
   polygon so a hole is outside, and inside any member of a `MultiPolygon`. A lane is `inside` when the
   kept pieces cover its whole length, `crossing` when they cover part, and `near` when there are none
   but its shape passes within `near_m` of the area — measured on the shape, not on its bounding box.
5. Positions are the kept pieces' arc lengths scaled by the lane's `length` over its shape length,
   which is how SUMO maps a position onto a shape. *Measured:* the two lengths differ by up to
   **5.76 m** on Arapahoe (`223207869#0_1`, 204.10 against 209.86) and 2.37 m on Bahonar, and by more
   than 0.1 m on 151 of Arapahoe's 657 normal lanes, so an unscaled arc length would put a stop metres
   from the area it was sited in. Rounded to the centimetre, as SUMO writes positions.
6. An edge is `inside` when all its lanes are listed and all are `inside`; `crossing` when any listed
   lane is `inside` or `crossing`; `near` otherwise. A lane of the edge that is not listed at all —
   further than `near_m` — counts as disagreeing.

**`near_m` = 50 m**, the resolver's default and recorded in every table. [`10`](10_Scale_And_Performance.md)
does not value it. It is a guess, on the reasoning that a vehicle on a lane within 50 m of an area is
plausibly about to interact with it, and it is overridable per publication.

### 7.4 At runtime in the world

Doc 20 §8.4's precedent is exact and is copied rather than redesigned. Staging bounds are held on a
dedicated, geometry-free, non-ticking actor with a blueprint-library accessor
(`Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/Source/CesiumCarlaBridge/Public/StagingBounds.h:21-65`)
and a pair of RPCs bound side by side — `set_staging_bounds` and `get_staging_bounds` at
`Unreal/.../Carla/Server/CarlaServer.cpp:831-865` — with shim wrappers beside them. The shape
[`05`](05_CarlaNet_Capability_Audit.md) §11 describes — flat primitives over `R<T>`, a tagged holder
actor, empty-result-means-absent — is followed exactly, and no LibCarla file is touched.

**Not built.** The engine side belongs to the Unreal plugin, which another engineer holds; this is the
interface it is to be built to.

**What crosses the RPC:** ids and geometry **already resolved to CARLA-local metres**, as flat arrays
the engine can draw without parsing anything, plus the resolved table itself as **one opaque string**
the engine stores and returns byte for byte. Never the GeoJSON, and nothing the engine must parse:
sending either would put a JSON parser and a second geodesy implementation in the engine. The opaque
table is what lets a client that did not build the world — the truth producer, an attached session —
recover the SUMO lanes and the provenance, not just the outlines.

**The engine.** In `CesiumCarlaBridge`, beside `StagingBounds.h`:

```cpp
/** Holder for the world's areas of interest. No geometry, no tick, hidden; tagged "areas_of_interest". */
UCLASS() class CESIUMCARLABRIDGE_API AAreasOfInterestActor : public AActor {
  UPROPERTY() FString ResolvedJson;        // areas.resolved.json, verbatim; opaque to the engine
  UPROPERTY() FString ResolvedSha256;      // lowercase hex SHA-256 of ResolvedJson's UTF-8 bytes
  UPROPERTY() TArray<FString> Ids;         // per area, in table order
  UPROPERTY() TArray<double>  RadiiMeters; // per area: > 0 a circle, 0 a polygon
  UPROPERTY() TArray<int32>   RingCounts;  // per area: rings it holds (1 for a circle)
  UPROPERTY() TArray<int32>   RingRoles;   // per ring: 0 exterior (starts a polygon), 1 hole of it
  UPROPERTY() TArray<int32>   RingSizes;   // per ring: vertices, NOT repeating the first
  UPROPERTY() TArray<double>  XY;          // per vertex: x, y interleaved, CARLA-local metres
};

UCLASS() class CESIUMCARLABRIDGE_API UAreasOfInterest : public UBlueprintFunctionLibrary {
  /** Record (or replace) the areas. Destroys any existing holder first. Returns the actor, or
      nullptr -- leaving any existing record untouched -- when there is no world or the arrays are
      inconsistent (below). */
  static AAreasOfInterestActor* Set(UObject* WorldContextObject, const FString& ResolvedJson,
      const FString& ResolvedSha256, const TArray<FString>& Ids, const TArray<double>& RadiiMeters,
      const TArray<int32>& RingCounts, const TArray<int32>& RingRoles,
      const TArray<int32>& RingSizes, const TArray<double>& XY);
  /** Read the record. False (outputs untouched) when this world has none -- any world loaded
      rather than generated. */
  static bool Get(UObject* WorldContextObject, FString& OutResolvedJson, FString& OutResolvedSha256);
};
```

`Set` refuses — returns `nullptr`, logs which rule, keeps any existing record — unless:
`Ids`, `RadiiMeters` and `RingCounts` have one element per area; `RingRoles` and `RingSizes` have
`sum(RingCounts)` elements; `XY` has `2 × sum(RingSizes)`; a circle has `RadiiMeters > 0`, one ring of
role 0 and size 1; a polygon has `RadiiMeters == 0`, its first ring of role 0 and every ring of size
≥ 3; every value in `XY` and `RadiiMeters` is finite. The engine does not check the digest or parse the
table; the client does both.

**The RPCs,** bound beside `set_staging_bounds` / `get_staging_bounds` in `CarlaServer.cpp`:

```cpp
BIND_SYNC(set_areas_of_interest) << [this](
    std::string resolved_json, std::string resolved_sha256,
    std::vector<std::string> ids, std::vector<double> radii_m,
    std::vector<int32_t> ring_counts, std::vector<int32_t> ring_roles,
    std::vector<int32_t> ring_sizes, std::vector<double> xy) -> R<bool>;
    // RESPOND_ERROR on no world, or when UAreasOfInterest::Set refuses; true otherwise.

BIND_SYNC(get_areas_of_interest) << [this]() -> R<std::vector<std::string>>;
    // {resolved_sha256, resolved_json}; an EMPTY vector for a world with no record.
    // RESPOND_ERROR only on no world.
```

An empty table (no areas declared) is set with all arrays empty; `get` then returns the two strings,
distinguishing "generated with no areas" from "loaded, no record".

**The client** does the flattening and the checking, once:

- `CarlaClient.SetAreasOfInterestAsync(string resolvedJson) → Task<bool>` parses the table with
  `System.Text.Json`, flattens `areas[].carla_local` into the arrays above (a polygon's rings in order,
  each without its closing repeat; a `MultiPolygon`'s polygons in order), computes the SHA-256 and calls
  `set_areas_of_interest`.
- `CarlaClient.GetAreasOfInterestAsync() → Task<string?>` calls `get_areas_of_interest`, returns
  `null` for an empty vector, recomputes the digest of the returned table and throws on a mismatch.
- Shim: `world.set_areas_of_interest(resolved_json: str) -> bool` and
  `world.get_areas_of_interest() -> dict | None`, the parsed table.
- **Call site:** the world build, straight after publishing the reference set — `WorldBuilder` reads
  `areas.resolved.json` from the package it just wrote (`WorldPackageReader.areas_of_interest`, which
  applies V5.11) and sets it. A world whose areas were refused sets the empty table.

**The engine's own use** is modest and worth having: a debug draw of the outlines, so an operator can
see a mis-sited area in the viewport, which is how one gets noticed at all. It reads only the flat
arrays.

### 7.5 Validation and failure modes

Checked at world build (`W`), at scenario package build (`S`), when a package is read (`P`), or at run
start (`R`). **Built** marks what exists; V5.8–V5.10 are stage F's.

| # | Rule | Where | Response | Built |
|---|---|---|---|---|
| V5.1 | Ids present in `properties`, match `[a-z][a-z0-9_]{0,63}`, unique. The pattern admits lower case only, so no two ids differ only in case | W | refuse, naming both features for a duplicate | yes |
| V5.2 | Rings closed, ≥ 4 positions, ≥ 3 distinct vertices, not crossing or touching themselves, enclosing ≥ 1 m²; a hole inside its exterior and not crossing or touching another ring of its polygon; `radius_m` a number > 0 on a Point, absent elsewhere | W | refuse | yes |
| V5.3 | Positions are WGS84 degrees, and the envelope (a circle's widened by its radius) intersects the OSM `<bounds>` — or, for an extract with none, the network's `origBoundary` once the world exists | W | refuse if disjoint; "not projected metres" when out of degree range | yes |
| V5.4 | **Transposition check** — if the envelope is disjoint from the bounds but *would* intersect with every position's components swapped, the message must say so by name: "positions appear to be `[latitude, longitude]`; GeoJSON requires `[longitude, latitude]`" | W | refuse, with that message | yes |
| V5.5 | Wholly inside the staging rectangle | W | warn if it crosses the edge, or lies wholly outside | yes |
| V5.6 | Not wholly inside the staging ring (the margin band) | W | warn — traffic enters and exits there (`TrafficController.in_ring`) | yes |
| V5.7 | Some lane admitting a four-wheeled road class lies inside, crosses, or passes within `near_m` of the area | W | warn — an area no vehicle can reach produces zero relations and reads as a broken pipeline | yes |
| V5.8 | An area referenced by a scenario for **siting behaviour** resolves to ≥ 1 lane with `containment` `inside` or `crossing` | S | **refuse** — this is the case V5.7 only warns about, promoted because the scenario now depends on it | no |
| V5.9 | Every lane an area sites behaviour on permits the referencing class's `sumo_vclass` — in the scenario's own network, since a scenario may rewrite permissions | S | **refuse**, naming the vClass and the lane. Measured relevance: the fence workflow sets private edges to allow only `army authority`, so a `passenger` flow sited there cannot run | no |
| V5.10 | Every `aoi` reference in an annotation names a declared area | S | refuse | no |
| V5.11 | `source_sha256` in the resolved table matches the GeoJSON carried beside it — empty when none is carried | P, S, R | refuse | yes, both readers |
| V5.12 | Every area vertex placed by SUMO's projection (§7.3), with y negated, lies within **0.05 m** of the same vertex placed by `GeodeticFrame` — CarlaNet's `Geodesy`, the frame the telemetry, the drape and the Cesium imagery share | W | refuse the areas; the rest of the reference set is published | yes |

**V5.12 compares two implementations, not one with itself.** CARLA-local metres are derived from SUMO's
by the identity, so checking the identity on the published numbers would pass by construction. What can
diverge is the network's frame and the world's geographic frame. *Measured* over the three shipped
worlds' whole staging rectangles, the two agree to 0.005 mm (Gardnerville), 0.010 mm (Arapahoe) and
0.397 mm (Bahonar, corners 4 km from the origin), so the limit has two orders of magnitude of headroom.
It fires on a manifest whose origin is 1 × 10⁻⁶ ° (0.11 m) from the network's, and on a network carrying
a road offset (`netOffset` non-zero): there the network is shifted from the geographic frame on purpose,
and an area drawn on the imagery and the same area placed on the network sit apart by the offset, so
which one the author meant is not decidable here.

**V5.7's classes.** `private`, `emergency`, `authority`, `army`, `vip`, `passenger`, `hov`, `taxi`, `bus`,
`coach`, `delivery`, `truck`, `trailer`, `evehicle`, `custom1`, `custom2`: SUMO's road classes without
pedestrians (brief decision 5) and two-wheelers (`D4.40`). Bahonar's private roads admit
`pedestrian delivery bicycle` in the world network, which `delivery` makes reachable.

**Failure modes not covered by a rule:**

- An area whose `kind` is not a declared term: carried through unchanged. There is no term list to
  check it against yet; when there is, this becomes a warning. Stratification terms are cheap to add
  and expensive to rename.
- An area edit with no geometry change: **warn only**, by the digest tiering of `C3` §5.4 V3.7 — not
  built; a recompile re-resolves every area the supervision names.
- A property the validator does not read: warned about and not carried.

**What the checks cannot see.** Whether an area is where the author meant: a polygon validated and
placed exactly as written can still be drawn around the wrong building. The permissions reported are
the world network's, not a scenario's rewritten ones (V5.9 is the scenario's check). V5.1–V5.4 run in
degrees, which preserves every topological property they test at the scale of one world; the square-
metre floor converts with a spherical approximation used only to reject the degenerate.

### 7.6 Versioning

`resolved_version` on the resolved table, `1`; both readers refuse any other value rather than read a
table in part. The source GeoJSON is versioned by its digest alone, which is what `area_block_sha256`
carries. Areas should additionally be written into the generated `.xodr` under `<header><userData>`,
where the build recipe is also planned to go, so the world carries its own area definitions and they
cannot be separated from it. Neither the recipe nor a `<userData>` emitter exists yet — a search of
`CarlaNet/src` finds no `<userData>` writer — so these are new together.

### 7.7 What breaks if C5 is violated

- **An annotation names a place only the author can see.** "Circling `school_lot`" is unverifiable
  unless `school_lot` exists as truth, and the corpus then contains a label nothing can be checked
  against.
- **Behaviour is welded to one build's road numbering.** Authored as lane positions rather than as an
  area reference, every scenario has to be rewritten when the world is rebuilt —
  [doc 18 §2.3](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)'s portability failure.
- **The whole ambient population stays unstratifiable.** Area relations computed for every vehicle turn
  hundreds of unlabelled vehicles into usable negatives at no authoring cost, and are the mechanism for
  auditing them for accidental positives (doc 20 §8.1).
- **The transposition error goes undiagnosed.** `[lat, lon]` instead of `[lon, lat]` puts a Denver area
  in the Indian Ocean or, worse, somewhere plausible. It is the single most likely authoring mistake
  and it is cheap to name.
- **A scenario sites behaviour on a lane its vehicle class cannot use**, and the flow silently fails to
  route — which surfaces as "no valid route" at SUMO load if you are lucky, and as a missing vehicle if
  you are not.
- **A stop is sited metres from its area**, when a position is taken as arc length along the shape
  rather than as a SUMO lane position — up to 5.76 m on the shipped maps.

---

## 8. C6 — Tick and clock

[`03_CoSimulation_Runtime.md`](03_CoSimulation_Runtime.md) owns the loop. This section states the
guarantee, because a guarantee is what the other seven contracts rely on.

### 8.1 Ownership

> **D4.11 — the co-simulation driver is the sole owner of the advance of simulated time. It advances
> SUMO and it advances the world, in that order, and nothing else advances either.**

> **D4.25 — the same owner owns civil time, because civil time is a function of the tick and of nothing
> else.** The driver derives the civil instant of every tick from `C9`'s epoch and writes the sun from
> it. No component may read the host clock, the host time zone or the host locale to decide what time
> the scene is. This extends `D4.11` rather than qualifying it: a second component that could move the
> sun would be a second owner of time, and the seam `C9` exists to close would reopen inside one run.

### 8.2 Fields

`sumo_step_s` is the scenario package's — the `step-length` of its `.sumocfg`, `traffic.step_length_s`
in its lock (`C3` §5.3). The rest are the run's, given at run start
([`12`](12_Operator_Control_Surface.md)); they are tabled together because `C6` binds them together:

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `sumo_step_s` | number | s | yes | SUMO `--step-length`. Must be a whole number of milliseconds |
| `world_fixed_delta_s` | number | s | yes | CARLA `fixed_delta_seconds` |
| `world_substeps_per_sumo_step` | integer | — | yes | `k`, where `sumo_step_s = k × world_fixed_delta_s`, `k ≥ 1` |
| `sumo_step_timeout_wall_s` | number | s (wall) | yes | How long the driver waits for a SUMO step before declaring a stall |
| `solar_audit_tolerance_s` | number | s (sun-clock) | yes | The G9 residual tolerance on the sun clock. **Named and typed here, valued in [`10`](10_Scale_And_Performance.md)**; §8.3a gives the form the value must take |
| `solar_audit_tolerance_elev_deg` | number | ° | yes | The G9 residual tolerance on solar elevation. Same ownership |
| `solar_audit_every_n_ticks` | integer | — | yes | How often the residual is evaluated. `1` means every tick; the audit **must** run on every *capture* tick whatever this says (§8.3a) |

The epoch and the policy those tolerances are measured against are `C9`'s, not `C6`'s. `C6` owns only
the statement that the sun observed at a tick agrees with the civil time derived from the epoch, and by
how much it is allowed to disagree.

**Measured relevance.** The Bahonar config declares `<step-length value="1.0"/>` with a comment saying
it "matches the CARLA fixed delta the world is ticked at" — which, at a typical
`fixed_delta_seconds = 0.05`, it does not. That comment is an artifact of a pipeline with no CARLA in
it; under `C6` the relationship is `k = 20` and is written down as a number rather than asserted in
prose. The team brief's second load-bearing consequence — a one-second SUMO step is far coarser than
any usable capture rate — is exactly this.

### 8.3 The guarantee at a tick boundary

After the driver returns from one advance, all of the following hold:

| # | Guarantee |
|---|---|
| G1 | The world's `elapsed_seconds` equals SUMO's simulated time **exactly** at every SUMO-step boundary, and `world_elapsed_s ≤ sumo_time_s` between boundaries |
| G2 | Every rendered actor's transform is SUMO's pose at the bracketing SUMO steps, interpolated by the sub-step index, with the bumper shift of `C7` applied and Z taken from the drape |
| G3 | The truth record for tick `n` describes the world **after** every write for tick `n`, never a mixture |
| G4 | No sensor frame for tick `n` is delivered to a recorder before the driver has applied tick `n`'s poses |
| G5 | The admissions and releases of a SUMO step (`C2` §4.2, §4.3) are applied before the first world sub-step of that SUMO step |
| G6 | The annotation state a capture is stamped with is the snapshot for **that capture's tick**, not "current" — the recorder's workers encode asynchronously while the world keeps ticking (`CarlaNet.Recording/FrameRecorder.cs` worker path), so a registry read at write time would annotate a frame with a later state |
| G7 | A given `(scenario package, sumo_seed, appearance_seed)` produces the same sequence of `(tick, sumo_vehicle_id, pose)` triples on every run, provided the world is in synchronous mode |
| G8 | **Civil time.** Every tick has exactly one civil instant, `civil(n) = epoch.civil_datetime + t_render(n)` seconds, computed in the epoch's declared offset. It is the same for every participant, every sensor and every artifact of that tick; it is a pure function of the epoch and the tick index; and it is independent of wall-clock time, host time zone, host locale and the order in which components ask for it (`D4.25`) |
| G9 | **The sun agrees with it.** The solar state observed at tick `n` corresponds to the sun declared for `civil(n)` within the tolerances of §8.3a. This is the residual that makes the silent failure loud |
| G10 | **Nothing but the clock owner moves the sun.** The observed engine-advance flag and rate (`get_solar_state` fields 9 and 10, `CesiumHeightSampler.cpp:786-796`) are off and zero for the whole run under every policy: under a freeze nothing moves the sun, and under `advance` the clock owner writes the sun for every tick before that tick's cue ([`03`](03_CoSimulation_Runtime.md) D3.19). Under a freeze the observed sun clock is additionally constant across the window |
| G11 | **The calendar does what was declared.** Define the *effective date rule* as: the date advances iff `epoch.calendar_advances` is true **and** the policy is `advance` or `illumination.freeze_date_advances` is true. Under it, the declared sun at tick `n` carries `civil(n)`'s date; otherwise it carries the epoch's date while the sun clock wraps at midnight. The observed date is compared as part of the observed instant (§8.3a), so a date a day out is 86,400 s out and can never pass; the one written instant on another date is the last half-second before a midnight under a held date, written as the following midnight on the following date because that whole second is the one nearest the declared instant. The date that was used is recorded per window beside the civil date it corresponds to (`C9` §11.8.1) |

The world's tick identity is available to every participant as `TickTimestamp(Frame, ElapsedSeconds,
DeltaSeconds, PlatformTimestamp)` (`CarlaNet.Transport/CarlaClient.cs:20-24`), raised on
`CarlaClient.OnTick` (`:305`).

### 8.3a Civil time at a tick boundary, and the residual that checks it

**Why this is in `C6` and not only in `C9`.** `C9` declares what `t = 0` means. `C6` is where that
declaration becomes a property of a *tick*, because the tick is the only instant every participant
agrees on, and because the failure mode is not a wrong declaration but a **declaration nothing
enforced**. The whole of `_TEAM_BRIEF.md` §3a rests on the observation that the truth sidecar would
faithfully record noon while the scenario asserted 23:00; the residual below is what turns that from a
silent contradiction into a failed run.

**The arithmetic, written out so two implementations agree.** It is implemented once, in
`CarlaNet.CoSim` (`DeclaredSun`, `SolarAudit`), and every consumer calls it.

```
t_render(n)          = begin_s + n · world_fixed_delta_s        # simulated seconds, C6
civil(n)             = epoch.civil_datetime + t_render(n)       # in epoch.utc_offset_hours
declared_sun(n)      = the date and clock the sun is declared to hold at t_render(n):
                         advance:                 the window's opening instant carried forward by
                                                  (t_render(n) − begin_s) × rate; its date moves with it
                                                  if G11's rule advances the date, and the clock wraps
                                                  onto the epoch's date if not
                         freeze_at_window_start:  the window's opening instant, to the whole second,
                                                  on its civil date if G11's rule advances the date,
                                                  else on the epoch's date
                         freeze_at:               the declared civil time of day, dated the same way
written(n)           = the whole second nearest (declared_sun(n) − 1 ms), plus 1 ms
                       — what the clock owner writes with set_solar_epoch, in epoch.utc_offset_hours
expected_elev_deg    = the sun's direction for declared_sun(n) at the world origin, from the
                       engine's own algorithm evaluated at the instant itself
```

`set_solar_epoch` writes the sun's zone as `epoch.utc_offset_hours` together with the date and the
clock ([`11`](11_Time_And_Illumination.md) D11.5), so the sun's clock is the civil clock and no
conversion between zones enters the arithmetic. Under a freeze `written` is computed once, at window
open. Under `advance` it is written for every tick, before that tick's cue, so the tick renders
exactly it. **The expectation is always the declared sun, never the sun the world happens to hold** —
otherwise the audit would compare the sun against itself and pass unconditionally, which is the one
way to make this check worthless.

**The observation.** The solar state paired to tick `n` is read from the world-observer snapshot the
tick delivered: `FWorldObserver` writes the solar block into every snapshot header
(`WorldObserver.cpp:322-340`), the client updates it lock-free per tick
(`CarlaClient.cs:165-169`), and both the recorder (`FrameRecorder.cs`) and the audit
(`GetCachedSolarState`) read it with **no RPC**. The audit therefore costs nothing on the tick
thread, which is already contended ([issue #14](https://github.com/sbrett9/carla/issues/14)).

**The residual, and the tolerance.**

| Residual | Definition | Tolerance |
|---|---|---|
| `Δsolar_s` | observed instant (the sun's date and clock) − `declared_sun(n)`, in seconds | **0.5 s** at every rate |
| `Δdir_deg` | angle between the observed sun direction (geometric elevation, azimuth) and `expected`'s | **0.01°** at every rate |
| `Δelev_corrected_deg` | observed refraction-corrected elevation − `expected`'s, where the snapshot carries it | **0.01°** |
| `Δpolicy` | observed zone against `epoch.utc_offset_hours`; engine advance flag and rate against off and 0 | **exact** |

**Why the tolerance has that value and does not depend on the rate.** The engine evaluates its sun at
whole seconds, so a clock nearer than half a second to the declared one cannot change the sun it
renders; `written(n)` is by construction within (−0.5 s, +0.5 s] of `declared_sun(n)`, and the sun it
renders is within about 0.002° of the declared direction. The clock owner writes `written(n)` in the
drain of tick `n`, so there is no lag between the write and the snapshot for a rate-dependent term to
absorb, and a tolerance that grew with the rate would, at an hour of sun per simulated second, admit a
clock three hundred and sixty seconds from the declaration that the writing never produces. The angle
floor is the resolution at which the engine was measured against the model, three orders of
magnitude inside it ([`11`](11_Time_And_Illumination.md) §8.3). The built audit runs at these values
and refuses the scenario's tolerance overrides of §8.2 until the bound on them is valued
([`11`](11_Time_And_Illumination.md) §4.4.1).

**When it is evaluated.** On every tick, and so on every capture tick — a tick on which any recorder
writes a frame. A capture whose solar block was never audited is a capture whose `<_solar>` is an
unverified claim, and those are the only frames that end up in a corpus.

**What is recorded.** The maximum residual over the run, the tick it occurred at, and the
`within_tolerance` verdict, all in the run manifest (`C9` §11.8); until stage J builds the manifest,
the co-simulation run report carries them (`CoSimRunReport`). A run that never exceeded tolerance
still records its maximum, because "the residual was 0.4 s" and "the residual was never measured" must
not look alike.

### 8.4 What every participant must not do

| Forbidden | Why |
|---|---|
| Call `world.tick()` | Two ticks per step desynchronises G1 irrecoverably |
| Call `traci.simulationStep()` | Same, on the other side |
| Change `synchronous_mode` or `fixed_delta_seconds` mid-run | Breaks G7 and invalidates `world_substeps_per_sumo_step` |
| Register a vehicle with the .NET traffic manager | Locked out by [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3.4. A second controller writing poses makes G2 false |
| Block the tick thread | The tick thread is already contended by telemetry emission ([issue #14](https://github.com/sbrett9/carla/issues/14)); the driver runs in the same budget |
| Emit telemetry synchronously from the tick thread | Same |
| Destroy an actor outside the render-set controller | The fourth destroyer ([issue #18](https://github.com/sbrett9/carla/issues/18)) |
| Read "current" annotation or render state at capture-write time | Violates G6 |
| Call `set_solar_time`, `set_solar_date`, `set_solar_epoch` or `set_time_advance` while a session is live, unless you are the clock owner | A second writer of the sun is a second owner of time (`D4.25`). The RPCs exist and are reachable from any client (`CarlaServer.cpp:614`, `:625`, `:644`, `:661`), so this is a rule a reviewer enforces, not one the transport can; the per-tick audit catches a write that lights a frame |
| Read the host clock, host time zone or host locale to decide what time the scene is | Violates G8. Measured as the present behaviour: the scene date defaults to `datetime.now()` (`WorldBuilder.py:229-230`), which makes a capture's seasonal sun angle depend on the day it was run |
| Assume `set_solar_time`'s argument is civil time | It is sun-clock time in a zone derived from longitude (`CesiumSunSky.cpp:571`), and `C9` §11.4 measures the difference as 14.7 minutes on the sizing scenario |
| Let a recorder write a frame on a tick whose solar residual has not been evaluated | Violates the capture-tick rule of §8.3a: the frame's `<_solar>` would be an unverified claim |

### 8.5 Stall

> **D4.12 — if either side stalls, the driver stops advancing *both* and fails the run. It never
> advances the world without SUMO.**

On `sumo_step_timeout_wall_s` elapsing: stop advancing; record `stalled: true` with the last good tick
in the run manifest; close every open interval with `closed_by = "aborted"`; close `render_states[]`
with the spans observed so far; exit non-zero. A world that keeps ticking while SUMO is stuck produces
imagery whose behavioural truth is a lie — and it is a *plausible* lie, because the vehicles simply
stop moving.

### 8.6 Validation

| # | Rule | Response |
|---|---|---|
| V6.1 | `sumo_step_s = k × world_fixed_delta_s` for integer `k ≥ 1`, within 1 µs | refuse at run start |
| V6.2 | `sumo_step_s` is a whole number of milliseconds | refuse |
| V6.3 | The world reports `synchronous_mode == true` and `fixed_delta_seconds == world_fixed_delta_s` | refuse |
| V6.4 | The SUMO config's `<seed>` equals the lock's `traffic.sumo_seed` | refuse |
| V6.5 | At every SUMO-step boundary, `\|world_elapsed_s − sumo_time_s\| ≤ 1 µs` | assertion; a violation fails the run |
| V6.6 | `Δsolar_s`, `Δdir_deg` and `Δelev_corrected_deg` between the **declared** sun and the observed sun, within the §8.3a tolerances at every audited tick | **fail the run** at the first violation, naming the tick, both residuals, the expected and observed values, and the policy in force. Not a warning: the sun is not doing what it was told, so every frame from here on carries a `<_solar>` nothing predicted — an inherited sun, a wrapped date or engine drift, never an authored choice |
| V6.6a | The commanded solar target equals the civil time the epoch derives for the tick, **unless** the run declares an illumination override | **warn and mark the corpus**, never fail. An operator may deliberately render a window under light its own clock does not imply; that is a parameterisation, not a defect, and the responsibility is theirs. The warning names the derived civil time, the commanded one and the gap; the manifest records `illumination_override` with both values so a consumer can filter on it. An **undeclared** gap is not this case — it is a bug in the driver and fails under V6.6 |
| V6.7 | Observed engine `advancing` and `rate` are off and zero (G10) | fail the run — the sun is being driven by something other than the clock owner |
| V6.8 | Observed solar date satisfies G11 | fail the run — a wrong date is a wrong seasonal sun angle, and the check is exact, not approximate |
| V6.9 | Every capture tick is an audited tick (§8.3a) | assertion in the recorder path; a capture written on an unaudited tick is a bug, not a condition |
| V6.10 | `solar_audit_every_n_ticks ≥ 1` and both tolerances `> 0` | refuse at run start |

### 8.7 What breaks if C6 is violated

- **The imagery and the behavioural truth describe different instants.** Every position in the sidecar
  is then wrong by up to one SUMO step — at the measured one-second step, up to 30 m at freeway speed.
- **Determinism is lost**, and with it counterfactual pairing, sweeps and any claim that two runs
  differ only in the thing that was varied.
- **A stall becomes invisible.** Vehicles stand still, the world keeps rendering, and the corpus
  acquires a long stretch of stationary traffic that no author wrote and no consumer can distinguish
  from a jam.
- **The night shift is captured in daylight, and every artifact agrees that it was.** Without G8–G11
  and V6.6 a 23:00 window renders under whatever the world was spawned with — measured as local solar
  noon (`CesiumHeightSampler.cpp:409`) — while `<_solar>` faithfully records noon and the scenario
  asserts 23:00. The corpus is internally contradictory and nothing flags it. This is the failure
  [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a calls out as the worst available, and the residual is the only
  thing in the system that can see it.
- **Illumination stops being a covariate and becomes noise.** A sweep that means to hold the sun fixed
  and does not, or means to advance it and drifts, produces a corpus whose lighting is neither
  controlled nor recorded accurately. Every stratification over solar bins is then wrong by an unknown
  amount, and the amount is unrecoverable because the only record of it is the thing that drifted.

---

## 9. C7 — Physics and control authority per actor

### 9.1 The contract for a SUMO-driven actor

| Property | State | Source |
|---|---|---|
| Rigid-body simulation | **disabled** | `set_actor_simulate_physics` (`CarlaNet.Transport/CarlaClient.cs:1530`) |
| Traffic-manager registration | **never**; lockout is run-level, not a warning | [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3.4 |
| Storyboard control | **never** in this mode | |
| Pose | written every world sub-step by the driver | `set_actor_transform` (`CarlaClient.cs:1497`) |
| X, Y | SUMO's front bumper exactly, negated in Y, shifted back by the measured bumper-to-origin distance along the heading and by the bounding-box centre — **the exact formula and its catalogue dependency are `C1` §3.2** | [doc 23 §6.7](../../Findings/23_SUMO_Traffic_Integration.md), `D4.17` |
| Yaw | the heading of the body's own path, less 90: the rear axle, 0.75 of the body's length behind the bumper, trails the bumper along its path, turned only by forward travel ([`03`](03_CoSimulation_Runtime.md) §6.4, D3.40). SUMO's reported angle is recorded beside it for audit, never written as the yaw. Before 2026-10-02, `sumoAngle − 90` in the plan and the lane tangent in the bridge | |
| Z | the drape ground height at (x, y), **not** SUMO's — the SUMO network is flat, measured zero distinct `z` in any lane shape | doc 23 §2, §6.5 |
| Bounding box | the blueprint's, unchanged; equal to the vType's by `C1` §3.6 | |
| Colour | drawn per `C1` §3.8, set at spawn, immutable | |
| **Vehicle light state** | **owned by the co-simulation bridge**, rewritten whenever it changes, in the same per-tick batch as the pose | §9.4; `SetVehicleLightStateCommand` (`CarlaNet.Types/Rpc/Commands/Command.cs:94`) |
| Collision response between two SUMO-driven actors | **undefined.** SUMO owns separation | |
| Wheel rotation, suspension, body roll | **not simulated** | |
| Vehicle fade | **not used.** Admission and release are abrupt and the instants are recorded | `C2` §4.3; [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md), *Vehicle fade is demoted* |

### 9.2 The velocity problem, verified

The team brief carries this forward from doc 23 §4 and asks for verification. **It is confirmed from
source, and it is worse than a reporting gap — the obvious workaround does not work either.**

- `FWorldObserver` serialises a non-dormant actor's velocity as
  `Velocity = TO_METERS * View->GetActor()->GetVelocity();`
  (`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/WorldObserver.cpp:373`). With simulation off,
  a `set_transform` does not update that.
- `FCarlaActor::SetActorTargetVelocity` writes
  `RootComponent->SetPhysicsLinearVelocity(Velocity, false, "None")` for a non-dormant actor
  (`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Actor/CarlaActor.cpp:392-411`), which is inert when
  physics simulation is disabled. So calling `set_actor_target_velocity` on a teleported body does
  **not** fix the reported velocity.

The truth record reads `snap.Velocity` from the world-observer cache
(`CarlaNet.Recording/VehicleTelemetryService.cs:77,88`) and derives both `speed` and — above 0.5 m/s —
`course` from it (`:89-98`). So a teleported body reports zero speed *and* falls back to yaw for
course, which then also reaches the CoT `track` element (`CotWriter.cs`), the occlusion and arrival
gating of [doc 17](../../Findings/17_Photoreal_Occlusion_Metric.md), and every consumer that reads speed
out of the truth record.

> **D4.13 — truth velocity for a SUMO-driven actor is a contract obligation of the producer, not a
> property recovered from the engine.** The truth record's `speed`, `course`, `vx`, `vy`, `vz` for an
> actor with `role_name = "sumo"` **must** be the vehicle's own motion as SUMO moved it -- since
> 2026-10-02 the velocity of the path the bridge renders it along, the front bumper's movement over the
> tick with a lane change's sideways movement in it, so the speed is SUMO's along the lane plus the
> sideways rate, and the course the direction the body moves -- and **must not** be recovered from
> physics. Its heading is the body's (`heading_deg`), which differs from the course through a turn and
> a lane change, and SUMO's reported angle rides beside both (`sumo_angle_deg`) for audit.

The conversion is already implemented and measured on the SUMO-only path
(`CarlaControl/src/carlacontrol/SumoCotBridge.py:311-318`), which exists precisely because the SUMO
side has correct velocity. *How* that value reaches `VehicleTelemetryService` — a per-actor velocity
override written by the driver, or the truth producer consulting the driver — is
[`03`](03_CoSimulation_Runtime.md)'s and [`06`](06_Truth_And_Annotation.md)'s to design. `C7` fixes only
that the resulting number is SUMO's and that reading `GetVelocity()` for such an actor is a defect.

### 9.3 Authority transfer

> **D4.14 — authority is a property of the actor, written at spawn in `role_name`, and immutable for
> the actor's life. A handover is expressed as destroy-and-respawn, never as a mutation.**

Three reasons, each independent:

1. Actor attributes are fixed at spawn — there is no RPC to change one (doc 20 §4.3) — so `role_name`
   is *already* immutable and any mutable authority flag would live somewhere else and disagree with it.
2. A mutable flag is exactly the state two subsystems will disagree about, which is the shape of
   [issue #18](https://github.com/sbrett9/carla/issues/18) already.
3. Destroy-and-respawn is observable in the truth record: a new `actor_id`, a new `rendered_span`, the
   same `sumo_vehicle_id` (`C4` `D4.8`). A mutation is not.

Doc 23 §6.9's scenario-coupling case — a storyboard entity inside SUMO traffic — is therefore expressed
as: the storyboard entity is spawned with `role_name = "scenario"` and is **mirrored into** SUMO
(`vehicle.add` plus `moveToXY(keepRoute=2)`) so ambient traffic yields to it, while SUMO never issues
control for it. That is not a transfer; it is two authorities over two disjoint actor sets.

### 9.4 Vehicle light state

`SetVehicleLightStateCommand` is one of the 22 batch commands, SUMO exposes
per-vehicle signals, and the .NET traffic manager — which owns light state today — is locked out in this
mode. Light state therefore has **no owner at all** unless `C7` gives it one, and a night capture with
no owner renders every vehicle dark.

> **D4.22 — the co-simulation bridge owns a SUMO-driven vehicle's light state, in two disjoint halves.
> SUMO owns the behavioural bits, because they are consequences of the driving it is simulating. The
> illumination policy owns the conspicuity bits, because SUMO has no model of them. Every other bit is
> undefined and must be left alone.**

#### What each half is, and why the split falls exactly there

**Measured — SUMO sets four signals and only four.** Searching the vendored SUMO source for every
`VEH_SIGNAL_*` constant:

| SUMO signal | Set by the simulation? | Where |
|---|---|---|
| `VEH_SIGNAL_BRAKELIGHT` (8) | **yes** | `Build/sumo-src/src/microsim/MSVehicle.cpp:4249-4257`, from `vNext < speed − pseudoFriction`, and unconditionally below halting speed |
| `VEH_SIGNAL_BLINKER_LEFT` (2), `VEH_SIGNAL_BLINKER_RIGHT` (1) | **yes** | lane change (`MSAbstractLaneChangeModel.cpp:317-318`, `:472`), junction turns (`MSVehicle.cpp:6803-6836`), stops (`:6843-6853`), teleport re-insertion (`MSVehicleTransfer.cpp:150`) |
| `VEH_SIGNAL_BLINKER_EMERGENCY` (4) | **yes**, as both blinkers, when stopping other than on the right | `MSVehicle.cpp:6850` |
| `VEH_SIGNAL_EMERGENCY_BLUE` (2048) | **yes**, for emergency vehicles | `MSVehicle.cpp:6866-6870` |
| `VEH_SIGNAL_FRONTLIGHT` (16), `VEH_SIGNAL_FOGLIGHT` (32), `VEH_SIGNAL_HIGHBEAM` (64), `VEH_SIGNAL_BACKDRIVE` (128), `VEH_SIGNAL_WIPER` (256), `VEH_SIGNAL_DOOR_OPEN_LEFT`/`RIGHT` (512, 1024) | **no** | Measured: each of these seven constants occurs **exactly once** in `Build/sumo-src/src/` — its own line in the enum declaration `microsim/MSVehicle.h:1110-1138` — and nowhere else |

So SUMO has a brake model and an indicator model and **no headlight model**. A TraCI client can still
*write* those bits, so their absence is SUMO's modelling choice rather than an interface limit — which
is exactly what makes the split below a decision worth recording. A brake light is a consequence of a
deceleration SUMO computed, and only SUMO knows it. A headlight is a consequence of the sun, and only
`C9` knows that.

**Measured — the existing headlight rule cannot be reused here.** The .NET traffic manager's
`VehicleLightStage` decides `Position`, `LowBeam` and `Fog` from `WeatherParameters.SunAltitudeAngle`
and the precipitation and fog densities
(`CarlaNet/src/CarlaNet.TrafficManager/Stages/VehicleLightStage.cs:231-249`, thresholds at
`CarlaNet.TrafficManager/Constants.cs:202-205`). In a georeferenced world **CARLA's own weather is
inert** — `CarlaServer.cpp:611-612` names CesiumSunSky the single sun and lighting authority and says so
— so that rule would be reading a weather actor that is not driving the lighting. The bridge's
equivalent must take its sun altitude from `get_solar_state`'s `sun_elevation_deg`
(`CesiumHeightSampler.cpp:782`), which is the sun that is actually lighting the scene. Note the two use
different conventions — the weather angle is CARLA's 0–180 form the thresholds above are written
against, `sun_elevation_deg` is degrees above the horizon — so the thresholds are not transferable as
numbers. The *values* of any threshold are
[`11_Time_And_Illumination.md`](11_Time_And_Illumination.md)'s; `C7` fixes only that the input is the
real sun and the owner is the bridge.

#### The mapping

| CARLA bit (`carlanet/__init__.py:2665-2676`) | Owner | Rule |
|---|---|---|
| `Brake` (0x8) | **SUMO** | set iff `VEH_SIGNAL_BRAKELIGHT` |
| `RightBlinker` (0x10) | **SUMO** | set iff `VEH_SIGNAL_BLINKER_RIGHT` or `VEH_SIGNAL_BLINKER_EMERGENCY` |
| `LeftBlinker` (0x20) | **SUMO** | set iff `VEH_SIGNAL_BLINKER_LEFT` or `VEH_SIGNAL_BLINKER_EMERGENCY` |
| `Position` (0x1), `LowBeam` (0x2) | **illumination policy** | a function of the tick's solar elevation and of nothing else, identical for every vehicle at that tick |
| `Fog` (0x80) | **illumination policy** | reserved; nothing in this mode sets it today, because the world has no weather to read (`CarlaServer.cpp:611-612`) |
| `Special1` (0x200), `Special2` (0x400) | **the class**, via `C1` `lamps_expected` | the only bits an author can ask for per class — a beacon on an emergency class. Never a function of supervision state |
| `HighBeam` (0x4), `Reverse` (0x40), `Interior` (0x100) | **nobody** | undefined; see below |

#### What is guaranteed

1. **Every rendered SUMO-driven vehicle has a defined light state at every tick**, written by exactly
   one component. There is no tick at which a vehicle's lights are whatever they last happened to be
   under some other owner.
2. **The behavioural bits are SUMO's, unmodified.** The bridge does not re-derive braking from the
   pose it just wrote; it reads the signal SUMO computed. Re-deriving would produce a brake light that
   disagrees with the deceleration in the behavioural record.
3. **The conspicuity bits are a function of the scene's sun, identical across every vehicle at one
   tick.** This is what keeps them from becoming an appearance separator: a scene-scoped scalar cannot
   distinguish two vehicles in the same frame (`C4` §6.2). Combined with `C1` V1.19, which keeps lamp
   *capability* from separating them either, illumination stays a covariate rather than a label
   ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a, standing constraint).
4. **A change is written, an unchanged state is not.** The light state rides the same batch as the pose
   and only when a vehicle's bitmask differs from the one last written for that actor. The engine
   already diffs all eleven bits before doing anything (`CarlaWheeledVehicle.cpp:686-700`), so a resend
   is harmless, but the bridge holds the last written value rather than polling, because the readback
   is the command and not the vehicle (§3.2a) and a poll would cost a round trip per vehicle per tick.
5. **The written bitmask is recorded nowhere as data.** Light state is a rendering input, not truth. It
   does not enter the sidecar, the run manifest or either handover root (`C8`). If a consumer needs to know
   whether a vehicle was braking, the answer is SUMO's behavioural record, which is where the fact
   originated.

#### What is undefined, explicitly

`HighBeam`, `Reverse`, `Interior`, and the door bits. No component sets them, SUMO never computes them
(the `BACKDRIVE` measurement above), and the render is whatever the blueprint does with a bit nobody
asked for. **A consumer must not infer anything from them**, and a later design that wants them must
claim an owner here first. Naming them as undefined is the point: an unclaimed bit that someone starts
writing later is how two owners appear.

Two further undefined cases, both consequences of measurements already in this document:

- **What a lamp does when the blueprint does not implement it.** `RefreshLightState` is a
  `BlueprintImplementableEvent` (`CarlaWheeledVehicle.h:310-311`), so a commanded bit may illuminate
  nothing. `C1` §3.2a measures which, `C1` V1.18 warns, and the run manifest records it. The *command*
  is still written, because suppressing it would make the artifact disagree with the policy.
- **Whether a light is detectable.** `C7` guarantees the command and, through `C1`, whether the lamp
  changes the image at all. Whether it is bright enough to be found by a detector at range is a
  collection question for [`08_Collection_And_EPoL.md`](08_Collection_And_EPoL.md).

### 9.5 Invariants a test can check

Named so a test can be written against them without further interpretation.

| # | Invariant | Tolerance |
|---|---|---|
| I1 | Every actor with `role_name = "sumo"` reports `simulate_physics == false` | exact |
| I2 | For such an actor with SUMO speed > 0.5 m/s, `\|truth.speed − sumo.speed\|` | ≤ 0.25 m/s over any 1 s window |
| I3 | For such an actor above 0.5 m/s, `\|truth.course − sumo.angle\|` modulo 360 | ≤ 2° |
| I4 | `\|actor.z − drape_ground_z(x, y) − seat_offset\|` | ≤ 0.10 m |
| I5 | Truth `length_m`, `width_m`, `height_m` equal the catalogue entry for `capture:class_id`'s realised blueprint | ≤ 0.01 m |
| I6 | No actor with `role_name = "sumo"` appears in the .NET traffic manager's registry | exact |
| I7 | After undoing the bumper shift, `carla_x == sumo_x` and `carla_y == −sumo_y` | ≤ 0.05 m |
| I8 | Every rendered actor carries `capture:sumo_id`, and no two carry the same one at one tick | exact |
| I9 | No rendered actor's truth record reports `speed == 0` while SUMO reports it moving | exact — this is `D4.13`'s regression test |
| I10 | The set of destroyed actors in a tick equals the set the render-set controller released | exact |
| I11 | For every rendered SUMO-driven actor at every tick, the written `Brake`, `LeftBlinker` and `RightBlinker` bits equal the mapping of that vehicle's SUMO signal bitmask in §9.4 | exact |
| I12 | At any one tick, the `Position` and `LowBeam` bits written are identical across every rendered SUMO-driven actor | exact — a difference means a per-vehicle input leaked into a scene-scoped decision |
| I13 | No component other than the bridge issues `SetVehicleLightStateCommand` or `set_vehicle_light_state` during a session | exact; the .NET traffic manager's `VehicleLightStage` is unreachable because no vehicle is registered (I6) |

### 9.6 Losses, named as the standing rule requires

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §4 requires a capability that cannot be preserved to be named
explicitly rather than lost silently. In this mode:

| Lost | Compensation |
|---|---|
| Vehicle dynamics — suspension travel, body roll, tyre slip | **None, and accepted.** The purpose is imagery plus behavioural truth ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3.2). Confined to this mode; the .NET traffic-manager path and the storyboard executor keep full dynamics |
| Engine-reported velocity | **Compensated** — `D4.13` replaces it with SUMO's, which is more accurate for this mode, not less |
| Terrain-responsive speed | Not provided by SUMO either (doc 23 §7); a separate piece of work |
| Collision response between rendered vehicles | **None.** SUMO's car-following owns separation. A SUMO collision is reported by SUMO |
| Terrain seating | **Preserved** — Z from the drape, I4 |
| Staging fade | **Deliberately not used** ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md), *Vehicle fade is demoted*). What it provided — a recorded notion of a vehicle having arrived — is replaced by `rendered_spans[]` (`C2` §4.5), which is a recorded instant rather than a visual transition. The per-frame RPC budget gains, since the fade was the heaviest existing client load |
| Truth arrival gate | **Preserved and inert** — `IsActorEstablished` returns true for an unfaded actor (`CarlaClient.cs:1571`), documented at `VehicleTelemetryService.cs:66-73` |
| The traffic manager's headlight and fog rule | **Compensated, and improved.** `VehicleLightStage` is unreachable because no vehicle is registered (I6), and it read `WeatherParameters.SunAltitudeAngle` (`VehicleLightStage.cs:231-238`), which is inert in a georeferenced world (`CarlaServer.cpp:611-612`). §9.4's replacement reads the sun that is actually lighting the scene. This is a capability that worked on stock content, did not work here, and now does |
| The traffic manager's brake and indicator inference | **Replaced by a better source.** It inferred them from its own motion plan (`VehicleLightStage.cs`); the bridge reads the signals SUMO computed for the driving it actually simulated (§9.4) |

### 9.7 What breaks if C7 is violated

- **Every truth speed is zero and every course is a yaw** — the measured consequence of
  `WorldObserver.cpp:373`. A corpus whose truth says nothing moves is not a corpus, and downstream
  consumers that gate on speed — occlusion, arrival, and anything downstream reading the truth record —
  all mis-fire.
- **Vehicles float or sink.** SUMO's network is flat; taking Z from it puts every vehicle at the
  world's Z = 0 plane regardless of terrain.
- **Two controllers write one actor's pose**, and it jitters between them at tick rate.
- **A systematic along-heading offset** if the bumper shift is skipped — half a vehicle length, every
  frame, in the same direction.
- **Authority becomes unrecoverable after the fact.** Without `role_name` as the authority record, a
  truth record cannot say what was driving the vehicle it describes.
- **Every vehicle in a night capture is dark.** With the traffic manager locked out nothing else writes
  light state, so a 23:00 window renders unlit bodies on unlit roads — and the imagery looks plausible,
  because a night scene with no headlights is simply a darker night scene. The measured cost is the
  whole point of capturing at night: brake lights and indicators are the strongest motion cues an
  electro-optical detector has in the dark, and SUMO was computing both all along.
- **Two owners of one bitmask.** If the traffic manager were ever registered alongside the bridge, both
  would write `Position` and `LowBeam` from different sun sources — one inert, one real — and the lights
  would flicker at tick rate with no record of why. I13 is the check.
- **A conspicuity lamp becomes a label.** If `Position`/`LowBeam` were derived per vehicle rather than
  per scene, or if a class's `lamps_expected` tracked supervision state, the corpus's positive class
  would be separable on lighting rather than on behaviour. The guarantee in §9.4 item 3 and `C1` V1.19 are the two halves that prevent it.

---

## 10. C8 — The handover, in two delivery modes

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b puts the detect-and-track model and the estimated-pattern-of-life
model outside this effort and forbids scoring; §3c adds that excluding **scoring** does not exclude
**running the chain**. The **live exercise is a primary use case** — our synthetic imagery generation
feeds an external detect-and-track stage, which sends tracks to an external model service, which
performs its anomaly detection and produces its reports live — and *everything past our imagery and
truth is external and unknown to us*.

`C8` is therefore **one contract with two delivery modes**, not a corpus contract with a live
afterthought bolted to it:

| Mode | What it is |
|---|---|
| **Deferred handover** | The records are written to files under the two artifact roots and published once the corpus is complete and has passed its checks |
| **Live handover** | The *same records* are emitted as they are produced, over a transport, to whatever is listening — with stated timing and identity guarantees, and no assumption whatever about the listener |

The records are the same records. What differs is the **carrier, the timing, the loss behaviour and the
closure** — and nothing else. A reader who has understood one mode has understood the other, and a
consumer writes one reader. §10.1a states the difference and the identity in full, and the rest of this
section is written once and applies to both unless it says otherwise.

Three consequences organise the rest of the section:

1. Everything **identical** across the modes is specified once — the identity keys, the observation
   field set, the truth content, the observer-derivability rule and the anti-leak split (§10.1a).
2. Everything the **external side** does is unspecified, deliberately, and the omission is checkable by
   the substitution test of §10.11. If a clause of this contract would have to change when the external
   chain is replaced wholesale, that clause is a defect in this contract.
3. Anything that **comes back** is received data with its own provenance: an opaque blob in a minimal
   container, recorded verbatim and never parsed for meaning we act on (§10.10).

The delivery mode changes none of the rulings this contract rests on: the two artifact roots
(`D4.26`), the observer-derivability principle (`D4.20`), the anti-leak isolation (`D4.16`), the
supervision-transfer rule as published-but-not-performed (`D4.27`) and the label-quality ruling
(`D4.28`) hold in both modes and are cited where each applies.

[`08_Collection_And_EPoL.md`](08_Collection_And_EPoL.md) owns the collection rationale — what a camera
can see, what a capture session is, how the roots are laid out and which transport a stream is bound to.
`C8` owns the contract: the records, the field sets, the guarantees, the refusals and the validation.
[`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) owns how a run is *invoked*; `C10`
(§12) owns the record of what it produced, and what an external process can observe while it runs.

### 10.1 Where this system ends

```mermaid
flowchart LR
  subgraph ours["This system — one set of records, two ways of delivering them"]
    OBSR[("OBSERVATION record<br/>imagery · sensor pose · intrinsics<br/>· radiometry · achieved solar · epoch")]
    TRUR[("TRUTH record<br/>CoT sidecars · labels · segmentation<br/>· supervision · coverage · manifests")]
    DEF["deferred delivery<br/>files under the two roots,<br/>published when complete"]
    LIVE["live delivery<br/>the same records, emitted<br/>after the tick that produced them"]
    TRX[("transcript store<br/>opaque · verbatim · write-only<br/>neither root — §10.10")]
    RES[/"run record — C10<br/>appended throughout"/]
  end
  CONS["an arbitrary external consumer<br/>its API, formats, transport, latency<br/>and failure modes are unknown here"]
  AUTO["an external process<br/>drives regeneration, observes a live run,<br/>and stops it when it has enough"]

  OBSR --> DEF
  TRUR --> DEF
  OBSR --> LIVE
  TRUR -. "separate endpoint, off by default<br/>in a live handover (08 §11.4)" .-> LIVE
  DEF --> CONS
  LIVE --> CONS
  CONS -. "whatever it returns, if anything" .-> TRX
  TRX -. "never read by any artifact<br/>this system produces (D4.32)" .-x TRUR
  RES --> AUTO
  AUTO -. "invokes the run<br/>(surface owned by 12)" .-> ours
```

The arrows out are the whole of `C8`'s runtime behaviour. **Exactly one arrow carries data in** — the
transcript's — and it goes into a store that nothing of ours reads, which is why §10.10 is the precise
statement of the one-directional guarantee rather than an exception to it. The dotted arrow from the
external process carries an *invocation*, not data, and the surface it uses is
[`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md)'s; what it reads back is the `C10`
run record, and what it watches while the run is alive is §12.8's observation surfaces.

### 10.1a One contract, two delivery modes

> **D4.29 — the `OBSERVATION` record and the `TRUTH` record are identical in both delivery modes. Only
> the carrier, the timing, the loss behaviour and the closure differ, and the two modes are *additive*
> rather than alternative — a live run may write the deferred roots as well, and when it does, the
> record a consumer received on the stream and the record in the corpus are the same record.** The test
> of this decision is that a consumer writes **one** reader. [`08`](08_Collection_And_EPoL.md) §7.2
> reached the same conclusion from the collection side — "the handover is defined as the record, not as
> a directory" — and `C8` is the ruling a later reader should cite.

**What is identical, field for field and rule for rule.** This is the list a reader should check a
proposed change against: a change that makes any row differ between the modes is a change that splits
one contract into two.

| Identical in both modes | Specified at | Why it must not differ |
|---|---|---|
| **The identity keys on every record** — `sensor_id`, `tick`, `sim_time_s`, `run_id` (and `scenario_id`, subject to §10.3's open conflict) | `C4` §6.1, §6.6; §10.3 | The tick is the only join key in either mode (`C4` §6.6; [`08`](08_Collection_And_EPoL.md) `D8.4`). A live consumer and a corpus consumer must be able to name the same frame, and a live exercise that is also recorded must yield one frame with one name, not two |
| **The `OBSERVATION` record's field set** | §10.3, §10.4 | It is an allow-list, and it is enforced at the file writer and at the stream emitter alike (V8.8, V8.14). A field that is unsafe in a corpus is unsafe on a wire |
| **The truth content** — sidecars, per-image labels, segmentation, `<_supervision>`, `<_aoi>`, coverage, the label-quality block | §10.6, §10.7 | Truth does not become less exact because it is delivered sooner |
| **The observer-derivability rule and the solar/epoch allow-list** | `D4.20`, `D4.21`, §10.4a | Both are rules about *our own data*. A delivery mode is not an argument for admitting a field |
| **The anti-leak split** — truth and observation written by separate components from the first byte, neither holding a reference to the other's artifacts | `D4.16`, §10.5 | In the live mode the same rule binds the *emitter*: one endpoint per root, never one stream carrying both |
| **Two artifact roots, and no third** | `D4.26` | The transcript is not a root (§10.10) and neither is a stream |
| **The supervision-transfer rule, published and never performed** | `D4.27`, §10.6 | Unaffected by delivery. A live exercise is the one place where performing it would be a few lines away, which is exactly why the rule is stated rather than implemented |
| **The obligation to declare what is not contained** | V8.9 (corpus manifest), V8.17 (stream close record) | A gap a consumer cannot see is indistinguishable from data that never existed, in either mode |

**What differs, exhaustively.** Everything not in this table is the same in both modes.

| | Deferred handover | Live handover |
|---|---|---|
| **Carrier** | files under the two roots; one `OBSERVATION` metadata document beside each frame, one `TRUTH` sidecar per `(sensor_id, tick)` | records on a transport, framed; the binding is [`08`](08_Collection_And_EPoL.md)'s and this contract requires only the three transport properties of §10.9.1 |
| **The pixels** | the image is a file; the record names it | the image rides inline as bytes in the same record |
| **When a record leaves** | at publication, after the run has closed and passed its checks | after the tick that produced it has completed, and never before (L1) |
| **Completeness** | complete by construction, validated before publication | necessarily partial while the run is in progress; gaps are legal, counted and recoverable (L2) |
| **Ordering** | none required — random access by `(sensor_id, tick)` | per stream: `tick` non-decreasing, `seq` strictly increasing, never reordered, never duplicated (L2) |
| **Loss** | none. A missing file is a defect, not a policy | drop-oldest at the emitter, counted, and written into coverage as *covered but not delivered* ([`08`](08_Collection_And_EPoL.md) §11.3) |
| **Pacing** | as fast as the machine allows | a **declared** real-time factor, never negotiated with the consumer (`D4.30`) |
| **Attachment** | a consumer copies what exists, whenever it likes | attach and detach at any time; a stream header on attach, no handshake, no acknowledgement, no replay (`D4.31`) |
| **Closure** | the corpus manifest, then the run record's closing row (`C10`) | a stream close record, then the run record's closing row (`C10`). A caller that kills the run leaves neither, and §12.7 says what a reader concludes |
| **Truth delivery** | the whole `TRUTH` root, for sessions in the release partition | a separate stream on a separate endpoint, **off by default**, and turning it on is a recorded choice ([`08`](08_Collection_And_EPoL.md) §11.4, V8.15) |
| **What a consumer's absence costs** | nothing | nothing. Zero consumers is a legal state and the default (`D4.31`) |

**Why the modes are additive and not alternative.** A live exercise that also writes the roots costs one
extra writer and yields a corpus as a by-product; a live exercise that *cannot* write them would make
the most expensive kind of run — one with an audience — the only kind that produces nothing reusable.
`D4.29` therefore makes the two independent switches rather than one selector, and the coverage record
distinguishes them: a frame that was captured, written to the corpus and dropped by the link is
`covered` in the corpus and `covered but not delivered` on the stream, which is two facts about one
tick and not a contradiction.

### 10.2 The artifact roots — the ruling

A root for associations and reports would have no writer in this system and no reader in it either:
associations and reports are computed from model output, which this pipeline never produces and never
receives ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b).

> **D4.26 — this system owns exactly two artifact roots, `OBSERVATION` and `TRUTH`. There is no third.
> Model output — detections, tracks, model assessments, associations, reports — is neither produced,
> consumed, stored, validated nor versioned here, and no artifact of this pipeline may be written into
> a location that holds it.** The separation that keeps truth out of a model's input is the
> `OBSERVATION`/`TRUTH` boundary, and it needs no third root to hold.

| Root | Written by | Contents | Guarantee it carries |
|---|---|---|---|
| `OBSERVATION` | the frame recorder and the collection-metadata writer, in the same call that writes the frame | imagery; sensor pose and intrinsics; radiometry; achieved solar state; the scenario epoch; the capture identity attributes `tick`, `sim_time_s`, `run_id`, `scenario_id`, `sensor_id` | **Observer-derivable in full.** Every field in it satisfies all four tests of `D4.20`. It can be put in front of a model with nothing removed, because nothing was ever added |
| `TRUTH` | the truth producer and the manifest writer | CoT truth sidecars; per-image label records; segmentation; `<_supervision>`; `<_aoi>`; coverage; the render-state accounting of `C2` §4.5; the run manifest and the corpus manifest | **Complete and self-describing.** It states what it contains, in what units, over what spans, and — `C2` `D4.7` — what it does *not* contain and why |

**Coordination with [`08`](08_Collection_And_EPoL.md).** The two documents reached this independently
and agree: `08` §3.5 specifies two roots and lays the session directory out over them, and `08`
§9.4 restates the anti-leak mechanisms over two writers. The division of labour is that `08` owns the
collection rationale — how the two roots are laid out, named and sessioned, and why — while `C8` owns
the contract, and `D4.26` is the ruling a later reader should cite. The properties this contract needs
from `08` are exactly two roots with one writer each, and the split performed at the writer rather than
by a later stripping step, which is `08`'s own second mechanism and is unaffected by the removal.
Where any text in the folder names a third root, `D4.26` is what it is measured against.

**Two roots, and two things that are not roots.** A **stream** is not a root — it is a delivery of the
same records (`D4.29`) — and the **transcript** is not a root either. The transcript holds bytes an
external chain returned; it is not an artifact of this pipeline, it is not corpus content, it is never
validated against a schema of ours and it is never versioned by us beyond its own container. `D4.26`'s
location clause — *no artifact of this pipeline may be written into a location that holds model output*
— is precisely what forces the transcript into a location of its own, beside the roots rather than
inside either. The clause is honoured by §10.10, not weakened by it; `D4.32` states the one respect in
which §3c refines `D4.26`'s word *stored*, and says so in place rather than quietly.

### 10.3 What the `OBSERVATION` root contains, exhaustively

Every field that travels with the pixels. **Adding a field to this list is a contract change**, and the
allow-list of V8.8 is the mechanical form of that sentence.

**This table is the record in both delivery modes** (`D4.29`). The only difference is how the pixels are
carried: in the deferred mode `frame` is a file the record names, and in the live mode it is inline
bytes in the same record. `image_sha256` makes the two provably the same image — a live consumer can
check the frame it received against the one the corpus later publishes, and an auditor can check that a
corpus frame was not re-encoded after emission. A digest of the pixels is
**not** an admission under `D4.20`: that rule governs inputs that are *neither truth nor pixels*, and a
hash of the pixels is a function of the pixels a consumer already holds, computable by anyone with the
image and telling them nothing they could not compute.

| Field | Type | Unit | Req. | Source |
|---|---|---|---|---|
| `frame` | image | — | yes | The capture PNG. A path in the deferred mode, inline bytes in the live mode — the only field whose *carrier* differs between modes |
| `image_sha256` | string | — | yes | Lowercase hex SHA-256 of the encoded image bytes, so one frame delivered twice is provably one frame |
| `sensor_id` | string | — | yes | `C4` |
| `tick` | integer | — | yes | The sidecar container's `tick` (`CotWriter.cs:42`) |
| `sim_time_s` | number | s | yes | Container's `sim_time_s` (`:43`) |
| `run_id` | string | — | yes | Container's `run_id` (`:44`) |
| `scenario_id` | — | — | **no — excluded** | **Resolved by the integration lead, 2026-09-18, in favour of [`08`](08_Collection_And_EPoL.md) §9.7 consequence 2.** `scenario_id` and `seed` are excluded from the `OBSERVATION` record and from the live stream header, in both delivery modes. They fail `D4.20` on two of its four clauses at once: no fielded observer can derive a simulator's scenario identifier or its seed from a sensor, a navigation solution, a clock and public reference data; and because every frame of one scenario carries the same value, the field indexes a *set of scenes* rather than describing one — which is a memorisation handle, not context. That is the same defect already recorded against the PNG's `carla:capture` chunk, and it would be worse in the live mode, where the value would ride a stream header to a consumer we know nothing about. Both fields remain **required in the `TRUTH` root and in the run manifest**, where §5.6's requirement that `scenario_id` actually be supplied still binds. Open question 15 is closed |
| `sensor_pose` | object | — | yes | `latitude`, `longitude`, `hae_m`, `azimuth`, `elevation`, `roll` — the platform event's `<point>` and `<sensor>` elements |
| `intrinsics` | object | — | yes | `width`, `height`, `fx`, `fy`, `cx`, `cy`, `hfov_deg`, `vfov_deg`, `model`, `distortion` — the `<_carla_intrinsics>` element |
| `radiometry` | object | — | yes | The camera's exposure and response record. The field set is [`08`](08_Collection_And_EPoL.md) §4.8's; `C8` requires only that it is present and is on this side |
| `solar` | object | — | yes | The frozen field set of §10.4a, from `<_solar>` and the `carla:solar` PNG chunk |
| `epoch` | object | — | yes | `civil_datetime`, `utc_offset_hours`, `utc_datetime` from `C9` §11.3, copied from the scenario package |

**It contains nothing else.** In particular it does not contain the sidecar's per-vehicle `<event>`
elements, `<_carla>`, `<_supervision>`, `<_aoi>`, the label records, the coverage file, the render-state
accounting, or any field reachable only from a manifest.

### 10.4 What is excluded from the `OBSERVATION` root, and where it lives instead

This table is a statement about **where our own fields live**, not about what is fed to a model. An
external model team receives **both** roots — it must, to train and to validate — and the separation exists so that the
team can put the observation root in front of a model without having to trust that something was
stripped out of it first.

**The same table binds the live emitter.** Every field in the right-hand column is as excluded from an
observation *stream* as it is from the observation root, and the exclusion is checked at the emitter
rather than assumed from the writer (V8.14). In a live handover the truth side is not merely a different
file but a different endpoint, off by default and enabled only by a recorded choice
([`08`](08_Collection_And_EPoL.md) §11.4, V8.15) — because on a live feed the audience may itself be
part of what is being exercised, which a file on disk cannot be.

| In the `OBSERVATION` root | In the `TRUTH` root, and only there |
|---|---|
| Imagery, sensor pose per frame, intrinsics, radiometry | `sumo_vehicle_id`, `entity_id`, `instance_id`, `actor_id` |
| `sensor_id`, `tick`, `sim_time_s`, `run_id`, `scenario_id` | `<_supervision>` in any form |
| The **authored** area table — `id`, `name`, `kind`, geometry (`C5` §7.1), which an author wrote and no truth derived | `<_aoi>` derived relations, which are computed from truth positions |
| The world's georeference and the bare-earth grid, for height | `_carla` truth extras: `type_id`, `base_type`, `special_type`, true dimensions, `color`, `role_name`, `capture:*` |
| **The scenario epoch and the per-frame solar state**, in the exact field set of §10.4a | `render_states[]`, the run manifest, the corpus manifest, `marked` |
| | `illumination`, the policy in force, and the solar residual — these are *statements about the capture*, not about the world |
| | The per-label quality block: occlusion, visible signature, apparent size, truncation, label crowding (§10.7) |

> **D4.15 — only the area *definitions* may be placed in the `OBSERVATION` root. Area *relations* are
> derived from truth positions and are therefore truth, and live in the `TRUTH` root.** An
> observation-side `<_aoi>` would state exact containment that no observer
> measured, so anything reading it would be reading a rule evaluated on noiseless inputs — the failure
> doc 20 §2.1 exists to prevent.

### 10.4a Observer-derivability: what may be placed in the `OBSERVATION` root

**The question.** Solar state is not truth about the scene — a fielded system knows the time and knows
where it is standing — but it is produced by the simulator and it is recorded beside truth. `C8` has to
say which root it belongs in, precisely enough that an implementer cannot get it wrong in either
direction: putting it on the truth side would withhold from a model something it is entitled to, and
admitting the wrong neighbouring field would leak.

> **D4.20 — the general principle. An input that is neither truth about the scene nor pixels may be
> placed in the `OBSERVATION` root if and only if it passes all four of these tests. Failing any one is
> disqualifying, and no argument from usefulness overrides a failure.**
>
> 1. **Fieldable.** A real system at the same place and time, with no access to the simulator, could
>    obtain it — from its own instruments, its own configuration, or public reference data.
> 2. **Scene-independent.** Its value does not depend on what is in the scene. Move every vehicle,
>    delete them all, change every annotation: the value is unchanged.
> 3. **Supervision-blind.** It is computed identically for every capture, by a rule fixed before the
>    run, that takes no supervision state as input.
> 4. **Sourceable from the observation side.** It is reachable from an artifact the observation writer
>    is already permitted to open, without opening a truth artifact. A field that is only in the run
>    manifest fails this test *even if it passes the other three*, because reaching it would breach
>    `D4.16`'s structural isolation.

*(`D4.20` is a question about our own data — what may be placed in the `OBSERVATION` root — and never a
permission granted to an external model. [`00_Overview.md`](00_Overview.md) cites it.)*

**The rule is delivery-blind, and the delivery is not a field.** `D4.20` admits or refuses a value; it
never asks how the value will travel. Nothing in §10.9's envelope — sequence numbers, drop counters,
emission wall time — is part of the record, none of it is admitted to the `OBSERVATION` field set, and
V8.13 requires that no envelope field be derived from truth, supervision or scene content. The envelope
describes the *link*; the record describes the *collection*; and the reason the distinction is written
down is the same reason `advancing` and `rate` are excluded below — a consumer that could read the
experiment off the delivery would be reading the capture plan.

Test 4 is the one that is easy to miss and it is why this is a contract clause rather than a policy
note. Tests 1–3 are about the *value*; test 4 is about the *path*. A field can be perfectly legitimate
and still be unavailable, and the right response is to publish it on the observation side rather than to
reach across.

**Applying it to solar state.** Every test passes, and the fourth passes because the plumbing already
exists:

| Test | Solar state |
|---|---|
| Fieldable | A fielded sensor knows its clock and its own georeferenced pose; solar elevation and azimuth follow from an almanac. This is arithmetic, not privileged information |
| Scene-independent | The value is a function of date, time, latitude and longitude only (`USunPositionFunctionLibrary::GetSunPosition`, called from `CesiumSunSky.cpp:422-434`). No actor, no annotation and no supervision state is an input |
| Supervision-blind | It is scene-scoped: one value per tick, identical for every actor and every sensor (`C4` §6.2). A scalar that is the same for every object in a frame cannot separate objects within it |
| Sourceable | It is already on the **observation** side. `<_solar>` is written into every sidecar (`CotWriter.cs:50-65`) and `carla:solar` into every PNG (`SolarMetadata.cs:14-19`, `PngEncoder.cs:44`, `FrameRecorder.cs:227`). The writer reads the same container attributes it already reads for `tick` and `sim_time_s` |

> **D4.21 — solar state and the scenario epoch are placed in the `OBSERVATION` root as *collection
> context*, in a named and frozen field set, written from the capture's `<_solar>` element and the PNG's
> `carla:solar` chunk — never copied from the run manifest.**
> [`08_Collection_And_EPoL.md`](08_Collection_And_EPoL.md) §8.2 already places `solar` and `epoch` in
> the context block that travels with the imagery; `C8` fixes which fields, and from where.

**The field set, exhaustively. Adding a field to this list is a contract change.**

| Field | In `OBSERVATION` | Source |
|---|---|---|
| `solar_time` | **yes** | `<_solar>@solar_time` (`CotWriter.cs:55`) |
| `date` (`YYYY-MM-DD`) | **yes** | `<_solar>@date` (`:56-57`) |
| `time_zone` | **yes** | `<_solar>@time_zone` (`:58`) — the sun's zone, needed to interpret `solar_time` |
| `sun_elevation_deg`, `sun_azimuth_deg` | **yes** | `<_solar>@sun_elevation_deg`, `@sun_azimuth_deg` (`:61-62`) |
| `lat`, `lon` | **yes** | `<_solar>@lat`, `@lon` (`:59-60`). These are the **georeference origin**, not any vehicle's position. A fielded sensor knows where it is |
| `epoch.civil_datetime`, `epoch.utc_offset_hours`, `epoch.utc_datetime` | **yes** | the `epoch` block, copied by the observation writer from the scenario package, not from the manifest |
| `advancing`, `rate` | **no** | `<_solar>@advancing`, `@rate` are present in the sidecar but are statements about *how the capture was produced*, not about the world. They fail test 2: two corpora of identical scenes differ in them. The observation writer strips them |
| `illumination_band`, `illumination_band_elevation` | **no** | `<_solar>@illumination_band`, `@illumination_band_elevation` (`C9` §11.8.3) are the corpus's stratification key ([`06`](06_Truth_And_Annotation.md) D6.23, V8.6), cut from the elevation by a convention ([`11`](11_Time_And_Illumination.md) §4.4) rather than measured. They add nothing a model could not derive from the elevation, and the set is not widened for them. The observation writer strips them |
| `illumination.policy`, `solar_residual`, `lamp_gaps[]`, `lamp_probe` | **no** | manifest-only; they fail test 4 and, for the residual, test 2 |

**Why `advancing` and `rate` are excluded even though they sit in the same element.** They describe the
experiment, not the scene. A model that learned "frozen sun means this is a sweep" would be reading the
capture plan, and capture plans correlate with what a run was built to show. It costs nothing to strip
two attributes, and the exclusion is where this clause's precision actually bites: the element goes into
the observation root, but not all of it does.

**The one thing that can still go wrong, and the check for it.** Solar state cannot separate two
vehicles in a frame, but it can separate *frames*. A corpus that placed every annotated interval at
night and every nominal one at noon would have made illumination a perfect between-frame separator of
its own supervision — a defect in **the data**, present whether or not anything is ever trained on it.
That is the standing constraint of [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a — illumination is derived
context, never a label — expressed as something checkable: V8.6 requires supervision prevalence to be
reported per solar bin, which is the number that makes the correlation visible in the corpus's own
description.

### 10.5 The anti-leak isolation, as a structural property

A policy that says "keep truth out of the observation root" is enforced by discipline. A structure that
makes truth unreachable from the observation writer is enforced by the build.

> **D4.16 — the `OBSERVATION` root is written by a component that holds no reference to any truth
> artifact, and the two roots are written as separate files by separate writers from the first byte.**
> The isolation is the whole of the rule: no join between truth and model output is produced here at
> all, and §10.6 is where that is stated.

Three properties, each checkable:

1. **Writer isolation.** The observation writer reads the frame, the camera's own configuration, the
   container attributes of §10.3 and the `<_solar>` element. It must be *incapable* of opening a truth
   artifact — in C# terms an assembly with no reference to `CarlaNet.Recording`'s truth types; in Python
   terms a module that does not import the truth reader. A validator can assert a reference graph; it
   cannot assert good intentions. V8.10 makes this a build-time check rather than a run-time one.
2. **The split happens at the writer, never at a copy step.** Today one `CotWriter` call produces one
   file holding both the sensor block and the truth events (`CotWriter.cs:71-198`). A single file that
   holds both forces a "strip the truth out" step to be invented later, and a stripping step is a thing
   that gets forgotten or gets a bug. The two roots must be separate files from the start, so that
   nothing is ever stripped because nothing was ever combined. This is
   [`08`](08_Collection_And_EPoL.md) `D8.17` mechanism 2, and `C8` depends on it.
3. **The handover is one-directional into every artifact, and the roots are immutable after it.**
   Nothing outside this system writes into either root, and nothing this system produces is derived from
   anything an external consumer returns. The live mode makes this sentence carry more weight rather
   than less, because bytes genuinely can arrive: a live exercise may receive tracks or reports. The
   guarantee is therefore stated over **artifacts**, not over sockets — *no artifact this system
   produces has any inbound path* — and the one place inbound bytes may go is a store that no producer
   of ours reads (§10.10, `D4.32`). A feedback path needs a reader; there is none, and V8.20 is the
   build-time check that keeps it that way.
4. **In the live mode the same isolation binds the emitter.** One endpoint per root, never one stream
   carrying both; the observation emitter's build carries no truth reference exactly as the observation
   writer's does (V8.10 covers both); and the envelope writer can no more reach truth than the record
   writer can (V8.13). A socket is not a loophole in a structural rule.

### 10.6 Supervision transfer — a format guarantee and a documented rule, never an operation

Doc 20 §7.6 requires that supervision be transferable from truth onto a consumer's tracks. Performing
that transfer needs model output, which this pipeline never sees. The obligation therefore splits
cleanly, and `C8` takes only the half that is about our artifacts.

> **D4.27 — this contract guarantees that truth is emitted in an *associable form*, and documents the
> rule by which supervision would transfer. It does not perform the association, ship a harness for it,
> or produce any artifact derived from a consumer's output.** The format is normative for us; the rule
> is documentation for the consumer.

**`D4.27` is unaffected by the live mode and is confirmed here in one line:** the transfer rule is
published, never performed, in either delivery mode — and the live mode is the single place in this plan
where performing it would be easy, since both streams exist in one process on one tick base, which is
why the prohibition is restated rather than assumed ([`02`](02_Use_Cases.md) `D2.24` reaches the same
conclusion from the use-case side).

**The format guarantee — normative, checkable, ours.** For every capture tick, for every rendered
vehicle, the `TRUTH` root carries:

| Guaranteed property | Where it is | Why a transfer rule needs it |
|---|---|---|
| **Per tick** | one sidecar per `(sensor_id, tick)`; the tick is on the container (`CotWriter.cs:42`) and is the cross-sensor join key (`C4` §6.6) | a consumer's track is a time series, and can only be matched instant by instant |
| **Positioned** | `<point>` with `lat`, `lon`, `hae` per vehicle, in the same frame and datum as the imagery's own georeference | the rule gates on position |
| **Timed** | `sim_time_s` on the container (`:43`), and interval bounds `t_begin_s` / `t_end_s` in the manifest, in the same simulated-seconds clock | clipping a track at an interval boundary needs the boundary |
| **Boxed** | the 2D and 3D boxes on the per-image label record, in the frame's own pixel coordinates and in world coordinates | image space is where a detector's error actually lives, so the box has to exist there too |
| **Identified, stably** | `sumo_vehicle_id` and `entity_id` on every truth event (`C4`), so transferred supervision names something that survives across runs | `actor_id` alone cannot support cross-run comparison (`C4` §6.8) |
| **Supervised, with bounds** | `<_supervision>` per tick, and the interval bounds in the manifest — the per-frame element alone cannot support clipping (doc 20 §7.6) | the manifest is the authoritative artifact for interval extent |
| **Honestly scoped** | `observed_spans[]` and `observed_union_s` per sensor (`C2` §4.5) | a transfer over a span nothing observed is a transfer onto nothing, and the corpus says which spans those are |

**The documented rule — not performed here, and the reason it is written down at all.** A consumer who
wants supervision on their own tracks applies this rule. Nothing in this pipeline implements it, and no
artifact here is derived from having applied it.

1. Match a track to a truth vehicle **by position and time, never by identifier**. Doc 09 §3 and §9 fix
   this: truth uids are `CARLA-TRUTH-<actor_id>` (`CotWriter.cs:134`) and a non-truth source uses a
   different prefix, so an identifier join is not merely discouraged — it is not available, and §10.8
   records that the corpus keeps it unavailable deliberately.
2. Transfer supervision **per `(sensor_id, track, interval)`**, never per entity. One truth vehicle maps
   to several tracks — identity switches, re-acquisitions — and with N sensors there are N independent
   track sets, so one truth interval yields up to N supervised spans that overlap in time and differ in
   coverage (doc 20 decision 15).
3. **Clip** a track that spans an interval boundary; never label it wholesale. The bounds come from the
   manifest, not from the per-frame element.
4. Where the corpus reports a label as crowded, ambiguous, heavily occluded, truncated, or of
   `visible_signature = none` (§10.7), the supervision transferred onto that label is correspondingly
   uncertain — and the corpus said so before anyone asked.

**What this contract therefore does not contain, and will not grow:** an assignment step, a gate radius,
a cost function, a bipartite solver, an adjudication table, or any artifact holding the result of
running one. Those belong to the consumer, outside the scope [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b
sets.

### 10.7 Label quality versus model performance — the ruling, field by field

An association-quality block names fields of two kinds: some describe **how good our label is**, the
rest describe **how well a model matched it**. Only the first kind is ours — and the first kind needs no
model at all. Every field this contract keeps is computable from truth and the rendered frame alone,
before any consumer exists.

> **D4.28 — a quality field survives in the corpus if and only if it is computable from this system's
> own truth and imagery with no model output as an input. Such fields attach to the *label*, per
> `(sensor_id, tick, vehicle)`, in the `TRUTH` root — not to an assignment, because there are no
> assignments here.**

| Association-quality field | Ruling | The form it takes here |
|---|---|---|
| `occlusion_at_assignment` | **Label quality — kept.** Computed by the occlusion measurement already built ([doc 17](../../Findings/17_Photoreal_Occlusion_Metric.md)) from our own render, with no detector in sight | `occlusion` on the per-image label record. A label on a 90 %-hidden vehicle is a weak label, and the corpus says which ones they are |
| `signature_at_assignment` | **Label quality — kept.** `visible_signature` ([`08`](08_Collection_And_EPoL.md) §5.8) is derived from our own imagery and our own lamp state | `visible_signature` on the label record. At night a box may enclose two lamps and no vehicle; that is a fact about the label |
| `truth_density` | **Label quality — kept, in truth-only form.** Defined as "truth vehicles inside the detection's gate", it needs a detection; the quantity that makes it useful does not — how crowded this label is by *other labels* | `label_crowding` — the number of other truth vehicles whose projected boxes fall within a declared radius of this one — and `nearest_label_px`, the distance to the closest. Both from truth projections only. The radius is a declared parameter, valued in [`10`](10_Scale_And_Performance.md) |
| `margin` — distance to the next-best truth candidate for a detection | **Split.** The margin itself needs a detection and is model performance: **not produced.** What makes a margin small is a property of the labels — two of them are close together | subsumed by `nearest_label_px`. How separable two labels are is ours; how well something resolved them is not |
| `association_ambiguous` | **Label quality — kept, in truth-only form.** It is almost truth-only already: it fires when *a second truth entity* is within the gate | `supervision_transfer_ambiguous` — true when `label_crowding > 0` within the declared radius **and** the neighbouring labels carry different supervision. That is exactly the case in which any transfer rule, run by anyone, could put the wrong supervision on a target, and it is decidable from truth alone |
| `association_quality` — the gate residual | **Model performance — not produced.** A residual between a detection and a label is a statement about the detection | not produced. A consumer computing it has the label-quality block to interpret it against, which is the whole reason the block exists |
| `residual_px`, `residual_norm` | **Model performance** | not produced |
| `assigned_fraction`, `dominant_truth_fraction`, `switch_count` | **Model performance.** All three are properties of a track | not produced |

**Apparent size and truncation** are not association fields at all: they are per-label
descriptors of what the corpus contains, they stay, and `C2` §4.5's `observed_spans[]` is their
per-vehicle counterpart over time.

The net effect is that the requirement doc 20 §7.6 actually raised — *"the association quality per
assignment should be recorded, so a mis-associated label is findable later rather than being an
unexplained hard example"* — is met **better** on this side of the line than on the other. A consumer
who finds a hard example looks the label up and reads that it was crowded by two differently-supervised
neighbours at 4 px separation under `visible_signature = lamps`. The example stops being unexplained,
and nobody measured a model to get there.

**`D4.28` is unaffected by the live mode and is confirmed here in one line:** every field it keeps is
computable from our own truth and our own render before any consumer exists, so the label-quality block
rides the truth side in both modes and needs nothing from anybody.

### 10.8 What the corpus deliberately does not contain

Stated positively, because a consumer who does not know an omission is deliberate will read it as a bug.

| Not in the corpus | Why |
|---|---|
| Model output of any kind — detections, tracks, assessments, reports | Not produced here, and `D4.26`: there is no root for it. A live exercise may **record** what came back, verbatim, outside both roots (§10.10) — recorded is not contained, and the transcript is excluded from the corpus unless releasing it is an explicit, recorded choice |
| A schema, a format or a transport for anything the external chain produces or consumes | §10.11. This contract specifies what *we* emit; the transcript's **container** is ours and its **content** is not our schema |
| Any field derived from the transcript | `D4.32`. The bytes are recorded; nothing is computed from them, and no field of any artifact specified here is a function of them |
| An assignment or association table | `D4.27`: the rule is documented, the operation is not performed |
| Any model metric, score, confusion matrix or model comparison | [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b |
| Any identifier by which a consumer's track could be joined to truth without measuring position and time | Deliberate. Truth's uid grammar is `CARLA-TRUTH-<actor_id>` (`CotWriter.cs:134`), truth's container is marked `source="truth"` (`:34`), truth's `how` is `m-g` (`:136`) and truth's `ce`/`le` are exactly `0.0` (`:145-146`). Those markers exist so that truth is **self-identifying** and can never be mistaken for, or silently merged with, something a consumer produced |
| Imagery-side truth for a `simulated_only` vehicle | `C2` `D4.7`. The vehicle's behaviour is in the corpus; no sensor ever saw it, and `render_state` says so explicitly rather than by absence |
| Truth for any partition the corpus declares as reserved | If [`08`](08_Collection_And_EPoL.md) reserves a partition whose truth is not released, `C8` requires only that the corpus manifest **declares** the partition and the fact of the omission. Whether to reserve one at all is `08`'s call; an undeclared omission is a defect either way (V8.9) |
| Pedestrians | [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3.5 |

### 10.9 The live delivery mode — timing, identity, attachment and detachment

A **live handover** emits the records of §10.3 and §10.6 as they are produced, to whatever is listening.
The emitter is ours and is fully specified here. The listener is not ours and is not specified anywhere
in this plan (§10.11). Everything below is a statement about what leaves this process and when; nothing
below is a statement about what receives it.

#### 10.9.1 The transport, as three properties and nothing more

`C8` does not name a socket, a framing, a broker or a wire format. It names the three properties any
binding must have, and [`08`](08_Collection_And_EPoL.md) picks the binding.

| # | Property | Why it is required rather than preferred |
|---|---|---|
| **P1** | **Record boundaries are preserved.** A consumer can tell where one record ends and the next begins without parsing the payload | A consumer that has to parse to find the end has to understand the format to receive it, which couples receipt to schema |
| **P2** | **Order is preserved within one stream to one consumer.** Records are never delivered out of order | L2's monotonicity is what lets a consumer detect loss by arithmetic instead of by bookkeeping we would otherwise have to send |
| **P3** | **The emitter never blocks on the consumer.** A transport whose only delivery mode is to block the producer is not admissible | `D4.30`. A blocking transport hands the clock to the consumer |

A transport may of course offer more — reliability, ordering across streams, authentication. This
contract neither requires nor forbids any of it. *(Carried forward from
[`08`](08_Collection_And_EPoL.md) §11.4, which records that today's live CoT feed is one event per UDP
datagram, `CotUdpEmitter.py:38-44` — one admissible binding of P1–P3, cited as precedent and not as a
requirement.)*

#### 10.9.2 Three record kinds, and the envelope that is not part of the record

A stream carries exactly three kinds of record. The **envelope** is delivery bookkeeping; the
**record** is the §10.3 observation record or the truth record, byte-identical to the deferred mode's
(`D4.29`). Nothing in the envelope is part of either, and nothing in the envelope may be derived from
truth, supervision or scene content (V8.13).

**`stream_header` — written once to every consumer at the moment it attaches.** It exists so that a
consumer that joined halfway through a seven-day run is as well equipped as one that was there at the
start: everything needed to interpret every later record on this stream is in it.

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `record_kind` | string | — | yes | `stream_header` |
| `handover_contract_version` | integer | — | yes | The `C8` version this stream speaks. A consumer that does not implement it stops reading (§10.12) |
| `stream_kind` | string | — | yes | `observation` or `truth`. **One kind per stream, never both** — the live form of `D4.16` |
| `root_version` | integer | — | yes | The version of the root whose records travel here, so a stream and a corpus are comparable |
| `run_id` | string | — | yes | `C4`. Provenance for one execution |
| `session_id` | string | — | yes | The capture session ([`08`](08_Collection_And_EPoL.md) §3.5) |
| `scenario_id` | string | — | on a truth stream, yes | On an observation stream, exactly as §10.3 rules — which is the open conflict recorded at open question 15 and must be closed before either mode ships |
| `sensor_ids[]` | array of string | — | yes | Every sensor whose records may appear on this stream (`C4` §6.1) |
| `epoch` | object | — | yes on an observation stream | The `C9` epoch block, so a late attacher can turn `sim_time_s` into civil time with no other artifact. Admitted by `D4.21` |
| `capture_interval_sim_s` | number | s | yes | The declared spacing of captures per sensor, in simulated seconds — **the rate a consumer must meet** |
| `declared_real_time_factor` | number | — | yes | Simulated seconds per wall-clock second the run is paced at; `0` means as fast as the machine allows |
| `drop_policy` | string | — | yes | `drop_oldest` — the only value this contract defines ([`08`](08_Collection_And_EPoL.md) §11.3) |
| `queue_depth_frames` | integer | — | yes | The emitter's per-consumer buffer depth. Stating it is what makes L4's staleness bound a number rather than a hope. Valued in [`10`](10_Scale_And_Performance.md) |
| `identity_keys[]` | array of string | — | yes | The join keys on this stream, in order — `["sensor_id", "tick"]`. Stated rather than assumed, so a consumer never guesses (`C4` §6.6) |
| `image_encoding` | string | — | yes on an observation stream | The encoding of the inline image bytes. A statement about **our** bytes, not a requirement on their decoder |
| `stream_opened_tick`, `stream_opened_sim_time_s` | integer, number | —, s | yes | Where this consumer joined, in the run's own clock |
| `run_started_wall_utc`, `stream_opened_wall_utc` | string | — | yes | Delivery bookkeeping only. Nothing rendered or recorded depends on it (§1) |

**`observation_frame` / `truth_frame` — the envelope.**

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `record_kind` | string | — | yes | `observation_frame` or `truth_frame` |
| `seq` | integer | — | yes | Assigned **at capture**, strictly increasing by one per `(stream_kind, sensor_id)`. It numbers the *capture*, not the delivery, so two consumers receiving one frame see one `seq`. A gap **after a consumer's first record** is exactly a drop, so loss is detected by arithmetic and needs nothing from us; a consumer that attached mid-run simply starts at whatever number the run had reached, and `stream_opened_tick` in its header is what says so |
| `dropped_cumulative` | integer | — | yes | Drops on this stream so far. A cross-check that survives a burst of drops at the very end of a run, when there is no later `seq` to reveal the gap |
| `emitted_wall_utc` | string | — | yes | When the emitter handed it to the transport. Delivery bookkeeping; **never a join key** (L3) |
| `record` | object | — | yes | The §10.3 observation record, or the truth record of §10.6 — identical to the deferred mode's, with `frame` carried as inline bytes and `image_sha256` covering them |

**`stream_close` — written once when the stream ends, for every stream that was opened.**

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `record_kind` | string | — | yes | `stream_close` |
| `closed_reason` | string | — | yes | `run_complete` \| `run_failed` \| `operator_stopped` \| `emitter_shutdown` |
| `last_tick`, `last_sim_time_s` | integer, number | —, s | yes | The last record emitted on this stream |
| `per_sensor[]` | array | — | yes | `{ sensor_id, frames_emitted, frames_dropped, first_tick, last_tick }`. Empty counts are written, never omitted |
| `not_delivered_recorded` | boolean | — | yes | True when every dropped `(sensor_id, tick)` has been written into coverage as *covered but not delivered* ([`08`](08_Collection_And_EPoL.md) §11.3). This is the stream's form of V8.9: an undeclared gap is indistinguishable from data that never existed |
| `run_record_uri` | string | — | no | Where the run record (`C10`, §12) will be found, when the deployment makes it reachable. **A close record carries no verdict**, because nothing in this system issues one (`D4.33`); a consumer that wants to know what a run produced and what was checked reads the record, and a run the caller killed leaves no close record at all (§12.7) |

#### 10.9.2a Worked example — one attach, one frame, one close

The sizing scenario's night window, on the epoch of `C9` §11.9. **The numeric values below are
illustrative and are [`10`](10_Scale_And_Performance.md)'s to fix; the shape is this contract's.** The
`record` is elided because it is §10.3's table and is identical to the corpus's — which is the whole
point of `D4.29`.

```json
{ "record_kind": "stream_header",
  "handover_contract_version": 1,
  "stream_kind": "observation",
  "root_version": 1,
  "run_id": "cap-20260321-2300",
  "session_id": "bahonar-night-01",
  "sensor_ids": ["OVERWATCH-1", "OVERWATCH-2"],
  "epoch": { "civil_datetime": "2026-03-21T00:00:00+03:30", "utc_offset_hours": 3.5,
             "utc_datetime": "2026-03-20T20:30:00Z" },
  "capture_interval_sim_s": 0.2,
  "declared_real_time_factor": 1.0,
  "drop_policy": "drop_oldest",
  "queue_depth_frames": 8,
  "identity_keys": ["sensor_id", "tick"],
  "image_encoding": "image/png",
  "stream_opened_tick": 1656000, "stream_opened_sim_time_s": 82800.0,
  "run_started_wall_utc": "2026-03-21T17:02:11Z", "stream_opened_wall_utc": "2026-03-21T17:04:03Z" }

{ "record_kind": "observation_frame",
  "seq": 561, "dropped_cumulative": 0,
  "emitted_wall_utc": "2026-03-21T17:04:05.214Z",
  "record": { "sensor_id": "OVERWATCH-1", "tick": 1656004, "sim_time_s": 82800.8,
              "run_id": "cap-20260321-2300", "image_sha256": "9f1c…", "frame": "<bytes>",
              "sensor_pose": { "…": "§10.3" }, "intrinsics": { "…": "§10.3" },
              "radiometry": { "…": "§10.3" },
              "solar": { "solar_time": 23.0000002778, "date": "2026-03-21", "time_zone": 3.5,
                         "lat": 27.15012, "lon": 56.18065,
                         "sun_elevation_deg": -59.6, "sun_azimuth_deg": 333.5 },
              "epoch": { "civil_datetime": "2026-03-21T00:00:00+03:30", "utc_offset_hours": 3.5,
                         "utc_datetime": "2026-03-20T20:30:00Z" } } }

{ "record_kind": "stream_close",
  "closed_reason": "run_complete",
  "last_tick": 1665000, "last_sim_time_s": 84600.0,
  "per_sensor": [ { "sensor_id": "OVERWATCH-1", "frames_emitted": 8994, "frames_dropped": 6,
                    "first_tick": 1656004, "last_tick": 1665000 },
                  { "sensor_id": "OVERWATCH-2", "frames_emitted": 9000, "frames_dropped": 0,
                    "first_tick": 1656004, "last_tick": 1665000 } ],
  "not_delivered_recorded": true,
  "run_record_uri": "…/cap-20260321-2300/run_record.jsonl" }
```

**Three things to read off it.** The record carries `solar` **without** `advancing` and `rate`, because
§10.4a strips them on the observation side and a stream is no exception (V8.14). The envelope carries
`seq` and `dropped_cumulative` and nothing about the scene (V8.13). And `frames_dropped: 6` on one sensor
is not a defect in the corpus — it is six ticks the link did not carry, recorded in coverage as covered
but not delivered (V8.16), with the corpus itself complete.

#### 10.9.3 The guarantees — seven, each checkable

| # | Guarantee | What it rules out |
|---|---|---|
| **L1 — emission follows the tick** | A record is emitted only after the tick that produced it has completed (`C6` §8.3). No record ever describes a tick the world has not finished | A consumer seeing a half-applied world: poses from tick *n*, sun from tick *n−1* |
| **L2 — monotone, gappy, never reordered, never duplicated** | Within a stream, `tick` is non-decreasing and `seq` increases by one per `(stream_kind, sensor_id)` from the consumer's first record onward. Gaps in `tick` are legal — capture is decimated and frames may be dropped. A gap in `seq` after the first record is a drop and nothing else; a duplicate `seq` is never legal | A consumer having to de-duplicate, and a consumer unable to distinguish decimation from loss |
| **L3 — the tick is the only join key** | `seq`, `emitted_wall_utc` and the record's arrival order are delivery bookkeeping and must never be used to pair anything, across sensors or across streams (`C4` §6.6; [`08`](08_Collection_And_EPoL.md) `D8.4`) | Two sensors paired by arrival order, which is a race, not a fact |
| **L4 — staleness is bounded and the bound is published** | The emitter holds at most `queue_depth_frames` per consumer and drops the oldest rather than delaying the newest, so the newest record on a stream is never more than `queue_depth_frames × capture_interval_sim_s` of simulated time behind the world | A consumer reading an hour-old world because a queue grew without bound |
| **L5 — the link never owns the clock** | No consumer, transport or transcript peer can stall, pace, rewind or otherwise influence the advance of simulated time (`D4.30`, and `D4.11`'s single owner) | A slow consumer silently changing the illumination, since the sun advances on the world tick (`D4.25`) |
| **L6 — streams are frame-coherent** | Every stream of a session emits records built from **one** world-observer snapshot per tick, so two streams stamping one tick cannot disagree about the sun or the supervision state ([`08`](08_Collection_And_EPoL.md) §3.4) | Two channels of one exercise showing two different skies |
| **L7 — truth is a separate stream on a separate endpoint, off by default** | The observation emitter holds no truth reference (`D4.16`, V8.10), and in a live handover the truth stream is enabled only by an explicit recorded choice (V8.15) | Truth reaching an audience that is itself part of what is being exercised |

#### 10.9.4 Attachment and detachment

> **D4.31 — a consumer attaches and detaches at will, and the run is indifferent to both.** Every stream
> opens with a `stream_header` and ends with a `stream_close`. There is no handshake, no
> acknowledgement, no replay of history and no request channel. **Zero consumers is a legal state and is
> the default**, and neither the absence, the arrival, nor the departure of a consumer changes a single
> byte of what the run produces.

```mermaid
sequenceDiagram
  autonumber
  participant W as World + collection (ours)
  participant E as Handover emitter (ours)
  participant C1 as Consumer A (unknown)
  participant C2 as Consumer B (unknown)

  Note over W,E: the run is already going; nobody is listening, and that is normal
  W->>E: record for tick n
  E->>E: no consumers — nothing is queued, nothing is an error

  C1->>E: attach
  E-->>C1: stream_header (identity, epoch, capture interval,<br/>drop policy, queue depth, join keys)
  W->>E: record for tick n+1
  E-->>C1: observation_frame seq=1

  C2->>E: attach (mid-run)
  E-->>C2: stream_header — a late joiner is fully equipped
  W->>E: record for tick n+2
  E-->>C1: observation_frame seq=2
  E-->>C2: observation_frame seq=2

  Note over E,C1: Consumer A falls behind; its queue is full
  W->>E: record for tick n+3
  E->>E: drop oldest for A, count it,<br/>write coverage: covered but not delivered
  E-->>C2: observation_frame seq=3
  Note over W,E: the world does not slow down (L5) and B is unaffected

  C1--xE: detach, without warning
  E->>E: count, continue. The run neither fails nor changes
  W->>E: last record
  E-->>C2: stream_close (counters, reason, run-record location)
```

**The rules the diagram encodes:**

1. **Attach at any time.** A new consumer receives a fresh `stream_header` and then the *next* record.
   The stream is not a queue and history is not replayed: a live handover is a window onto a running
   world, and a consumer that wants the past reads the corpus, which is the same records (`D4.29`).
2. **Detach at any time, without notice.** The emitter counts what it could not deliver and continues.
   A consumer that vanishes is not a failure; it is a fact recorded in `per_sensor[].frames_dropped` and
   in coverage.
3. **Several consumers are independent.** Each has its own queue and its own drop accounting. **One slow
   consumer must not affect another, and neither may affect the world** (L5). A shared queue would make
   the slowest consumer the pacer, which is `D4.30` by the back door.
4. **No consumer state reaches any artifact.** Counters of what *we* could not deliver are ours and are
   recorded; nothing a consumer says about itself is recorded anywhere except, as opaque bytes, in the
   transcript (§10.10).

#### 10.9.5 Pacing — the answer to the brief's open engineering question

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c asks what happens when the external chain cannot keep up, and
names three candidates: dropping frames and recording the drop, letting simulated time advance more
slowly in wall-clock terms, or running ahead and buffering. **The answer is two of them, and they are
different knobs at different times — and neither is under the consumer's control.**

| When | What happens | Why |
|---|---|---|
| **Before the run** | The operator **declares** a real-time factor. Running the world slower in wall-clock terms is legitimate and **costs no truth at all**, because every truth field is stamped in *simulated* time: a world that ticks slower is still internally exact, and the sun still advances on the world tick, so the scene stays consistent with the scenario ([`08`](08_Collection_And_EPoL.md) §11.1, carried forward, which cites `CesiumTimeOfDayController.cpp:34-36`) | This is §3c's second candidate, taken deliberately and in advance. If the chain is known to be slow, the honest response is to run the exercise slower, not to show it a sparser world |
| **During the run** | The world does **not** slow down in response to the link. The emitter drops the oldest queued frame, counts it, and coverage records that `(sensor, tick)` as *covered but not delivered* ([`08`](08_Collection_And_EPoL.md) §11.3) | This is §3c's first candidate. It is the only one that can be applied without the consumer becoming an owner of simulated time (`D4.11`, `D4.30`) and without the illumination changing under everyone (`D4.25`) |
| **Never** | Running ahead into an unbounded buffer | Latency would grow without bound and the consumer would be reasoning about a world that has moved on — and nothing would say so. L4's bounded depth is the explicit refusal |

> **D4.30 — the link never owns the clock. Pacing is declared before the run and never negotiated during
> it; no consumer, transport or transcript peer may stall, pace or otherwise influence the advance of
> simulated time.** A consumer that cannot keep up loses frames, the loss is counted, and the coverage
> record states it. Back-pressure is refused not because it is hard but because it would make the
> consumer a second owner of time (`D4.11`) and would move the sun (`D4.25`), which would change the
> illumination of an exercise in response to how fast somebody else's software happened to run.

**What the operator sees** is [`12`](12_Operator_Control_Surface.md) §7.1's monitor, and it already has
the right fields: frames written and `Dropped` per channel, with a non-zero `Dropped` as one of its
three loud conditions. Two facts make that surface cheap rather than new: `FrameRecorder` already
applies drop-oldest to its own encode queue and already counts it — the channel is created with
`FullMode = BoundedChannelFullMode.DropWrite` and the comment *"never block the stream-reader thread"*
(`CarlaNet/src/CarlaNet.Recording/FrameRecorder.cs:116-118`), the counter is incremented at `:185` and
exposed at `:49` — **and it has no reader anywhere in the tree**, which is
[`12`](12_Operator_Control_Surface.md) §7.3's first silent failure. The live mode needs the same counter
for a second queue, so one reader closes both. *(Read from the source at the lines cited, 2026-09-18.)*

**The property this needs from [`03`](03_CoSimulation_Runtime.md):** a real-time factor on the *world*
tick, computed against an absolute target rather than a per-step sleep, and the **achieved** factor
reported per window. The pattern exists and is worth copying rather than reinventing: `SumoCotBridge`
paces SUMO alone with an absolute target — `behind = (started_at + (now - sim_start) / real_time_factor)
- time.monotonic()` — under the comment *"a step that overruns is absorbed by the next one instead of
accumulating drift over a long run"*
(`CarlaControl/src/carlacontrol/SumoCotBridge.py:243-246`). *(Read from the source, 2026-09-18.)* Over
seven simulated days a per-step sleep would drift; that is why the detail is in the contract and not
left to an implementer.

If a deployment makes the factor adjustable while a run is in progress, the adjustment goes **through
the clock owner** and is recorded per window like any other declared quantity — it is an input to the
owner, not a second owner — and it is never a function of consumer state. Whether the factor is mutable
at all is [`12`](12_Operator_Control_Surface.md)'s call.

### 10.10 The transcript — an opaque container for whatever comes back

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c: *"Anything that comes back is received data, with its own
provenance… we may record them verbatim and tick-stamped as a transcript, because a transcript is a
record of what happened."* [`02`](02_Use_Cases.md) UC-8 step 4 is the use case, and
[`02`](02_Use_Cases.md) `D2.24` is the same ruling from the use-case side.

**What the transcript is:** a record that certain bytes arrived from a certain source at a certain
instant of simulated time. That is the entire claim it makes.

**What the transcript is not:** truth, supervision, a label, an input, a measurement, a denominator, a
corpus artifact, or a schema of ours.

> **D4.32 — anything an external chain returns is recorded, if it is recorded at all, as an opaque blob
> with a timestamp, a source id and a content type: verbatim, never parsed for meaning we act on, never
> merged into truth or supervision, never measured, and never inside either artifact root.** We define
> and version the **container**; **its content is not our schema**. The transcript is write-only from
> this system's point of view — nothing we produce reads it — and releasing it is an explicit, recorded
> choice rather than a default.
>
> *This refines [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b's and `D4.26`'s word **stored** in exactly one
> respect — verbatim retention of received bytes, outside both roots — and in no other. `D4.26`'s
> two-root ruling stands, its prohibition on **consuming**, **validating** and **versioning** model
> output stands, and its location clause is what puts the transcript in a location of its own rather
> than being weakened by it.*

**Artifact.** One directory per run, beside the two roots and inside neither:
`<run>/transcript/index.jsonl` with one row per received record, and `<run>/transcript/blobs/<seq>.bin`
holding the bytes. Written by a transcript recorder that holds a reference to neither root's types, and
read by nothing in this system.

**Fields — the whole container, and there is deliberately nothing else in it.**

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `transcript_version` | integer | — | yes | Versions **the container**. It says nothing about the content and never will |
| `transcript_seq` | integer | — | yes | Strictly increasing per run; also the blob's file name |
| `received_tick` | integer | — | yes | The simulated tick current when the bytes arrived (`C6`). This is the tick-stamp §3c asks for, and it is *our* clock reading, not anything the sender claimed |
| `received_sim_time_s` | number | s | yes | The same instant in simulated seconds |
| `received_wall_utc` | string | — | yes | Wall-clock receipt. The **third and last** place wall-clock time appears in this contract (§1) |
| `source_id` | string | — | yes | Which configured peer it came from. **Minted by us at configuration time**, never a name a peer chose for itself, so two peers cannot collide and no peer can assert its own provenance |
| `content_type` | string | — | yes | Exactly as the sender declared it; `application/octet-stream` when it declared none. **Recorded, never used to select a parser** |
| `byte_count` | integer | — | yes | Length of the blob |
| `sha256` | string | — | yes | Digest of the blob, so a released transcript is provably the one that was recorded |
| `blob_path` | string | — | yes | Relative path to the bytes |
| `truncated` | boolean | — | yes | True when a cap below cut this record. Never omitted — a silent truncation is a lie about what arrived |
| `run_id`, `session_id` | string | — | yes | So one transcript row is self-locating without the manifest |

**The rules, each checkable:**

1. **Verbatim.** The bytes are written as received. Nothing is decoded, re-encoded, pretty-printed,
   normalised, re-serialised or re-ordered. A transcript that has been through a formatter is not a
   transcript.
2. **No parser.** `content_type` is recorded and never dispatched on. No component in this system may
   contain a decoder selected by a transcript's content type — which is a code-review rule with teeth,
   because the decoder is the thing that would later grow a field somebody reads.
3. **Nothing is derived from the content** except `byte_count` and `sha256`, both of which are
   properties of the bytes rather than of their meaning.
4. **Nothing reads it.** No truth record, supervision record, coverage row, manifest field, corpus
   summary, quality gate or field of the `C10` run record is a function of any transcript row (V8.20). The record
   may *report* that a transcript exists and how large it is; it may never **gate** on it, because a
   gate on received data would make somebody else's software a determinant of whether our data is fit.
5. **Caps, declared and never fatal.** A per-record byte cap and a per-run byte cap, valued in
   [`10`](10_Scale_And_Performance.md). Exceeding either sets `truncated` or stops recording, records
   the fact, and **never stops the run** — the exercise does not fail because the far end became
   talkative.
6. **Outside both roots, and outside the corpus by default.** Releasing a transcript is legitimate
   ([`02`](02_Use_Cases.md) UC-8 alternate flow: it is *their* output beside *our* truth on a common
   tick base) and it is an explicit, recorded choice, declared in the corpus manifest like any other
   inclusion.
7. **Not a feed.** A transcript peer is a receiver of nothing from us beyond what §10.9 already emits;
   opening a transcript endpoint grants no additional access to any artifact.

**Stated plainly, because it is the point of the whole section: the content is not our schema.** We do
not define it, validate it, migrate it, document it or promise anything about it. If the external chain
changes its report format tomorrow, or is replaced by a different chain with a different format, **not
one field above changes** — which is the substitution test of §10.11 applied to the one place where
bytes come back.

### 10.11 What this contract does not specify about the external side

> **D4.34 — the substitution test. No clause of this contract may become false, ambiguous or
> unimplementable if the entire external chain is replaced by a different one.** Any clause that names a
> consumer's format, schema, transport, stage, latency or behaviour is a defect in this contract and is
> removed on sight. What we owe is: **what we emit, in what form, with what timing and identity
> guarantees, and how an arbitrary consumer attaches and detaches.** Nothing more — and those four
> obligations live in §10.3 (what we emit and in what form), §10.6 (the truth we emit and the guarantees
> on it) and §10.9 (the timing, the identity, and attaching and detaching).

| Not specified here, deliberately | Why | What a reader might mistake for it |
|---|---|---|
| A detector's or a model service's **input schema** | It is theirs. We publish a record (§10.3); how anybody maps it into their own inputs is their design | [`08`](08_Collection_And_EPoL.md) §7.2 defines **our** record. It is not a detector's input schema and does not become one by being read by a detector |
| A **track format** | Ditto | [`08`](08_Collection_And_EPoL.md) §7.3 writes down a *minimum shape* for one reason only: the transfer rule of §10.6 has to state the shape it applies to. It is **illustrative**, it is `08`'s, and `C8` does not import it, does not validate against it and does not break if no consumer ever matches it |
| A **report or assessment schema** | Never ours. §10.10's container is the only thing we define, and it holds bytes, not fields | An `<_epol>` detail child on a CoT feed — an idea [`08`](08_Collection_And_EPoL.md) §11.4 explicitly **withdrew**, because this pipeline emits no assessments |
| A **fusion or normalisation stage** for consumer output | None exists here. Coverage is published **both** per sensor and unioned ([`08`](08_Collection_And_EPoL.md) §11.4), so a consumer picks whichever matches what they did, and nothing of ours waits on a stage that was never ours | The multi-channel question, which is ours only insofar as it changes what *our* coverage record means |
| **Latency, retry or error semantics past our emission** | Our budget ends at emit. §10.9's header states the capture interval and the drop policy, which is the whole of what a consumer needs from us in order to size themselves | [`08`](08_Collection_And_EPoL.md) §11.2's budget diagram, which deliberately ends at the boundary and times nothing beyond it |
| An **identity for a consumer's output** | We mint none. §10.8's "no identifier join" row is deliberate: truth is self-identifying so that it can never be silently merged with something a consumer produced | Doc 09 §3's `CARLA-DET-<track_id>` grammar, which exists for **their** use on a TAK feed and is not a contract of ours |
| An **acknowledgement, request or control channel** | `D4.31`. A consumer that could ask us for something would be a consumer that could change what we produce | The transcript, which is inbound **bytes** and never inbound **requests** (§10.10 rule 7) |

**Where a concrete integration appears anywhere in this plan, it is one possible adapter, thin and
outside.** [`02`](02_Use_Cases.md) UC-8's narrative chain and [`08`](08_Collection_And_EPoL.md) §11.2's
sequence diagram both name a detect-and-track stage and a model service because a use case is
unreadable without a concrete reader on the far end. Neither is normative, neither is a dependency of
this contract, and `C8` is written so that substituting a completely different chain — or no chain at
all — changes nothing in it.

### 10.12 Validation, failure, versioning

Every rule is a check on **our own artifacts**, run by us — before a corpus is handed over (V8.1–V8.10),
at the emitter or at build time for the live mode (V8.11–V8.17), and over the container of what came
back (V8.18–V8.20). The response column names what this system does; **none of them is a verdict on
anything outside it**, and V8.18 is the one to read twice: a malformed arrival from a system we do not
own is refused and recorded, and never fails our run.

| # | Rule | Response |
|---|---|---|
| V8.1 | The `OBSERVATION` root contains no field listed in §10.4's right-hand column | refuse to publish the handover |
| V8.2 | Every truth event carries `source="truth"` on its container (`CotWriter.cs:34`), a `CARLA-TRUTH-` uid (`:134`), `how="m-g"` (`:136`) and `ce`/`le` of exactly `0.0` (`:145-146`); the truth writer never mints a uid with any other prefix | refuse to publish the truth root. The check is on *our* writer, which is what makes truth self-identifying in the first place; nothing here inspects anything incoming |
| V8.3 | The handover exposes no identifier by which a consumer's track could be joined to truth; the documented rule (§10.6) names position and time and nothing else | assertion at corpus build. The rule is about what the handover exposes, not about an assignment, because no assignment is performed here |
| V8.4 | Every truth event in the `TRUTH` root carries the label-quality block of §10.7 — `occlusion`, `visible_signature`, `label_crowding`, `nearest_label_px`, `supervision_transfer_ambiguous` | refuse to publish. The quality attaches to the label rather than to an assignment, which is where it is computable without a model |
| V8.5 | Prevalence is reported per sensor **and** unioned, never as one unlabelled number | refuse to publish a corpus summary without both |
| V8.6 | A corpus summary reports supervision prevalence **per solar bin** as well as per sensor. The bin edges are a declared parameter, valued in [`10`](10_Scale_And_Performance.md); solar elevation is the binning variable, because it is what an electro-optical sensor actually experiences | refuse to publish a summary without it. This is the check that makes an illumination–supervision correlation visible **in the corpus's own description** (§10.4a) |
| V8.7 | The `OBSERVATION` root contains `advancing`, `rate`, `illumination_band`, `illumination_band_elevation`, any `illumination.*` field, any residual, or any field read from the run manifest | refuse to publish — §10.4a's excluded list, and test 4 of `D4.20` |
| V8.8 | Every field in the observation root's `solar` and `epoch` blocks appears in §10.4a's "in `OBSERVATION`: yes" list | refuse to publish. An allow-list, not a deny-list: a new field stays out until the contract admits it |
| V8.9 | The corpus manifest declares every partition whose truth is not released, and every capture window, sensor and vehicle the corpus does not cover | refuse to publish. An undeclared omission is indistinguishable from data loss |
| V8.10 | The observation writer's build carries no reference to any truth type (`D4.16` property 1) | fail the build, not the run |
| V8.11 | Every stream record carries the identity keys of `C4` — `sensor_id` and `tick` always, with `sim_time_s` and `run_id` on every record. A record that cannot be named is not emitted | refuse to emit, and fail the run at that tick. The alternative is a consumer holding pixels it cannot place |
| V8.12 | `seq` increases by exactly one per `(stream_kind, sensor_id)` over a stream's life, and `dropped_cumulative` is non-decreasing and equals the count of skipped `seq` values | assertion in the emitter (L2) |
| V8.13 | No envelope field is derived from truth, supervision or any scene content, and the envelope writer's build carries no reference to any truth type | fail the build, not the run — the same kind of check as V8.10 |
| V8.14 | An observation **stream** record satisfies V8.1, V8.7 and V8.8 unchanged, checked **at the emitter** rather than inherited from the corpus writer | refuse to open the stream. A field that is unsafe in a corpus is unsafe on a wire |
| V8.15 | A truth stream is opened in a live handover only when the choice to open it was explicitly recorded, in the run manifest and in the `C10` run record | refuse to open the stream. Off by default; on by record (L7) |
| V8.16 | Every dropped frame is counted, every dropped `(sensor_id, tick)` appears in coverage as *covered but not delivered*, and the two counts agree | a run whose counters and coverage disagree is marked invalid — one of them is wrong and neither can then be trusted |
| V8.17 | A `stream_close` is written for every stream that was opened, with `not_delivered_recorded` true | assertion. If the process was killed there is no close record, and its absence is the signal — the expected ending of a live run (§12.7). The `C10` run record on disk is what a reader consults either way |
| V8.18 | Every transcript row carries `received_tick`, `source_id`, `content_type`, `byte_count` and `sha256`, and `source_id` is one of the configured peers | refuse the row and record the refusal. **Never stop the run** — a malformed arrival from outside is not a fault in the capture |
| V8.19 | Every transcript blob's digest and length match its row | mark the transcript untrusted at corpus build rather than shipping it silently |
| V8.20 | No component that writes truth, observation, coverage, a manifest, a corpus summary or the `C10` run record holds a reference to the transcript reader, and no field of any of them is a function of a transcript row | fail the build. The structural form of `D4.32` |

**Versioning.** Each root carries its own integer version, declared in the corpus manifest alongside the
`scenario_package_version` the run was produced from. A consumer that does not implement a root's
version refuses to read that root. The two roots version independently, because a change to the label
record should not invalidate imagery already captured.

**And in the live mode, the same versions, declared up front.** A stream's header carries
`handover_contract_version` and the `root_version` of the one root it delivers, so a consumer decides
whether it can read the stream **before** the first frame rather than on encountering a field it does
not know. A consumer that does not implement either stops reading and says so. The transcript's
`transcript_version` versions its container only and is independent of both — it has to be, because the
container is ours and the content is not (`D4.32`).

### 10.13 What breaks if C8 is violated

- **The corpus teaches the simulator instead of the world.** If `base_type` or true dimensions reach the
  `OBSERVATION` root, class is readable straight off the input; if a truth position reaches it, position
  is. Neither is visible afterwards — the data looks ordinary, and everything anyone concludes from it
  is a fact about our renderer. This is a defect in the corpus the moment it is written, whether or not
  anything is ever trained on it.
- **The rule-derived label failure returns.** If `<_aoi>` reaches the observation root, the containment
  predicate — evaluated on noiseless inputs — is in the input, which is the failure doc 20 §2.1 exists
  to prevent.
- **A hard example stays unexplained.** Without the label-quality block of §10.7, a label that was
  crowded, half-occluded or lamp-only is indistinguishable from a clean one, and the corpus cannot tell
  anyone which of its labels were weak. Doc 20 §7.6 asked for exactly this, and `D4.28` is where it is
  recorded now that there is no assignment to hang it on.
- **The corpus's own denominators are wrong.** `C2` §4.5 exists so that counts taken over imagery use
  `observed_union_s` and not `sumo_span_s`; `C8` is where those numbers are published. Publishing the
  wrong one makes every prevalence figure in the corpus description a misstatement about the corpus, and
  at the sizing case the error is most of the simulation.
- **A corpus is handed over that cannot support the context the data was built to exercise, and nothing
  says so.** A pattern-of-life model cannot represent "a heavy goods vehicle in a residential area at
  03:00" — one of doc 20's ten pattern classes — if the observation root carries no notion of time of
  day. `D4.20` exists so this is settled by a rule rather than by whoever is most nervous on the day,
  and §10.4a is the worked application of it.
- **The capture plan leaks through the lighting.** If `advancing` and `rate` reach the observation root,
  or if supervision correlates with the solar bin and nobody looks, the corpus encodes when the run was
  interesting rather than what happened in it. V8.6 is cheap and is the only thing that would notice.
- **A deliberate absence is read as a bug, or a bug is read as a deliberate absence.** §10.8 and V8.9
  exist because a consumer who cannot tell the two apart will either work around data that is fine or
  trust data that is not.
- **The live exercise becomes a different product from the corpus.** If the record differs by mode, a
  consumer writes two readers, the frame they saw live cannot be checked against the frame the corpus
  published, and the two will eventually disagree about something nobody notices. `D4.29` and
  `image_sha256` are what make them provably one thing.
- **A slow consumer changes the experiment.** Back-pressure looks like politeness and is not: the sun
  advances on the world tick, so a link that stalls the world changes the illumination of the exercise
  in response to how fast somebody else's software ran, and the corpus would record the altered
  lighting as though it had been asked for (`D4.30`, `D4.25`).
- **A gap in the stream is read as an empty scene.** Without `seq`, the drop counters and the
  covered-but-not-delivered coverage rows, a consumer receiving nothing for twenty ticks cannot tell a
  quiet world from a lost one, and will conclude the wrong thing about the scene rather than about the
  link (V8.16).
- **The transcript becomes a label.** Received model output sitting in the same process as truth, on one
  tick base, with a common frame identity, is one refactor away from being merged — and a merged
  transcript is supervision derived from somebody else's model, which is neither truth nor ours
  (`D4.32`, V8.20).
- **The contract has to be renegotiated every time the far end changes.** If any clause here named a
  consumer's format or transport, replacing the external chain would reopen this document. `D4.34` is
  the test that keeps that from happening, and §10.11 is the list it was applied to.

---

## 11. C9 — The simulated-time epoch and the illumination policy

The other eight contracts exist because two components have to agree about an artifact. This one exists
because **no artifact says the thing at all**: a scenario declares its windows
in simulated seconds and never declares what civil time those seconds mean, so nothing downstream can
set a sun from them. That is a contract gap before it is a rendering problem
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a).

[`11_Time_And_Illumination.md`](11_Time_And_Illumination.md) owns the semantics and the rationale —
what a site's civil time is, how a window is placed against the sun, why one policy is preferred.
[`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) owns how an operator *expresses* a
choice. **`C9` owns the wire shape and the validation**: what is declared, in what units, with what
arithmetic, checked how, failing how, and what a consumer does when the declaration is missing. §11.13
states exactly what `C9` needs from `11` and in what form.

### 11.1 The gap, measured

**Measurement 6 — the mapping exists, is perfectly consistent, and is machine-readable nowhere.**
Parsed `BahonarPatternOfLife/scenario/Shahid_Bahonar_Port_PatternOfLife.rou.xml` from the sizing archive
with `xml.etree`:

| | |
|---|---|
| `<trip>` elements whose id matches `guard_d<D>_h<H>_t<N>` | **335** |
| Of those, satisfying `depart == D × 86400 + H × 3600` **exactly** | **335 of 335**, zero mismatches |
| Distinct `D` | 0–6 — seven days |
| Distinct `H` | **7, 15, 23** — the three guard shifts |
| Departure times that follow | 25,200 / 54,000 / 82,800 s and the same three every 86,400 s thereafter |

So `t = 0` is **midnight of day 0**, asserted 335 times, and asserted **only inside identifiers**. The
brief's three sampled shift times are the first day of that pattern; the pattern holds across the whole
week without exception.

**Where it is not.** The `.sumocfg` declares `<begin value="0"/>` and `<end value="604800"/>` and
nothing else about time. The `.labels.json` gives the guard-no-show anomaly as `begin_s: 370800`,
`end_s: 399600` — day 4's 07:00 shift and the 15:00 one that relieves it — again in bare simulated
seconds. Neither file, nor the network, nor the world package, carries a date, a zone or a civil hour.

**Measurement 7 — the one epoch that does exist is UTC-only, defaults to the wall clock, and
contradicts the identifiers.** `SumoCotBridge` already has the field:
`epoch: datetime | None`, documented as "wall-clock instant that simulation time zero maps to"
(`CarlaControl/src/carlacontrol/SumoCotBridge.py:149-151`), defaulting to `datetime.now(UTC)` (`:197`),
used as `stamp = epoch + timedelta(seconds=now)` (`:254`) and written into the XML header as
`epoch="…"` (`:211`), with `--epoch` on the CLI (`CarlaControl/scripts/sumo_cot_telemetry.py:80-82`).
Three things follow, all of which `C9` has to fix rather than inherit:

1. **It is a run setting, not a scenario property.** It lives beside the emitter's UDP host and
   affiliation map, so it is not carried with the scenario, not digested, and not validated.
2. **It is UTC with no civil offset**, so it cannot express "the site's midnight".
3. **The shipped sample proves the contradiction.** Parsed from
   `BahonarPatternOfLife/samples/bahonar_cot_sample.csv`: `sim_time_s = 1.00` carries
   `time_utc = 2026-01-01T00:00:01.000Z`, so that dataset was produced with `t = 0` pinned to
   **2026-01-01T00:00:00Z**. Under the trip identifiers' own reading, `t = 0` is the *site's* midnight;
   the site is at +03:30, so the two declarations disagree by **3.5 hours**. The `guard_d0_h7` trips
   would be stamped 07:00 UTC — 10:30 local — while their names say 07:00.

**Measurement 8 — without a declaration the sun falls back to two defaults, one of which is the host's
calendar.** The generated world spawns its own `ACesiumSunSky` with `SolarTime = 12.0`,
`UseDaylightSavingTime = false`, and the time zone derived from the origin longitude
(`Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/Source/CesiumCarlaBridge/Private/CesiumHeightSampler.cpp:396-414`),
and the operator path defaults the *date* to `datetime.now()`
(`CarlaControl/src/carlacontrol/WorldBuilder.py:226-230`, flags at
`CarlaControlArgumentParser.py:243-270`). So today a 23:00 window renders at local solar noon on
whatever date the run happened, and the sidecar records that faithfully.

> **D4.18 — a scenario declares the civil instant that `t = 0` corresponds to, with an explicit UTC
> offset, as part of the scenario package. The mapping is never inferred from identifiers, never
> defaulted from the host clock, and never left to a run setting.**

### 11.2 Artifact, format and location

| | |
|---|---|
| **Artifact** | Two JSON objects, `epoch` and `illumination`, in the scenario package's lock (`C3` §5.3). Plus an `illumination` override supplied at run start, and the achieved state written into the run manifest (§11.8) |
| **Written by** | `epoch` and `illumination` by the scenario package builder, from what the author declared. The override by the operator surface ([`12`](12_Operator_Control_Surface.md)). The manifest block by the solar clock, closed at run end |
| **Read by** | The co-simulation driver and its solar clock at run start and every tick; the validator at package build and at run start; the corpus auditor; and — for the epoch and the per-frame solar state only — the observation writer (`C8` §10.4a) |
| **Format** | UTF-8 JSON, canonicalised per §1 so `epoch_block_sha256` is well defined. Civil times are ISO-8601 with an **explicit numeric offset**; `Z` is permitted only where the offset genuinely is zero |
| **Bound by** | `epoch_block_sha256` in the lock, at the refuse tier (`C3` §5.4 V3.11) |
| **Built** | The scenario compiler reads both objects from the specification with the session's own readers and carries them verbatim, the epoch with its `epoch_block_sha256`, in `<scenario_id>.lock.json` ([`07`](07_Scenario_Authoring.md) §5.1). The session reads both from a JSON file that carries them, the lock among them (`run_sumo_drive.py --epoch`), and does not compare the digest at run start (`C3` V3.11) |

### 11.3 `epoch` fields

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `epoch_version` | integer | — | yes | Schema shape of this object. A consumer that does not implement the version it reads **refuses**, never best-efforts |
| `civil_datetime` | string | — | yes | The civil instant `t = 0` corresponds to, ISO-8601 with an explicit numeric offset: `2026-03-21T00:00:00+03:30`. **This is the authoritative declaration**; everything else in this table either restates it machine-readably or qualifies it |
| `utc_offset_hours` | number | h | yes | The site's civil offset as a signed number, e.g. `3.5` for +03:30, `5.75` for +05:45, `-3.5` for −03:30. Must be a multiple of `0.25` — quarter-hour zones exist (Nepal, Chatham) and half-hour zones are the sizing scenario's own case |
| `utc_datetime` | string | — | yes | The same instant in UTC, `2026-03-20T20:30:00Z`. Redundant **by design**: it is what the existing CoT stamping path already consumes (`SumoCotBridge.py:254`), and a validator that checks the two against each other catches the single most likely authoring error, which is an offset applied in the wrong direction |
| `calendar_advances` | boolean | — | yes | Whether the civil **date** advances when simulated time crosses a civil midnight. `true` for a multi-day scenario that means seven consecutive days; `false` pins the seasonal sun angle to the epoch's date while the clock still wraps — legitimate when a sweep wants one sun geometry across a week of behaviour, and it must be *declared*, because the engine's own advance leaves the date alone either way (§11.6) |
| `dst_in_effect` | boolean | — | yes | Whether `utc_offset_hours` already includes daylight saving. The engine's DST is disabled at spawn (`CesiumHeightSampler.cpp:410`) and `C9` never re-enables it, so the declared offset must be the **effective** one for the declared date. The flag exists so a reader can tell "+02:00 standard" from "+02:00 because it is summer" |
| `time_zone_id` | string | — | no | IANA identifier, e.g. `Asia/Tehran`. **Provenance only. Never resolved at runtime** — resolving it would make a render depend on the host's tz database version, and two machines with different databases would disagree about a corpus |
| `note` | string | — | no | One human sentence saying what `t = 0` is in the scenario's own terms |

**The site's latitude and longitude are deliberately absent.** The sun's geometry is computed from the
world's georeference (`GetSolarState` reads it from the default georeference,
`CesiumHeightSampler.cpp:764-772`), and the scenario is already bound to that world by
`world_origin_latitude` / `world_origin_longitude` / `world_georeference` at the refuse tier
(`C3` §5.4 V3.3). A second copy here could disagree with the first, and there would be no way to say
which was right.

### 11.4 Civil time on the sun, and why the zone is written with the clock

This is the part an implementer will get wrong if the contract does not write it down, because both
quantities are called "hours" and both look like a time of day.

**Measured — the sun reads its clock in its own zone.** Three readings:

1. `ACesiumSunSky::EstimateTimeZoneForLongitude` sets `TimeZone = clamp(longitude, −180, 180) / 15.0`
   (`Unreal/CarlaUnreal/Plugins/CesiumForUnreal/Source/CesiumRuntime/Private/CesiumSunSky.cpp:570-572`)
   — a **continuous** value, not a rounded civil zone — and configuring a world's georeference sets
   exactly that (`CesiumHeightSampler.cpp:423-424`).
2. `UpdateSun` passes `TimeZone` and the hours-minutes-seconds decomposition of `SolarTime` into
   `USunPositionFunctionLibrary::GetSunPosition` (`CesiumSunSky.cpp:420-434`), which computes
   `TrueSolarTime = clockMinutes + EqOfTime + 4·Longitude − 60·TimeZone`
   (`UE_5_7_4/Engine/Plugins/Runtime/SunPosition/Source/SunPosition/Private/SunPosition.cpp:97`).
3. With `TimeZone = longitude / 15`, the `4·Longitude − 60·TimeZone` terms cancel exactly. **So in a
   world whose sun nobody has bound, `SolarTime` is local apparent solar time at the map origin**, not
   civil time.

**The zone is written in the same call as the date and the clock.** `set_solar_epoch(year, month,
day, hours, utc_offset_hours)` (`CarlaServer.cpp:644`; `UCesiumHeightSampler::SetSolarEpoch`,
`CesiumHeightSampler.cpp:778`; shim `carlanet/__init__.py:1523`) sets `TimeZone` to the declared
offset together with the date and `SolarTime`, turns the engine's daylight-saving rule off and
recomputes the sun once ([`11`](11_Time_And_Illumination.md) D11.5). With the sun's zone equal to the
declared civil offset, `SolarTime` **is** the civil clock, so the arithmetic carries no conversion
between zones:

```
sun zone   = epoch.utc_offset_hours
sun clock  = the declared instant's civil clock, written one millisecond past its whole second (C6 §8.3a)
sun date   = the declared instant's civil date, under C6 G11's effective date rule
```

The session writes it once when the window opens and, under `advance`, again for every tick (§11.6,
[`11`](11_Time_And_Illumination.md) D11.19). It reads the sun back after the first write and refuses
the session on any field that differs, the zone included (`SolarLease`, V9.12), and the per-tick
audit compares the observed zone with `epoch.utc_offset_hours` exactly (`C6` §8.3a, `Δpolicy`).

**Worked, on the sizing scenario.** Origin `lat 27.15012, lon 56.18065`
(`CarlaControl/scripts/make_bahonar_scenario.py:69`), Iran's civil offset +03:30, no daylight saving:

| | |
|---|---|
| The zone configuring the georeference sets | `56.18065 / 15` = **3.7453767 h** (+03:44.7) |
| `epoch.utc_offset_hours`, the zone the session writes | **3.5** (+03:30) |
| Difference | **+0.2453767 h = +14.72 minutes = 883.4 s = 3.68° of hour angle** |
| Civil 23:00 day 0 (`t = 82,800`) | `set_solar_epoch(2026, 3, 21, 23.0000002778, 3.5)` — 23:00:00.001 |
| Civil 07:00 day 4 (`t = 370,800`), date held on the epoch's | `set_solar_epoch(2026, 3, 21, 7.0000002778, 3.5)` |

**Why 14.7 minutes matters.** The site sits 3.68° of longitude east of its zone meridian, so the sun
crosses the meridian a quarter of an hour before the civil clock says noon. A sun given civil hours
while its zone is left at `longitude / 15` sits a quarter of an hour from where the declared time says
it is, **in every frame, in the same direction** — a systematic bias in shadow direction and length,
and worst exactly where the sizing scenario's windows are. At this latitude the sun's elevation
changes at most `15·cos(27.15°) = 13.35°` per hour, so near sunrise 14.7 minutes is up to **3.3° of
solar elevation** (computed, not measured) — around the 07:00 shift-change window that is the
difference between civil twilight and the sun being up. It is a small number that is never noise.

> **D4.19 — the sun is written with the declared civil offset as its zone, in the same call as the
> date and the civil clock, and read back before anything renders. No component converts a civil time
> into another zone's clock.** The zone the world's sun held when the session found it is recorded
> (§11.8), because a loaded world keeps whatever the last session or configuration left — `longitude /
> 15` once its georeference is configured — and the session gives it back on every path out.

```mermaid
flowchart LR
  T["simulated t<br/>(C6 tick)"] --> CIV
  EP[/"C9 epoch<br/>civil_datetime · utc_offset_hours<br/>calendar_advances"/] --> CIV
  CIV["civil instant<br/>date + clock, at the declared offset"] --> DS
  IL[/"C9 illumination<br/>policy · rate · freeze fields"/] --> DS
  DS["declared sun<br/>C6 §8.3a · G11"] --> W["set_solar_epoch<br/>date · clock · zone = utc_offset_hours"]
  W --> SUN["CesiumSunSky"]
  SUN --> OBS["solar block on the<br/>world-observer snapshot"]
  OBS --> AUD{"C6 §8.3a audit<br/>Δsolar_s · Δdir_deg · Δpolicy"}
  DS --> AUD
  AUD -->|in tolerance| REC["frame recorded with &lt;_solar&gt;"]
  AUD -->|out of tolerance| FAIL["fail the run, V6.6"]
```

### 11.5 `illumination` fields

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `illumination_version` | integer | — | yes | Schema shape. Unimplemented version ⇒ refuse |
| `policy` | string | — | yes | One of `advance`, `freeze_at_window_start`, `freeze_at`, `ignore`. §11.6 defines each |
| `rate_sun_s_per_sim_s` | number | — | yes when `advance` | Sun-clock seconds per **simulated** second. `1.0` means civil time and the sun keep step. `> 1` compresses a day into a window. Must be `> 0` |
| `freeze_at_civil_time` | string | — | yes when `freeze_at` | `HH:MM:SS` civil time of day at the site, the instant the sun is pinned to. Not an offset from the window; an absolute civil hour, so two windows can share one lighting condition |
| `freeze_date_advances` | boolean | — | no | Under a freeze policy, whether the *date* still advances with `epoch.calendar_advances`. Default `false` — a freeze that meant to hold illumination constant and let the seasonal angle drift across a week would be a freeze in name only. Declared rather than assumed because the opposite is defensible and `11` may prefer it |
| `require_sun` | boolean | — | no | Default `true`. When true, a world with no `CesiumSunSky` is a refusal (`C3` V3.13) rather than a run under unknown lighting |
| `solar_audit_tolerance_s` | number | s | no | Overrides `C6`'s value for this scenario. Must be `> 0` and must not exceed a bound valued in [`10`](10_Scale_And_Performance.md) — an override is for a scenario with an unusual rate, not a way to switch the audit off |
| `solar_audit_tolerance_elev_deg` | number | ° | no | Same, for the elevation residual |
| `note` | string | — | no | Why this policy, in one human sentence |

**`rate` is per *simulated* second, because the session applies it.** Under `advance` each frame's
declared instant is the window's opening instant carried forward by the frame's elapsed simulated time
times the rate (`DeclaredSun.SunAt`), and the session writes that sun before the frame's tick cue
(`SolarLease.WriteForFrame`), so a `rate` of 1.0 is one sun-clock second per simulated second. The
engine's own advance — `DeltaHours = DeltaSeconds × Rate / 3600` in the controller's `Tick`
(`Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/Source/CesiumCarlaBridge/Private/CesiumTimeOfDayController.cpp`),
on the world tick's delta — is set off by the session under every policy (`set_time_advance(false,
0)`, `DeclaredSun.EngineAdvances`), so nothing about the sun depends on the world's clock mode.

### 11.6 The four policies, and what each guarantees

| `policy` | The sun does | Guaranteed | Use |
|---|---|---|---|
| `advance` | Tracks civil time at `rate_sun_s_per_sim_s`, written by the clock owner for every tick at the whole second nearest the frame's declared instant | `C6` G9 and G10 hold, with the engine's own advance off: `<_solar>` reads `advancing = false`, `rate = 0`, and `<_illumination>` carries the declared policy and rate. At `rate = 1.0`, the civil time of a frame and its solar state are within half a second of the same instant | A long window that should show the light changing — dawn over a shift change |
| `freeze_at_window_start` | Is set once, to the civil instant each capture window opens, and does not move within the window | The observed sun clock is constant across the window to within the §8.3a tolerance, and `advancing = false`. Different windows get **different** frozen suns, each correct for its own opening instant | The default for a sweep: illumination is a controlled constant within a window and a deliberate variable between windows |
| `freeze_at` | Is set once, to `freeze_at_civil_time`, for every window | As above, and **identical across every window**. The declared civil time of a frame and its solar state then deliberately disagree, and the manifest records that they do | Holding lighting fixed while varying behaviour — the counterfactual pair whose only difference is the thing that was varied |
| `ignore` | Is not written at all | Nothing. The manifest carries `illumination_in_force.policy = "ignore"`, `epoch_honoured: false` and `corpus_eligible: false` | Diagnostics, and the only legal behaviour when no epoch is declared (§11.7) |

**`freeze_at` is the one that can lie, so it is the one that must be recorded loudly.** Under it a frame
whose civil time is 23:00 may be lit as though it were 15:00. That is a legitimate experiment and an
illegitimate corpus if nobody knows, so the manifest records both numbers per window (§11.8) and the
`<_solar>` block already records the sun that was actually used. A consumer comparing the two gets the
right answer; a consumer that reads only one of them was going to be wrong under any design.

**The calendar is the driver's job under every policy.** The clock owner writes the date with the
clock in one `set_solar_epoch` — at window open, and under `advance` for every tick — so a window
crossing civil midnight is carried onto the next date when `C6` G11's effective date rule advances it
and held on the epoch's date when it does not. Nothing relies on the engine: its own advance, which
the session keeps off, does carry whole days onto the date (`CesiumTimeOfDayController.cpp`,
`RollSolarDate`). G11 and V6.8 are the check, and a date a day out is 86,400 s out of the compared
instant.

### 11.7 What a consumer does when the declaration is absent

The rule that stops this contract from being decorative.

> **D4.24 — a consumer that finds no epoch does not invent one. It either refuses, or runs with
> `policy = "ignore"` and records that it did. Silently defaulting to noon, to the host date, or to
> `t = 0` being UTC midnight is prohibited.**

| Situation | Response |
|---|---|
| A scenario with no `epoch` | **Refused at compile** (check 33), so no lock carries none (`C3` V3.11). There is no honest default |
| A legacy package predating `C9` | **Refuse by default.** An operator may opt in to `policy = "ignore"`, which stamps `epoch_declared: false` and `corpus_eligible: false` into the manifest and marks every sidecar's solar block as unbacked by an epoch — an added `<_solar>` attribute that [`06_Truth_And_Annotation.md`](06_Truth_And_Annotation.md) owns the shape of. It never guesses an epoch from trip identifiers, however regular they look — Measurement 6 shows the pattern is perfectly consistent and Measurement 7 shows it contradicts the only epoch anyone wrote down |
| A bare `.sumocfg` run outside a package | Same as legacy. `C9` does not require a package to exist; it requires the declaration not to be fabricated |
| A world with no `CesiumSunSky` | `require_sun: true` ⇒ refuse (`C3` V3.13). `require_sun: false` ⇒ run, with `no_sun: true` in the manifest and the audit skipped and **recorded as skipped**, never recorded as passed |
| An epoch declared but the policy is `ignore` | Legal, and the manifest carries both — the epoch, so the civil time of every frame is still recoverable, and `epoch_honoured: false`, so nobody reads the lighting as evidence of it |

This mirrors `render_uses_vtype_colour` (§3.7.1): a
diagnostic escape hatch exists, it is loud, it is recorded, and it costs the run its corpus eligibility.
The alternative — a default that renders something plausible — is the failure mode this whole contract
was added to prevent.

### 11.8 What the run manifest records

The run manifest has several authors. `C2` §4.5 owns `render_states[]`; `C6` §8.5 owns `stalled` and
the last good tick. **The blocks below are `C9`'s, written by the solar clock and by nothing else**, as
append-only rows, so what was already true survives a kill at any instant (`D4.36`, `C10` §12.7).

| Field | Type | Unit | Req. | Meaning |
|---|---|---|---|---|
| `epoch` | object | — | yes | The `epoch` block **verbatim** from the package, so a manifest is readable without the package |
| `epoch_declared` | boolean | — | yes | False only in the §11.7 legacy cases |
| `epoch_block_sha256` | string | — | yes | The digest the package carried, so the manifest identifies *which* epoch |
| `illumination_declared` | object | — | yes | What the package asked for |
| `illumination_override` | object | — | no | What the operator supplied at run start, if anything ([`12`](12_Operator_Control_Surface.md) owns how) |
| `illumination_in_force` | object | — | yes | Which one won, resolved. **Always written even when there was no override**, so no consumer has to re-derive precedence |
| `epoch_honoured` | boolean | — | yes | True when the policy wrote the sun from the epoch — i.e. `advance` or `freeze_at_window_start`. False under `freeze_at` and `ignore` |
| `corpus_eligible` | boolean | — | yes | False whenever `epoch_declared` is false, `policy == "ignore"`, `no_sun` is true, or the audit failed. The single field a corpus builder filters on |
| `sun_time_zone_hours` | number | h | yes | The zone the world's sun held when the session found it, read before the first write (§11.4, D4.19). The zone written is `epoch.utc_offset_hours`; this records what the world held beforehand — `longitude / 15` once its georeference is configured, or whatever the last session left — and is given back on exit |
| `no_sun` | boolean | — | yes | True when the world had no `CesiumSunSky` |
| `advance_mechanism` | string | — | yes | `per_tick_write`, the only value: under `advance` the session writes the sun for every tick and the engine's own advance is off under every policy ([`03`](03_CoSimulation_Runtime.md), [`11`](11_Time_And_Illumination.md) D11.19). Not a policy choice and not declarable; recorded so a corpus states the mechanism its residuals come from |
| `solar_achieved[]` | array | — | yes | One entry per capture window, §11.8.1 |
| `solar_residual` | object | — | yes | The run's worst case, §11.8.2 |
| `lamp_gaps[]` | array | — | yes | `{ class_id, blueprint_id, lamp, verdict }` for every V1.18 / V1.18a warning that fired. Empty array when none, never absent |

#### 11.8.1 `solar_achieved[]` — one entry per capture window

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `window_index` | integer | — | Index into `capture_windows[]` (`C2`) |
| `begin_s`, `end_s` | number | s | The window, in simulated seconds |
| `civil_begin`, `civil_end` | string | — | The same two instants as civil times, from the epoch |
| `solar_date_begin`, `solar_date_end` | string | — | The dates actually written, under the effective date rule of `C6` G11. They differ from each other only when the date is advancing and the window crosses a civil midnight, and they may differ from `civil_begin`/`civil_end`'s dates whenever the date is held |
| `solar_time_begin`, `solar_time_end` | number | h | The sun-clock hours actually observed at the first and last capture tick of the window |
| `sun_elevation_begin_deg`, `sun_elevation_end_deg` | number | ° | Observed |
| `sun_azimuth_begin_deg`, `sun_azimuth_end_deg` | number | ° | Observed |
| `advancing` | boolean | — | Observed |
| `rate` | number | — | Observed |
| `declared_civil_vs_solar_delta_h` | number | h | How far the sun that was used is from the window's own civil time. **Zero under `advance` and `freeze_at_window_start`; non-zero and deliberate under `freeze_at`** — this is the field that makes a `freeze_at` corpus honest |

#### 11.8.2 `solar_residual` — the declared-versus-actual check

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `max_delta_solar_s` | number | s | The largest `Δsolar_s` seen at any audited tick (`C6` §8.3a) |
| `max_delta_elev_deg` | number | ° | The largest `Δdir_deg` (`C6` §8.3a) |
| `max_at_tick` | integer | — | The tick the maximum occurred at, so it can be found |
| `max_at_sim_time_s` | number | s | The same instant in simulated seconds |
| `tolerance_s`, `tolerance_elev_deg` | number | s, ° | The tolerances actually in force (`C6` §8.3a; the same at every rate) |
| `audited_ticks` | integer | — | How many ticks were audited |
| `capture_ticks` | integer | — | How many capture ticks there were. `audited_ticks ≥ capture_ticks` always (`C6` V6.9) |
| `within_tolerance` | boolean | — | The verdict |
| `audit_skipped` | boolean | — | True only when `no_sun` is true. **A skipped audit is never reported as a passed one** |

A run that stayed in tolerance still writes every field. "The residual was 0.4 s" and "the residual was
never measured" must not look alike, which is why `audit_skipped` is a separate field from
`within_tolerance` rather than a value of it.

#### 11.8.3 Per capture: the sun's band, and a capture without a sun

`C9`'s per-capture record is not the solar clock's: it is the `<_solar>` element and the `carla:solar`
chunk the frame recorder writes from the world-observer snapshot nearest the pixels, and it is stated
here because the manifest counts the captures that lack it. Beside the sun it carries two derived
fields, written after every attribute the element already carried:

| Field | Type | Meaning |
|---|---|---|
| `illumination_band` | string | One of `day`, `golden`, `civil_twilight`, `nautical_twilight`, `astronomical_twilight`, `night`, by [`11`](11_Time_And_Illumination.md) §4.4's edges, each band holding its upper edge. Derived from this block and nothing else, so from the sun the world **achieved** on the capture's tick and never from the declared time ([`06`](06_Truth_And_Annotation.md) D6.23). Absent where the block's elevation is not a sun's — the −180° the engine reports for an impossible date ([`11`](11_Time_And_Illumination.md) F4) |
| `illumination_band_elevation` | string | `refraction_corrected`, the elevation the band's edges are stated against, wherever the block carries it; `geometric` from a server that carries only that. Written wherever the band is |

The edges are one table, `CarlaNet.Types.Illumination.IlluminationBands`, which every reader of a band
uses. Like everything derived from illumination, the band is context and never supervision
([`11`](11_Time_And_Illumination.md) D11.12).

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `captures_missing_solar_block` | integer | — | Captures written with no `<_solar>` and no `carla:solar`, because the snapshot nearest the pixels carried no sun. **Zero in a healthy run**, and anything else is a gate not met: such a capture can be neither stratified nor replayed ([`11`](11_Time_And_Illumination.md) §8.4) |

Until the run manifest exists, each channel's recorder counts `captures_missing_solar_block`
(`FrameRecorder.SolarBlockMissing`) and the run's closeout records it as the gate
`capture.solar_block_missing[<sensor>]`, threshold 0 ([`12`](12_Operator_Control_Surface.md) §7.2).

### 11.9 Worked example

The sizing scenario, declaring what Measurement 6 shows its identifiers already meant, with the night
window drawn from the three daily peaks [`10_Scale_And_Performance.md`](10_Scale_And_Performance.md)
§3.1.2 measures — 07:00–08:00, 15:00–16:00 and 23:00 — at the 1,800 s default length of its `D10.3`.
The date is the author's choice; only the midnight is measured.

```json
{
  "epoch": {
    "epoch_version": 1,
    "civil_datetime": "2026-03-21T00:00:00+03:30",
    "utc_offset_hours": 3.5,
    "utc_datetime": "2026-03-20T20:30:00Z",
    "calendar_advances": true,
    "dst_in_effect": false,
    "time_zone_id": "Asia/Tehran",
    "note": "t = 0 is midnight at the start of day 0 at the port, as every guard_dD_hH trip id already assumes."
  },
  "illumination": {
    "illumination_version": 1,
    "policy": "freeze_at_window_start",
    "freeze_date_advances": false,
    "require_sun": true,
    "note": "Illumination held constant within each window so the six windows differ only in hour."
  }
}
```

**What the driver computes for the night window** — `capture_windows[0] = { begin_s: 82800, end_s: 84600 }`,
which is 23:00–23:30 on day 0, the night shift:

| Step | Value | Where it comes from |
|---|---|---|
| `t = 82,800 s` | day 0, 23:00 | `C6` `t_render` |
| Civil instant | `2026-03-21T23:00:00+03:30` | `epoch.civil_datetime + t` (`SolarEpoch.CivilInstantAt`) |
| Zone as found | `3.7453767` — `longitude / 15`, from configuring the georeference | read before the first write and recorded as `sun_time_zone_hours` (§11.8); written over, and given back on exit |
| Written | `set_solar_epoch(2026, 3, 21, 23.0000002778, 3.5)` — 23:00:00.001 at +03:30; then `set_time_advance(false, 0)` | `carlanet/__init__.py:1523`, `:1575`; `C6` §8.3a for the millisecond |
| Audited every capture tick | observed `solar_time ≈ 23.0000002778`, `time_zone == 3.5`, `advancing == false`, date `2026-03-21` | `C6` §8.3a |
| Recorded | `declared_civil_vs_solar_delta_h: 0.0`, `epoch_honoured: true`, `corpus_eligible: true` | §11.8 |

**And for the day-4 shift change**, `t = 370,800` — the instant the shipped `.labels.json` gives as the
start of the guard-no-show anomaly (`begin_s: 370800`) — the civil instant is
`2026-03-25T07:00:00+03:30` and the clock written is 07:00:00.001 at +03:30 either way. The **date** is
where the two declarations in the example above meet: `calendar_advances: true` says the civil date is
25 March, and `freeze_date_advances: false` says the frozen sun keeps the epoch's date, so the write is
`set_solar_epoch(2026, 3, 21, 7.0000002778, 3.5)` and `solar_achieved[]` records
`solar_date_begin: "2026-03-21"` beside `civil_begin: "2026-03-25T07:00:00+03:30"`. Declaring
`freeze_date_advances: true` instead writes `set_solar_epoch(2026, 3, 25, 7.0000002778, 3.5)`, and `C6`
V6.8 then checks it exactly. Both are legitimate, and the manifest records which date was written.

**The same window with no epoch declared**, for comparison: a session with no epoch can declare only
`ignore` (`IlluminationPolicy.Ignore`), so it writes no sun and the world renders whatever it holds —
`ACesiumSunSky`'s class-default date 2019-09-21, or what the last session or operator left — with
`<_solar>` recording it faithfully and the scenario asserting 23:00. The manifest's
`epoch_declared: false` and `corpus_eligible: false` are what keep that run out of a corpus (§11.7).

### 11.10 Validation

Checked by the scenario package builder (`S`) or by the co-simulation driver at run start (`R`), except
where a rule is a per-tick assertion (`T`).

| # | Rule | Where | Response |
|---|---|---|---|
| V9.1 | `epoch` and `illumination` are both present and both carry their `*_version` | S, R | refuse |
| V9.2 | `civil_datetime` parses as ISO-8601 **with an explicit numeric offset**. A bare local time, or `Z` where `utc_offset_hours ≠ 0`, is rejected | S, R | refuse, naming the field. A civil time without an offset is the error this contract exists to eliminate |
| V9.3 | `utc_offset_hours` is a multiple of `0.25` and lies in `[−12, +14]` | S | refuse |
| V9.4 | The offset carried inside `civil_datetime` equals `utc_offset_hours` exactly | S, R | refuse — two declarations of one fact must agree |
| V9.5 | `utc_datetime == civil_datetime − utc_offset_hours`, to the second | S, R | refuse. Catches an offset applied in the wrong direction — at the sizing site's +03:30 that is a **7-hour** error produced by a one-character mistake, and it renders a scene that looks entirely plausible |
| V9.6 | `civil_datetime` is a real calendar date. `set_solar_date` **clamps** rather than rejects — `Month = Clamp(1,12)`, `Day = Clamp(1,31)` (`CesiumHeightSampler.cpp:747-748`) — so 31 February would silently become 31 February | S | refuse, because the engine will not |
| V9.7 | `policy` is one of the four values; the field required by that policy (`rate_sun_s_per_sim_s` or `freeze_at_civil_time`) is present, and fields belonging to the other policies are absent | S, R | refuse — an ignored field is a field someone will later believe |
| V9.8 | `rate_sun_s_per_sim_s > 0`; `freeze_at_civil_time` parses as `HH:MM:SS` in `[00:00:00, 24:00:00)` | S | refuse |
| V9.9 | Every `capture_windows[]` entry's civil span is computable from the epoch, and the whole span lies within `[epoch, epoch + sumocfg end]` | S | refuse — a window whose civil time is outside the scenario's own span is an authoring error |
| V9.10 | `epoch_block_sha256` matches the `epoch` object as carried | S, R | refuse (`C3` V3.11) |
| V9.11 | The world reports a sun, or `require_sun` is false | R | refuse (`C3` V3.13) |
| V9.12 | The sun read back after the first write holds the zone `utc_offset_hours`, the date and the clock written | R | **refuse**, naming each field that differs, and give the sun back. A write is not the world having it; the read-back is taken on demand, not from the observer cache, which predates the write (`SolarLease`) |
| V9.13 | `\|utc_offset_hours − engine_time_zone_hours\| ≤ 1.0`, where `engine_time_zone_hours` is `longitude / 15` as the world package's solar frame publishes it (`solar.json`, [`07`](07_Scenario_Authoring.md) §2.10) | S | **warn**, naming both. The session writes the declared offset as the sun's zone, so the difference moves no sun; more than an hour means the declared offset and the map are probably not the same place. 0.245 h is the sizing site's value. [`07`](07_Scenario_Authoring.md) check 40 |
| V9.14 | Any tolerance override is `> 0` and within the bound valued in [`10`](10_Scale_And_Performance.md) | S | refuse — an override is not an off switch |
| V9.15 | Under `C6` G11's effective date rule, the driver wrote a date for every civil midnight the run crossed — and under a held date, wrote none after the first | T | assertion; a miss is a bug, and `C6` V6.8 catches its effect independently |
| V9.16 | Under any freeze policy, no `set_solar_time` is issued between the window's first and last capture tick | T | assertion — a write inside a frozen window is a second owner of the sun (`D4.25`) |
| V9.17 | `illumination_in_force` is written to the manifest whether or not an override was supplied | R | refuse to close the manifest without it |

At `S` the scenario compiler carries these out: V9.1–V9.8 by handing both objects to the session's own readers, `CarlaNet.CoSim.SolarEpoch` and `IlluminationPolicy` ([`07`](07_Scenario_Authoring.md) checks 33, 34 and 39), so the compiler and the session cannot disagree about either; V9.9 as check 38; and V9.13 as check 40. V9.10 and V9.14 need the package and the bound on overrides, neither of which exists; the session refuses the tolerance overrides until the bound does.

### 11.11 Failure modes

| Violation | Detected | Behaviour |
|---|---|---|
| No epoch declared | run start, V9.1 | **Refuse**, naming the package, and print what an `epoch` block looks like. Never guess (`D4.24`) |
| Civil time with no offset | package build, V9.2 | **Refuse.** Say which field and say that an offset is required even when it is zero |
| `utc_datetime` disagrees with `civil_datetime` | package build, V9.5 | **Refuse**, printing both and their difference in hours. This is the sign-error catcher |
| Impossible date | package build, V9.6 | **Refuse.** State that the engine would have clamped it silently (`CesiumHeightSampler.cpp:747-748`) |
| World has no sun, `require_sun: true` | run start, V9.11 | **Refuse.** `get_solar_state` returning empty is the detection (`CesiumHeightSampler.cpp:760-763`) |
| World has no sun, `require_sun: false` | run start | Run; `no_sun: true`, `audit_skipped: true`, `corpus_eligible: false` |
| Sun drifts out of tolerance mid-run | per tick, `C6` V6.6 | **Fail the run**, naming the tick and both residuals. Every frame after the drift began would carry a `<_solar>` contradicting the scenario |
| Date not advanced across midnight | per tick, `C6` V6.8 | **Fail the run.** Exact check; a wrong date is a wrong seasonal sun |
| `freeze_at` used, civil and solar disagree | by construction | Not a failure. Recorded in `declared_civil_vs_solar_delta_h` per window and in `epoch_honoured: false` |
| A second component writes the sun | per tick, V9.16 and `C6` G10 | **Fail the run.** The observed `advancing`/`rate` no longer match the policy in force |
| `time_zone_id` present and disagreeing with `utc_offset_hours` for that date | — | **Not checked, by design.** Resolving it would need a tz database and make the render host-dependent (§11.3). It is provenance; the offset is the declaration |

### 11.12 Versioning

- `epoch_version` and `illumination_version` are independent integers. Either may advance without the
  other, because a new policy does not change what an epoch is.
- A consumer that does not implement the version it reads **refuses**. There is no partial reading of a
  time declaration: a consumer that ignored `calendar_advances` because it did not recognise it would
  render six of seven days on the wrong date.
- `epoch_block_sha256` identifies the exact epoch and is bound at the refuse tier (`C3` §5.4 V3.11). It
  covers the `epoch` object only, **not** `illumination`, and that split is deliberate: re-running one
  scenario under a different illumination policy is an ordinary, legitimate sweep, whereas re-running it
  under a different epoch produces a different corpus wearing the same name.
- Adding a field to `epoch` changes the digest and therefore invalidates every package that carried the
  old one. Adding a `policy` value is additive and needs only `illumination_version`.
- **An `illumination` override never changes any digest.** It is a property of a run, recorded in the
  manifest, and `illumination_in_force` is what a consumer reads.

### 11.13 What `C9` needs from `11_Time_And_Illumination.md`

Stated as properties needed, not as requests, in the style of §14.

| Property | Why `C9` cannot decide it |
|---|---|
| A recommended default `policy`, and the argument for it | It is a question about what makes a good corpus, not about what the wire carries. `C9` lists four and defines each |
| The solar-elevation thresholds at which `Position`, `LowBeam` and `Fog` are commanded, in the `sun_elevation_deg` convention (degrees above the horizon), **not** the CARLA weather convention the traffic manager's constants are written in (`Constants.cs:202-205`) | A threshold is a rendering judgement. `C7` §9.4 fixes only that the input is the real sun and that the value is scene-scoped |
| Whether `freeze_date_advances` should default `true` or `false` | `C9` defaults it `false` and says why; the opposite is defensible and the choice is about seasonal geometry, which is `11`'s |
| Whether illumination is a declared corpus stratifier, and the solar-bin edges for `C8` V8.6 | A stratification decision. `C9` requires the bins to exist; [`10`](10_Scale_And_Performance.md) values them |

### 11.14 What breaks if C9 is violated

- **The night shift is captured in daylight, and every artifact agrees that it was.** This is the
  failure this contract exists to prevent. The sidecar records the sun it got (`CotWriter.cs:52-65`), the PNG
  carries the same block (`SolarMetadata.cs:19`), the scenario asserts 23:00, and the two never meet.
  A corpus is internally contradictory and **nothing flags it** — there is no error, no warning and no
  dropped frame, only a night scene that is bright.
- **A pattern class becomes unrenderable.** Doc 20's class 4 is "a heavy goods vehicle in a residential
  area at 03:00". Without an epoch there is no 03:00; the scenario can place a lorry at `t = 10,800`
  and cannot state that this is the middle of the night.
- **The largest covariate an electro-optical detector faces is uncontrolled and unrecorded.** A corpus
  captured entirely at noon cannot validate a model that must work at dusk, and without the manifest's
  solar block a corpus cannot even say which it is.
- **Two runs of one package differ and nothing says why.** With the date defaulting to the host's
  (`WorldBuilder.py:229-230`), the same scenario run in March and in September has different sun
  elevations, different shadow lengths and different apparent contrast — a difference that looks like
  model variance and is calendar variance.
- **The sun is 15 minutes out even when everything else is right.** A sun handed civil hours while its
  zone is left at `longitude / 15` puts every shadow at the sizing site 3.68° of hour angle from where
  the declared time says it should be, in every frame, in one direction, and nothing in the imagery
  shows it. D4.19's write of the zone with the clock, and the audit's exact comparison of the zone, are
  what prevent it.
- **The seventh day is lit like the first.** A driver that sets the date once and lets the clock run
  renders six days of a week-long scenario under day 0's seasonal sun while the behavioural record
  correctly says day 6. The date is written with every clock (§11.6), and `C6` V6.8 checks it exactly.
- **The epoch is re-invented incompatibly in every tool.** One already exists, is UTC-only, defaults to
  the wall clock, and contradicts the scenario's own identifiers by 3.5 hours (Measurement 7). Without
  one declaration that everything reads, the next tool adds a second.
- **Illumination quietly becomes a label.** If nothing declares the mapping, windows get placed by
  whoever is authoring, correlations with supervision go unmeasured, and a model learns the capture plan
  — the outcome [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a's standing constraint forbids and `C8` V8.6 is
  the only check for.

---

## 12. C10 — The run record: what a run produced, and what was checked

**Cyclic generation is not ours to control.** External processes drive it, and they are in full control
of when to terminate, what to do with the data and what comes next. They may kill the SUMO process, the
CARLA server or our client **at any instant, deliberately**, or query through CarlaNet and the Python
shim to decide when enough is enough ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d).

`C10` is therefore a record of facts, not a judgement. It says **what a run produced and what was
checked**. It does not say whether the result suits a purpose, because the purpose belongs to a caller we
know nothing about — §3b's principle applied to our own run: we do not judge a model, and we do not judge
a run on the caller's behalf either. The gate rows are facts about our own data and are published in
full; the aggregation is the caller's.

Three properties shape everything below.

1. **The caller ends the run, so the record cannot wait for the end.** It is appended from before the
   first capture, and every prefix of it is valid (§12.1, §12.7).
2. **The record is a projection, not a new measurement.** Every field is copied or computed from an
   artifact this plan already specifies. **If a field is not derivable from an artifact this plan already
   specifies, it does not belong in the record.** A record that measures something of its own is a second
   source of truth about a run, and the first disagreement between it and the manifest is the one nobody
   can adjudicate.
3. **Division of labour.** [`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) owns the
   **invocation surface** — the flags, the configuration layering and resolution, the effective
   configuration and its digest, the live monitor, the closeout rendering of its §7.2 and the exit-status
   set of its §3.10.2. `C10` owns the **record artifact**: its identity, what it says was produced, and
   its record of the gates. Neither restates the other.

| Where the record's content comes from | What it contributes |
|---|---|
| The **run manifest** — `C2` §4.5 (`render_states[]`, observed spans), `C6` §8.5 (`stalled`, last good tick), `C9` §11.8 (`epoch`, `illumination_in_force`, `solar_residual`, `lamp_gaps[]`, `corpus_eligible`) | Identity, what ran, and most of the gate inputs |
| The **corpus manifest** (`C8` §10.12) | Root versions, declared omissions, partitions — when the run reached publication. A run the caller stopped has none, and §12.7 says what a reader does then |
| The **stream close records** (`C8` §10.9.2) | What a live delivery actually delivered, and what it dropped, where they exist |
| [`12`](12_Operator_Control_Surface.md) §7.2's closeout gates, and the values [`10`](10_Scale_And_Performance.md) owns | The status of each check, with its threshold |

**What `C10` does not touch.** The record describes a run; it changes nothing about what a run emits, so
each of these holds exactly as its own section states it:

- **`C8`'s two delivery modes** (`D4.29`) — a run record is written for a deferred run, a live run or
  both, and the records a consumer receives are unaffected by it.
- **`D4.20`, observer-derivability** — no field of the run record may be placed in the `OBSERVATION`
  root, and the record is reachable only from the truth side (§10.4a, test 4).
- **`D4.26`, two artifact roots and no third** — the run record sits beside the run manifest in the
  `TRUTH` root's location and is not a root.
- **`D4.27`, supervision transfer as a published rule and never an operation** — the record counts
  intervals; it performs nothing and joins nothing.
- **`D4.32`, the transcript** — the record may report that a transcript exists and how large it is, and
  no field of the record is a function of any transcript row (V10.7, V8.20).
- **The `OBSERVATION` exclusion of `scenario_id` and `seed`** (open question 15) — both are required in
  the run record and in the `TRUTH` root, and neither appears on the observation side or in a live
  stream header.

### 12.1 Artifact

| | |
|---|---|
| **Artifact** | `run_record.jsonl` — one per run, written beside the run manifest, at a location keyed on `run_id` (`D4.35`) |
| **Format** | **JSON Lines** — one complete JSON object per line, UTF-8, `\n` terminated. The same container shape the transcript index uses (`C8` §10.10), and for the same reason: an append-only file of self-contained rows is readable as a prefix |
| **Written by** | the component that owns the run manifest, on **every** path — before the first capture, as checks are evaluated, when a stop is received, and at closeout if closeout is reached |
| **Read by** | an automated caller; an operator; a corpus builder deciding what to include; a human diagnosing a run months later |
| **Durability** | each row is appended and flushed to disk **before the next row is composed**. A buffered append is not a guarantee, and this artifact's whole value is that it survives a kill that no handler runs for |

#### 12.1.1 Four row kinds, and nothing else

| `record_kind` | When | Cardinality |
|---|---|---|
| `run_opened` | Before the first irreversible step of the run — before the first tick, the first spawn or the first capture | Exactly one, and it is the first line |
| `gate` | As each check is evaluated. Most are evaluated at closeout; any that can be evaluated earlier is appended earlier | Zero or more (§12.5) |
| `stopped` | When a stop *arrives* — an operator stop, a signal we catch, a stall (`D4.12`), or a fault — written **before** any shutdown work | At most one |
| `run_closed` | When closeout ran to its end | At most one, and it is the last line |

> **D4.36 — every artifact this plan produces is incrementally written, self-describing without a
> closing record, and valid at every instant. A kill at an arbitrary point leaves a shorter artifact,
> never a corrupt one, and a reader distinguishes a complete artifact from an interrupted one by the
> presence of a terminal record, never by whether the file parses.** Abrupt external termination is a
> normal operating mode ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d), so "closed at the end" may never be
> what makes an artifact readable. §12.7 applies this to every artifact by name and says which ones fail
> it today.

#### 12.1.2 The terminal states, and how a reader tells them apart

| What happened | Rows present | What the reader concludes |
|---|---|---|
| The run reached closeout | `run_opened` … `run_closed` | Everything the run set out to record was recorded. The required gate set was evaluated or its omissions are named (V10.3) |
| The run was stopped and we saw it coming — operator stop, a signal we handle, a stall, a fault | `run_opened` … `stopped`, and `run_closed` only if closeout then ran | The run was **stopped, not finished**, and `stopped.reason` says by what. The corpus is shorter, not defective |
| The process was killed outright — `SIGKILL`, the server killed under us, the host lost | `run_opened` … whatever had been appended | The run was interrupted at an unknown instant. The last appended row bounds how far it got; the run manifest and the artifacts on disk are the record of what exists |
| Nothing at all | *(no file)* | The run never reached its first irreversible step. Nothing was produced under this `run_id`, and no corpus exists to be misread |

**A trailing partial line is expected and is not corruption.** A kill can arrive in the middle of an
append. A reader parses lines until one fails to parse, discards that one and stops. Every complete line
before it is a complete fact. This is the mechanical form of `D4.36` for this artifact, and V10.6 is the
rule.

**There is no `incomplete` flag**, because the case that matters most is the one where nothing is alive to
set it. The absence of `run_closed` carries the same information, cannot be forgotten, and cannot be
wrong.

### 12.2 `run_opened` — identity and bindings, written before anything can be lost

Everything here is known before the first tick, which is why it is written first: a run killed one second
in still has a complete statement of what it was.

| Field | Type | Req. | Meaning, and where it comes from |
|---|---|---|---|
| `record_kind` | string | yes | `run_opened` |
| `record_version` | integer | yes | Versions this artifact (§12.13) |
| `run_id` | string | yes | `C4`. The run's identity |
| `session_id` | string | yes | The capture session ([`08`](08_Collection_And_EPoL.md) §3.5) |
| `scenario_id` | string | yes | `C4`, and §5.6 is why it must actually be supplied |
| `invocation_id` | string | no | Supplied by the caller and **echoed verbatim**; opaque to us. How an external process correlates a record with the invocation it made, without us inventing a job model |
| `scenario_package_sha256`, `scenario_package_version` | string, integer | yes | `C3`. Which scenario is being run: the SHA-256 of its `<scenario_id>.lock.json`, which digests every other file of the package, and the lock's `lock_version` |
| `world_opendrive_sha256`, `catalogue_digest`, `blueprint_set_digest`, `epoch_block_sha256` | string | yes | `C3`, `C1`, `C9`. The bindings the run validated against at start |
| `effective_configuration_sha256` | string | yes | The digest of the resolved configuration ([`12`](12_Operator_Control_Surface.md) §3.7 owns the resolution; `C10` carries its digest and nothing else) |
| `seeds` | object | yes | Every seed the run consumed, by name |
| `delivery_modes[]` | array of string | yes | `deferred`, `live`, or both (`D4.29`) |
| `roots[]` | array | yes | `{ root, location, root_version }` for `OBSERVATION` and `TRUTH`. **Locations, not counts** — so a caller whose run was killed knows where to look without any closing row |
| `manifest_paths` | object | yes | `{ run_manifest, corpus_manifest_expected }`. The second is where a corpus manifest *would* be written; its absence later is then defined rather than ambiguous (§12.7) |
| `regeneration` | object | yes | §12.5. Written here because every field of it is known up front |
| `reproduce` | object | yes | `{ configuration_path, effective_configuration_sha256, non_interactive: true }`. **Not a new format**: the configuration is [`12`](12_Operator_Control_Surface.md)'s and this block points at it |
| `limit_declared` | object | no | Present **only** if the caller set a convenience limit, as `{ kind, value }` with `kind` one of `duration_s`, `frames`, `windows`. Absent means the run had no declared end, **which is the normal case** (§12.9) |
| `started_wall_utc` | string | yes | Bookkeeping for a caller's own correlation. Nothing rendered or recorded depends on it (§1) |

### 12.3 `stopped` — the honest record that a run was stopped rather than finished

One row, written the moment a stop is observed and **before** any shutdown work, because shutdown work is
exactly what a second kill interrupts.

| Field | Type | Req. | Meaning |
|---|---|---|---|
| `record_kind` | string | yes | `stopped` |
| `reason` | string | yes | `caller_signal` \| `operator_stop` \| `stall` (`D4.12`) \| `fault`. **Never a value that implies we chose to stop** unless we did |
| `tick`, `sim_time_s` | integer, number | yes | Where the run was in its own clock. The bound on everything after it |
| `detail` | string | no | One line for a human; never parsed |
| `stopped_wall_utc` | string | yes | Bookkeeping |

**Why this row exists, measured.** In the tree today an interrupted run is indistinguishable from a
completed one. `run_SCTMV.py` installs a `SIGINT` handler that only sets a flag (`:246`), catches
`KeyboardInterrupt` and discards it (`:291-292` — the body is `pass`), runs its cleanup and returns `0`
(`:337`), which `sys.exit(main())` makes the process's exit status (`:341`). **`SIGTERM` is not handled
at all** — only `SIGINT` is registered — so an ordinary `kill` never reaches the `finally` block, and
nothing anywhere records that a stop happened. *(Read from the source, 2026-09-18.)*

A deliberate kill is the expected path (§3d), so the record has to say the run was stopped rather than
finished. And because a hard kill runs no handler at all, the structural guarantee — `D4.36`, the absence
of `run_closed` — has to hold **without** this row. This row is the better-quality answer when we are
given the chance to write one; it is never the only answer.

### 12.4 `run_closed` — the projection, written only when closeout ran

Everything here requires the run to have ended in an orderly way, which is why none of it may be
load-bearing for a reader of an interrupted run (V10.11).

| Field | Type | Req. | Meaning |
|---|---|---|---|
| `record_kind` | string | yes | `run_closed` |
| `completion` | string | yes | `stopped_by_caller` \| `stopped_by_operator` \| `limit_reached` \| `stalled` \| `failed` \| `ran_to_scenario_end`. **`limit_reached` may be written only when `limit_declared` was present**, and no consumer may treat its absence as abnormal (§12.9) |
| `ended_wall_utc` | string | yes | Bookkeeping |
| `gates_evaluated` | string | yes | `required_set` when every gate of §12.5's list was evaluated, `partial` otherwise. **Not an aggregate verdict**; a statement about how much checking happened |
| `gates_not_evaluated[]` | array of string | yes when `partial` | Gate ids that were not evaluated, so absence is explicit rather than inferred from a missing row |
| `produced` | object | yes | §12.4.1 |
| `manifest_digests` | object | yes | `{ run_manifest, corpus_manifest }`, the second null when no corpus manifest was written — so the record is provably the record *of* those manifests |

#### 12.4.1 `produced` — what the run actually made

A **convenience projection of the run manifest**, not a second measurement. A reader of an interrupted
run computes the same facts from the manifest and the directory (§12.7, §12.8) and gets the same answers;
this block exists so that a reader of a closed run does not have to.

| Field | Type | Req. | Meaning |
|---|---|---|---|
| `roots[]` | array | yes | `{ root, location, root_version, frame_count, byte_count, manifest_digest }`. A root that was not written appears with `frame_count: 0`, never by omission |
| `sensors[]` | array | yes | `{ sensor_id, frames_written, frames_emitted, frames_dropped, observed_union_s }` — the first from the recorder, the middle two from the stream close records (`C8` §10.9.2), the last from `C2` §4.5. **`frames_written` counts writes the writer completed**, never captures it accepted (§12.7 rule W3) |
| `windows[]` | array | yes | `{ window_index, begin_s, end_s, civil_begin, civil_end, capture_ticks, achieved_ticks_per_wall_s, closed }` — from `C9` §11.8.1 and [`12`](12_Operator_Control_Surface.md) §7.1's clock ratio. **`closed: false`** is legal and means the run ended inside that window |
| `supervision` | object | yes | `{ instances, intervals_closed, intervals_open_at_end, prevalence_units_reported[] }` — counts only, from the truth manifest. **No prevalence figure is a gate**, because a corpus with few positives is not a defective corpus. `intervals_open_at_end` is non-zero for any run stopped mid-interval and is a fact, not a defect |
| `transcript` | object | yes | `{ present, sources[], rows, bytes, truncated, released }` — **reported, never a gate** (§10.10 rule 4). A run whose far end said nothing is a perfectly ordinary run |
| `declared_omissions[]` | array | yes | Every omission the corpus manifest declares (V8.9), echoed so a caller sees them without opening the manifest |

#### 12.4.2 Worked example

The same run as §10.9.2a, **stopped by the caller inside its window**, which under §3d is the ordinary
case rather than the exception. Abbreviated where a block is a table above, and **the numeric values are
illustrative**; what is normative is that every row is self-contained and that no row is an aggregate
verdict.

```jsonc
{ "record_kind": "run_opened", "record_version": 1,
  "run_id": "cap-20260321-2300", "session_id": "bahonar-night-01",
  "scenario_id": "bahonar_pattern_of_life",
  "invocation_id": "regen-2026-03-21T14:00Z#3",
  "scenario_package_sha256": "4c7a…", "scenario_package_version": 1,
  "world_opendrive_sha256": "b02f…", "catalogue_digest": "e51d…",
  "blueprint_set_digest": "77aa…", "epoch_block_sha256": "1d3e…",
  "effective_configuration_sha256": "aa90…",
  "seeds": { "sumo_seed": 20260321, "render_draw_seed": 7 },
  "delivery_modes": ["deferred", "live"],
  "roots": [ { "root": "OBSERVATION", "location": "…/observation", "root_version": 1 },
             { "root": "TRUTH", "location": "…/truth", "root_version": 1 } ],
  "manifest_paths": { "run_manifest": "…/truth/manifest.jsonl",
                      "corpus_manifest_expected": "…/truth/corpus_manifest.json" },
  "regeneration": { "supersedes_run_id": "cap-20260314-2300",
                    "input_digests": { "…": "§12.6" }, "inputs_unchanged": false,
                    "declared_nondeterminism": [
                      "render-thread frame timing affects which ticks a channel captures" ] },
  "reproduce": { "configuration_path": "…/effective.json",
                 "effective_configuration_sha256": "aa90…", "non_interactive": true },
  "started_wall_utc": "2026-03-21T17:02:11Z" }

{ "record_kind": "gate", "id": "C9.corpus_eligible", "owner": "04", "severity": "fail",
  "status": "passed", "observed": true,
  "name": "epoch declared, sun written from it, audit passed",
  "evaluated_at_tick": 1656000, "evaluated_at_sim_time_s": 82800.0 }

{ "record_kind": "gate", "id": "V1.18", "owner": "04", "severity": "warn", "status": "failed",
  "name": "every class in a night window has a working headlight",
  "detail": "lamp_gaps[]: 1 blueprint, low beam not optically confirmed",
  "evaluated_at_tick": 1656000, "evaluated_at_sim_time_s": 82800.0 }

{ "record_kind": "stopped", "reason": "caller_signal",
  "tick": 1661200, "sim_time_s": 83840.0,
  "detail": "SIGTERM received", "stopped_wall_utc": "2026-03-21T17:24:38Z" }

{ "record_kind": "run_closed", "completion": "stopped_by_caller",
  "ended_wall_utc": "2026-03-21T17:24:41Z",
  "gates_evaluated": "partial",
  "gates_not_evaluated": ["D10.7", "V8.16", "12.clock_ratio_recorded"],
  "produced": {
    "roots": [ { "root": "OBSERVATION", "location": "…/observation", "root_version": 1,
                 "frame_count": 10402, "byte_count": 23811240064, "manifest_digest": "5ab1…" },
               { "root": "TRUTH", "location": "…/truth", "root_version": 1,
                 "frame_count": 10401, "byte_count": 1668204411, "manifest_digest": "c93f…" } ],
    "sensors": [ { "sensor_id": "OVERWATCH-1", "frames_written": 5201, "frames_emitted": 5195,
                   "frames_dropped": 6, "observed_union_s": 986.2 },
                 { "sensor_id": "OVERWATCH-2", "frames_written": 5201, "frames_emitted": 5201,
                   "frames_dropped": 0, "observed_union_s": 968.9 } ],
    "windows": [ { "window_index": 0, "begin_s": 82800, "end_s": 84600,
                   "civil_begin": "2026-03-21T23:00:00+03:30",
                   "civil_end": "2026-03-21T23:30:00+03:30",
                   "capture_ticks": 5201, "achieved_ticks_per_wall_s": 4.9, "closed": false } ],
    "supervision": { "instances": 7, "intervals_closed": 6, "intervals_open_at_end": 3,
                     "prevalence_units_reported": ["per_vehicle", "per_interval",
                                                   "per_observed_second"] },
    "transcript": { "present": true, "sources": ["dt-stage-a"], "rows": 24110,
                    "bytes": 52104933, "truncated": false, "released": false },
    "declared_omissions": [] },
  "manifest_digests": { "run_manifest": "d4c2…", "corpus_manifest": null } }
```

**What an automated caller reads off it.** That the run it killed produced 10,402 observation frames and
10,401 truth sidecars — one unpaired capture at the tail, which is §12.7's expected shape and not a
defect; that three annotated intervals were open when it stopped; that one window never closed; that
three gates were never evaluated and are named rather than missing; and that a warn-severity check
observed a blueprint whose low beam was never optically confirmed. **What it does not read off it: a
verdict.** Whether 10,401 sidecars with three open intervals is enough is the caller's judgement, made
against a purpose we do not know.

**What a human reads off it.** The same, plus `stopped.detail` — `SIGTERM received` — which is the one
line that says the run was stopped rather than finished.

### 12.5 The gate record

One `gate` row per check the run was subject to. Each row is a fact about our own data: which check ran,
what it observed, what threshold it compared against, and whether it passed, failed or was skipped. The
point of the row is that a caller — or a human months later — can see **what was checked, what was
observed, against what threshold, and who owns the rule**, without reading a log. **Nothing sums these
rows into a judgement**, here or anywhere else in this contract.

| Field | Type | Req. | Meaning |
|---|---|---|---|
| `record_kind` | string | yes | `gate` |
| `id` | string | yes | The rule's identifier in its owning document — `V8.16`, `V2.6`, `D4.6`, `D10.7` |
| `name` | string | yes | A descriptive name that stands alone, so a reader who does not have the owning document open still knows what was checked |
| `owner` | string | yes | The document that owns the rule — `04`, `08`, `10`, `12` |
| `severity` | string | yes | `fail` or `warn`. **A severity is a property of the rule, not a verdict on the run** — it tells a caller how the rule's author graded the check, and the caller decides what to do about it |
| `status` | string | yes | `passed` \| `failed` \| `skipped` \| `not_applicable` |
| `observed` | any | yes when applicable | The value the run produced |
| `threshold` | any | yes when applicable | The value it was checked against, and where a value is [`10`](10_Scale_And_Performance.md)'s, the one actually in force |
| `skip_reason` | string | yes when `skipped` | Why the check did not run. A skip without a reason is the one thing this record may not contain (V10.9) |
| `detail` | string | no | One line for a human |
| `evaluated_at_tick`, `evaluated_at_sim_time_s` | integer, number | yes | When the check was evaluated, in the run's own clock. In an append-only record a row's position does not say when it was written |

**Four rules about the record itself:**

1. **Every gate that was evaluated appears, including the ones that passed.** A record that lists only
   failures cannot be distinguished from a record produced by a build that forgot to run the checks.
2. **`skipped` is never reported as `passed`.** This is `C9` §11.8.2's `audit_skipped` principle applied
   generally: *"the residual was 0.4 s"* and *"the residual was never measured"* must not look alike. A
   skip carries its reason (V10.9).
3. **A gate that was never evaluated is absent, and absence is explicit where we can make it so.** Most
   runs end when the caller ends them, so a gate evaluated only at closeout is routinely never evaluated
   at all. `run_closed.gates_not_evaluated[]` names them; in a run with no `run_closed`, the absence of
   the row is the only statement available and it means *not evaluated* — **never** *passed*, and never
   *failed* either (V10.3).
4. **No row is an aggregate.** No `gate` row may have as its `observed` value a function of other gate
   rows, and no row anywhere in this artifact may summarise them (V10.2). The caller aggregates.

**A gate whose definition presumes an ending.** Several of the rules below are written "at run end" —
`C2` V2.6 is the clearest. A run routinely has no end of its own, and one stopped mid-window leaves
annotated intervals open **by construction**, which violates nothing. Such a gate is evaluated at
closeout only; the fact that intervals were open is carried by
`produced.supervision.intervals_open_at_end` and by the run manifest, where it belongs.

**The gates this contract requires to be present**, in a run that reaches closeout. `C10` does not invent
gates; it requires that each of these appears with its owner named, because these are the ones whose
absence would let a corpus be misread:

| Gate | Owner | Severity |
|---|---|---|
| Every participant was drawn throughout each open annotated interval | `04` `D4.6` | fail |
| `render_states[]` covers every SUMO vehicle, and no participant ended un-rendered with an open interval | `04` V2.6, V2.7 | fail |
| Neither side stalled | `04` `D4.12`, `C6` §8.5 | fail |
| `corpus_eligible` | `04` `C9` §11.8 | fail |
| The solar residual stayed in tolerance, and the audit was not skipped | `04` `C9` §11.8.2 | fail |
| The observation root contains nothing from §10.4's right-hand column | `04` V8.1, V8.7, V8.8 | fail |
| Every declared omission is declared | `04` V8.9 | fail |
| Drop counters and coverage agree | `04` V8.16 | fail |
| `Dropped` is zero on every channel | `10` `D10.7` | fail |
| The clock ratio was recorded | `12` §7.2 | fail |
| Corpus-affecting events — collisions, teleports, emergency stops, reconciliation refusals | `01` | warn |
| Lamp gaps (`lamp_gaps[]` non-empty) | `04` `C1` V1.18 | warn |
| A supervision–illumination correlation was reported per solar bin | `04` V8.6 | fail |

**`corpus_eligible` keeps its own meaning and is not restated as a verdict.** `C9` §11.8 defines it as a
statement about the epoch and the sun — *"the single field a corpus builder filters on"* — and it appears
here as one gate row among the others, stated once and projected once.

### 12.6 Regeneration and lineage

A corpus regenerated by an external process is a **new run, never an overwrite**. The cadence, the
decision to regenerate and the decision to stop are all the caller's; what the caller needs from us is
that a relaunch cannot destroy what the previous launch produced, and that it can tell whether anything
actually changed without comparing corpora.

This block is written in `run_opened`, because every field of it is known before the run starts — and
because a caller that kills a run one second in should still be able to see what that run was going to be
a regeneration of.

| Field | Type | Req. | Meaning |
|---|---|---|---|
| `supersedes_run_id` | string | no | The previous run this one regenerates, when the caller supplied it. We do not infer it |
| `input_digests` | object | yes | `{ scenario_package_sha256, world_opendrive_sha256, catalogue_digest, effective_configuration_sha256, seeds }` — the complete set of inputs that determine a run |
| `inputs_unchanged` | boolean | no | True when every entry of `input_digests` equals the superseded run's. **Computed only when `supersedes_run_id` was supplied**, and a statement about the *inputs*, never about the outputs |
| `declared_nondeterminism[]` | array of string | yes | Named sources of run-to-run variation that survive identical inputs, each with the artifact that records it. Empty is a claim, not a default, and V10.10 is why |

**Why `inputs_unchanged` is a statement about inputs only.** Asserting that two corpora are identical
would require comparing them, and two runs with identical inputs may still differ wherever
`declared_nondeterminism[]` says they may. The honest field is the cheap one: *the inputs were the same*.
What a caller does with that — skip, re-run for a new sample, or diff the outputs itself — is outside this
contract and is emphatically the caller's.

### 12.7 Crash safety, per artifact

A deliberate kill at an arbitrary instant is a normal operating mode
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d), so every artifact needs a stated answer to four questions: *is
it valid, is it truncated but readable, is it self-describing without a closing record, and how does a
reader tell a complete one from an interrupted one.*

Three writer rules make `D4.36` checkable rather than aspirational. They are stated once and referenced
from the table:

| # | Writer rule | Why |
|---|---|---|
| **W1** | **Whole-file artifacts are written to a temporary name in the same directory and renamed into place.** A rename within a filesystem is atomic, so a reader never sees a partially written file under its final name, and a leftover temporary file is exactly the interrupted tail | Without it, "the file is present" and "the file is complete" are different statements and nothing distinguishes them |
| **W2** | **Growing artifacts are append-only files of self-contained rows, flushed per row.** A reader parses rows until one fails to parse, discards that one, and has a valid prefix | This is what makes a closing record unnecessary for readability. The closing row becomes a *statement about closure* rather than the thing that makes the file parseable |
| **W3** | **Nothing claims a frame the writer did not complete.** Coverage rows, observed spans and `frames_written` are written from the writer's completion, never from the capture callback | The encode queue is bounded and in memory; a kill discards what is in it. A manifest that counted captures rather than writes would claim frames that do not exist on disk |

| Artifact | How it is written | After a kill at an arbitrary instant | How a reader tells complete from interrupted | Meets `D4.36`? |
|---|---|---|---|---|
| **Capture imagery** (PNG, `OBSERVATION`) | One whole file per capture | Every completed PNG is valid and **self-describing**: the `carla:capture` and `carla:solar` tEXt chunks are written **between IHDR and IDAT** (`PngEncoder.cs:44-47`), so even a truncated file carries its tick, simulated time and run id before any pixel data — the writer's own comment says as much: *"so a still is self-describing even once separated from its sidecar"* (`CaptureMetadata.cs:31-32`). At most one file is truncated | The `IEND` chunk. A file without it is a truncated capture | **Nearly.** Measured: the file is written straight to its final name (`FileStream(path, FileMode.Create…)`, `PngEncoder.cs:19`), so *present* does not yet imply *complete*. **W1 closes it** |
| **Truth sidecar** (CoT XML, `TRUTH`) | One whole document per capture, `XmlWriter.Create(path, …)` (`CotWriter.cs:25`), root element `<events>` carrying `tick`, `sim_time_s` and `run_id` as attributes (`:42-44`) | Every completed sidecar is a valid, independently readable document. The last one may be truncated, and a truncated XML document **does not parse at all** | An XML parse. There is no cheaper signal, which is the problem | **No — this is the artifact that fails the property today.** Its readability depends on a closing record (`</events>`) and it is written in place. **W1 closes it**: a truncated document then never appears under a final name |
| **The capture pair** (PNG + sidecar for one tick) | PNG first, sidecar second (`FrameRecorder.cs:225`, `:230`), counted only after both (`:233`) | The tail of an interrupted run is **an image with no sidecar, never the reverse**. In-flight captures still in the bounded encode queue (`Channel.CreateBounded(max(4, workers×2))`, `FrameRecorder.cs:116`) are lost outright | An unpaired file stem | **Yes, and the write order is load-bearing** — reversing it would produce truth for a frame that does not exist. An unpaired stem is *captured but unlabelled*: not observation data whose truth may be assumed empty, and W3 keeps it out of every denominator over labelled frames (`C2` §4.5) |
| **Run manifest** (`TRUTH`) | Append-only rows under W2, closed at run end (`C2` §4.1, `C9` §11.8) | Everything already true is on disk. A vehicle admitted but not released has an admission row and no release row; a window begun but not finished has a begin row and no end row; the `C9` solar blocks are as complete as the run got | A `manifest_closed` row. Its absence means the run was interrupted, and every unpaired open is *still open at the interruption* — **never zero-length, never dropped** | **Yes.** The incremental write is the guarantee; the closing row is a statement about closure and nothing depends on it to read the file |
| **Supervision content** (`<_supervision>` per sidecar; instance and interval records in the run manifest; payload owned by [`06`](06_Truth_And_Annotation.md)) | Per-frame supervision rides inside each whole sidecar; interval opens and closes are manifest rows | Per-frame supervision survives automatically, because each sidecar is whole. An interval open at the kill has an open row and no close row | The close row. An interval without one is **`open_at_interruption`** — a fact about when the run stopped, not a defect and not a silently dropped interval | **Yes, given W2** — and the property [`06`](06_Truth_And_Annotation.md) must preserve is that an interval is written **at open**, not composed at close. An interval materialised only on closing is an interval a kill erases |
| **Corpus manifest** (`TRUTH`) | A **closure artifact** by definition: it describes a corpus that has been published, so it cannot be incremental | A run the caller stopped has none | Its presence. **Absence is defined, not ambiguous**: it means *this corpus was never published*, and the reader falls back to the run manifest, which is incremental and says what exists | **By exception, and the exception is declared.** This is the one artifact that cannot meet `D4.36`, and the honest treatment is to define its absence rather than to pretend. `run_opened.manifest_paths.corpus_manifest_expected` names where it would have been, so *absent* is distinguishable from *elsewhere* |
| **Transcript** (`index.jsonl` + `blobs/<seq>.bin`, `C8` §10.10) | Append-only index rows, one blob per row. **The blob is written and flushed first, then the index row** | Complete rows are valid; a trailing partial row is discarded on parse. A blob may be truncated | V8.19: a blob whose length or digest disagrees with its row is untrusted. The write order means a row can never name a blob that does not exist; a blob with no row is an orphan and is ignored | **Yes.** No field of the container is affected (`D4.32` stands); the guarantee is in the write order |
| **Live handover streams** (`C8` §10.9) | Not files. Records are emitted after the tick that produced them (L1) | The consumer receives **no `stream_close`**, and every record it already received remains valid and self-describing, because the `stream_header` gave it everything needed to interpret them (`D4.31`) | Silence, plus the absence of a close record — V8.17's *"if the process died there is no close record, and its absence is the signal"*. That is the **expected** ending of a live run, so a consumer treats a stream that simply stops as a normal termination and not as an error | **Yes, and by design.** A consumer that attached mid-run and one that was there from the start are equally well equipped, so a stream that ends without ceremony loses nothing already delivered |
| **World truth track** (`truth/world_truth_track.csv` and its `.summary.json`, `TRUTH`; payload owned by [`06`](06_Truth_And_Annotation.md) §8.3) | Append-only CSV under W2: the header first, then one line per vehicle per sampled SUMO frame, each flushed before the next is composed. The summary beside it is whole under W1, written at the start with the columns and the rate and again when the session ends | Every line ending in a line break is a complete row, and at most one trailing line, without its line break, was cut off and is left off: the prefix is valid. The summary already gives the columns, the SUMO step and the interval, so the rows are self-describing without a closing record | The summary's `ended`: `scenario_finished`, `caller_stopped` or `run_stopped` with its stage and cause. `ended: null` means the run was interrupted, and the last row bounds how far the track got -- **never** zero vehicles at the instants after it | **Yes.** Tested by cutting a written track at every line break, either side of it and part-way along the next line |
| **Run record** (`run_record.jsonl`) | Append-only under W2, flushed per row, `run_opened` before the first irreversible step | The prefix is valid. Identity, bindings, seeds, root locations and `declared_nondeterminism[]` are all present from the first line | The `run_closed` row (§12.1.2) | **Yes.** This is the artifact that makes the others' interruption legible |
| **Effective configuration and lock** ([`12`](12_Operator_Control_Surface.md) §3.6) | Whole files, written at resolution — **before** the window | Present and complete for any run that got as far as starting | Their presence. A run with neither never resolved a configuration | **Yes, by being early.** Written before anything a kill could interrupt |
| **Scenario package, world package, catalogue** (`C3`, `C1`) | Inputs. Read-only during a run | Unaffected. A kill cannot damage what is only read | — | **Not applicable**, said explicitly so nobody looks for a rule |

**One artifact fails `D4.36` today and one cannot meet it.** The truth sidecar fails it, and W1 fixes it;
the capture PNG fails only the weaker *present-implies-complete* half, and W1 fixes that too; the corpus
manifest cannot meet it, and its absence is defined instead. Everything else either holds already or
holds once W2 is applied to a writer this plan specifies anyway.

### 12.8 Observing a run in progress

> **`C10` publishes no verdict, so the interface that matters is the one that lets a caller decide for
> itself.** §3d: the external process *"[uses] the CarlaNet and Python shim to query data so as to decide
> when enough is enough"*. This section states what is observable **through surfaces that already
> exist**, at what cost, and — where nothing existing answers a question — **names the gap as a gap
> rather than designing a channel around it**.

> **D4.37 — a caller observes a run in progress through the surfaces that already exist: a second client
> on the CARLA server, the live handover stream if one is open, and the incrementally written artifacts
> on disk. This plan adds no status service, no progress RPC, no completion percentage and no callback to
> the caller.** Where an existing surface cannot answer a question, the gap is recorded as a gap. A status
> service would be a fourth place a run's state is stated, and the first disagreement between it and the
> manifest is the one nobody can adjudicate — the same argument that keeps this record a projection
> (§12).

#### 12.8.1 Surface one — a second client on the CARLA server

The shim and the C# client both connect additional clients to a running server; nothing about a
SUMO-driven run changes that. Read from the client and shim sources, 2026-09-18.

| Question a caller might ask | Call | Cost | Read from |
|---|---|---|---|
| **How many vehicles are rendered right now, and which SUMO vehicles are they?** | `world.get_actors()` | A **free** cache read for the id list plus **one** RPC for the descriptions | `GetCachedActorIds()` returns the world-observer cache's keys with no RPC (`CarlaClient.cs:1985`); `get_actors_by_id` is the single round trip (`:1431-1432`). The cache evicts any actor absent from the latest snapshot (`:1896-1908`), so the list is the live render set, not a high-water mark |
| **Which SUMO id, class, entity and instance is each rendered actor?** | `actor.attributes` on the same result | Free once the descriptions are in hand | The `capture:*` spawn attributes of `C4` §6.4, which the server returns to **every** client (`CarlaEpisode.cpp:253-270`; unfiltered at `ActorDescription.h:47-56`). This is the one place a foreign process can see the render set's identity without our cooperation |
| **Where is each one, and how fast?** | `actor.get_transform()`, `get_velocity()` | Free cache reads | `CarlaClient.cs:1915-1919`. **Velocity is useless here** — a SUMO-driven body is non-simulating and reports zero (`D4.13`, `C7` §9.2, audit `G5.1`). A caller must not read progress from velocity |
| **How far has the run got in its own clock?** | `world.get_sim_time()` | Free cache read | `carlanet/__init__.py:2017-2025` → `LatestElapsedSeconds` (`CarlaClient.cs:146`) |
| **What tick is it on?** | `world.on_tick(cb)` | Free, **push** — no polling at all | `carlanet/__init__.py:2145-2162`, which passes the observer frame's own `Frame`. `wait_for_tick`'s returned timestamp is synthetic (audit `G5.6`), so a caller reads a tick number from `on_tick` and not from `wait_for_tick` |
| **What civil time and sun is the scene at?** | `world.get_solar_state()` | Free cache read, tick-paired, **no RPC** | `carlanet/__init__.py:1511-1534`. Subject to audit `G5.17` (the cached solar block is not tick-stamped) and `G5.18` (a world with no `CesiumSunSky` publishes a fabricated state rather than none) |
| **What is the sandbox extent, to test containment myself?** | `world.get_staging_bounds()` | One RPC, once — it does not change during a run | `carlanet/__init__.py:1596-1606`, `CarlaServer.cpp:828-842` |

**The cost model, stated because it is not the usual one.** Under synchronous ticking the per-frame RPC
budget does not apply at all: the engine drains the request queue until the tick cue arrives
(`Game/CarlaEngine.cpp:332-347`; `-RPCBudgetMs` is documented as ignored in synchronous mode —
[`05`](05_CarlaNet_Capability_Audit.md) §13). **Inferred, and stated as an inference:** an observer's RPC
therefore does not compete for a slice, it delays the next frame by its own latency. The practical rule
follows and is cheap: **subscribe once and read the cache.** Every question in the table above except two
costs nothing per poll, and the two that cost an RPC are answered once or at a bounded rate.

**The one thing an observer must not do.** `C6` §8.4 forbids every participant from calling
`world.tick()`, from changing `synchronous_mode` or `fixed_delta_seconds`, and from writing the sun; an
external process holding a client is a participant for that purpose. A second ticker desynchronises the
co-simulation irrecoverably (`D4.11`, `D4.12`) and does so **silently**, and the transport cannot prevent
it — the RPCs are reachable from any client. It is the only thing a caller can do from the observation
surface that damages the data, and it is worth one sentence in whatever the caller is handed.

#### 12.8.2 Surface two — the live handover stream, when one is open

If the run is emitting a live handover (`C8` §10.9), the caller already has the strongest progress signal
in the system and it costs **no query at all**, because it is pushed: `seq` increases by one per
`(stream_kind, sensor_id)` at capture, and `dropped_cumulative` counts what the link did not carry
(§10.9.2, guarantee L2). A consumer therefore knows exactly how many frames were produced and how many it
missed, by arithmetic. The `stream_header` gives it the capture interval and the declared real-time
factor, so it can also say how far behind it is.

This surface is **specified in this plan** and is not code in the tree — stated so nobody counts it as
existing. Where it exists, it answers "how many frames have been produced" better than anything else
listed here.

#### 12.8.3 Surface three — the artifacts on disk

Because `D4.36` makes every artifact incremental, the run directory *is* a progress surface, and it is the
cheapest one: it touches neither the server nor the tick thread.

| Question | Answer, from the directory alone |
|---|---|
| How many frames have been written? | Count the files in the `OBSERVATION` root. Under W3 every one of them is a completed write, and under W1 every one of them is complete |
| How far has the run got? | The `tick` and `sim_time_s` attributes on the most recent complete sidecar (`CotWriter.cs:42-43`) — **not** the file name, which is the camera's name and local wall-clock to the millisecond (`CameraName.StillStem`) and is not a join key (`C4` §6.6) |
| Which vehicles are in the render set, and which were released? | The run manifest's appended admission and release rows (`C2` §4.5) |
| How many annotated intervals have closed? | The manifest's interval close rows — for intervals that were rendered and written; see gap 1 below |
| What did the run bind to, and what is it a regeneration of? | `run_record.jsonl`'s first line, present from before the first capture (§12.2) |

#### 12.8.4 What is a genuine gap

Named, not designed around. Each is a fact about the tree as read on 2026-09-18.

| # | Gap | What it blocks | Where it belongs |
|---|---|---|---|
| **1** | **Annotation state is not readable outside the driver's process.** There is no RPC, no shim method and no world actor publishing pattern-instance or interval state. A caller asking *"how many annotated intervals have closed"* can only parse `<_supervision>` from the sidecars on disk, which lags by the encode queue and sees only intervals that were rendered and written | The single most likely "is this enough?" question, answered directly | **This section's open question 5**, which asks whether annotation state must be readable across processes and recommends publishing it on a world actor in the staging-bounds shape ([`05`](05_CarlaNet_Capability_Audit.md) §11 describes that pattern completely: flat primitives, no LibCarla file touched). An external caller's need raises the stakes on that question without changing its options, and this contract does not pre-empt it |
| **2** | **No recorder counter leaves the process.** `FrameRecorder.Saved` and `Dropped` are in-memory fields (`FrameRecorder.cs:48-49`, incremented at `:233` and `:185`) **with no reader anywhere in the tree** ([`12`](12_Operator_Control_Surface.md) §7.3's first silent failure). *Frames dropped* is therefore observable nowhere outside the process, and a caller counting files cannot distinguish a capture the queue dropped from one the capture interval never took | Telling a short corpus from a lossy one while the run is still going | [`12`](12_Operator_Control_Surface.md), which needs the same counter for its monitor, and `C8` §10.9, which needs it for the stream's drop accounting. One reader closes all three. **Not a new channel** |
| **3** | **`LatestObservedFrame` is not wrapped in the shim** although the C# client exposes it (`CarlaClient.cs:149`). A Python observer can pull simulated seconds for free but must attach an `on_tick` callback to see a tick number | Nothing important — `on_tick` answers it — but it is a one-line asymmetry of the kind [`05`](05_CarlaNet_Capability_Audit.md)'s gap register exists to hold | [`05`](05_CarlaNet_Capability_Audit.md)'s register. No id is minted here, because that numbering is `05`'s |
| **4** | **If the caller cannot see the run directory, only the live stream answers "what has been written".** The artifact surface of §12.8.3 assumes a shared filesystem. A caller on another host with no share and no stream open has the server surface only, which says what is *rendered*, never what was *written* | Remote operation without a live handover | Named here as a deployment constraint. **We propose no artifact-status RPC for it** — that is the status service `D4.37` refuses |

**One thing that is not a gap, and is worth separating.** *"How much of a declared area has been
covered"* has no publisher, but every input to it already exists on the caller's side: the resolved area
table is a file it holds (`C5` §7.2), live actor positions are free cache reads (§12.8.1), and the
per-vehicle observed spans are in the manifest (`C2` §4.5). **A caller can compute area coverage itself
from artifacts and reads that already exist.** We publish no percentage, because a coverage fraction
depends on what the caller means by covered — by area, by lane metres, by observed seconds, per sensor or
unioned — and choosing one for it would be exactly the judgement §3d removes from us.

### 12.9 No run-length policy

> **D4.38 — nothing in this contract requires a run to have a declared length. A convenience limit may
> exist on the invocation surface; no field, rule, gate or reader here may assume one was set, and
> reaching the end of a limit is one ordinary way a run can end among several.** The caller stops us
> ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d), so a contract that needed a duration would be a contract that
> only worked for callers who did not want to use it that way.

What follows, stated so nobody reintroduces a length requirement by accident:

- `limit_declared` in `run_opened` is **optional and usually absent**; its absence is the normal case and
  is never a defect (§12.2).
- `completion: "limit_reached"` may be written only when `limit_declared` was present (§12.4).
- A capture window that never closed is recorded with `closed: false` and is not a failure of anything. A
  window is a declaration of *what simulated time to render*, not a stop rule.
- No gate may be defined such that it can only pass in a run that reached a declared end. Gates evaluated
  at closeout simply do not appear in an interrupted run, and §12.5 rule 3 says what absence means.
- `--duration` and `--frames`, if [`12`](12_Operator_Control_Surface.md) offers them, are conveniences.
  This contract records that one was used; it never requires one, and a corpus produced without one is
  not less describable than a corpus produced with one.

### 12.10 The measured facts this contract rests on

| Measurement | Why it matters here |
|---|---|
| **The interrupt is swallowed.** `run_SCTMV.py` registers a `SIGINT` handler that only sets a flag (`:246`), catches `KeyboardInterrupt` and discards it (`:291-292`), and returns `0` (`:337`, `:341`). **`SIGTERM` is not handled at all.** *(Read from the source, 2026-09-18)* | A deliberate kill is the expected path, so it must leave valid artifacts (§12.7) and an honest record that the run was *stopped* rather than *finished* (§12.3). Because a hard kill runs no handler, the structural guarantee has to hold without one (`D4.36`) |
| **`FrameRecorder.Dropped` and `Saved` have no reader** (`FrameRecorder.cs:48-49`, `:185`, `:233`); the recorder's encode channel is `BoundedChannelFullMode.DropWrite` with the comment *"never block the stream-reader thread"* (`:118`) | The caller decides *enough* by querying, and a counter that cannot leave the process is a question that cannot be answered while the run is alive (§12.8.4 gap 2). The drop-oldest policy itself is right — it is what keeps the link from owning the clock (`D4.30`) |
| **Five environment variables reach behaviour and none is recorded**, measured by [`12`](12_Operator_Control_Surface.md) §3.10.1 | A caller that relaunches us repeatedly is exactly the caller a host-dependent default would silently vary the corpus for (§12.11 property 2, `D4.24`, `D4.25`) |
| **The pacing pattern** — `SumoCotBridge` paces against an absolute target, *"a step that overruns is absorbed by the next one instead of accumulating drift over a long run"* (`CarlaControl/src/carlacontrol/SumoCotBridge.py:243-246`) | The real-time factor for a live exercise (`C8` §10.9.5), and the reason a long run does not drift away from the civil time its artifacts claim |

### 12.11 What non-interactive invocation has to be, stated as properties

`C10` owns the record; [`12`](12_Operator_Control_Surface.md) owns the surface. A caller that cannot
answer a question still cannot answer one ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d), so these properties
bind whatever that surface looks like.

1. **Non-interactive.** A run never prompts, never requires a terminal, and never waits for a human. A
   surface that can block on input cannot be driven by a process.
2. **Parameterised from artifacts only.** Everything that determines the run comes from the scenario
   package and the resolved configuration. **Nothing may come from the host clock, host time zone or host
   locale** — `D4.24` and `D4.25` — and repeated automated invocation is exactly the condition under
   which a host-clock default would produce a differently-lit corpus every time with nothing saying so.
3. **Reproducible.** Identical `input_digests` produce an identical corpus except where
   `declared_nondeterminism[]` says otherwise.
4. **Addressed by `run_id`.** A run writes into a location keyed on its own `run_id`, so a repeated
   invocation cannot overwrite a previous corpus. A caller that kills and relaunches at will would
   otherwise be one relaunch away from destroying the only good corpus it had.
5. **The record is always written, and nothing depends on an exit status.** The exit-status set is
   [`12`](12_Operator_Control_Surface.md) §3.10.2's and is not restated here; **a killed process has no
   exit status worth reading at all.** The artifact on disk is the record; an exit status is a
   convenience for the case where we were allowed to return one.

### 12.12 Validation

| # | Rule | Response |
|---|---|---|
| V10.1 | A `run_opened` row exists for every run, written before the first irreversible step | assertion at the writer. A run that produced artifacts but has no record is a run nothing can describe |
| V10.2 | No row in the record is an aggregate of other rows, and the record contains no field that grades the corpus as a whole | refuse to publish the record ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d) |
| V10.3 | In a run that wrote `run_closed`, every gate of §12.5's required list either appears as a `gate` row or is named in `gates_not_evaluated[]`. In a run with no `run_closed`, an absent gate means *not evaluated*, and no reader may treat it as passed or failed | refuse to publish the record; the reader's half is §12.5 rule 3 |
| V10.4 | `corpus_eligible` appears exactly once, as a gate row projected from `C9` §11.8, and the record states no verdict derived from it | refuse to publish |
| V10.5 | Every identity digest in `run_opened` is present and equal to the run manifest's | refuse to open the run — a record that cannot be tied to its manifest is an assertion about nothing |
| V10.6 | Every row is a single line of valid JSON, flushed to disk before the next row is composed; a reader discards a trailing line that does not parse | assertion at the writer, and a reader rule. This is `D4.36` for this artifact |
| V10.7 | No field of the record is a function of any transcript row (`D4.32`, V8.20) | fail the build |
| V10.8 | The record contains no model metric, score, comparison or statement about anything outside this system | refuse to publish ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b) |
| V10.9 | No gate's `status` is `passed` when its check did not run, and every `skipped` row carries a `skip_reason` | refuse to publish |
| V10.10 | `declared_nondeterminism[]` is present in `run_opened`; an empty array is written explicitly and is a claim the run is making | refuse to open the run, because an omitted list reads as "none" without anybody having said so |
| V10.11 | No field outside `run_closed` requires the run to have ended, and `run_closed` is never a prerequisite for reading any other row | checkable by reading the record with its last row removed and confirming every remaining row still parses and still means what it meant |
| V10.12 | No field, gate or reader rule assumes a declared run length; `completion: "limit_reached"` appears only when `limit_declared` was present | refuse to publish (`D4.38`) |
| V10.13 | Every artifact named in §12.7 satisfies W1 or W2, and the two declared exceptions — the corpus manifest, and a live stream's missing close record — are the only ones | a review rule against the writer set rather than a runtime check; V10.14 is what a test can do |
| V10.14 | **The kill test.** A run is killed at a randomly chosen instant and every artifact it left is read: every complete PNG decodes, every sidecar parses, the run manifest parses as a prefix, the run record parses as a prefix, and the terminal-state rule of §12.1.2 returns the right answer | a test. It is the only way to *check* `D4.36` rather than assert it, and it is cheap to run |

### 12.13 Versioning

`record_version` is an integer carried on the `run_opened` row, so a reader knows before anything else
whether it can read the file. A reader that does not implement the version **refuses to read the record**
and says so; it draws no conclusion about the corpus from that refusal, because the record's version says
nothing about the data's quality. Adding a field is a version bump only when a reader that ignores it
would be misled; adding a gate to the required list is **not** a version change, because gates are rows
and a reader that does not recognise an id can still read the row. Adding a **row kind** *is* a version
change, because a reader that skips an unknown `record_kind` would be silently discarding a fact.

### 12.14 Failure modes

| Situation | What happens |
|---|---|
| **The caller kills the client mid-window** | The artifacts on disk are valid and shorter (§12.7). If the signal is one we handle, a `stopped` row says so; if it is not, the absence of `run_closed` says the run was interrupted. **This is a normal ending, not a failure** |
| **The caller kills the CARLA server** | The client's next call fails; the run stops. Whatever the writers had completed is on disk and valid. Captures still in the encode queue are lost and, by W3, were never claimed |
| **The caller kills the SUMO process** | The stall rule fires (`D4.12`): stop advancing both sides, `stopped` with `reason: "stall"`, close what can be closed. If the kill also takes us, `D4.36` covers it |
| **The run is killed between a PNG and its sidecar** | An unpaired stem, which is the expected tail shape (§12.7). The frame is captured-but-unlabelled and is excluded from denominators over labelled frames, never assumed to have empty truth |
| **The process dies before `run_opened`** | No record, and none is needed: nothing irreversible had happened (§12.1.2, last row) |
| **A gate's threshold was never valued** | The gate is `skipped` with a `skip_reason`. It is **not** a verdict about the run; a caller that cares about that check sees that it did not run |
| **The record disagrees with the manifest** | V10.5 catches the identity case at open; anything subtler is a defect in the projection, which is why §12 forbids the record from measuring anything of its own |
| **A caller wants a single field to branch on** | There is not one, deliberately. It has `gates[]`, which is strictly more information, and the aggregation rule is its own |

### 12.15 What breaks if C10 is violated

- **A kill leaves an artifact nobody can read.** This is the whole of `D4.36`. A truth sidecar written in
  place and truncated by a kill does not parse, and the tick it described is simply gone — where under W1
  it would have been either complete or absent, both of which a reader can handle.
- **A stopped run looks like a finished one.** Measured, today: `run_SCTMV.py:291-292` discards the
  interrupt and `:337` returns `0`. Without `stopped` and without the terminal-state rule, a corpus cut
  short at minute nine of forty is indistinguishable from one that ran to its end, and every count taken
  over it is quietly a count over a fragment.
- **A check that never ran looks like a check that passed.** The asymmetry `C9` §11.8.2 guards for the
  solar audit applies to every gate: `skipped` and `passed` must not look alike, and in an interrupted run
  *absent* must not look like either (V10.3, V10.9).
- **Someone re-invents the verdict.** If any field here aggregated the gates, a caller would branch on it
  — and would be branching on our guess about a purpose we do not know (§3b, §3d). The rows are the
  product; the judgement is the caller's.
- **A caller has to poll something that does not exist.** Without the observation contract (§12.8) the
  obvious next move is a status service — a fourth place a run's state is asserted, and the first one to
  disagree with the manifest. `D4.37` refuses it, and the price of refusing it is that the genuine gaps
  are named instead of papered over.
- **A relaunch destroys the corpus that worked.** A caller that kills and relaunches at will needs
  `run_id`-keyed locations (§12.11 property 4, `D4.35`) far more than a polite caller does.
- **A value with no reader stays unread.** `FrameRecorder.Dropped` exists and nothing reads it
  (`:48-49`), so a run that silently dropped a tenth of its frames looks identical to one that dropped
  none — to the operator, to the corpus, and to the external process deciding whether it has enough.
- **The transcript becomes a gate.** If any field of the record were a function of what came back, our
  data's description would depend on somebody else's software being up — and a corpus would be recorded
  as deficient because a service we do not own was restarting (`D4.32`, V10.7).

---

## 13. The protocol, end to end

```mermaid
sequenceDiagram
  autonumber
  participant OP as Operator
  participant SRV as CARLA server
  participant SW as Catalogue sweep
  participant AU as Author (assisted or by hand)
  participant SB as Scenario package builder
  participant DR as Co-simulation driver
  participant SC as Solar clock (C9)
  participant SU as SUMO
  participant RS as Render-set controller
  participant REC as Frame recorder

  Note over OP,SW: Catalogue generation — once per content build
  OP->>SW: make_vehicle_catalogue.py
  SW->>SRV: get_actor_definitions
  SRV-->>SW: 17 definitions, no dimensions (C1 §3.2)
  loop per blueprint
    SW->>SRV: spawn_actor at a clear transform
    SRV-->>SW: rpc::Actor with bounding_box
    SW->>SRV: destroy_actor
  end
  SW->>SRV: set_solar_date + set_solar_time (night), set_time_advance(false)
  loop per blueprint, per lamp bit
    SW->>SRV: spawn, set light state, capture, compare to the NONE reference
  end
  SW-->>OP: vehicles.catalogue.json<br/>catalogue_digest, blueprint_set_digest,<br/>lamp_capability per blueprint (C1 §3.2a)

  Note over AU,SB: Scenario authoring — no CARLA in the loop
  AU->>SB: classes wanted, flows, trips, areas, annotations,<br/>epoch and illumination policy
  SB->>SB: C1 V1.5-V1.19 · C5 V5.8-V5.12 · C4 V4.1-V4.3 · C9 V9.1-V9.10
  SB->>SB: emit one vType per member blueprint,<br/>one vTypeDistribution per class (C1 §3.6)
  SB->>SB: resolve areas to lanes + arc lengths (C5 §7.3)
  SB-->>AU: <scenario_id>.lock.json and the files beside it:<br/>network, routes, sumocfg, supervision plan (C3)

  Note over DR,REC: Playback
  OP->>DR: run the package's .sumocfg against a loaded world,<br/>with an optional illumination override (12)
  DR->>SRV: get_actor_definitions
  DR->>DR: C3 V3.1-V3.14 — world binding, digests, epoch
  DR->>DR: C1 V1.14 — blueprint_set_digest
  DR->>DR: C1 V1.14a — load the catalogue the lock names;<br/>extents drive the pose conversion (D4.17)
  DR->>DR: C6 V6.1-V6.4, V6.10 — clock
  DR->>SC: hand over epoch + illumination_in_force
  SC->>SRV: get_solar_state — the sun as found, recorded as sun_time_zone_hours (C9 §11.8)
  SC->>SC: C9 V9.11-V9.14
  alt any refuse-tier mismatch
    DR-->>OP: refuse, naming the artifact and the field
  else all pass
    DR->>SU: start, seed, step-length
    loop each SUMO step
      DR->>SU: step
      SU-->>DR: subscribed poses, types, edges, signal bits
      DR->>RS: admission pass (C2 §4.2)
      RS->>SRV: spawn admitted — capture:sumo_id, role_name=sumo,<br/>colour by the seeded draw (C1 §3.8, C4 §6.4)
      RS->>SRV: destroy released, release instant recorded (C2 §4.3)
      loop k world sub-steps
        SC->>SC: civil(n) from the epoch (C6 G8)
        SC->>SRV: set_solar_epoch — date, clock and zone — at window open,<br/>and for every tick under advance (C9 §11.4, §11.6)
        DR->>SRV: set_actor_transform, interpolated,<br/>bumper shift undone, Z from drape (C7)
        DR->>SRV: SetVehicleLightStateCommand on change —<br/>brake and blinkers from SUMO, conspicuity from the sun (C7 §9.4)
        DR->>SRV: world tick
        SRV-->>SC: solar block on the world-observer snapshot (no RPC)
        SC->>SC: audit Δsolar_s, Δdir_deg, Δpolicy (C6 §8.3a, V6.6-V6.8)
        SRV-->>REC: frame
        REC->>REC: truth with SUMO velocity (D4.13),<br/>annotation snapshot for THIS tick (G6),<br/><_solar> for this tick (C9)
      end
    end
    SC-->>DR: solar_achieved[], solar_residual (C9 §11.8)
    DR-->>OP: run manifest — render_states, observed spans, prevalence (C2 §4.5),<br/>epoch, illumination_in_force, solar residual (C9 §11.8)
  end
```

**The same protocol in the live delivery mode.** Not one message above changes. The recorder's records
are *additionally* emitted as they are produced (`C8` §10.9), the run is paced at a declared real-time
factor rather than as fast as the machine allows (`D4.30`), and each stream ends with a `stream_close`
beside the manifest. That is the whole difference, and it is the point of `D4.29`: a live exercise is
this protocol with a second delivery attached, not a second protocol.

**And the same protocol driven from outside.** The `Operator` lane becomes an external process: the
opening message carries an `invocation_id` and a resolved configuration digest instead of a typed
command line ([`12`](12_Operator_Control_Surface.md) owns that surface), the process may observe the run
as it goes through the surfaces of `C10` §12.8, and it may end the run at any instant by killing any
participant — in which case the closing messages simply do not happen and the artifacts on disk are what
remains (`D4.36`). Where a closeout does run, the last thing written is the run record's `run_closed`
row (`C10`, §12).

---

## 14. Dependencies on other sections

Stated as properties needed, not as requests.

| Section | Property this section needs it to have |
|---|---|
| [`03_CoSimulation_Runtime.md`](03_CoSimulation_Runtime.md) | A single advance entry point that satisfies G1–G11 and implements the stall rule `D4.12`. A mechanism by which SUMO's velocity reaches the truth producer per actor (`D4.13`). A solar clock that derives civil time from the tick alone (`D4.25`), writes the sun under whichever policy is in force, writes the **date** at every civil midnight the effective date rule of G11 calls for, and reports which advance mechanism it used. A light mapper that reads SUMO's signal bits and the tick's solar elevation and emits `SetVehicleLightStateCommand` in the pose batch (`D4.22`). A **real-time factor on the world tick**, computed against an absolute target in the manner of `SumoCotBridge.py:243-246` rather than as a per-step sleep, with the achieved factor reported per window — and never a function of consumer state (`D4.30`, §10.9.5) |
| [`05_CarlaNet_Capability_Audit.md`](05_CarlaNet_Capability_Audit.md) | Whether the Python shim exposes a spawned actor's bounding box (needed by the sweep, §3.2); whether `set_actor_transform`, `set_actor_simulate_physics` and `apply_batch_sync` are implemented end to end. For `C9`: the solar surface is present and complete end to end, including the one call the session writes the sun with, `set_solar_epoch` (`carlanet/__init__.py:1523`, `CarlaClient.SetSolarEpochAsync` at `CarlaClient.cs:1117`, `CarlaServer.cpp:644`), beside `set_solar_time`, `set_solar_date`, `get_solar_state` and `set_time_advance` (`carlanet/__init__.py:1512`, `:1518`, `:1541`, `:1575`). For `C7`: `SetVehicleLightStateCommand` is present at every layer — C# record (`Command.cs:94`), formatter (`CommandFormatter.cs:64`), client methods (`CarlaClient.cs:1615`, `:1621`), shim command wrapper (`carlanet/__init__.py:1141-1147`), shim actor methods (`:781`, `:786`) — so §9.4 needs nothing built on the transport side. What is needed is confirmation that nothing else in the engine writes light state for an actor the bridge owns |
| [`06_Truth_And_Annotation.md`](06_Truth_And_Annotation.md) | An `AnnotationSet` payload whose `entity_id` and `instance_id` match `C4`'s grammars; `<_supervision>` identical across sensors at one tick; the sidecar carrying `sumo_id` and `entity_id` on `_carla`. The `<_solar>` element already exists (`CotWriter.cs:52-65`) and needs no change; what is needed is the container additionally carrying the scenario's `epoch` so a sidecar states its own civil time without the manifest |
| [`07_Scenario_Authoring.md`](07_Scenario_Authoring.md) | An authoring surface that emits only catalogue classes, never bare vTypes; area references rather than raw edge ids where an area exists; and an `epoch` that is **authored**, not defaulted — the authoring surface is where the 3.5-hour contradiction of Measurement 7 gets fixed at source |
| [`08_Collection_And_EPoL.md`](08_Collection_And_EPoL.md) | **Two** artifact roots, not three (`D4.26`) — `08` owns the collection rationale for how they are laid out, named and sessioned; `C8` owns the ruling that there is no third. An observation writer with no reference to truth artifacts, and the split performed at the writer rather than by a stripping step (`D4.16`, and `08`'s own `D8.17` mechanism 2). A `context` block whose `solar` and `epoch` fields are exactly §10.4a's allow-list, taken from the sidecar and the PNG chunk rather than from the run manifest (`D4.21`). The per-label quality fields of `D4.28` — `occlusion`, `visible_signature`, `label_crowding`, `nearest_label_px`, `supervision_transfer_ambiguous` — computed from truth and the rendered frame alone and written onto the label record. If `08` reserves a partition whose truth is not released, that the corpus manifest declares it (V8.9). **And for the live delivery mode** (`D4.29`): a transport binding satisfying §10.9.1's three properties; one endpoint per root, with the truth endpoint off by default (`08` §11.4, guarantee L7); drop-oldest at the emitter with a per-sensor counter and the covered-but-not-delivered coverage row (`08` §11.3, V8.16); a real-time factor observed rather than owned (`08` §11.1); and one world-observer snapshot per tick behind every stream (`08` §3.4, guarantee L6) |
| [`09_Toolchain_And_Packaging.md`](09_Toolchain_And_Packaging.md) | `vehicles.catalogue.json` shipped in the distribution under `catalogue/`; `sumo` and `duarouter` staged with `tools/traci` and `SUMO_HOME` set |
| [`10_Scale_And_Performance.md`](10_Scale_And_Performance.md) | Values for `prewarm_s`, `near_m`, `sumo_step_timeout_wall_s`, **`solar_audit_tolerance_s`, `solar_audit_tolerance_elev_deg`, `solar_audit_every_n_ticks`, the bound on a per-scenario tolerance override, the solar-bin edges `C8` V8.6 stratifies on, the live emitter's `queue_depth_frames` (§10.9.2, guarantee L4), and the transcript's per-record and per-run byte caps (§10.10 rule 5)** |
| [`11_Time_And_Illumination.md`](11_Time_And_Illumination.md) | The five properties listed in §11.13: a recommended default policy, the headlight thresholds in the `sun_elevation_deg` convention, the `freeze_date_advances` default, whether illumination is a declared stratifier, and a view on a time-zone setter. `C9` carries and checks whatever `11` decides; it does not decide any of them |
| [`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) | An override that produces exactly an `illumination` object of §11.5's shape, so the driver validates the operator's choice with the same rules as the author's; and a surface that can express the four policies without inventing a fifth. `C9` requires only that whatever an operator expresses resolves to `illumination_in_force` in the manifest (§11.8). **And for non-interactive invocation**: the five properties of §12.11 — non-interactive, parameterised from artifacts only, reproducible, addressed by `run_id`, and a record always written that depends on no exit status, because a killed process has none. `12` owns the surface, the configuration resolution, the exit-status set and the closeout rendering; `C10` owns the record artifact, and its gate rows are `12` §7.2's gate record projected rather than a second set of gates. `C10` publishes no aggregate verdict (`D4.36`, §12), so `12`'s `quality_gate` rendering is the only summary in the plan and is `12`'s to justify |

---

## 15. Decisions

| # | Decision |
|---|---|
| **D4.1** | **The vehicle catalogue is generated by a build-time spawn-and-measure sweep against a running server; it cannot be a projection of the blueprint library.** Measured: dimensions do not exist on `rpc::ActorDefinition`, on `FVehicleParameters`, or in any attribute `MakeVehicleDefinition` emits; the bounding box first exists on the spawned actor (§3.2). Upstream's static `vtypes.json` is the same conclusion reached once and frozen; our blueprint set differs, so ours is new work |
| **D4.2** | **The catalogue has one serialisation, `vehicles.catalogue.json`, and it is authoritative; the sweep emits no second projection of it.** Most of the payload — `vClass`, `guiShape`, `sigma`, `speedDev`, class membership, colour palette, lamp capability — has no home in a vehicle-description standard except vendor extensions, so a second serialisation would be a vendor document in a standard's clothing and a second artifact to keep in step (§3.3) |
| **D4.3** | **One `vType` per catalogue blueprint with dimensions copied verbatim -- the width the body's without its mirrors (D4.43); one `vTypeDistribution` per catalogue class.** The author asks for a class, SUMO draws the member, the member *is* the blueprint. No matching, no nearest neighbour, tolerance 0.01 m for rounding only (§3.6) |
| **D4.4** | **Colours are `#RRGGBB` in every SUMO artifact and `"R,G,B"` 0–255 in every CARLA artifact.** SUMO reinterprets an all-≤1 integer triple as fractions (`RGBColor.cpp:308-311`); hex removes the ambiguity (§3.7) |
| **D4.5** | **`vType@color` is a `sumo-gui` property and is never rendered.** Measured: Bahonar's four anomaly types are the only conspicuous colours in the file and cover all nine marked vehicles, so carrying colour through would make it a perfect separator of the positive class. The rendered colour is drawn from the blueprint's own palette by a seeded rule, identically for marked and unmarked vehicles of one class (§3.7.1) |
| **D4.6** | **A participant in an open interval is drawn throughout it, by construction** — every vehicle SUMO has in a window is drawn, from its departure or its window's prewarm until SUMO removes it or the window closes, so nothing can displace a participant. The one way it could go without a body, a vehicle type with no measured body, is refused before a run starts (07 check 14, 12 check 25) (§4.4) |
| **D4.7** | **Behavioural truth exists for every SUMO vehicle; imagery truth only for rendered ones; every SUMO vehicle carries an explicit `render_state` with a reason and its rendered and observed spans.** Absence never carries that fact (§4.5) |
| **D4.8** | **`sumo_vehicle_id` → `actor_id` is one-to-many.** A released and re-admitted vehicle is a new actor with the same SUMO id; `rendered_spans[]` is how the mapping stays recoverable (§6.2) |
| **D4.9** | **`role_name` is a provenance field and carries the authority class** — `autopilot`, `scenario`, `sumo`. `hero` and `ego` are never used, because they change what the simulation does (§6.4) |
| **D4.10** | **An area resolves to a lane-and-position table, not to a list of edge ids**, because `laneId`/`startPos`/`endPos` is what a `<stop>` needs and an edge id is not. Positions are SUMO lane positions, not arc lengths along the shape — measured, the two differ by up to 5.76 m (§7.3) |
| **D4.11** | **The co-simulation driver is the sole owner of the advance of simulated time** (§8.1) |
| **D4.12** | **If either side stalls, the driver stops advancing both and fails the run.** A world that ticks without SUMO produces a plausible lie (§8.5) |
| **D4.13** | **Truth velocity for a SUMO-driven actor is the vehicle's own motion as SUMO moved it -- since 2026-10-02 the velocity of the path the bridge renders it along, lateral movement included -- never recovered from physics.** The body's heading rides beside it (`heading_deg`) and SUMO's reported angle with both (`sumo_angle_deg`) for audit (§9.2). Verified: the observer reads the physics velocity (`WorldObserver.cpp:373`) and `SetActorTargetVelocity` writes `SetPhysicsLinearVelocity` (`CarlaActor.cpp:392-411`), which is inert with simulation off, so the obvious workaround does not work either (§9.2) |
| **D4.14** | **Authority is written at spawn and is immutable; a handover is destroy-and-respawn, never a mutation** (§9.3) |
| **D4.15** | **Only area *definitions* may be placed in the `OBSERVATION` root; area *relations* are derived from truth positions and live in the `TRUTH` root** (§10.4). An observation-side `<_aoi>` would state exact containment that no observer measured |
| **D4.16** | **The `OBSERVATION` root is written by a component that holds no reference to any truth artifact, and the two roots are written as separate files by separate writers from the first byte.** A structural guarantee, not a policy (§10.5). No join between truth and model output is produced here at all, which is `D4.27` |
| **D4.17** | **The catalogue is a runtime dependency of the co-simulation bridge, not only an authoring aid.** SUMO's reference point is the front bumper centre and CARLA's is the actor origin, so the pose conversion needs the measured `length_m` and `bbox_centre_m`; SUMO has neither. A vehicle whose extent is unknown is **not rendered** and is recorded as `simulated_only` with reason `unknown_extent`. The bridge must never substitute SUMO's declared length (§3.2) |
| **D4.18** | **A scenario declares the civil instant `t = 0` corresponds to, with an explicit UTC offset, carried in the scenario package.** Never inferred from identifiers, never defaulted from the host clock, never left to a run setting. Measured: 335 of 335 `guard_dD_hH_tN` trips satisfy `depart == D×86400 + H×3600` exactly, so the mapping is asserted perfectly consistently — and **only inside identifiers**, while the one epoch that does exist is UTC-only, defaults to `datetime.now(UTC)` (`SumoCotBridge.py:197`) and contradicts those identifiers by 3.5 hours (§11.1) |
| **D4.19** | **The sun is written with the declared civil offset as its zone, in the same call as the date and the civil clock (`set_solar_epoch`), and read back before anything renders; no component converts a civil time into another zone's clock.** A sun whose zone is left at `longitude / 15` (`CesiumSunSky.cpp:570-572`, cancelling the longitude terms at `SunPosition.cpp:97`) reads civil hours as local apparent solar time: measured on the sizing site, **14.72 minutes, 3.68° of hour angle** out. The zone the world held when the session found it is recorded as `sun_time_zone_hours` (§11.4, §11.8) |
| **D4.20** | **The general admission rule for an input that is neither truth about the scene nor pixels: it may be placed in the `OBSERVATION` root only if it is fieldable, scene-independent, supervision-blind, *and* reachable from the observation side without opening a truth artifact.** The fourth test is about the path rather than the value, and it is the one that is easy to miss: a legitimate field that lives only in the run manifest still stays out, because reaching it would breach `D4.16` (§10.4a). *Unaffected by the scope decision — it always governed our own data* |
| **D4.21** | **Solar state and the scenario epoch are placed in the `OBSERVATION` root as collection context, in the frozen field set of §10.4a, written from the sidecar's `<_solar>` and the PNG's `carla:solar` chunk — never copied from the run manifest.** `advancing`, `rate`, the policy and the residual stay out: they describe the experiment, not the world. The element goes in; not all of it does (§10.4a) |
| **D4.22** | **The bridge owns a SUMO-driven vehicle's light state, in two disjoint halves** — SUMO owns brake and indicators because they are consequences of the driving it simulated; the illumination policy owns position and low beam because SUMO has no headlight model. Measured: `VEH_SIGNAL_FRONTLIGHT`, `FOGLIGHT`, `HIGHBEAM` and `BACKDRIVE` appear only in the enum declaration (`MSVehicle.h:1110-1138`) and are never set anywhere in SUMO. Everything else is **undefined and must be left alone** (§9.4) |
| **D4.23** | **Lamp capability is measured optically per blueprint per lamp and carried in the catalogue; `has_lights` is recorded verbatim and used for nothing.** Measured `true` on all 17, so it discriminates nothing; and the read-back returns the command rather than the vehicle (`CarlaWheeledVehicle.cpp:486-489`) because illumination is a `BlueprintImplementableEvent` (`CarlaWheeledVehicle.h:310-311`), so a set-and-read probe cannot substitute for an optical one (§3.2a) |
| **D4.24** | **A consumer that finds no epoch does not invent one.** It refuses, or runs with `policy = "ignore"` and records `epoch_declared: false`, `corpus_eligible: false`. Defaulting to noon, to the host date, or to `t = 0` being UTC midnight is prohibited — all three exist in the tree today and all three are silent (§11.7) |
| **D4.25** | **The clock owner owns civil time too, because civil time is a function of the tick and of nothing else.** No component may read the host clock, host time zone or host locale to decide what time the scene is, and no component but the owner may write the sun. A second writer of the sun is a second owner of time (§8.1, §8.3a) |
| **D4.26** | **This system owns exactly two artifact roots, `OBSERVATION` and `TRUTH`. There is no third.** Model output — detections, tracks, assessments, associations, reports — is neither produced, consumed, stored, validated nor versioned here, and no artifact of this pipeline may be written into a location that holds it. The anti-leak separation that matters is the `OBSERVATION`/`TRUTH` boundary and it needs no third root (§10.2) |
| **D4.27** | **Supervision transfer is a format guarantee plus a documented rule, never an operation performed here.** Truth is emitted in an associable form — per tick, positioned, timed, boxed, stably identified, with interval bounds in the manifest — and the rule by which supervision would transfer is written down. No association is performed, no harness ships, and no artifact is derived from a consumer's output (§10.6) |
| **D4.28** | **A quality field survives in the corpus if and only if it is computable from this system's own truth and imagery with no model output as an input, and it attaches to the *label* rather than to an assignment.** Surviving: `occlusion`, `visible_signature`, `label_crowding`, `nearest_label_px`, `supervision_transfer_ambiguous`. Not produced: the gate residual, the match margin, and every track-level rate. Doc 20 §7.6's requirement — that a mis-associated label be findable rather than an unexplained hard example — is met on the label side, without measuring anything (§10.7) |
| **D4.29** | **`C8` is one contract with two delivery modes, and the `OBSERVATION` and `TRUTH` records are identical in both.** Only the carrier, the timing, the loss behaviour and the closure differ, and the modes are **additive rather than alternative** — a live run may write the corpus as well, and `image_sha256` makes the record a consumer received provably the record the corpus holds. The test of the decision is that a consumer writes one reader (§10.1a) |
| **D4.30** | **The link never owns the clock.** Pacing is declared before a run and never negotiated during it; no consumer, transport or transcript peer may stall, pace or otherwise influence the advance of simulated time. A consumer that cannot keep up loses frames, the loss is counted, and coverage records the tick as covered-but-not-delivered. Back-pressure is refused because it would make the consumer a second owner of time (`D4.11`) and would **move the sun** (`D4.25`), changing an exercise's illumination in response to how fast somebody else's software ran (§10.9.5) |
| **D4.31** | **A consumer attaches and detaches at will, and the run is indifferent to both.** A `stream_header` on attach, a `stream_close` at the end, and in between no handshake, no acknowledgement, no replay and no request channel. Zero consumers is the default and a legal state; several consumers are independent, and one slow consumer may affect neither another consumer nor the world (§10.9.4) |
| **D4.32** | **Anything the external chain returns is recorded, if it is recorded at all, as an opaque blob with a timestamp, a source id and a content type** — verbatim, never parsed for meaning we act on, never merged into truth or supervision, never measured, never inside either root, and never an input to any artifact we produce. We define and version the **container**; **its content is not our schema**. This refines `D4.26`'s word *stored* in exactly one respect — verbatim retention outside both roots — and in no other (§10.10) |
| **D4.33** | **Every run writes a machine-readable record of what it produced and what was checked, and the record contains no verdict on either.** Gate rows are published in full — which check ran, what it observed, what threshold it compared against, whether it passed, failed or was skipped — and the caller aggregates them against a purpose we do not know. The record is a projection of the run manifest and the gates that already exist: a field not derivable from an artifact this plan already specifies does not belong in it (`C10`, §12) |
| **D4.34** | **The substitution test.** No clause of this contract may become false, ambiguous or unimplementable if the entire external chain is replaced by a different one. Any clause naming a consumer's format, schema, transport, stage, latency or behaviour is a defect in this contract and is cut. What we owe is what we emit, in what form, with what timing and identity guarantees, and how an arbitrary consumer attaches and detaches (§10.11) |
| **D4.35** | **A regenerated corpus is a new run, never an overwrite.** A run writes into a location keyed on its own `run_id`, and the record carries the digests of every input plus — when the caller names a predecessor — whether those inputs changed. An external process driving regeneration can therefore tell a fresh corpus from an identical one without comparing corpora, and a relaunch cannot destroy the corpus it already had (§12.6, §12.11) |
| **D4.36** | **Every artifact this plan produces is incrementally written, self-describing without a closing record, and valid at every instant.** A kill at an arbitrary point leaves a shorter artifact, never a corrupt one, and a reader distinguishes a complete artifact from an interrupted one by the presence of a terminal record, never by whether the file parses. Abrupt external termination is a normal operating mode, so "closed at the end" may never be what makes an artifact readable. §12.7 applies this artifact by artifact, with three writer rules and two declared exceptions |
| **D4.37** | **A caller observes a run in progress through the surfaces that already exist** — a second client on the CARLA server, the live handover stream if one is open, and the incrementally written artifacts on disk. **This plan adds no status service, no progress RPC, no completion percentage and no callback to the caller.** Where an existing surface cannot answer a question, the gap is recorded as a gap (§12.8.4) rather than designed around, because a status service would be a fourth place a run's state is asserted (§12.8) |
| **D4.38** | **Nothing in this contract requires a run to have a declared length.** A convenience limit may exist on the invocation surface; no field, rule, gate or reader here may assume one was set, and reaching the end of a limit is one ordinary way a run can end among several. The caller stops us, so a contract that needed a duration would be a contract that only worked for callers who did not want to use it that way (§12.9) |
| **D4.39** | **The annotation vocabulary travels inside the scenario package and is bound by digest at the refuse tier, exactly as the annotation set and the epoch are.** A package that carries terms and not their definitions is a package whose labels only the author can read, and a vocabulary bound by nothing can be edited after the annotation set was compiled against it — after which every label still resolves, to a meaning nobody declared. The resolved vocabulary document is carried in the supervision plan, `<scenario_id>.supervision.json`; `vocabulary_digest` in the plan and in the lock binds it; and V3.15 refuses a mismatch. The **content** of the document — what the core holds, how an author term declares itself, how a namespace is versioned — is [`06`](06_Truth_And_Annotation.md) §3.7, §3.8 and §8.7's; this contract owns only that it travels, where, and what binds it (§5.2, §5.3, §5.4) |
| **D4.40** | **Motorcycles, mopeds and bicycles are outside the vehicle mapping contract.** No catalogue class names one, no `vType` declares one, and an author asking for one is refused rather than substituted. A two-wheeler carries a rider and riders are not rendered; and the content build registers no two-wheeled blueprint for the sweep to measure (§3.1, V1.20) |
| **D4.42** | **A lane change takes 3 s, for every vehicle**: the compiler writes `lanechange.duration` 3 and the lock records it. SUMO's default of 0 crosses a lane width inside one step; a passenger car takes about 3 to 5 s, and one value holds for every vehicle until a value per class is decided. A body wider than its lane deadlocks a spread lane change -- the Fuso bus did with its mirrors counted -- so SUMO is given body widths (D4.43) (§5.2a) |
| **D4.43** | **SUMO is given each body's width without its mirrors; the truth box and seating keep the full extent.** Measured from each blueprint's mesh in the editor, carried in the catalogue as `body_width_m` with its method; no model rescaled; a body without one is refused (§3.2b) |
| **D4.44** | **A limit on which vehicles get a body is an optional performance control, off by default and recommended for no scenario.** With none every vehicle SUMO has in a window is drawn. A run may choose a circle, the registered cameras' footprints, or a capacity under any of them (`in_limit`, §4.2); a vehicle the limit leaves out is simulated, has no body and no imagery-side truth, and carries `outside_limit` in `render_states[]`; E5 and E6 release for it under new numbers, and the participant guarantee D4.6 holds only with no limit (§4.4). The limit is named in the run's configuration, said at launch and counted in its record |
| **D4.45** | **A draw distance is an optional performance control, off by default, and changes no admission.** Every vehicle keeps its body, its pose and its truth; a camera does not draw a body farther than the distance from it, and that camera's sidecar marks such a vehicle `beyond_draw_distance` (`wholly` or `partly`), so it is never counted as observed by that camera (§4.2, §4.5; [`06`](06_Truth_And_Annotation.md) §8.2) |

---

## 16. Open questions

Each carries the options and a recommendation; none is decided here.

1. **Where the catalogue's class list comes from.** `C1` requires `classes[]` to be curated — measured
   necessity, since `base_type` is wrong for 7 of 17 blueprints and `special_type` is empty for all 17.
   But the sweep generates the catalogue, and a sweep cannot curate. Options: (a) a hand-maintained
   `classes` fragment merged by the sweep, which reintroduces a hand-maintained file that doc 20 §5.6
   argues against; (b) fixing `VehicleParameters.json` in the content and deriving classes from it,
   which makes the content correct but couples the catalogue to a cook; (c) both — derive, then allow a
   curated override that the sweep validates against the derived set. **Recommend (c)**, because it
   makes the wrong metadata visible rather than papering over it, and because the override file is tiny
   and reviewable. The user's call, because (b) alone is the honest fix and costs a content change.
2. **Whether the catalogue lives in the world package or the distribution.** It is a property of the
   content build, not of a world, so the distribution is right — but a world package handed to someone
   without the distribution then cannot be run. Options: distribution only; distribution plus an
   embedded copy in every scenario package; or all three. The scenario package as built references the
   catalogue by digest and does not embed it (`C3` §5.2), so today it is distribution only, with the
   lock naming which catalogue a scenario needs. `D4.17` bears on the choice: because the bridge needs
   the catalogue *at runtime* for the pose conversion, a scenario package handed over without its
   catalogue is not runnable at all, not merely unvalidatable. **Recommend embedding a copy**, bound by
   the digests the lock already records.
3. *Withdrawn 2026-09-30.* Whether the render cap was a count or a budget: there is no render cap, and
   every vehicle SUMO has in a window is drawn (`C2` §4.2).
4. **What `render_state` should say about a vehicle SUMO teleported.** The shipped configs set
   `<time-to-teleport value="-1"/>`, forbidding it, with the comment that a teleport is a vehicle
   jumping position that nothing downstream can reproduce faithfully. If a scenario ever raises it,
   a rendered vehicle jumps. Options: a fourth `render_state` value; a per-rendering flag; forbid
   teleporting in `C3` validation. **Recommend forbidding it** — `time-to-teleport` must be `-1` for a
   corpus-eligible run — with a flag for diagnostic runs that marks the run not corpus-eligible, by
   analogy with `render_uses_vtype_colour`.
5. **Whether the annotation state must be readable across processes.** Doc 20 decision 11 leaves this
   open and says it must be decided before multi-camera capture, not after. `C2`'s observability
   accounting is per sensor and `C4`'s `sensor_id` rule assumes several sensors, so this plan pushes
   towards the decision. Options: publish the annotation state on a world actor the way staging bounds
   are (`C5` §7.4 has the pattern ready-made); or require every recorder to live in the driver's
   process. **Recommend publishing it**, because the second option's failure mode is silent — a
   recorder in another process reads an empty registry and writes `unlabelled` on every vehicle — and
   because `C5` is building the actor-plus-RPC-pair pattern anyway.
6. **Whether a SUMO-driven actor should be given a non-zero physics velocity anyway.** `D4.13` fixes
   the truth record, but other consumers read the engine's velocity directly — the traffic manager's
   collision stage, and the arrival and occlusion gating of
   [doc 17](../../Findings/17_Photoreal_Occlusion_Metric.md). The traffic manager is locked out, so the
   question reduces to whether anything else in the engine needs it. Options: leave it zero and fix
   each consumer; or re-enable simulation with zero gravity and drive by velocity rather than by
   transform, which restores the engine's velocity at the cost of the pose no longer being exact.
   **Recommend leaving it zero** and treating any consumer that needs velocity as a caller of the truth
   producer — but this needs [`05`](05_CarlaNet_Capability_Audit.md)'s inventory of who reads
   `GetVelocity()` before it can be closed.
7. **Whether `capture:instance_id` on a spawn attribute is worth having at all.** It is only valid for
   a participant in exactly one instance, and doc 20 §7.3's dynamic registry carries the general case.
   Options: drop it and let the registry own all instance membership; keep it for the common case so a
   replayed capture is supervised without the registry (doc 20 §7.7's argument). **Recommend keeping
   it**, since replay is the case a registry cannot serve, but the "exactly one" restriction is ugly
   and [`06`](06_Truth_And_Annotation.md) may have a better encoding.
8. **How a movement through a junction is named** — carried forward from doc 20 §11 question 8, and
   sharpened by the SUMO side: SUMO's internal junction lanes are named `:<nodeId>_<n>`, matching the
   generated `.xodr`'s junction-internal road names, so the two *agree* on an unstable machine name.
   Whether a stable derived junction name is worth minting, and whether it should be minted once and
   carried in both artifacts, is open. It affects `C5`, because an area sited on a junction resolves to
   lanes whose ids change on rebuild.
9. **The sun's time zone is written, not converted around.** `set_solar_epoch` sets the declared
   offset as the sun's zone with the date and the clock (D4.19, [`11`](11_Time_And_Illumination.md)
   D11.5); nothing about it is open.
10. **Whether a freeze should freeze the date as well as the clock.** `C9` defaults
    `freeze_date_advances` to `false` on the argument that a freeze meant to hold illumination constant
    should not let the seasonal angle drift across a week. The counter-argument is real: under a
    seven-day scenario with `calendar_advances: true`, a frozen date makes the manifest's civil dates
    and the sun's date disagree for six of seven days, which is another two numbers a consumer must
    reconcile. Options: default `false` (current); default `true`; or forbid the combination of
    `calendar_advances: true` with a freeze policy outright. **Recommend the current default** and
    recording both dates per window (§11.8.1 already does), but this is a judgement about seasonal
    geometry and it is [`11`](11_Time_And_Illumination.md)'s.
11. **Whether an unlit lamp should refuse rather than warn.** `C1` V1.18 warns and records, on the
    argument that a blueprint without a working headlight is a content fact rather than an authoring
    error, and that forbidding the capture would lose imagery that is still useful for everything except
    claims about headlights. The counter-argument is that a night corpus of dark vehicles is worse than
    no night corpus, because it will be used. Options: warn and record (current); refuse when a night
    window's classes are not fully lit; or make it a per-scenario declaration,
    `night_lighting_required`. **Recommend the current warn-and-record**, with the manifest's
    `lamp_gaps[]` as the filter a corpus builder applies — but if [`11`](11_Time_And_Illumination.md)
    decides illumination is a declared stratifier, the third option becomes the consistent one.
12. **Whether a supervision–illumination correlation should block a corpus or only be reported.**
    `C8` V8.6 requires prevalence per solar bin and refuses a corpus summary without it, but does not
    refuse on the correlation itself. This is a question about the corpus describing itself honestly,
    not about anything downstream: a corpus whose supervision is separable on illumination is defective
    the moment it is written. A threshold would be arbitrary and gameable; no threshold means someone
    has to look. Options: report only (current); report plus a declared maximum correlation that fails
    the summary; report plus an automatic re-weighting recommendation. **Recommend report only for
    now**, because the bin edges themselves are not yet valued
    ([`10`](10_Scale_And_Performance.md)) and a threshold over undecided bins would be noise. Revisit
    once one corpus exists to measure the natural correlation on.
13. **Where the label-quality radius of `D4.28` is fixed, and whether `label_crowding` is per sensor or
    per frame.** `C8` §10.7 names `label_crowding` and `nearest_label_px` and declares the radius a
    parameter without valuing it, as this section does with every number. Two things are genuinely
    open. First, the radius is in pixels and apparent size varies across a frame by a large factor at
    these look angles ([`08`](08_Collection_And_EPoL.md) §8.2 records the geometry), so a constant
    radius is wrong at the corners — options are a constant, a radius normalised by the subject's own
    apparent size, or both recorded. **Recommend both recorded**, since the normalised one is the
    meaningful field and the raw one costs nothing and makes the normalisation auditable. Second,
    whether a label crowded only in *another* sensor's frame is crowded: `C4`'s `sensor_id` rule assumes
    several sensors and the fields are per `(sensor_id, tick, vehicle)`, so **recommend per sensor**,
    with the union left to a corpus summary. Belongs jointly to
    [`08`](08_Collection_And_EPoL.md) and [`10`](10_Scale_And_Performance.md).
14. **Whether a reserved partition exists at all, and who declares it.** [`08`](08_Collection_And_EPoL.md)
    `D8.17` names a held-back split whose truth is never released. `C8` V8.9 requires only that any such
    omission is **declared** in the corpus manifest, because an undeclared omission is indistinguishable
    from data loss. Whether to reserve one, and at what granularity, is a corpus-construction judgement
    and is [`08`](08_Collection_And_EPoL.md)'s — recorded here so the two documents do not each assume
    the other decided it.
15. **CLOSED 2026-09-18 — `scenario_id` and `seed` do not belong in the `OBSERVATION` record.**
    Decided by the integration lead in favour of [`08`](08_Collection_And_EPoL.md): both are excluded
    from the observation side and from the live stream header, and both remain required in the `TRUTH`
    root, the run manifest and the `C10` run record. The field-level statement is in §10.3.
    **The argument, kept because it is the worked application of `D4.20` a later reader will want.** The
    recorder writes `scenario_id` into the sidecar container today (`CotWriter.cs:45`, with `seed`
    beside it at `:46-47`, read from the source 2026-09-18), so carrying it into the observation root
    would have been free. [`08`](08_Collection_And_EPoL.md) §9.7 consequence 2 is why it is not:
    `scenario_id` and `seed` are *"handles that index a set of scenes"*, so a corpus carrying them lets
    a model key on the scenario rather than on the scene — `D4.20`'s supervision-blindness test applied
    exactly. The live mode sharpens it, because the field would otherwise ride a stream header to an
    unknown consumer, and the join a consumer actually needs is the tick. `D4.29` makes it one rule in
    one place: the modes carry the same record.
16. **Whether a live run should also write the deferred roots by default.** `D4.29` makes the two modes
    additive so that the most expensive kind of run — one with an audience — need not be the only kind
    that produces nothing reusable. Whether *default-on* is right is a different question: writing the
    corpus costs disk and a writer on the same machine that is trying to hold a real-time factor.
    Options: both on by default; live-only by default with an opt-in; a per-session choice with the
    record recording which. **Recommend the third**, with `delivery_modes[]` in the `C10` run record
    already carrying the answer, and the sizing of the extra writer belonging to
    [`10`](10_Scale_And_Performance.md).
17. **Whether an interrupted corpus can be published at all, and by whom.** §12.7 makes the corpus
    manifest the one artifact that cannot be incremental — it describes a *published* corpus — so a run
    the caller kills leaves valid imagery, valid truth and a valid run manifest, and no corpus manifest.
    The artifacts are readable; what is missing is the statement of contents and declared omissions that
    `C8` V8.9 requires before a handover. Options: (a) a corpus manifest is written only by an orderly
    closeout, and an interrupted run's artifacts stay readable but unpublished — what §12.7 currently
    says; (b) a separate publish step can be run over an interrupted run's directory afterwards,
    building a corpus manifest from the run manifest and declaring the interruption as an omission; (c)
    the corpus manifest is incremental too, which means publishing a statement of contents that is
    wrong until the moment it is not. **Recommend (b)**: it keeps the manifest a statement about a
    finished thing, and it makes the corpus from a killed run usable rather than merely present — which
    matters most precisely because killing us is the expected path. It needs
    [`08`](08_Collection_And_EPoL.md), which owns corpus construction, to agree that a corpus may be
    published from a run that was stopped.
18. **Whether a transcript is recorded by default, and who authorises releasing it.** `D4.32` fixes the
    container and the prohibitions; it does not decide whether a live exercise records one unasked. The
    case for default-on is that a transcript is a record of what happened and is unrecoverable
    afterwards; the case for default-off is that it is the only inbound path in the system and an
    unasked-for recording of somebody else's output may not be ours to keep. Options: off unless asked;
    on with the release withheld by default (`released: false` in the `C10` run record); on and released
    with the corpus. **Recommend the second** — record it, do not ship it — with releasing it an
    explicit, recorded choice as §10.10 rule 6 requires. The authorisation question is the user's, not
    this document's.
