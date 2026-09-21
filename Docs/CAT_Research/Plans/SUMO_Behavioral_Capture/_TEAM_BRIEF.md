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

## 3a. Added requirement — simulated time of day, and an operator control surface

**Added by the user 2026-09-18, after reviewing the first draft. This is why the plan is being
redrafted rather than amended.** The first draft specified windowed capture in simulated time and
never connected it to the sun. That was an oversight, and it is load-bearing.

### What is required

1. **The simulated time of day must be driven in tandem with the network playback.** A capture window
   that begins at 23:00 on day 4 of a scenario must render under a 23:00 sun, not under whatever
   light the world was spawned in. Today the default spawn is local solar noon, so a night window
   would silently render in daylight.
2. **Time-of-day advancement must be toggleable per pipeline run.** A capture may want the sun frozen
   at the window's start instant (so illumination is a controlled constant across a sweep), or
   advancing with simulated time (so a long window shows the light changing). Both are legitimate and
   the choice belongs to the run, not to the code.
3. **The tool suite needs an easy way to control all of this.** The mechanisms already exist in
   CarlaNet; what does not exist is a coherent operator surface over them. This is now a first-class
   deliverable, not a by-product.

### Why it is load-bearing, not cosmetic

- `10_Scale_And_Performance.md` already recommends capture windows at **07:00** (shift change) and
  **23:00** (night shift) on the sizing scenario. Without the coupling, the night window is daylight.
- It would fail **silently and in the worst possible way**: the truth sidecar records solar state, so
  the record would faithfully report noon while the scenario asserts 23:00. A corpus would be
  internally contradictory and nothing would flag it.
- It makes one of doc 20's ten pattern classes unrenderable. Class 4 is *"a heavy goods vehicle in a
  residential area at 03:00"* — a pattern defined by time of day.
- Illumination is the single largest covariate an electro-optical detector faces. A corpus captured
  entirely at noon cannot validate a model that must work at dusk.

### Ground truth — the mechanisms exist and are complete end to end

Verified 2026-09-18. **Do not plan to build these; plan to use them.**

| Layer | Surface |
|---|---|
| Python shim | `set_solar_time(hours)`, `set_solar_date(y, m, d)`, `get_solar_state()`, `set_time_advance(enabled, rate)` — `carlanet/__init__.py:1500`, `:1506`, `:1512`, `:1535` |
| C# client | `SetSolarTimeAsync`, `SetSolarDateAsync`, `GetSolarStateAsync`, `SetTimeAdvanceAsync` — `CarlaClient.cs:1043`, `:1049`, `:1054`, `:1059` |
| Server RPC | `set_solar_time`, `set_solar_date`, `get_solar_state`, `set_time_advance` — `CarlaServer.cpp:614`, `:625`, `:640`, `:661` |
| Engine | CesiumSunSky, which `CarlaServer.cpp:611-612` names **the single sun and lighting authority for the georeferenced world**, with CARLA's own weather inert there |

Three properties of that surface shape the design and should be exploited rather than rediscovered:

- **`set_time_advance` already does the right thing under synchronous ticking.** Its own
  documentation states it "advances with the world tick, so it tracks wall-clock in asynchronous mode
  and **simulation time under synchronous ticking**". Windowed capture runs synchronously, so
  advancement is already tied to simulated time. The `rate` argument is sun-clock seconds per second;
  pin down precisely which second it means under synchronous ticking and state it.
- **`get_solar_state` is already free and tick-stamped.** It reads the world-observer cache paired to
  the latest tick with **no RPC**, falling back to an RPC only before the cache is populated. It
  returns `{solar_time, year, month, day, time_zone, lat, lon, sun_elevation_deg, sun_azimuth_deg,
  advancing, rate}`. This is exactly the publication mechanism `08` D8.3 chose for world-scoped state,
  already working for this payload.
