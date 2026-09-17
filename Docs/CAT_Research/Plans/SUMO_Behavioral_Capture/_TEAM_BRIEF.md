# Team brief — SUMO-driven behavioural capture

The constraints every section of this plan was written under, kept as provenance rather than as a
deliverable. [`00_Overview.md`](00_Overview.md) is the entry point; this file records the standing
rules, the user's fixed decisions, the ground truth the team started from, and the two corrections
issued mid-way (the retired `SCTMV.py` path, and the demotion of vehicle fade). Read it if you want
to know *why* a section decided something the way it did, or before adding a section of your own.

---

## 1. What is being built, in one paragraph

A **SUMO traffic simulation drives the vehicles rendered in a CARLA world** that was generated from
an OpenStreetMap extract. Cameras in that world capture electro-optical imagery. The imagery is fed
to detection-and-tracking algorithms whose tracks go to an **estimated-pattern-of-life (EPoL) model
service**. Meanwhile SUMO and CARLA between them hold the **truth**: where every vehicle actually
was (optical detect-and-track truth) and **what the author asserted each vehicle was doing**
(behavioural truth). The product is a synthetic imagery corpus whose truth sidecar carries
behavioural annotation, usable to train and to validate EPoL models, plus a live mode that exercises
an EPoL model end to end.

## 2. The two source documents and the seam between them

| Document | What it establishes | What it assumes that is now false |
|---|---|---|
| [`Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md`](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md) | The supervision model: three-valued supervision, pattern instances with participants and intervals, areas of interest, the run manifest, the `<_supervision>` / `<_aoi>` sidecar elements, the vehicle catalogue, four identifiers | That the authoring surface is an **OpenSCENARIO storyboard** executed by `CarlaNet.Scenario`, and that CARLA's own executor owns behaviour |
| [`Findings/23_SUMO_Traffic_Integration.md`](../../Findings/23_SUMO_Traffic_Integration.md) | That the SUMO toolchain is built and verified, that SUMO ships first-party C# bindings, that our pipeline already produces and then discards the SUMO network, and the hurdle list | That the production shape is **"SUMO decides, CARLA physics executes"** and that teleporting is only a comparison oracle |

**The seam is the work.** Doc 20's supervision model has to be re-seated on a SUMO authoring
surface, and doc 23's recommended integration shape has to be revisited against a use case it was
not written for. Neither document is wrong; each was written without the other's requirement in
hand. Say so plainly where you find a conflict, and resolve it — do not paper over it.

## 3. Decisions the user has already taken. Do not re-litigate these.

1. **SUMO-controlled traffic is required.** Scenarios of the intended size are not expressible in
   the existing traffic manager or in OpenSCENARIO storyboards.
2. **Teleport-style control is accepted for this mode.** The purpose is imagery plus behavioural
   truth, not vehicle dynamics fidelity. The known cost — the truth record reporting zero speed for
   a non-simulating body — is a problem to be *solved*, not a reason to reject the mode.
3. **An entirely new suite of tools, scripts and clients is welcome.** `run_SCTMV.py` and the
   `carlacontrol` modules are reference material, not a template to extend by force.
4. **Ambient traffic (the .NET traffic manager) must be unavailable while SUMO is driving.** It adds
   nothing to this use case and adds uncertainty. Design the lockout; do not make it a runtime
   warning the operator can ignore.
5. **Pedestrians are out of scope.** The world-generation pipeline does not yet produce footway
   meshes good enough to be believable, so there is nothing to render them onto.
6. **The `.NET` traffic-manager path and the OpenSCENARIO executor are not to be removed.** They
   remain the path for stock content and for storyboard work. SUMO drive is a *mode*.

## 4. Standing project rules that bind this plan

- **Never regress an existing capability.** Improving or replacing a capability is welcome; silently
  losing one is not. Where the SUMO-driven mode cannot preserve something the current pipeline does
  (real velocity, suspension, terrain seating, vehicle fade, staging ring), name the loss explicitly,
  and either compensate for it or record it as a deliberate, bounded trade confined to this mode.
