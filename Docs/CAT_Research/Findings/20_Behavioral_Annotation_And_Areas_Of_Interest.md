# 20 — Behavioral Annotation of Tracks, and Areas of Interest

**Status:** Research and design note. Source audit against the working tree; no code changed and no
runtime measurement taken here. Every claim about existing behaviour is read from the sources cited, or
carried forward from a measurement already recorded in
[18](18_Scenario_Fabrication_For_EPoL_Training.md) and marked as such.
**Date:** 2026-08-12
**Scope:** How a storyboard says *this* vehicle is executing *this* behaviour over *this* interval; how
that statement reaches the Cursor-on-Target truth sidecar without being confused with "was placed by a
scenario"; what shape the supervision has to take for an estimated-pattern-of-life (EPoL) model that
consumes detector tracks; and whether declared areas of interest are needed to make any of it work.
Two adjacent questions are answered here because they land on the same authoring surface and the same
extension points: how a storyboard names the vehicle it wants (§5.6), and what an author — assisted or
working by hand — has to be given in order to write one that resolves (§5, §11 question 9).
**Extends:** [18 — Scenario Fabrication for Pattern-of-Life Model Training](18_Scenario_Fabrication_For_EPoL_Training.md)
(§4.2's pattern spec and §6.2's "pattern annotation" line are the placeholders this document fills in).
**Related:** [09 — Telemetry CoT Contract](09_Telemetry_CoT_Contract.md) ·
[12 — CarlaNet.Labeling](12_CarlaNet_Labeling.md) ·
[16 — Sensor Pose In Recordings](16_Sensor_Pose_In_Recordings.md) ·
[17 — Photoreal Occlusion Metric](17_Photoreal_Occlusion_Metric.md) ·
[19 — Turn-Restriction Obedience](19_Turn_Restriction_Obedience.md)

**Out of scope, deliberately.** Nothing here is scoring in the sense CARLA and ScenarioRunner use the
word — no criteria, no pass/fail verdict, no driving-quality metric. That machinery was rejected in
[18 §3.2](18_Scenario_Fabrication_For_EPoL_Training.md) and stays rejected. This document is about
*annotation*: attaching a statement of what a vehicle was doing to the truth record of that vehicle, so
a trainer can supervise on it. How an EPoL model is subsequently evaluated is a downstream question
this document only constrains, by making sure the truth it needs exists.

---

## 1. The deficiency, stated precisely

Supervision for a pattern-of-life model is a triple: **(track, interval, annotation)**. The model
consumes tracks; truth exists to say which track, over which span of time, carried which behavioural
statement. Three separate things are missing before that triple can be written.

| Missing | Why it bites |
|---|---|
| **A stable track identity** | The sidecar's `uid` is `CARLA-TRUTH-{actor_id}` (`CarlaNet.Recording/CotWriter.cs:134`) and CARLA actor ids are assigned at spawn, so the same authored vehicle is a different track in every run. Nothing in the truth record names the storyboard entity |
| **An annotation channel** | The truth record has eighteen fields (`CarlaNet.Recording/VehicleTelemetry.cs:8-26`) and not one of them says what the vehicle is doing. The only free-form per-actor string that reaches the sidecar is `role_name`, and it is not free (§4.3) |
| **An interval** | The executor knows exactly when an entity entered and left each phase — it holds that state on `EntityRuntime` and `ActRuntime` (`CarlaNet.Scenario/ScenarioExecutor.cs:611-629`) — and discards it. Nothing is written down |

And one thing must be actively avoided rather than merely added:

> **Scenario membership is not an annotation.** A storyboard is a control mechanism, not a claim about
> behaviour. A capture wants scripted vehicles that are deliberately ordinary — because ambient traffic
> from the Traffic Manager is a coarse, uniform-random ambience and cannot express "a delivery van doing
> a normal delivery round" — and treating every scripted vehicle as a positive would poison exactly the
> examples that make the positives learnable.

So the default supervision state of a scenario-placed vehicle must be **unlabelled**, and an annotation
must be an explicit, separately authored act.

## 2. What supervision has to look like, and what it must not become

The user of this data is a model that learns the *distribution of ordinary movement* and flags
departures from it. That imposes constraints a detector's label file does not have. Each of the
following is a design constraint on the annotation format, not commentary.

### 2.1 The label is authored intent — never a rule applied to truth

The tempting shortcut is to derive annotations geometrically: *speed above 25 m/s within 200 m of a
site ⇒ `speeding_approach`*. It is cheap, it needs no authoring, and it is worthless. A label computed
by a rule over perfect state teaches the model the rule, evaluated on noiseless inputs, and the trained
model's apparent skill is the detector's positional error and nothing else. Worse, it is
self-confirming: the same rule can be run over the model's own inputs, so the "model" is redundant.

The split that keeps this honest:

- **Annotation** — a statement the *author* made, recorded because only the author knows it. "This
  entity is executing a loiter." It cannot be recovered from the trajectory, because a trajectory that
  looks like a loiter and one that *is* a loiter are the same trajectory.
- **Derived context** — quantities computed identically for **every** vehicle, scripted and ambient
  alike: range to each area of interest, inside/outside, continuous time inside, speed percentile.
  Useful as covariates, for stratifying a corpus, for auditing, and for finding accidental positives.
  **Never a label.**

The two must be carried in distinguishable places in the sidecar so a downstream consumer cannot
mistake one for the other. §7.4 keeps them in separate elements for exactly this reason.

### 2.2 Supervision is three-valued, never binary

The obvious encoding — annotated vehicles positive, everything else negative — is wrong here, and
wrong in the expensive direction. A capture deliberately contains dense ambient traffic; over a long
run that traffic will, by chance, produce genuine instances of the patterns being trained. A car
legitimately parked for forty minutes near the site is a real loiter that would be silently filed as a
negative. False negatives in the training set are far more damaging to an anomaly or
one-class model than missing positives, because they teach the model that the target behaviour is
normal.

That a forty-minute ambient stop is currently impossible is not a defence — it is a separate problem,
and fixing it makes this one larger. See §2.8.

Three states, then:

| State | Meaning | Who gets it |
|---|---|---|
| `annotated` | The author asserts this entity is executing the named pattern over this interval | Scripted entities carrying an annotation |
| `nominal` | The author asserts this entity is *not* executing any target pattern | Scripted entities explicitly declared so — the hard negatives of §2.7 |
| `unlabelled` | No assertion either way | Every ambient vehicle, and every scripted entity the author said nothing about |

`unlabelled` is the default and must be emitted explicitly, not by omission — a consumer that sees no
supervision element has no way to distinguish "nothing was asserted" from "the field was not written".

### 2.3 The unit of supervision is a pattern instance, not a vehicle

Several of the patterns worth authoring involve more than one vehicle, or more than one span of time,
or both:

- A convoy holding formation is one phenomenon with N participants.
- A revisit cadence — the same vehicle returning to a site four times across two hours — is one
  phenomenon with four disjoint intervals.
- A rendezvous is one phenomenon with two participants, each with a different role in it, whose
  intervals overlap but do not coincide.

An annotation attached only to (vehicle, interval) cannot express any of these without the consumer
re-deriving the grouping. So the record is:

```
PatternInstance
  id                    stable within a run, comparable across runs of the same scenario
  labels[]              one or more; a vehicle can be both circling and speeding
  parameters{}          the swept values that defined this instance
  aoi_refs[]            areas of interest the pattern is defined against, where any
  participants[]        { entity_id, role }        role: subject | accomplice | foil | …
  intervals[]           { participant, phase, issued … observed }   three onsets, §2.4
  supervision           annotated | nominal
```

The per-frame sidecar then carries a *projection* of this onto (vehicle, tick) — which pattern
instances this vehicle is participating in at this instant, and in what phase (§7.4). Both forms are
needed; neither substitutes for the other, and §7.5 keeps the instance form as the authoritative
artifact.

### 2.4 There is no single instant at which a phase begins

**There is no dwell primitive.** The executor has no dwell construct, no dwell action and no dwell
state — the word appears in `CarlaNet.Scenario` only in comments. A dwell is emergent, assembled from
three ordinary constructs: a speed action targeting zero, a stand-still trigger carrying the dwell
duration, and a speed action returning to a travelling speed. That is worth stating plainly because it
means the annotation channel cannot key off a phase the executor recognises; there is none to key off.

What the executor does with a **single** authored speed action to zero is decompose it
(`ScenarioExecutor.cs:239-277`, `:350-373`, `:516-541`):

| | What happens | Where |
|---|---|---|
| Action fires | A transition is stored: from the current speed, to the target, over the authored duration | `ApplySpeed`, `:350-373` |
| Each tick during the transition | The interpolated target is commanded — **floored at 1.0 m/s**, so the Traffic Manager is never given a zero target | `AdvanceEntities` `:257-264`; `Command` `:516-522` |
| Transition ends, target ≤ 0 | The vehicle is unregistered, autopilot is turned off, and brake and handbrake are applied together | `Hold`, `:267-271`, `:528-541` |
| Thereafter | Stationary time accumulates while the **world-reported** speed is ≤ 0.15 m/s | `:245-253` |