- **Vehicle light state is reachable and batchable.** `Actor.set_light_state` / `get_light_state`
  (`carlanet/__init__.py:781`, `:786`) over `VehicleLightStateFlags`, and
  **`SetVehicleLightStateCommand` is one of the 22 batch commands** (imported at `:487`). SUMO
  exposes per-vehicle signals — brake lights, indicators — so a night capture can carry correct
  brake and turn signals at no extra round trip. Whether it should is a design question; that it
  *can* is established.

### The gap this exposes in the scenario format

**A scenario does not declare the civil time its simulated seconds mean.** Measured on the sizing
scenario: guard shifts depart at 25,200 s, 54,000 s and 82,800 s — 07:00, 15:00 and 23:00 — so
`t = 0` is midnight of day 0. That mapping exists **only inside trip identifiers**
(`guard_d0_h7_t3`) and in the author's head. Nothing machine-readable states it, so nothing can set
a sun from it. An epoch declaration — civil date, time zone, and the instant `t = 0` corresponds to —
is now a required part of the scenario contract. Note the sizing scenario's site is in Iran, whose
civil offset is **+03:30**, so a half-hour time zone is a real case and not a curiosity.

### Standing constraint, inherited from the supervision rules

**Illumination is derived context, never a label.** It is computed identically for every capture and
is a legitimate covariate for stratifying a corpus — and a legitimate input to a fielded system,
which knows the time and its own location. It must never become a supervision signal, and a
scenario must never encode its annotation in the lighting.

## 3b. Scope boundary — this pipeline labels; it never scores

**Added by the user 2026-09-18.** The detect-and-track model and the estimated-pattern-of-life model
are both **external to this effort**. This body of work exists to create synthetic imagery and the
truth and label data that can be used to train and validate those models downstream. **No part of
this pipeline or tool suite scores anything.**

This is narrower than [`Findings/20`](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)'s
existing exclusion, which rejected scoring only in the ScenarioRunner sense of driving-quality
criteria. It now also excludes measuring the performance of the external models.

**In scope — produce, and label as richly as we can:**

- imagery, and every per-frame truth attribute: pose, kinematics, dimensions, class, occlusion,
  visibility, apparent size, solar state, lamp state;
- labels for supervised training: two- and three-dimensional boxes, segmentation, and the
  three-valued supervision with its pattern instances, participants and intervals;
- corpus metadata that describes the data honestly: observability spans, rendered spans, prevalence
  in its several units, illumination bands, render states and refusals, what was captured and what
  was not;
- the contracts by which an external consumer reads all of the above, and the rule by which
  supervision *would* be transferred onto detector tracks;
- quality gates on **the data**: is it internally consistent, is it leak-free, is it complete, does
  it say what it does not contain.

**Out of scope — do not design, build or specify:**

- running a detector, a tracker or an EPoL model as part of the pipeline;
- associating external model output to truth in order to measure that model;
- precision, recall, F1, temporal-localisation scores, confusion matrices, or any model metric;
- evaluation harnesses, scoreboards, model comparison, or any pass/fail verdict on a model;
- a "score" artifact root. There are artifacts we produce and artifacts we consume; model output is
  neither.

**Three things sit near the line and stay in, reframed. Do not delete them.**

1. **"Does an annotated interval survive contact with a detector?"** This asks whether *our corpus* is
   fit for purpose, not whether a model is good. It stays as a **corpus fitness probe** that uses a
   stock detector as an *instrument* — the way a thermometer checks an oven. Its output is "our data
   does or does not yield trackable targets", never a figure of merit for the detector. It must not
   emit model metrics.
2. **The illumination-only predictor.** Predicting the label from illumination with no imagery at all
   measures a property of *the dataset* — whether the label has leaked into a covariate. It stays as a
   **leakage probe**, not as a baseline for a model to beat.
3. **Truth-to-track association.** We publish truth that is *associable* — per tick, positioned, timed,
   boxed — and we document the rule by which supervision transfers. **We do not perform the
   association**, because that requires model output this pipeline never sees. Keep the contract and
   the format; drop the harness and anything that measures association as model performance.