- **Rebuilds are neutral.** Needing an engine rebuild, a LibCarla rebuild or a CarlaNet rebuild is
  *not* a cost worth avoiding and must not appear in a pros-and-cons list. Choose the right design
  and rebuild whatever it needs. Bolting on a worse design to dodge a rebuild is the actual failure.
- **Measure, do not theorise.** Distinguish, in your text, what you **read from a source** (cite
  `path:line`), what you **measured** (say how), and what you **infer**. An unlabelled inference
  presented as fact is the single most expensive mistake available here.
- **No conversational jargon in anything that ships.** Every stage, contract, field and identifier
  gets a descriptive name. `Stage 2`, `Option A`, `the new approach` mean nothing to a later reader.
  Numbered stages are fine *only* when each also carries a descriptive name that stands alone.
- **Windows and Linux script parity.** Any change to `Scripts/Windows/*.ps1` requires its
  `Scripts/Linux/*.sh` counterpart in the same change, help text and documentation included.
- **Python conventions** are in [`carla/AGENTS.md`](../../../../AGENTS.md): one public class per file,
  file named for the class in PascalCase, modern union type hints (`X | None`), absolute imports
  outside the package, all imports at the top, `logging` not `print`, thin `main`.
- **C# lives in `CarlaNet/src/CarlaNet.*`**; a new assembly may only reference assemblies that do
  not create a cycle. `CarlaNet.Types` references nothing and is the common ancestor of
  `CarlaNet.Scenario` and `CarlaNet.Recording`.

## 5. Ground truth about the existing system

Verified by reading the tree on 2026-09-17 unless marked otherwise.

### Repository layout
- Repo root for the simulator: `carla/` (git branch `ue5-dev`).
- .NET replacement for LibCarla's client: `carla/CarlaNet/src/CarlaNet.{Types,Transport,Map,Sensors,Recording,Scenario,TrafficManager,Nav,Python}`.
- The single canonical Python shim: `carla/CarlaNet/python/carlanet/__init__.py` (3465 lines). There
  is exactly one copy; do not create a second.
- Control-side Python package: `carla/CarlaControl/src/carlacontrol/` with CLIs in
  `carla/CarlaControl/scripts/`. (`CarlaControl/build/lib/carlacontrol/` is build output — ignore it.)
- The authoring skill that describes today's SUMO scenario workflow:
  `.agents/skills/sumo-traffic-scenarios/SKILL.md` (workspace root, one level above `carla/`).

### What already exists and works
- **World generation** — `run_SCTMV.py --build --osm … --height-align drape --emit-world-package DIR`
  clips the OSM, runs `netconvert`, injects elevation, and emits a world package
  (`world.json` with origin latitude/longitude and the netconvert argument set, `map.xodr`,
  `bareearth.bin` per-cell bare-earth height grid).
- **SUMO scenario authoring, with no CARLA in the loop** — `carlacontrol.SumoScenarioBuilder`
  (netconvert settings, network build, road-network reader, ambient flows, private-road fencing,
  opposite-lane overtaking, config writer), `SumoPatternOfLifeBuilder` (multi-day timelines), and
  the CLIs `make_sumo_scenario.py`, `make_arapahoe_scenario.py`, `make_bahonar_scenario.py`.
- **SUMO-to-CoT telemetry, with no CARLA in the loop** — `carlacontrol.SumoCotBridge` drives a
  scenario over TraCI (Python) and emits Cursor-on-Target to UDP, XML and CSV, reading `bareearth.bin`
  for ellipsoidal height and a `.labels.json` sidecar for ground truth.