So a stop is a ramp down to 1 m/s followed by a brake, not a smooth approach to zero, and the floor is
deliberate: a zero target produces neither throttle nor brake, which
[18 §4.4](18_Scenario_Fabrication_For_EPoL_Training.md) measured as a 22.5 s roll-down to rest against
1.1 s with the brake applied, and which the idle cull then destroys. That mechanism is measured,
rejected, and unreachable from the executor — it is named here only so it is not mistaken for a case
the annotation has to cover.

**The consequence for annotation is that "when the dwell began" has three defensible answers**, and
they are separated by an interval the scenario itself sets:

- **Action fired** — the tick the transition was stored. Exact, and the earliest defensible onset.
- **Hold applied** — the tick the brake went on, which is the action's start plus the authored
  `SpeedActionDynamics` duration. This is where the vehicle is *committed* to stopping.
- **Observed standstill** — the tick the world first reported ≤ 0.15 m/s. The executor already tracks
  it, from the world's own velocity rather than from what was commanded, so a vehicle that fails to
  stop cannot satisfy the trigger.

The gap between them is **not a constant and cannot be calibrated away**, because the ramp duration is
an authored property of each speed action, not a property of the system. The 5.1 s recorded in
[18 §4.4](18_Scenario_Fabrication_For_EPoL_Training.md) is one probe's profile, not a system figure.
At a 2 Hz capture even a short ramp is several frames of a "loitering" label on a vehicle that is
visibly still moving, and temporal-localization evaluation is interval-overlap-sensitive, so this is
not a rounding detail.

Record all three. They are already computed or trivially available at the moment they occur, none can
be reconstructed from the others afterwards, and which one defines the interval is the trainer's
choice, not the simulator's.

### 2.5 An annotation nothing observed is a hole in the denominator

Truth is omniscient and the collection is not. Every vehicle in the world appears in the sidecar
whether or not any sensor could see it, so an annotated interval can be recorded in full while no
imagery of it exists. What follows from that is narrower than it first appears, and the narrowing
matters.

**It is not a training problem.** An unobserved interval yields no detection, therefore no track,
therefore no training example. It cannot corrupt what the model learns, because it never reaches the
model. Nothing has to be done to protect training from it.

**It is an accounting problem, in two specific places.**

- **The evaluation denominator.** Asking "how many of the authored loiters did the model flag?" and
  dividing by the number of *authored* intervals charges the model with instances no sensor ever saw.
  The honest denominator is intervals observed by at least one collection sensor. Without the
  observability record, that denominator cannot be reconstructed after the fact, because the manifest
  and the imagery are the only two artifacts and neither alone says which intervals were covered.
- **The base rate.** §2.6 records prevalence from the manifest. Computed over authored rather than
  observed intervals it is overstated, and precision at low prevalence — the regime an anomaly model
  actually operates in — is dominated by exactly that number.

So the proposal is precisely the one the accounting implies: **record, per annotated interval, the
span during which the participant was observable, and by which sensors.** That turns an authored
interval into an observed sub-interval, which is the unit both the denominator and the base rate
should be computed over.

**Observability is a property of the collection, not of one camera.** CARLA is multi-client and
supports any number of cameras in a scene simultaneously, so an interval can be covered by one sensor,
several, or none, and coverage can pass between sensors mid-interval. Two quantities are therefore
wanted and neither substitutes for the other: the **union** across all collection sensors, which is
what the denominator above needs, and the **per-sensor** span, because each camera feeds its own
detector producing its own tracks, so supervision is transferred per sensor unless something fuses
them first (§7.6).

The present implementation records one camera, and that is an implementation state rather than a
design limit: `FrameRecorder` subscribes to a single stream token (`FrameRecorder.cs:59-98`) and the
shim holds one recorder per client (`carlanet/__init__.py:1824`). Additional cameras today mean
additional client processes, which has a consequence for the annotation registry that §7.3's
process-local design does not survive unchanged — recorded against decision 11 in §10 rather than
buried here.

Everything needed for the geometry already exists per capture: the collection platform's pose and full
pinhole intrinsics (`CotWriter.cs:101-124`) and each vehicle's position. In-frustum coverage is worth
having immediately and needs nothing new. Whether a vehicle inside the frustum was actually *visible*
is the harder question — occlusion by photoreal geometry — and is the subject of
[17](17_Photoreal_Occlusion_Metric.md); it refines this measure rather than being a prerequisite for
it.

### 2.6 Confounders the annotation channel must not introduce

Three ways a synthetic corpus lets a model cheat, all of them created by *how* the annotated vehicles
get into the world rather than by the annotation itself:

- **Appearance.** If annotated entities are drawn from a different blueprint pool than ambient traffic,
  the model learns appearance. This is live today: `BlueprintChooser` resolves a whole category to a
  single blueprint by first match (`CarlaNet.Scenario/BlueprintChooser.cs:53-62`), which
  [18 §8.4](18_Scenario_Fabrication_For_EPoL_Training.md) already flags as producing five identical
  cars in one convoy. Annotated entities must draw from the same pool as ambient ones, by the run seed.
  This confounder and the presentational problem of naming a specific vehicle pull in opposite
  directions and are reconciled in §5.6.
- **Spawn signature.** If scripted entities are the only vehicles that appear mid-scene rather than
  entering from the staging ring, entry style becomes the label. Scripted entities are placed by
  `TeleportAction` at a lane position (`ScenarioExecutor.cs:84-121`), ambient traffic enters from the
  inward staging ring (`CarlaNet/python/SCTMV.py:555-574`). These are visibly different behaviours and
  a nominal scripted entity is the control that keeps them from separating the classes.
- **Prevalence.** Anomaly-model evaluation at low base rates is dominated by the base rate. The
  manifest gives it exactly — count of annotated intervals against total observed vehicle-seconds — so
  it should be recorded per run rather than reconstructed by counting XML files.

### 2.7 The scenario system's unique product is hard negatives

Ambient traffic can produce ordinary movement in bulk, and the storyboard system can produce the target
patterns. The thing neither produces on its own, and which is the most valuable output of the two
together, is a **deliberately authored near-miss**: a vehicle that stops for forty-five minutes in a
legitimate place for a legitimate reason, authored as `nominal`, sharing the appearance pool, the entry
style and the site with an annotated loiter.

Without them the model has no way to learn that duration alone is not the signal, and the first real
parked car in a fielded scene fires the detector. This is the case that most justifies the
`nominal` state of §2.2 existing at all, and it is the reason the "control non-anomalous vehicles in
the same scenario" requirement is not a convenience.

### 2.8 The Traffic Manager's housekeeping edits the distribution being learned

An EPoL model learns the distribution of ordinary movement. Anything that silently reshapes that
distribution is therefore editing the training target, and the Traffic Manager contains one such
mechanism operating on the ambient population by design.

**The idle cull imposes a ceiling on how long any ambient vehicle can be stationary.** A registered
vehicle idle for `BLOCKED_TIME_THRESHOLD` — 90 seconds, or 180 held at a red light
(`Constants.cs:39-42`) — is destroyed and deregistered (`Stages/ALSM.cs:192-203`). It exists to clear
vehicles that have become genuinely stuck, and for that purpose it is correct. For a capture it means
**no ambient vehicle can ever be observed parked**, which is a strong and entirely artificial claim
about ordinary behaviour to bake into a corpus whose headline pattern class concerns stationary
vehicles.

Three consequences, each independent of the others:

- **It truncates the very distribution the model is meant to learn.** Long stops occur constantly in
  real traffic. A corpus in which they never exceed ninety seconds teaches a ceiling that is a property
  of this simulator's housekeeping and of nothing else.
- **It masks the accidental positives of §2.2 rather than preventing them.** The reason ambient
  traffic rarely produces a convincing loiter today is that the cull removes any vehicle that would
  have. Relaxing it improves realism and raises the accidental-positive rate at the same time, so the
  auditing story of §2.2 and the relaxation are **coupled** and should not be sequenced apart.
- **Its firing point is not reproducible.** The threshold is measured against ALSM's wall-clock
  timestamp rather than simulation time (`Stages/ALSM.cs:218-225`), and
  [18 §4.3](18_Scenario_Fabrication_For_EPoL_Training.md) records the clock ratio as load-dependent —
  90 seconds of wall clock landing near 76 seconds of simulated time at the ratio measured there. So
  the cull removes different vehicles at different simulated moments in two runs of the same seed, and
  the ambient population is not reproducible between runs while it is active. For a pipeline whose
  value rests on reproducible captures, that is an independent reason to be able to switch it off, not
  merely permission to.

**Scenario entities are already outside this.** The executor's stop unregisters the vehicle
(`ScenarioExecutor.cs:528-541`) and idle bookkeeping covers registered vehicles only, which is why
[18 D6](18_Scenario_Fabrication_For_EPoL_Training.md) could drop the per-vehicle exemption as a
precondition. The requirement recorded here is therefore about the **ambient** population, and it is a
different requirement from the one doc 18 considered.