When you find scoring language, ask which of these it is: an assertion about the **data** (keep,
reword so it cannot be read as model evaluation) or an assertion about a **model** (remove, and say in
one line what an external consumer would do instead). Do not silently delete a measured finding —
if a measurement is real but its framing was evaluative, keep the measurement and re-frame it.

## 3c. The live exercise is a primary use case, and it is generic past our boundary

**Added by the user 2026-09-18**, clarifying §3b. Narrowing the scope removed *scoring*; it did not
remove *running the chain*. Two things are wanted, and the first is wanted far more than the second.

### The live exercise — wanted, and first class

```
synthetic imagery generation  ->  Detect & Track consumes  ->  sends tracks to the EPoL model service
                                                          ->  the model performs its anomaly detection
                                                          ->  and produces reports, live
```

Treat this as a **primary use case**, not one that survives on sufferance. Everything up to and
including "synthetic imagery generation" is ours. Everything after it is not.

**We know nothing about the external projects, and the plan must not pretend otherwise.** Their APIs,
their formats, their transports, their report schemas, their latencies and their failure modes are all
unknown to us and are none of this plan's business. So:

- **Specify what we emit and how it can be consumed.** Do not specify what consumes it. Frames, truth
  sidecars, and their timing and identity guarantees are contracts we own; a detector's input format
  is not.
- **Assume an adapter, and keep it thin and outside.** Where a concrete integration is needed to make
  a use case readable, present it as *one possible adapter*, clearly marked as illustrative, never as
  the interface. A reader must be able to substitute a completely different detector without any part
  of this plan changing.
- **Anything that comes back is received data, with its own provenance.** If the external chain
  offers tracks or reports, we may record them verbatim and tick-stamped as a transcript, because a
  transcript is a record of what happened. We do not parse them for meaning we then act on, we do not
  merge them into truth or supervision, and — per §3b — we do not measure them.
- **Do not design a fusion stage, a normaliser, or a schema for their outputs.** If a transcript needs
  a container, it is an opaque blob with a timestamp, a source id and a content type.

**Pacing is the one genuinely new engineering question.** A captured corpus runs as fast as the
machine allows; a live exercise runs against a wall clock with a human watching. Decide, with the
clock ownership already established, what happens when the external chain cannot keep up — the
candidates are dropping frames and recording the drop, letting simulated time advance more slowly in
wall-clock terms, or running ahead and buffering. Note that the second is nearly free here and costs
no truth, because truth is stamped in simulated time: a world that ticks slower is still internally
exact. Say which, and what the operator sees.

### Cyclic self-training — wanted, second, and explicitly not dynamic

The user's words: *"potentially self training (done cyclically via an automated process, not
dynamically during execution)"*. Nothing trains during a run, and nothing in this pipeline trains at
all. What is wanted is that a corpus can be **regenerated on a cadence by an automated process** that
something outside then trains on.

The requirement that places on us is modest and is entirely about the operator surface: **unattended,
parameterised, reproducible, non-interactive invocation**, with a machine-readable result saying what
was produced and whether it is fit to use. No scheduler, no training loop, no model lifecycle — those
are outside. Where a section already specifies a run configuration and a manifest, this mostly falls
out; say so rather than inventing machinery.

## 3d. Cyclic generation is driven from outside, and we do not judge our own runs

**Added by the user 2026-09-18**, correcting §3c's second half. The previous revision went too far: it
designed a cadence, a fitness verdict and a termination policy. **None of that is ours.**

The user's words: *"Cyclic generation is not for us to control. Our tools suite is used to create the
synthetic imagery. The external processes that drive the cyclic regeneration are in full control of
when to terminate and what to do with the data generated and what comes next. The external processes
that control the cyclic aspects of generation have the ability to kill the SUMO and/or CARLA server at
their leisure or use the CarlaNet and Python shim to query data so as to decide when enough is
enough."*