- **Coordinate identity** — because the SUMO network is rebuilt from the same clipped OSM at the same
  pinned origin with `--offset.disable-normalization`, **SUMO (x, y) equals CARLA (x, −y)** with no
  offset arithmetic. Doc 23 §2 measured `netOffset` as `0.00,0.00` on Arapahoe.
- **The SUMO toolchain is built** — doc 23 §1.2 records `sumo`, `duarouter` and `libtracics` (the
  first-party SWIG C# binding, namespace `Eclipse.Sumo.Libtraci`, 94 generated C# files) compiling
  clean from the unmodified CMake configuration, exit 0. `netconvert` is the only binary *staged*
  into `Build/sumo-install/bin`; `SUMO_HOME` is set nowhere.

### Shim API surface that matters here
Confirmed present in `carla/CarlaNet/python/carlanet/__init__.py`:
`Actor.set_transform` (:754), `Actor.set_simulate_physics` (:790), `Actor.set_target_velocity` (:793),
`Actor.set_enable_gravity` (:817), `World.get_settings` (:1459), `World.apply_settings` (:1463),
`World.tick` (:2086), `Client.apply_batch` (:2475), `Client.apply_batch_sync` (:2482),
`WorldSettings.synchronous_mode` / `fixed_delta_seconds` (:2726-2764).
Whether each of these is actually *implemented end to end* down through `CarlaNet.Transport` to the
server is a question for the capability audit, not an assumption.

### The scale the design has to survive
`BahonarPatternOfLife.zip` at the workspace root is the largest authored scenario and is the sizing
case. Measured from the archive:

| | |
|---|---|
| Simulated span | **604 800 s — seven days** (`<end value="604800"/>`) |
| SUMO step length | **1.0 s** |
| Vehicle types | 14, spanning `passenger`, `taxi`, `truck`, `bus`, `authority`, `army` |
| Flows | **245**; individually declared vehicles: 0 (all traffic is flows) |
| Marked (anomalous) vehicle ids | 9 |
| Network | 2.5 MB `.net.xml`; source clipped OSM 2.9 MB; bare-earth grid **61 MB** |
| Ground truth today | `.labels.json`: `marked_ids`, `affiliation_by_type`, `anomaly_notes` — one of which is an *absence* (a guard who never arrives) with no vehicle to attach to |

Two consequences are load-bearing for the whole plan and every section should be written knowing
them. First, **a seven-day simulation at one-second steps cannot be rendered frame-for-frame**; the
set of SUMO vehicles is far larger than the set CARLA should ever instantiate, and the design needs
an explicit, stated rule for which vehicles become CARLA actors and over what span of simulated time.
Second, **a one-second SUMO step is far coarser than any usable capture rate**, so the relationship
between the SUMO step, the CARLA fixed delta and the camera rate is a contract, not a configuration
detail.

### A correction that invalidates citations in both source documents

**`CarlaNet/python/SCTMV.py` no longer exists.** It was deleted from the tree; the only remaining copy
is a stale one inside a built distribution (`carla/Build/Dist/.../scripts/SCTMV.py`), which is build
output and must not be cited. The live entry point is
**`carla/CarlaControl/scripts/run_SCTMV.py`** over the `carlacontrol` package. Every `SCTMV.py:NNN`
citation in `Findings/20`, in `Findings/23` and in earlier revisions of this brief is therefore stale
and must be re-resolved against `carla/CarlaControl/` before it is repeated. Verified 2026-09-17.

The *substance* of those citations survives — only the location moved. For example, doc 20 §4.2's
finding that `scenario_id` is accepted by the recorder and never supplied is still true: the live call
passes `run_id` and `seed` and no `scenario_id`
(`carla/CarlaControl/src/carlacontrol/NativeRecorder.py:96-111`).

### Vehicle fade is demoted and is not a concern for this plan

**Directive from the user, 2026-09-17.** Per-actor opacity fade on spawn and across the staging
margin is **no longer the default and is not to be designed around**. Vehicles entering the scene at
full opacity is acceptable; refinement can come later if it is ever wanted.

The reason is cost, and it is recorded in the code: the opacity is computed client-side and pushed to
the server as **one blocking RPC per vehicle per reconcile, which is the heaviest load this client
puts on the server's per-frame RPC budget**
(`carla/CarlaControl/src/carlacontrol/CarlaControlArgumentParser.py:318-328`, where `--fade` now
carries `default=False`; `--no-fade` is retained at `:329-334` so existing command lines keep working).

Three consequences, all verified:

- **No fade mechanism belongs in the SUMO-driven mode.** A vehicle admitted to the render set appears
  at full opacity and a released one disappears. Do not design a dissolve, do not give a registry a
  fade role, and do not publish fade state anywhere.
- **The arrival gate degrades gracefully to inert, so nothing is lost by switching fade off.**
  `CarlaClient.IsActorEstablished` returns true for any actor nobody has faded
  (`CarlaNet/src/CarlaNet.Transport/CarlaClient.cs:1571`), and the truth producer's gate is
  documented as inert in exactly that case
  (`CarlaNet/src/CarlaNet.Recording/VehicleTelemetryService.cs:66-73`). `GetActorOpacity` returns 1.0
  for an unfaded actor (`CarlaClient.cs:1562`), so `VehicleTelemetry.Opacity`
  (`VehicleTelemetryService.cs:112`) is a constant 1.0 under this mode.
- **The per-frame RPC budget gains rather than loses.** Removing the heaviest existing client load is
  headroom the pose-application write path can spend instead.

What still has to be recorded is the **render-set admission and release instant** per vehicle, because
a vehicle appearing or vanishing abruptly is a fact about the capture that the observability
accounting needs. That replaces the fade-derived notion of a vehicle having "arrived"; it is a
recorded instant, not a visual transition.

### Known problems carried in from the source documents
- **Teleported bodies report zero velocity in truth.** `WorldObserver.cpp:373` serialises
  `GetActor()->GetVelocity()`, which a `set_transform` on a non-simulating body does not update. This
  reaches the Cursor-on-Target truth record, the traffic manager's collision stage, and the occlusion
  and arrival gating of [doc 17](../../Findings/17_Photoreal_Occlusion_Metric.md). (Read from doc 23
  §4; verify against the source before relying on it.)
- **The SUMO network is flat** — zero distinct `z` values in any lane shape (doc 23 §2, measured).
- **Turn restrictions are discarded before netconvert sees them** — `osm_clip.py` drops all OSM
  relations ([issue #12](https://github.com/sbrett9/carla/issues/12)).
- **Three pose conventions must each be applied**: CARLA's Y is negated relative to SUMO's; CARLA yaw
  is `sumoAngle − 90`; **SUMO's reference point is the front bumper centre, CARLA's is the body
  centre**, so every pose needs a half-length shift along the heading.
- **`scenario_id` is accepted by the recorder and never supplied** by the viewer
  (doc 20 §4.2; `SCTMV.py:1472-1479`).
- **Two subsystems already destroy vehicles with different signals**
  ([issue #18](https://github.com/sbrett9/carla/issues/18)); SUMO's arrival and removal make a third.
- **The tick thread is already contended** by telemetry emission
  ([issue #14](https://github.com/sbrett9/carla/issues/14)).

## 6. The contracts the user has asked to see written down

These are named because the user named them. They are not the complete list; find the rest.

1. **The vehicle catalogue** — how a user hands the assistant authoring a SUMO scenario a catalogue of
   vehicles to choose from, with real dimensions (length, width, height) and colour, and how a SUMO
   `vType` in the resulting network maps to a CARLA blueprint at playback. Doc 20 §5.6 and decision 12
   already argue for an OpenSCENARIO vehicle catalogue generated from a running server; reconcile that
   with SUMO's `vType`, whose `length` and `width` change car-following gaps and therefore the
   behaviour itself.
2. **The render-set contract** — which SUMO vehicles CARLA instantiates, when they appear, when they
   are released, and what the truth record says about a vehicle that SUMO is simulating but CARLA is
   not rendering.
3. **Tick and clock ownership** — exactly one component owns the advance of simulated time. Say which,
   say what every other component does when it is not the owner, and say what happens when one side
   stalls.
4. **Physics and control authority per actor** — what is disabled for a SUMO-driven vehicle, what
   still has to be true of it (seating on the draped terrain, velocity in truth, bounding box), and
   how authority is handed over if it is ever handed over.
5. **The behavioural annotation contract** — doc 20's pattern instances, three-valued supervision,
   intervals and manifest, expressed against a SUMO authoring surface, and the migration path from
   today's `.labels.json`.
6. **Areas of interest** — doc 20 §8's GeoJSON contract, shared by SUMO scenario authoring and by the
   CARLA world.
7. **The EPoL-facing interfaces** — what the detect-and-track stage consumes and emits, what the EPoL
   model service is given, and how truth is joined to it for scoring without leaking truth into it.

## 7. How to work

- **Read the tree.** Every claim about existing behaviour must be read from a source and cited as
  `path:line`, or carried forward from a measurement already recorded in a Findings document and
  marked as carried forward.
- **Do not modify any code, and do not run a build, a cook, or the engine.** This is a planning
  exercise. The only files you write are documents in this folder.
- Running read-only commands to *measure* something (inspecting an XML file, counting rows, reading a
  generated network) is welcome and is worth more than an argument.
- **Diagrams in Mermaid, inside the Markdown.** Use the right kind for the job: `flowchart` /
  `graph` for structure, `sequenceDiagram` for a protocol exchange over time, `stateDiagram-v2` for a
  lifecycle, and a `flowchart` with swimlane subgraphs for a UML-style activity diagram with
  partitions. Use-case diagrams have no native Mermaid type — draw them as a `flowchart LR` with the
  actors on the left, the system boundary as a subgraph, and association edges.
- **Write for an engineer who has not read the conversation that produced this.** Name the audience
  explicitly if it is narrower than that.
- **No schedules.** No weeks, no days, no story points. Sequence and dependency, yes; calendar, no.
- Where you find something genuinely undecidable without the user, write it into your section's
  **Open questions** list with the options and a recommendation, rather than choosing silently.

## 8. Document set

Each author owns exactly one file and writes only that file.

| File | Owner role |
|---|---|
| `00_Overview.md` | Integration lead (written last) |
| `01_Architecture.md` | Systems architect |
| `02_Use_Cases.md` | Systems architect |
| `03_CoSimulation_Runtime.md` | Co-simulation runtime engineer |
| `04_Contracts.md` | Interface and contracts engineer |
| `05_CarlaNet_Capability_Audit.md` | CarlaNet and LibCarla port auditor |
| `06_Truth_And_Annotation.md` | Truth and annotation data engineer |
| `07_Scenario_Authoring.md` | Scenario authoring engineer |
| `08_Collection_And_EPoL.md` | Collection and EPoL integration engineer |
| `09_Toolchain_And_Packaging.md` | Build, toolchain and packaging engineer |
| `10_Scale_And_Performance.md` | Scale and performance engineer |
| `11_Work_Breakdown.md` | Integration lead (written last) |

Cross-reference other sections by relative link. Do not restate another section's content; link to
it. If you need a decision that belongs to another section, state the dependency and the property you
need it to have.

## 9. House style for these documents

Follow the Findings documents' style, because this plan will be read beside them: a short header
block stating status, date and scope; claims cited to source; tables where a table is clearer than
prose; an explicit list of what the section does *not* cover; and decisions recorded in a numbered
table at the end so they can be referenced from elsewhere. Number your decisions with your section
number as a prefix (for example `D4.3` in `04_Contracts.md`) so they are unique across the folder.