**The requirement: cull behaviour must be switchable at run level, and detection must be separable
from destruction.** Simply disabling it is not safe, because the cull is the only mechanism that ever
clears a queue behind a stopped vehicle — collision negotiation is purely geometric and exempts
nothing, and the lane-change escape needs the obstacle seen between 20 and 50 m out with free adjacent
lanes ([18 §4.3](18_Scenario_Fabrication_For_EPoL_Training.md)). A deadlock in a multi-hour capture
would therefore persist and grow, which is its own corruption of the ambient distribution and is
**silent**, since the removal report is currently the only thing that says anything at all. Retaining
detection while suppressing destruction keeps the diagnostic that says a capture is degrading while it
is still degrading, and gives §2.2's audit a ready-made signal: a vehicle stationary far beyond the
threshold with no authored reason is exactly the record a human should review.

Two notes for whoever builds it. The plumbing has a **complete precedent** — the dead-end removal in
the very next block is already gated on a Traffic Manager parameter (`Stages/ALSM.cs:205-217`), and
that parameter runs `Parameters.cs:225,443` → `ITrafficManagerCallback.cs:112` → `TrafficManager.cs:222`
→ the Python shim (`carlanet/__init__.py:1377`), so a second flag follows a path that already exists.
And the two removal paths are **genuinely distinct**: a vehicle at a graph dead-end is finished, not
stuck, and should not be swept up in the same switch.

A run-level switch is also strictly safer than the per-vehicle exemption doc 18 sketched. That
exemption had to be applied at the nomination site rather than at destruction, because only the single
most-idle vehicle is nominated per pass (`Stages/ALSM.cs:189-190`) and a permanently parked exempt
vehicle would otherwise hold that slot forever, shielding every genuinely stuck vehicle behind it and
disabling the cull for the whole population by accident. A run-level switch has no such failure mode,
because nothing is nominated when nothing is destroyed.

## 3. The pattern classes the design has to cover

The dwell is one exemplar, and designing around it would produce a format that expresses only it.
These are the classes used as the design test; the right-hand column is the demand each places on the
annotation model, which is what the table is for.

| # | Pattern | What it demands |
|---|---|---|
| 1 | **Dwell / loiter near a site** | Three distinct onsets separated by the authored ramp (§2.4); a site reference. Note there is no dwell primitive — it is assembled from a speed action, a stand-still trigger and a second speed action |
| 2 | **High-rate approach** — enters the vicinity of a site above a speed band, never stops | No standstill to key on; the interval is defined by *place*, not by a motion event, so the annotation cannot be inferred from a kinematic state machine |
| 3 | **Circling / repeated orbit** | An unbounded pattern; the interval must be closable by the scenario's end, and the record must distinguish "ended because it finished" from "ended because the run did" |
| 4 | **Class-conditioned presence** — a heavy goods vehicle in a residential area at 03:00 | Not a motion pattern at all. The annotation must be able to span an entity's whole life and reference class, place and time jointly. Also the case where the detector's class output is part of the phenomenon, so truth must carry the class (it does: `base_type`, `type_id`) |
| 5 | **Convoy / coordinated group** | Multiple participants in one instance, with per-participant roles (§2.3) |
| 6 | **Revisit cadence** | One instance, multiple disjoint intervals per participant |
| 7 | **Rendezvous** — two vehicles converge, hold briefly, one departs | Multiple participants with non-coincident intervals and asymmetric roles |
| 8 | **Route repetition** — the same circuit driven twice | Instance-level parameters (the circuit) that are not derivable from any single interval |
| 9 | **Deliberate violation** — an illegal turn, wrong-way travel | Realised by driving the vehicle outside Traffic Manager control ([19 §4](19_Turn_Restriction_Obedience.md)); the annotation channel must not depend on the vehicle being Traffic-Manager-registered |
| 10 | **Authored hard negative** (§2.7) | The `nominal` state, with the same expressive power as an annotation |

Two consequences fall straight out. **Multi-label**: 2 and 4 can co-occur on one vehicle, so labels are
a set. **Place-relative definition**: 1, 2, 3, 4 and 8 are all defined against a location rather than
against a motion event — which is what §8 is about.

Class 3 also exposes an executor gap worth recording: a route is a finite waypoint list, and when the
path buffer empties the vehicle reverts to random successors at junctions
([18 §4.3](18_Scenario_Fabrication_For_EPoL_Training.md)). An orbit therefore needs either a repeated
route or an explicit lap count, and until it has one the annotation would be describing something the
vehicle stops doing partway through.

## 4. Where the path from storyboard to sidecar is severed today

### 4.1 The truth producer has no scenario-side input at all

`VehicleTelemetryService.Compute` builds the truth record by enumerating world actors from the
world-observer snapshot cache, filtering to `vehicle.*`, and reading each one's transform, velocity and
blueprint attributes (`CarlaNet.Recording/VehicleTelemetryService.cs:35-107`). Its only inputs are the
`CarlaClient` and a georeference origin. It has no reference to `ScenarioExecutor`, and
`FrameRecorder` — which calls it once per capture (`CarlaNet.Recording/FrameRecorder.cs:120`) — has
none either.

The two are, however, **in the same process**: `world.start_scenario` and `world.start_recording` both
construct .NET objects on the same client (`CarlaNet/python/carlanet/__init__.py:1846-1880`,
`:1805-1843`), and SCTMV drives both. So the gap is a missing reference, not a missing transport. §7.3
takes advantage of that.

### 4.2 What the sidecar already carries, and the one thing it cannot say

The per-vehicle event carries `point`, `track` (course and speed), `contact`, and the `_carla`
truth-extras block with `source`, `actor_id`, `type_id`, `base_type`, `special_type`, dimensions,
`color`, `role_name` and raw velocity components (`CotWriter.cs:130-178`). Run identity — `tick`,
`sim_time_s`, `run_id`, `scenario_id`, `seed` — sits on the `<events>` container rather than on each
event, on the reasoning that every event in a sidecar shares one tick and that a strict CoT client may
reject unknown attributes on `<event>` (`CotWriter.cs:36-48`).

That is a good precedent and it settles where annotations go: **a new child of `<detail>`**, not new
attributes on `<event>`. Unknown `<detail>` children are ignored by WinTAK
([09 §5](09_Telemetry_CoT_Contract.md)), the existing `_carla`, `_carla_intrinsics` and `_solar`
elements already rely on that, and — decisively — annotations are a *set*, and XML attributes cannot
repeat.

One gap surfaced while reading this path: **`scenario_id` is never populated in practice.** The
recorder accepts it (`FrameRecorder.cs:57-62`) and writes it (`CotWriter.cs:45`), but SCTMV's call
passes only `run_id` and `seed` (`CarlaNet/python/SCTMV.py:1472-1479`), so every sidecar recorded from
the viewer today omits the scenario it was recorded under. That has to be closed regardless of anything
else here, because the manifest of §7.5 is joined to captures by exactly that field.

### 4.3 `role_name` is the only per-actor free text that reaches truth, and it is not free

`role_name` is defined for every actor definition as a string variation with `bRestrictToRecommended =
false` (`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Actor/ActorBlueprintFunctionLibrary.cpp:208-214`),
so arbitrary values are accepted. It is read into the truth record
(`VehicleTelemetryService.cs:93`) and emitted (`CotWriter.cs:170`). It looks like the obvious carrier.

It is not, for two independent reasons.

**Two of its values carry behaviour.** `hero` exempts a vehicle from the Traffic Manager's idle cull
and anchors the hybrid-physics active radius (`CarlaNet.TrafficManager/Stages/ALSM.cs:243-256`,
`:193-195`); `hero` and `ego` both wire up ROS2 control callbacks
(`Actor/ActorDispatcher.cpp:208-210`); the replayer and the engine's frame-data path both special-case
`hero` (`Recorder/CarlaReplayer.cpp:500`, `Recorder/CarlaReplayerHelper.cpp:205`,
`Game/FrameData.cpp:827`). Any annotation scheme that reaches for these values changes what the
simulation does, which is the failure [18 §4.3](18_Scenario_Fabrication_For_EPoL_Training.md) already
identified when it rejected `hero` as a dwell mechanism.

**It is one string, and it is immutable after spawn.** Actor attributes are fixed at spawn — there is
no RPC to change one — so `role_name` can carry static facts and nothing time-varying. Packing an
entity id, a role, a label and a phase into one comma-separated string is the kind of encoding that
survives exactly until someone puts a comma in a name.

The right reading is that `role_name` is a **provenance** field, not an annotation field, and its
existing values (`autopilot` for ambient traffic, `CarlaNet/python/SCTMV.py:852`) already mean that.

### 4.4 Identity: what is stable, and over what

| Identifier | Stable within a run | Stable across runs | Where it is today |
|---|---|---|---|
| CARLA actor id | Yes | **No** — assigned at spawn | `_carla@actor_id`, and the `uid` |
| Storyboard entity name | Yes | **Yes** — authored | Parsed into `ScenarioEntity.Name` (`CarlaNet.Scenario/ScenarioModel.cs:34`) and never emitted |
| Pattern-instance id | Yes | Yes, if the compiler assigns deterministically | Does not exist |