### What that removes

- **No cadence, no scheduler, no run-length policy of ours.** Do not design `--duration` or `--frames`
  as a requirement. If a caller wants a bounded run it can stop us; a convenience limit may exist, but
  nothing in the plan may depend on one.
- **No fitness verdict.** This is the same principle as §3b applied to the run itself: we do not judge
  a model, and we do not judge a run on the caller's behalf either. **Publish the facts; the caller
  decides.** Individual quality gates are facts about our own data and stay in full — this check ran,
  this is what it observed, this is the threshold it compared against. An *aggregate* verdict that
  says "therefore this corpus is fit for your purpose" presumes a purpose we do not know. Remove it.
- **No assumption that we are asked politely to stop.**

### What it requires instead — and these are real requirements

1. **Abrupt external termination is a normal operating mode, not a failure mode.** The caller may kill
   the CARLA server, the SUMO process, or our client, at any instant, deliberately. Everything we
   write must therefore be **incrementally written, self-describing and valid at every instant** — a
   corpus interrupted mid-window is a shorter corpus, never a corrupt one. Say what is guaranteed
   about artifacts after a kill at an arbitrary point, per artifact. The run manifest was already
   specified as written incrementally and closed at the end; that is no longer a nicety, it is the
   load-bearing property, and "closed at the end" must not be what makes it readable.
2. **Observability while running is the interface that matters.** The caller decides "when enough is
   enough" by **querying**, through CarlaNet and the Python shim, not by reading a verdict at the end.
   State what an external process can observe about a run in progress, through surfaces that already
   exist, and what it would have to poll to answer questions like how many annotated intervals have
   closed, how many frames have been written, or how much of a declared area has been covered.
   `05_CarlaNet_Capability_Audit.md` already documents the client surface — use it rather than
   inventing a new channel, and name any genuine gap rather than designing around it.
3. **Non-interactive invocation still holds** (§3c), because a caller that cannot answer a question
   still cannot answer one. Everything already recorded about no prompts, recorded configuration and
   reproducibility stands.

### The measured findings from the previous revision are still valid — reread them in this light

The swallowed interrupt, the absent run-length termination, the unwritten run report and the five
unrecorded environment variables were all measured and are all real. **What changes is why they
matter.** The swallowed interrupt is no longer "a scheduler cannot tell success from a kill"; it is
"a deliberate kill is the expected path, and it must leave valid artifacts and an honest record that
the run was stopped rather than finished". Keep the measurements; re-motivate them.

## 3e. Traffic lights are simulated in SUMO and never rendered in CARLA

**User decision 2026-09-18.** *"Traffic lights need not be rendered for this toolchain … Semantic
verification in imagery of traffic lights is not worth it at this stage, mostly because the limited
traffic light meshes available and the way the traffic light models placed within the photoreal are
often misaligned; meshed traffic lights are more of a hazard for our purposes than a benefit. The
traffic light actors/signs need not be rendered. Also we would not have to spend compute and network
resources sending messages relating to traffic light states."*

**The distinction that must not be lost.** SUMO **does** simulate traffic lights, and its vehicles
**do** obey them — the right-of-way table and the `tlLogic` programs are a large part of why the
ambient behaviour is believable at all, and they are the reason the network is built with
`traffic_light_type="actuated"` rather than netconvert's fixed-time default. **None of that changes.**
What changes is everything on the CARLA side of the bridge:

| | |
|---|---|
| **Stays** | SUMO's `tlLogic` programs, its right-of-way `<request>` rows, the actuated-signal netconvert setting, and every vehicle behaviour that follows from them. The scenario's own traffic-light quality still matters and is still checked at compile time |
| **Goes** | Rendering traffic-light and sign actors in CARLA; driving CARLA's traffic-light state from SUMO; every per-tick or per-change light-state message; and any plan to make traffic-light state semantically verifiable in imagery |