Intra-run association is therefore already sound; it is only cross-run comparison that fails. Since
comparing runs is the whole point of a parameter sweep — one authored pattern, many variants, diffed
against each other — carrying the entity name into truth is the minimum viable fix and is worth doing
even if nothing else in this document is built.

### 4.5 The recorder log preserves spawn attributes verbatim — which decides §7.2

Two facts, both read from source, combine into a useful property.

The server performs **no validation of spawn attributes against the blueprint's declared variations**.
The RPC-to-engine conversion copies every attribute straight into the description's `Variations` map
(`LibCarla/source/carla/rpc/ActorDescription.h:47-56`), and the spawn path hands that description to
the actor factory unfiltered (`Server/CarlaServer.cpp:1139-1146`); the factories look attributes up by
name and default when absent, so an unrecognised one is inert. It is then returned to every client by
`SerializeActor`, which is how `VehicleTelemetryService` would see it.

And `ACarlaRecorder::CreateRecorderEventAdd` copies **all** of an actor's variations into the log's
actor-add event (`Recorder/CarlaRecorder.cpp:757-770`).

So a custom attribute set at spawn is (a) accepted with no engine change, (b) visible to the truth
producer with no new transport, and (c) **survives record and replay**, which no in-process mechanism
can. That last property matters because replay is a candidate appearance-permutation mechanism
([18 §5.4](18_Scenario_Fabrication_For_EPoL_Training.md)) and no executor is running during a replay.

One asymmetry to note: the Python shim refuses to set an attribute the blueprint does not declare
(`CarlaNet/python/carlanet/__init__.py:611-614`), mirroring upstream. Adding one from C# — where
`BlueprintChooser.Describe` already builds the attribute list (`BlueprintChooser.cs:33-39`) — is
trivial; adding one from Python needs a shim addition. The scenario executor is the C# path, so this
costs nothing where it is needed.

## 5. The authoring channel: four candidates

**How storyboards are actually written here, since it decides this section.** The primary path is
**assisted authoring against a generated world**: the area is described in ordinary terms — street
names, directions of travel, where a vehicle starts and what it then does — and the storyboard is
emitted directly as XML against the world's own `.xodr`. The graphical canvas of
[18 §8.3](18_Scenario_Fabrication_For_EPoL_Training.md) is used mainly to **preview and sanity-check** a
storyboard without standing up the server and the viewer, which is a different and narrower job than
authoring. Hand-authoring must remain possible regardless.

That resolves what was, at the time [18 §8.3](18_Scenario_Fabrication_For_EPoL_Training.md) was written,
a live worry: that the canvas's closed exporter would bound what a storyboard could say. It does bound
what the *canvas* can emit, and that still matters for anything authored there — but it does not bound
the format, because the format is not reached through the canvas. **Any construct the executor
understands can be authored today.** The empty `<Properties/>` on every storyboard in `carla/Import/`
reflects that nothing has yet asked for one, not that nothing could.

Two consequences run through the rest of this section. Whatever channel is chosen must be
**writable by an author working in text with no special tooling**, and it must be **checkable**, because
an assisted author is fluent in the format's shape and cannot know this fork's conventions unless
they are written down and validated. Both point the same way: prefer the in-file, in-standard channel,
and make every reference in it resolvable and every unresolvable one an error.

The mechanism that makes the whole path work is worth recording because nothing currently records it
and everything depends on it. **The generated `.xodr` carries human street names.** In
`Build/sumo-smoketest/Gardnerville_Centerville_Lane_elevated.xodr` all 213 roads carry a `name`, and
non-junction roads carry the real one — `<road name="Centerville Lane">`, `"Cobblestone Drive"`,
`"Rock Terrace Drive"`. Junction internals instead carry `:<nodeId>_<n>`, the converter's own edge
identifier. So "eastbound on Centerville Lane" resolves to a `roadId` by a direct lookup in the file
the world was built from, which is exactly what makes a description in street names into a
`LanePosition`. The gap is junctions: a movement *through* an intersection cannot be named that way and
has to be identified by the roads either side of it.

### 5.1 Entity properties — static facts, in-standard

OpenSCENARIO sanctions vendor data on a vehicle via `<Properties><Property name= value=/></Properties>`.
This fork already reads it: `Property(vehicle, "drawtonomy:template")` and `"drawtonomy:color"`
(`CarlaNet.Scenario/OpenScenarioParser.cs:146-147`, helper at `:387-392`). Adding namespaced keys is a
one-line parser change each.

Carries well: stable entity id, participant role, appearance pool constraints, and a whole-life
annotation for pattern class 4 (§3), where the statement genuinely is a property of the entity rather
than of an interval.

Cannot carry: anything interval-scoped, which is most of §3.

### 5.2 Custom command action — interval-scoped, in-standard

`<UserDefinedAction><CustomCommandAction type="…">payload</CustomCommandAction></UserDefinedAction>` is
the standard's sanctioned extension point for a vendor-specific *action*, and an action is exactly the
right shape: it lives inside an `Event`, so it fires on the same trigger vocabulary as everything else,
and it starts and stops with the phase it describes. The annotation becomes a first-class storyboard
element rather than a comment about one, and its onset is authored rather than inferred.

```xml
<Event name="dwell_begins">
  <Action name="annotate">
    <UserDefinedAction>
      <CustomCommandAction type="epol:annotate">
        begin instance=pi_loiter_a label=loiter role=subject aoi=school_lot
      </CustomCommandAction>
    </UserDefinedAction>
  </Action>
</Event>
```

Status in this fork: **refused at load, correctly.** `ParseActions` collects speed, route and delete
actions, and errors naming the first unrecognised `*Action` descendant when it finds none
(`OpenScenarioParser.cs:251-258`). `UserDefinedAction` is listed as a wrapper (`:377-379`), so the
message would name `CustomCommandAction` precisely. Support is purely additive: one `ScenarioAction`
subtype, one parser branch, one executor arm that records an interval instead of commanding a vehicle.
The parser's refuse-rather-than-ignore stance is what makes this safe to add — a storyboard using a
syntax the executor does not understand fails at load rather than running unannotated.

The complementary trigger, `UserDefinedValueCondition`, is unsupported likewise and is not needed for
this.

### 5.3 Companion annotation file — keyed to storyboard element names

A separate document alongside the `.xosc`, keyed by entity name and act name, both of which are already
parsed (`ScenarioModel.cs:34`, `:74`) and both of which any authoring tool lets the author set.

This is not a new idea in this project: [18 §8.6](18_Scenario_Fabrication_For_EPoL_Training.md) already
puts a compile step between authoring and execution whose job is to "attach the training metadata
`.xosc` cannot carry". This is that metadata.

Independence from the authoring tool is the obvious argument for it, and it is **weaker than it first
appears** given how storyboards are actually written (§5 preamble). A construct the canvas will not
emit cannot be authored *in the canvas*, but the canvas is a preview surface here, not the authoring
one. So this is not the channel of necessity it would be if the canvas were the only way in.

Two arguments for it survive that, and they are the ones to keep:

- **The storyboard stays portable.** A `.xosc` carrying no vendor constructs runs in any conformant
  player unchanged — including the canvas's own preview, which is the job the canvas is doing here. A
  storyboard with custom actions in it is still valid OpenSCENARIO, but a player that ignores unknown
  user-defined actions previews a scenario subtly different from the one that will execute.
- **Some of the metadata is not about the storyboard at all.** Run identity, the world digest, sweep
  bounds and the vocabulary version describe a *capture*, not a scenario, and putting them in the
  storyboard would weld one to the other. [18 §8.6](18_Scenario_Fabrication_For_EPoL_Training.md)
  already puts a compile step in the pipeline whose job is to "attach the training metadata `.xosc`
  cannot carry"; this file is that, and it exists whether or not annotations travel in it.

Its disadvantage is unchanged: two files can drift apart, and a key naming an entity or act that no
longer exists is silent unless checked. Since the compile step is validating references anyway (§7.1),
that is a cost of implementation rather than an unresolved risk.

### 5.4 Name convention on entities and acts

Encode the annotation in names the author already sets: an entity called `subj_01`, an act called
`loiter@school_lot`. Zero work anywhere; both survive any exporter.

Rejected as the primary channel. A typo produces no label and no error, which is precisely the failure
mode the parser's design goes out of its way to prevent elsewhere. It also collides with human naming —
an act genuinely named "loiter" because that is what it does would be silently reinterpreted as an
assertion.

It survives as an **optional shorthand the compiler expands**, subject to the same rule as everything
else: a name matching the convention's shape but naming a term outside the declared vocabulary is an
error, not a shrug.

### 5.5 Comparison and recommendation

| | Entity properties | Custom command action | Companion file | Name convention |
|---|---|---|---|---|
| In-standard | Yes | Yes | n/a | Yes, vacuously |
| Interval-scoped | No | **Yes** | Yes | Weakly — act granularity |
| Multi-participant | Per entity only | Yes | Yes | No |
| Writable by an author working in plain text | Yes | Yes | Yes | Yes |
| Emittable from the preview canvas | Yes | No | n/a | Yes |
| Leaves the storyboard previewable as authored | Yes | Degrades silently in a foreign player | **Yes** | Yes |
| Cost in this fork | One parser line per key | Parser branch + executor arm | Loader + resolver | Parser-side expansion |
| Fails loudly on error | Yes | Yes | Yes, if names are validated | **No** |

**Recommendation: the custom command action is the primary channel, entity properties carry static
identity and whole-life annotations, and the companion file is a supported equal-status alternative
that compiles to the same intermediate representation.** The name convention is expansion sugar only.

The reasoning is that the storyboard should be able to state the whole thing on its own — a scenario
that cannot say what it is depicting is an incomplete artifact, and the interchange format has a
sanctioned place to say it. Nothing should *depend* on the storyboard saying it, because a second
surface is already in use for preview and a third is plausible later. Both paths landing on one
compiled representation is what keeps that from becoming two implementations of the same thing.

The one asymmetry worth being deliberate about: an annotation carried as a custom action is invisible
to a foreign player, so a preview shows the motion faithfully and the annotation not at all. That is
acceptable — the annotation changes no motion, so the preview is still previewing the right thing — but
it means **the preview cannot be used to check that annotations are right**. Only the compile step can,
which is an argument for that step reporting what it resolved rather than merely failing when it
cannot.

### 5.6 The adjacent problem: which vehicle the storyboard asks for

Not an annotation question, but it lands on the same extension points and on the same authors, and
solving it separately would produce two conventions where one will do.

**The current state is a defect, not a gap.** A storyboard describes an entity by
`vehicleCategory`, and `BlueprintChooser` resolves a category to the first catalogue entry whose
identifier contains a preferred substring (`CarlaNet.Scenario/BlueprintChooser.cs:53-62`). One category
therefore maps to exactly one blueprint, so a five-car convoy is five identical cars — which
[18 §8.4](18_Scenario_Fabrication_For_EPoL_Training.md) already records as worse than arbitrary for
imagery meant to train a detector. The authoring-tool template hint names a template in that tool and
matches nothing here.

There is a standard answer, and it is the one that also produces the reference table an author needs:
**`<CatalogLocations><VehicleCatalog>` with entities referenced by `<CatalogReference
entryName="vehicle.audi.tt"/>`**. This is OpenSCENARIO's own mechanism for "which vehicles exist and
how do I ask for one". All three storyboards in `carla/Import/` emit `<CatalogLocations/>` empty, and
the parser would refuse a catalogue reference today — `ParseEntities` requires a literal `<Vehicle>`
child (`OpenScenarioParser.cs:113-115`). The change is additive and small.

Why a catalogue rather than another vendor property:

- **It is the same artifact for both audiences.** An author working in text and an author working in
  the preview canvas both need to know what vehicles exist; a catalogue file is a standard input to
  both, rather than a table invented here that only one of them can read.
- **Selection becomes semantic rather than substring matching.** `entryName` names a blueprint;
  nothing infers.
- **It closes a silent inconsistency.** The authored storyboards declare `<BoundingBox>`,
  `<Performance>` and `<Axles>` values that describe a vehicle nobody checked against the one that will
  be spawned, while the truth sidecar reports `length_m`/`width_m`/`height_m` from the **actually
  spawned** actor's bounding box (`CarlaNet.Recording/VehicleTelemetryService.cs:90,96`). The
  storyboard and the truth therefore disagree about the vehicle's size today, and nothing notices.
  Catalogue entries generated from real blueprints make those the same numbers by construction.

Two properties the catalogue must have, both consequences of what it is:

- **Generated from a running server, never hand-maintained.** The blueprint set is a property of the
  cooked content, and the executor already fetches it — `GetActorDefinitionsAsync` at
  `ScenarioExecutor.cs:86-87`, carrying the attributes truth already reads (`base_type`,
  `special_type`, `number_of_wheels`, `color`). Generation is a projection of that call.
- **Versioned and shipped with the distribution**, so a storyboard authored against one content build
  can be validated against the world it is run in, and an entry that no longer exists fails at compile
  rather than falling back to something plausible.

**This does not replace category selection, and must not.** §2.6 requires annotated entities to be
drawn from the *same* appearance pool as ambient traffic; hand-picking distinctive vehicles for the
annotated entities is precisely how appearance becomes the label. So both mechanisms coexist, with a
clear precedence: an explicit catalogue reference wins where the author has a presentational reason for
a specific vehicle, and where there is none, the category resolves to a **set** and the entry is drawn
from the run seed — which is the fix [18 §9 question 2](18_Scenario_Fabrication_For_EPoL_Training.md)
already asks for, giving appearance variety that is reproducible without touching behaviour.

The presentational need therefore arrives before the labelling one, and building the catalogue first
makes the annotation channel cheaper: the same parser work that admits a catalogue reference is the
work that admits namespaced properties on the same element.

## 6. The compiled representation

### 6.1 Records

```
AnnotationSet
  spec_version        integer; the shape of this document
  vocabulary_version  integer; the term list below
  scenario_id         joins to the sidecar's scenario_id and the manifest
  aoi_refs[]          areas of interest referenced, resolved at compile time
  instances[]         PatternInstance

PatternInstance
  id                  stable; assigned by the compiler from the scenario and the authored name
  supervision         annotated | nominal
  labels[]            terms from the vocabulary; at least one when annotated
  parameters{}        the swept values that produced this instance
  aoi_refs[]          zero or more area ids the pattern is defined against
  participants[]      { entity_id, role }
  intervals[]         Interval

Interval
  participant         entity_id
  phase               free term within the instance — approach, dwell, depart, lap
  issued_start_tick / issued_end_tick        the tick the action fired (§2.4)
  committed_start_tick / committed_end_tick  the tick the ramp finished and the phase was entered
  observed_start_tick / observed_end_tick    the tick the physical predicate first held; absent for a
                                             phase that has no physical predicate
  closed_by           trigger | scenario_end | entity_removed | aborted
```

`closed_by` is what makes pattern class 3 (§3) recordable: an orbit truncated when the run ended is
distinguishable from one that completed, and a truncated interval is a different training example from
a completed one.

### 6.2 A declared vocabulary, versioned, extensible by declaration

Labels come from a term list carried with the annotation set rather than invented per scenario, for the
same reason the parser refuses unknown constructs: a corpus assembled from scenarios that each spelled
`loiter` differently is not a corpus. New terms are added by declaring them, which is an edit to a file
rather than a code change, and `vocabulary_version` lets a consumer refuse a corpus it does not
understand.

Two things stay *out* of the vocabulary and in `parameters` instead: magnitudes (a dwell duration, an
approach speed) and places (an area id). Encoding them into terms — `loiter_45min_near_school` —
produces an unbounded term list and makes stratification impossible.

### 6.3 Three identifiers, three jobs

| Identifier | Answers | Lifetime |
|---|---|---|
| `actor_id` | Which simulated object is this, right now | One run; already emitted |
| `entity_id` | Which authored entity is this, in any run of this scenario | Across runs of a scenario |
| `instance_id` | Which occurrence of a pattern is this | Across runs, and across participants of one phenomenon |

All three belong in the truth record. Emitting only the first is the current state; emitting only the
second would break intra-run association with the detector; the third is what makes multi-participant
and multi-interval patterns reassemblable.

## 7. Carrying it through the system

### 7.1 Compile

The compile step of [18 §8.6](18_Scenario_Fabrication_For_EPoL_Training.md) gains a job: read the
annotations from whichever channel carried them, resolve area references against the world actually
loaded, assign instance ids deterministically, validate every reference (unknown entity, unknown area,
unknown vocabulary term — all errors), and hand the executor an `AnnotationSet` beside the
`ScenarioDefinition`.

Determinism of instance ids matters: a sweep produces many runs of one scenario and they must be
joinable. Deriving the id from the scenario id and the authored instance name, with no counter and no
timestamp, achieves that.

### 7.2 Spawn-time identity

`entity_id`, `instance_id` and the participant role are static per entity, so they go on the spawn
description as custom attributes, which §4.5 established are accepted unvalidated, visible to the truth
producer, and preserved in the recorder log. `BlueprintChooser.Describe` already assembles that list
(`BlueprintChooser.cs:33-39`) and is the natural place.

`VehicleTelemetryService` reads attributes by name already (`VehicleTelemetryService.cs:85-90,170-174`),
so surfacing them costs three lines and two fields on `VehicleTelemetry`.

`role_name` stays a provenance field and gains one convention: scenario-placed vehicles are marked as
such, ambient traffic keeps `autopilot`. Neither `hero` nor `ego` is used for any purpose in this
document (§4.3).

### 7.3 The dynamic part: an annotation registry

Phase, current instances and interval state change per tick and cannot ride on an immutable attribute.
They need a table the executor writes and the truth producer reads, keyed by actor id and stamped with
the tick it describes.