**Why, in the user's terms:** the available traffic-light meshes are limited, and where they are placed
against the photoreal they are frequently misaligned. A misaligned mesh in the imagery is a **hazard**
for this corpus, not a benefit — it is a rendered object that does not correspond to the world the
photoreal shows, and a detector trained on it learns an artefact.

**What this removes from the plan.** Be thorough; this is a simplification and should read as one:

- The traffic-light synchronisation work item, and its dependency on an RPC exposing a light's
  OpenDRIVE signal id — a gap the audit recorded as unexposed. **That blocker no longer blocks
  anything**; record it as not required rather than as an obstacle.
- `SetTrafficLightState` from the per-tick batch, and its share of the batch budget.
- The concern that the sizing scenario has zero traffic lights and therefore cannot exercise
  synchronisation. There is nothing to exercise.
- The requirement I placed on the measurement fixture that it contain a signalised junction. It was
  there only to test synchronisation; drop it.

**What replaces it in the runtime:** this mode renders no traffic-light or sign actors. The existing
viewer already has the toggle — `signals_visible` with an `L` hotkey
(`CarlaControl/src/carlacontrol/PygameInterface.py:106`, `:580`) — so the mechanism exists; what this
mode needs is for it to be **off and fixed off**, not operator-toggled mid-run, and recorded in the
manifest so a consumer knows no signal geometry was in frame. Signals reach a world through
`SignInjector` writing `<signal>` elements that native `SpawnSignals` turns into actors; the world
build is shared with other modes, so **do not change world generation** — suppress at the session,
not at the source.

**The capability audit keeps its traffic-light findings.** They are facts about the client and the
engine and remain useful to other work. What changes is their status in *this* plan: present,
audited, **not required**. The RPC name mismatch it found stays a recorded defect on its own merits.

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
  `carla/CarlaControl/skills/sumo-traffic-scenarios/SKILL.md`. (The `.agents/skills/` copy at the
  workspace root is a stub pointing at it.)

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
| `11_Time_And_Illumination.md` | Time and illumination engineer |
| `12_Operator_Control_Surface.md` | Operator control-surface engineer |
| `13_Work_Breakdown.md` | Integration lead (written last) |

Cross-reference other sections by relative link. Do not restate another section's content; link to
it. If you need a decision that belongs to another section, state the dependency and the property you
need it to have.

## 8a. The document states what is true; it never narrates its own history

**User directive 2026-09-18.** *"'What this draft adds to the list' is useless. The draft is a draft
and should have the information placed correctly. A change history section at the top is fine but
should be very very concise and no more than 20 words in description of what changed between drafts."*

**Forbidden anywhere in the body of a document:** "this revision adds", "the earlier draft said",
"previously this was", "amended in place", "withdrawn", "[extended]", "I corrected my own claim",
disposition tables recording what changed site by site, and decision rows annotated with how the
decision changed. A reader arriving cold must never have to reconstruct a previous version in order to
understand the current one.

**Required instead:** put the information where it belongs and state it as fact. A decision's text
states the decision, not its history. A finding states what is true, with its evidence.

**Allowed, and wanted:** one change-history block at the top of each document — one line per revision,
**at most 20 words** describing what changed. That is the only place a document may refer to its own
past.

**This is about narration, not content.** Every measurement, citation, decision and `path:line` stays.
Where something was framed as "we used to think X, now Y", keep Y and its evidence and drop X — unless
X is itself a measured fact about the tree, in which case it is a finding and belongs on its merits.

## 9. House style for these documents

Follow the Findings documents' style, because this plan will be read beside them: a short header
block stating status, date and scope; claims cited to source; tables where a table is clearer than
prose; an explicit list of what the section does *not* cover; and decisions recorded in a numbered
table at the end so they can be referenced from elsewhere. Number your decisions with your section
number as a prefix (for example `D4.3` in `04_Contracts.md`) so they are unique across the folder.