Placement is constrained by the assembly graph. `CarlaNet.Scenario` references `Transport`, `Map`,
`TrafficManager` and `Types`; `CarlaNet.Recording` references `Types`, `Transport` and `Sensors`. The
only common ancestor is **`CarlaNet.Types`, which references nothing** — so the record types and the
registry belong there, and both sides reference it without a cycle.

Three properties the registry needs, each for a reason already observed in this project:

- **Tick-stamped, not "current".** The recorder's worker threads encode captures asynchronously
  (`FrameRecorder.cs:151-178`) while the world keeps ticking; a registry read at write time would
  annotate a frame with a later state. The annotation must be captured into the job alongside the
  telemetry at `FrameRecorder.cs:146`, exactly as `CaptureIdentity` already is.
- **Lock-free reads.** It is read on the sensor-stream thread once per capture, and written from the
  tick thread (`ScenarioExecutor.OnTick`, `:186-212`). A snapshot-swap of an immutable dictionary is
  sufficient and matches how the world-observer cache is already consumed.
- **Empty is a valid state, and means `unlabelled`.** No scenario running must produce `unlabelled` on
  every vehicle, not a missing element (§2.2).

The registry is process-local. That is correct for SCTMV, which runs the executor and the recorder in
one process (§4.1), and it is sufficient for a single collection camera — which is all the recorder
supports today, since it binds one stream token (`FrameRecorder.cs:59-98`) and the shim holds one
recorder per client (`carlanet/__init__.py:1824`).

**It does not extend to multiple collection cameras, and that limit is closer than it looks.** CARLA
is multi-client and several cameras can observe one scene at once; adding one today means adding a
client process, and a recorder in another process reads an empty registry and writes `unlabelled` on
every vehicle. Nothing errors — the second camera's captures simply arrive unsupervised, and look
exactly like a run in which no scenario was active.

Two ways out, and the choice should be made before multi-camera capture rather than after: publish the
annotation state to the server the way staging bounds are (§8.4), so any client can read it; or keep
the registry and require every recorder to live in the process running the executor. The first is more
work and removes the constraint; the second is free and must then be enforced, because the failure mode
is silent. Recorded as decision 11.

### 7.4 The sidecar

Two new `<detail>` children, kept separate precisely so §2.1's boundary is visible in the file:

```xml
<detail>
  <track course="271.8" speed="0.00"/>
  <contact callsign="car-412"/>
  <_carla source="truth" actor_id="412" type_id="vehicle.audi.a2" base_type="car"
          … role_name="scenario" entity_id="subj_01" provenance="scenario"/>

  <!-- asserted by the author; never derived -->
  <_supervision state="annotated" vocabulary="1">
    <annotation instance="pi_loiter_a" label="loiter" phase="dwell" role="subject"
                aoi="school_lot"
                issued_start_tick="10420" committed_start_tick="10460"
                observed_start_tick="10502"/>
  </_supervision>

  <!-- computed identically for every vehicle, including ambient; not a label -->
  <_aoi>
    <relation id="school_lot" state="inside" range_m="0.00" continuous_s="812.4"/>
  </_aoi>
</detail>
```

Notes on the shape:

- `state` is always written. `annotated` carries one or more `<annotation>` children (multi-label,
  §3); `nominal` carries none and is itself the assertion; `unlabelled` carries none and asserts
  nothing.
- `entity_id` and `provenance` go on `_carla` because they are properties of the object, not of an
  assertion about it. `_carla` is the established home for object extras
  ([09 §5](09_Telemetry_CoT_Contract.md)).
- `<_aoi>` is emitted for every vehicle. With M areas and N vehicles this grows as M·N, so relations
  should be limited to areas containing the vehicle plus those within a configured radius, with the
  nearest always present — otherwise a map with fifty areas triples the sidecar for no information.
- **The CoT `type` affiliation is not touched.** [09 §8](09_Telemetry_CoT_Contract.md) sets truth to
  `a-n-G-E-V` with per-vehicle override supported, and marking annotated vehicles hostile is an
  available and tempting shortcut. It should be refused: affiliation is a display and threat semantic,
  a detector-derived track has no way to produce it, and encoding a training label there breaks the
  truth-versus-detection comparison that the identical-shape contract exists for. A viewer may colour
  annotated tracks differently from the `_supervision` element; the emitted affiliation stays neutral.

The live UDP feed (`SCTMV.py:1235-1280`) should carry the same elements, on the same footing as
`_capture` there — **diagnostic only**, so an operator watching the map can see which vehicle is the
subject. The recorded sidecar remains the sole truth source.

For the PNG, `carla:capture` already makes a still self-describing when separated from its sidecar
(`CarlaNet.Recording/CaptureMetadata.cs:31-36`). A compact `carla:supervision` chunk listing annotated
actor ids and labels would extend that property to the annotation. It duplicates the sidecar, which is
the argument against; the argument for is that the duplication is exactly what makes an orphaned still
usable. Recommended, with the sidecar authoritative on any disagreement.

### 7.5 The run manifest

The per-frame sidecar is a projection and cannot express the instance form of §2.3, cannot record an
interval that has not closed yet, and does not exist for ticks that were not captured. So the
authoritative supervision artifact is a **manifest written once per run** beside the captures:

- run identity — `run_id`, `scenario_id`, `seed`, `spec_version`, `vocabulary_version`, fixed step
- the world binding of [18 §5.5](18_Scenario_Fabrication_For_EPoL_Training.md) — the `.xodr` digest
- the resolved area-of-interest table (§8), so the manifest is self-contained
- every `PatternInstance` with its participants and closed intervals
- per interval: the participant's actor id in this run, and its observed span (§2.5) — both the union
  across collection sensors and the per-sensor breakdown, since tracks are produced per sensor
- run-level prevalence: annotated vehicle-seconds against total observed vehicle-seconds (§2.6)

It must be written incrementally and closed at scenario end, not held in memory until then; a run that
crashes at minute forty of forty-five otherwise loses its supervision entirely while keeping every
capture.

### 7.6 Transfer to detector tracks

Worth stating because it constrains the format and is easy to leave until it is expensive. The EPoL
model consumes **detector** tracks, which carry detector track ids and are associated to truth by
position and time, never by uid — [09 §9](09_Telemetry_CoT_Contract.md) already fixes that. So the
post-process must transfer supervision from truth tracks onto detector tracks, and:

- one truth entity can map to several detector tracks (identity switches, re-acquisitions), so the
  exported supervision is per (detector track, interval), not per entity;
- **with several collection cameras there are several detectors**, each producing its own tracks over
  the same world, so one truth entity maps to a track set *per sensor* and supervision is transferred
  once per sensor. A single truth interval can therefore yield several supervised spans that overlap
  in time and differ in coverage — which is correct, and is why §2.5 wants the per-sensor breakdown
  and not only the union;
- a detector track that spans an interval boundary must be clipped, not labelled wholesale;
- the association quality per assignment should be recorded, so a mis-associated label is findable
  later rather than being an unexplained hard example.

Whether the model is fed per-sensor tracks or tracks fused across sensors is a modelling choice that
sits outside this document, but it changes what "observed" means — union under fusion, per-sensor
without it — so the manifest records both and lets the choice be made downstream rather than baked in
at capture time.

None of this requires anything of the simulator beyond the manifest and sidecar above. It does require
the manifest to be the source, because a detector track can only be clipped against interval bounds
that exist in interval form.

### 7.7 Replay

During a replay no executor runs, so the registry is empty and every vehicle reports `unlabelled`. Two
things salvage it, and they are why §7.2 puts identity on a spawn attribute:

- Replayed actors are respawned from the log's actor-add event, which preserved every spawn attribute
  (§4.5), so `entity_id` and the static role come back intact and appear in the sidecar as usual.
- The manifest from the original run supplies the intervals, joined by `entity_id` and normalised tick.
  [18 §6.1](18_Scenario_Fabrication_For_EPoL_Training.md) already establishes that ticks are
  episode-scoped and that comparing runs requires normalising against each run's first tick; the same
  normalisation applies here.

So a replayed capture is fully supervised provided its manifest travels with it, which is one more
reason the manifest is the authoritative artifact rather than a convenience.

## 8. Areas of interest

### 8.1 Whether they are actually needed

They are not needed to *carry* an annotation — an annotation is authored intent and stands alone. They
are needed for three other things, and the third is the one that changes what the corpus can be.

1. **Portable authoring.** Half the patterns in §3 are defined relative to a place. Authored as lane
   positions they are welded to one build's road numbering, which
   [18 §2.3](18_Scenario_Fabrication_For_EPoL_Training.md) already identifies as the portability
   failure. A named area is stable across rebuilds; positions derived from it are re-resolved.
2. **Checkable annotations.** "Circling `school_lot`" is unverifiable unless `school_lot` exists as
   truth. Without it, the annotation names a place only the author can see.
3. **A usable negative set, at no authoring cost.** Area relations are computed for *every* vehicle,
   scripted and ambient alike (§2.1). That turns the whole ambient population into stratifiable
   data — which vehicles passed the site, how close, how long they stayed — without annotating a single
   one of them. It is also the mechanism for auditing the ambient set for the accidental positives of
   §2.2: a `unlabelled` vehicle whose derived relations look exactly like an annotated pattern is
   exactly the record a human should review before it trains as a negative.

Verdict: **adopt, sequenced after the annotation channel.** The annotation channel is what removes the
deficiency; areas are what let it scale past hand-sited scenarios.

### 8.2 Format and supply

A sidecar beside the OSM extract, discovered by name (`<extract>.aoi.geojson`) and overridable by
argument, mirroring how the extract itself is supplied (`SCTMV.py:225`).

**GeoJSON (RFC 7946)** is the right format and not a close call: it is what every GIS tool writes, it
is already lat/lon so it is portable across rebuilds by construction, and the standard mandates WGS84 —
which is the datum this project locked end-to-end. The one trap to write down loudly is that GeoJSON
positions are **`[longitude, latitude]`**, the opposite order from every internal signature here
(`Geodesy.GeodeticToCarlaLocal` takes a `GeoLocation(lat, lon, alt)`,
`CarlaNet.Types/Geom/Geodesy.cs:104`).

Both shapes the requirement calls for are expressible, and both are wanted:

| Shape | Encoding | Use |
|---|---|---|
| Polygon | `Polygon` / `MultiPolygon` geometry | A car park, a block, a compound — anything whose extent matters |
| Centre and radius | `Point` geometry with `properties.radius_m` | A standoff ring, a site whose extent is unknown. A circle is not a GeoJSON primitive, so this is the conventional encoding rather than an invention |

Per-feature properties: `id` (stable, referenced by scenarios and annotations), `name` (human), and
optionally `kind` (a declared term, for stratification). Heights are deliberately absent — these are
ground footprints and vehicles are on the ground; adding a vertical extent would need reconciling with
the drape for no current benefit.

### 8.3 Validation at build time

Validation belongs at world build, where the OSM bounds, the sandbox rectangle and the road network are
all in hand, and where a failure costs one build rather than one training run. Each check below has a
concrete failure it prevents.

| Check | Response | Prevents |
|---|---|---|
| Ids unique, non-empty, no whitespace or case-only collisions | Refuse | An annotation silently referencing the wrong area |
| Polygon rings closed, ≥ 4 positions, non-self-intersecting, positive area; `radius_m > 0` | Refuse | Containment tests that are undefined rather than false |
| Envelope intersects the OSM `<bounds>` | Refuse if disjoint | An area outside the world entirely — the single most likely authoring mistake |
| Wholly inside the sandbox rectangle | Warn if it crosses the edge | An area half outside the simulated world, whose derived relations are meaningless for the missing half |
| Not wholly inside the staging ring | Warn | Traffic enters and exits through the ring (`SCTMV.py:555-574`); a pattern sited there is competing with spawn and despawn |
| Some drivable road within, or within a small radius of, the area | Warn | An area no vehicle can reach, which produces zero relations and reads as a broken pipeline |

The last two are warnings rather than refusals because both are legitimate in principle — an area
deliberately placed off the road network to site an off-road dwell is exactly what
[18 §4.5](18_Scenario_Fabrication_For_EPoL_Training.md) contemplates.

### 8.4 Where they live at runtime

The user's expectation that the plugin and the engine must know about areas is right, and there is a
precedent to copy exactly rather than a design to invent. Staging bounds are held on a dedicated,
geometry-free, non-ticking actor with a blueprint-library accessor
(`Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/Source/CesiumCarlaBridge/Public/StagingBounds.h:20-65`),
written and read by a pair of RPCs bound side by side (`Server/CarlaServer.cpp:727-760`), and surfaced
on the shim (`carlanet/__init__.py:1562`, `:1569`). Areas of interest are the same kind of object —
world-scoped metadata produced at build time, needed by any client, and required to outlive the client
that built the world — and should be the same kind of thing: an areas-of-interest actor, a `Set`/`Get`
library, `set_areas_of_interest` / `get_areas_of_interest` beside the staging-bounds pair, and shim
wrappers.

One decision within that: what crosses the RPC. Sending the GeoJSON verbatim would put a JSON parser
and a second geodesy implementation in the engine, and divergence between two implementations of the
same coordinate transform is a failure this project has already paid for. Sending **ids plus geometry
already resolved to CARLA-local metres** by `Geodesy.GeodeticToCarlaLocal` keeps one implementation and
gives the engine coordinates it can draw directly; the source geographic definition rides along as an
opaque string for provenance.

The engine's own use is modest and worth having: a debug draw so an operator can see the areas in the
viewport, which is how a mis-sited area gets noticed at all.

Areas should additionally be written into the generated `.xodr` under `<header><userData>`, where
[18 §5.5](18_Scenario_Fabrication_For_EPoL_Training.md) already plans to put the build recipe — the
world then carries its own area definitions and they cannot be separated from it. That has a
consequence for the world-binding gate which should be decided rather than discovered: an area edit
changes the file digest while changing no road geometry, so a recording made before the edit is still
faithfully replayable. The gate should therefore hold the area block under its own digest and treat an
area-only difference as a warning, not the refusal a recipe mismatch earns.

Neither the recipe nor the `userData` writer exists yet — a search of `CarlaNet/src` finds no
`<userData>` emitter — so this is a new capability in both cases and they should be built together.

### 8.5 The boundary, restated where it will be forgotten

Areas make derivation easy, and §2.1's rule is the thing most likely to erode once they exist. The rule
in operational terms:

- `<_supervision>` is written **only** from an authored annotation. No geometric predicate, no
  threshold, ever writes into it.
- `<_aoi>` is written **only** by computation, for every vehicle, with no reference to any annotation.

A consumer must be able to discard `<_aoi>` entirely and still have complete supervision. If that ever
stops being true, the labels have become the rule and the corpus has stopped being useful.

### 8.6 Cost

Bounded, and mostly outside the engine: a GeoJSON reader and validator in the build path, the local
resolution, the actor and RPC pair, the shim wrappers, a per-tick relation computation in the truth
producer (a point-in-polygon and a distance per vehicle per area, at capture rate — nothing), the
`userData` block, and a debug draw. The one open question with real weight is §8.4's digest tiering,
because it interacts with a contract that is itself still unbuilt.

## 9. Work ledger

Tier 1 removes the deficiency; tiers 2 and 3 make it scale and make it portable.

| Tier | Work | Where |
|---|---|---|
| 1 | Annotation record types and the tick-stamped registry | C# — new file in `CarlaNet.Types` (the only assembly both sides can reference, §7.3) |
| 1 | `CustomCommandAction` parse and an executor arm that opens and closes intervals | C# — `CarlaNet.Scenario/OpenScenarioParser.cs` (`ParseActions`, `:226-261`), `ScenarioModel.cs`, `ScenarioExecutor.cs` (`Apply`, `:336-348`) |
| 1 | Entity properties for `entity_id`, role and whole-life annotations | C# — `OpenScenarioParser.cs:146-147` pattern, `ScenarioModel.cs:32-55` |
| 1 | Custom spawn attributes carrying static identity | C# — `BlueprintChooser.cs:33-39`; no engine change (§4.5) |
| 1 | Surface those attributes in the truth record | C# — `VehicleTelemetry.cs`, `VehicleTelemetryService.cs:85-96` |
| 1 | `<_supervision>` in the sidecar; annotation snapshot captured into the job beside `CaptureIdentity` | C# — `CotWriter.cs:149-176`, `FrameRecorder.cs:142-146` |
| 1 | Run manifest, written incrementally, closed at scenario end | C# — `CarlaNet.Recording` or a peer |
| 1 | **Pass `scenario_id` to the recorder** — accepted and written but never supplied (§4.2) | Python — `SCTMV.py:1472-1479` |
| 1 | **Vehicle catalogue generated from a running server**, in the OpenSCENARIO catalogue format, versioned and shipped with the distribution (§5.6) — the presentational need arrives before the labelling one, and it is the reference table both an assisted and a manual author work from | Tooling, over `GetActorDefinitionsAsync` |
| 2 | `CatalogReference` accepted as an alternative to a literal `<Vehicle>`, resolved against the loaded world, unknown entry an error (§5.6) | C# — `OpenScenarioParser.cs:113-115`, `BlueprintChooser.cs` |
| 2 | Companion annotation file: schema, loader, name resolution against the parsed storyboard, validation | Tooling + C# loader |
| 2 | Vocabulary declaration and versioning; compile-time validation of every reference | Tooling |
| 2 | Blueprint selection from a per-category **set** drawn on the run seed, when no catalogue reference is given — the appearance confounder of §2.6, and already open as [18 §9 question 2](18_Scenario_Fabrication_For_EPoL_Training.md) | C# — `BlueprintChooser.cs:43-63` |
| 2 | Compile step reports what it resolved — entity, area and catalogue references, and the roads a street name resolved to — since a preview cannot check any of it (§5.5) | Tooling |
| 2 | Observed span per interval, per sensor and unioned, from the sensor pose and intrinsics already in the sidecar (§2.5) | Post-process, or `CarlaNet.Recording` |
| 2 | Make the annotation state reachable by a recorder in another process, so a second camera on a second client is not silently unsupervised (§2.5, §7.3, decision 11) | C# — publish alongside the world's staging bounds, or an equivalent server-held record |
| 2 | `<_supervision>` and `<_aoi>` on the live feed, diagnostic only | Python — `SCTMV.py:1235-1280` |
| 2 | **Run-level switch over idle-cull behaviour, separating detection from destruction** (§2.8), plumbed as a launch option so the choice is made per capture. Follows the existing dead-end-removal parameter path exactly | C# — `Parameters.cs`, `Stages/ALSM.cs:192-203`, `ITrafficManagerCallback.cs`, `TrafficManager.cs`, Python shim; flag in `SCTMV.py` |
| 2 | Surface retained stuck-detections as derived context, so a vehicle stationary far past the threshold with no authored reason is auditable (§2.2, §2.8) | C# — `VehicleTelemetryService.cs`, alongside `<_aoi>` |
| 3 | Area-of-interest file: reader, validator, local resolution | Python/C# in the build path |
| 3 | Areas-of-interest actor, `Set`/`Get` library, RPC pair, shim wrappers, debug draw | C++ — mirroring `StagingBounds.h` and `CarlaServer.cpp:727-760` |
| 3 | `<_aoi>` derived relations in the truth producer, with the emission cap of §7.4 | C# — `VehicleTelemetryService.cs` |
| 3 | Areas into `<header><userData>`, with the build recipe; digest tiering | C# — `CarlaNet.Map/OpenDrive` |
| 3 | Repeating or lap-counted routes, so an orbit does not decay into random successors (§3, class 3) | C# — `ScenarioExecutor.cs` route handling |
| 3 | `GeoPosition` and area-relative positions, closing the map-portability gap | C# — parser + `RoadNetwork` |
| — | Engine, for tiers 1 and 2 | **No changes required** |

## 10. Decisions recorded

Numbered for reference within this document; they do not extend
[18 §7](18_Scenario_Fabrication_For_EPoL_Training.md)'s list.

| # | Decision |
|---|---|
| 1 | **Scenario membership is not an annotation.** A scenario-placed vehicle is `unlabelled` unless annotated. Deliberately ordinary scripted vehicles are a first-class requirement, not an edge case (§1, §2.7) |
| 2 | **Supervision is three-valued** — `annotated`, `nominal`, `unlabelled` — and the state is always written explicitly. Absence of an element is not a negative (§2.2) |
| 3 | **Annotations are authored intent; area relations are derived context.** They live in separate sidecar elements and no geometric predicate ever writes an annotation (§2.1, §8.5) |
| 4 | **The unit of supervision is a pattern instance** with participants and intervals, not a per-vehicle flag. The sidecar carries a per-tick projection; the manifest carries the instance form and is authoritative (§2.3, §7.5) |
| 5 | **All three interval onsets are recorded** — action issued, phase committed, physical predicate observed. They are separated by the authored ramp duration, so the gap is a property of the scenario rather than a constant that could be calibrated away, and which one defines the interval is the trainer's choice (§2.4) |
| 6 | **The primary in-file channel is `UserDefinedAction`/`CustomCommandAction`**, with entity `<Properties>` for static identity, and a companion file as an equal-status alternative compiling to the same representation. Name conventions are expansion sugar and never the sole carrier (§5.5) |
| 7 | **Static identity travels as a custom spawn attribute**, because the engine stores it unvalidated and the recorder preserves it, so it survives record and replay where an in-process registry cannot (§4.5, §7.2, §7.7) |
| 8 | **`role_name` stays a provenance field.** `hero` and `ego` are not used to mean anything about annotation; they carry Traffic Manager, ROS2 and replayer behaviour (§4.3) |
| 9 | **The CoT affiliation is not overloaded.** Annotated vehicles stay `a-n-G-E-V`; display distinction is a viewer concern driven by `<_supervision>` (§7.4) |
| 10 | **Areas of interest are adopted**, supplied as GeoJSON beside the OSM, validated at build, and held in the world on a dedicated actor with a `Set`/`Get` RPC pair in the manner of staging bounds. Both polygons and centre-with-radius are supported (§8) |
| 11 | **The annotation registry is process-local**, which suits SCTMV where the executor and recorder share a process — **but this does not survive multiple collection cameras.** Additional cameras mean additional client processes, and a recorder in another process sees an empty registry and reports every vehicle `unlabelled`, silently. So either the annotation state is published to the server the way staging bounds are, or multi-camera capture is restricted to one process. This is a decision to take before multi-camera capture, not after (§2.5, §7.3) |
| 12 | **Vehicle selection is expressed as an OpenSCENARIO catalogue reference where the author has a presentational reason, and as a category resolved to a set on the run seed where they do not.** The catalogue is generated from a running server and versioned with the distribution; hand-picking distinctive vehicles for annotated entities is the appearance confounder of §2.6 and stays prohibited (§5.6) |
| 13 | **Text authoring is the format's primary surface, and the graphical canvas is a preview surface.** No construct is chosen or rejected on the grounds that the canvas cannot emit it; hand authoring stays possible throughout, which means every convention has to be documented and validated rather than merely implemented (§5) |
| 14 | **Idle-cull behaviour must be switchable per capture, and detection must remain available when destruction is suppressed.** The cull truncates the ambient stationary distribution at ninety seconds, fires at an irreproducible simulated moment, and is simultaneously the only mechanism that clears a queue — so neither leaving it on nor switching it off wholesale is right for a capture. This amends [18 D6](18_Scenario_Fabrication_For_EPoL_Training.md), which considered only the per-vehicle case (§2.8) |

## 11. Open questions

1. **Does the annotated interval survive contact with the detector?** Everything above assumes an
   annotated interval yields a usable detector track. Unmeasured. The first thing to run once tier 1
   exists is one authored dwell plus ambient traffic, captured, detected, and the supervision
   transferred — and then a count of how many annotated frames produced a track at all. §2.5's
   in-frustum span is the cheap proxy; the real number needs the detector.
2. **What the vocabulary should contain at v1.** The ten classes of §3 are a design test, not a
   proposed term list. The list should be settled with the model's requirements in hand, since terms
   are cheap to add and expensive to rename once a corpus exists.
3. **Interval semantics for a pattern with no natural end** (class 3). `closed_by` records *that* an
   orbit was truncated; whether a truncated instance is a usable training example, or must be discarded,
   is a modelling question.
4. **Whether the derived area relations should be emitted per frame at all**, or computed downstream
   from positions the sidecar already carries. Emitting them costs sidecar size and duplicates
   derivable state; not emitting them means every consumer re-implements containment against the area
   table. Leaning towards emitting, with the cap of §7.4, because a consumer disagreeing with the
   simulator about which vehicles were inside an area is a worse failure than a larger file.
5. **How an accidental positive in the ambient population should be handled once found** (§2.2) —
   excluded from the corpus, or promoted to `annotated` with a provenance marker distinguishing it from
   an authored one. The second is more valuable and more dangerous. Note this question gets *larger*
   the moment the idle cull is relaxed (§2.8), so it is not safely deferrable past that change.
6. **Digest tiering for area edits** (§8.4). Interacts with the world-binding contract of
   [18 §5.5](18_Scenario_Fabrication_For_EPoL_Training.md), which is itself unbuilt, so both should be
   decided together rather than one constraining the other by accident.
7. **Whether counterfactual pairing is worth building into the sweep** — the same seed, the same
   ambient population, one instance's annotation and behaviour switched off — which isolates the
   authored behaviour as the only difference between two runs. Cheap here, because the seed and the
   parameters are already run inputs, and it is the strongest validation signal available for an EPoL
   detector. Not required by anything above, which is why it is a question rather than a decision.
8. **How a movement through a junction is named.** Street names resolve to roads (§5), but junction
   internals carry the converter's edge identifier, so an instruction phrased as a turn at an
   intersection has no name to resolve against and must be expressed as the roads either side. Whether
   that is adequate for authoring, or whether junctions want stable derived names of their own — which
   would have to survive a rebuild to be worth anything — is unsettled.
9. **Whether the authoring conventions should ship as a packaged skill with the distribution.** The
   inputs an author needs are, by the end of this document, all machine-readable artifacts of a build:
   the vehicle catalogue (§5.6), the annotation vocabulary (§6.2), the area table (§8), the street-name
   to road mapping (§5), and the world digest that binds them (§7.5). Packaging them with the
   conventions that use them would make assisted authoring reproducible rather than dependent on what a
   given author happens to know — and the same package is what a human authoring by hand needs to read.
   The open part is scope and where it lives, not whether the artifacts are worth having, since every
   one of them is already earned by something above.
10. **What a capture should do when the idle cull is suppressed and a queue forms anyway** (§2.8).
    Retained detection tells you it happened; it does not say whether the affected span of the run is
    still usable, whether the jammed vehicles should be excluded from the ambient population rather
    than counted as ordinary behaviour, or whether some bounded intervention — clearing only vehicles
    idle far beyond any plausible authored stop — is preferable to a plain on/off. That last option is
    effectively a third cull policy, and it should be decided deliberately rather than invented during
    implementation.
