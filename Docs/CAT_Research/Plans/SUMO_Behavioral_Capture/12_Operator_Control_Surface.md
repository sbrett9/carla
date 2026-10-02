# 12 — The operator control surface

**Status:** Plan section. Specification of a control surface. Built: `run_capture` and every class in
§3.9 but `WorldBuildConfiguration`, with the source-tree launchers of §10 on both platforms; §3.9, §5.2
and §6.2.1 say what each built piece does and what it has no source for. The distribution launchers
are not built. Every measurement in §1 was taken read-only by introspecting
the live parser object and grepping the live source tree on 2026-09-18; the further measurements in
§3.5, §3.10.1, §3.10.2, §5.2 and §7.6 were taken the same way, and each says where.
**Date:** 2026-09-18
**Revisions:**
`2026-10-01` — §9.6: during a drive any process's truth -- `world.get_vehicle_telemetry()`, the CoT feed, a recorder started outside the drive -- lists only the bodies a frame drew, by SUMO vehicle, from the render set the server carries on each snapshot; the drive's report prints how many changes it named to the server, or the server's refusal.
`2026-10-01` — §7.1 `population / rendered`: a vehicle SUMO has just inserted is drawn from the frame SUMO first reports it in, one SUMO step after the pass that admits it, never before.
`2026-09-30` — The render cap (128, hard 192) was never measured — M2 never ran — and the scenario is the arbiter of population: every vehicle SUMO has is drawn, and a heavier scenario runs slower, never thinner. Removed with it: the settings `capture.render_region`, `capture.render_hysteresis_m`, `capture.render_cap`, `capture.render_cap_hard`, `capture.render_set`, `capture.render_min_pixels`, `capture.render_admit_lead_s` and `capture.render_release_lag_s` (§5.2); `run_sumo_drive.py`'s render-set, region and capacity options and the free view's region coverage (§9.6); checks 20, 21 and 33, withdrawn with their numbers retired, and the warning `render_cap_bound_at_window_open`; the rendered-fraction floor and its loud condition (§7.1, §7.2); D12.20, withdrawn. A stare aimed at the traffic starts over the centre of the world's staging bounds.
`2026-09-30` — Measured on Bahonar, the picture witness failed with traffic in view: it now leaves out the blocks rendered vehicles cover, needs half the view judged, and is counted in ticks, ten apart with a 120-tick ceiling (check 50); a stare aimed at the traffic holds for one SUMO step and 120 ticks.
`2026-09-30` — `run_capture` waits for every channel's view inside the prewarm — tiles in, picture settled (checks 50 and 51, D12.38); an orbit holds its opening pose until the window opens, and a stare aimed at the traffic holds for the last 120 of its frames.
`2026-09-30` — The render set follows the cameras: `capture.render_set` (`cameras` by default, or `circle`) and its three settings (§5.2); `run_sumo_drive.py --render-set` and the free view followed wherever it flies (§9.6); check 33 names what was eligible.
`2026-09-30` — §9.6 corrected: a camera image's header carried the next frame's pose; the server stamps it at capture, and a capture's pose is its frame's snapshot's, the header checked and counted (§7.2 gates).
`2026-09-30` — `run_sumo_drive.py --view free`: a camera flown inside the drive, recording spans with the session's render set once its tiles are in (§9.6).
`2026-09-28` — Staged refusals, the window's own instant, live admission passes and check 33 read; stare aimed at rendered traffic.
`2026-09-28` — `run_capture` built: layered resolution, validation, echo, capture, result, termination, monitor, both launchers.
`2026-09-25` — Stare pose and orbit centre fields; `ChannelDescription` and the camera follower built.
`2026-09-18` — Termination under external kill made first class; aggregate verdict removed; in-run observability stated.
`2026-09-18` — Unattended caller, live-exercise pacing and transcript, exposure-profile correction.
`2026-09-18` — Initial: measured the present surface; layered resolution, validation phases, time-of-day expression.
**Owner role:** Operator control-surface engineer. Added to the team by
[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a item 3, which makes the surface a first-class deliverable
rather than a by-product.
**Scope:** How a caller expresses, validates, launches, watches, stops and records a capture run; the
complete inventory of what is switchable and what is not; what the tool does when it is killed; how the
new surface coexists with `run_SCTMV.py` without losing a capability; and how the time-of-day choice
reaches the record.
**There are two callers, and both are first class:** a human at a terminal, and an **external
process that starts us, watches us through interfaces that already exist, and stops us when it
decides to** ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d). That second caller is not a scheduler waiting
for a verdict. It may kill the CARLA server, the SUMO process or this client at any instant,
deliberately, and it decides when enough is enough by **querying while we run**. §3.10, §6.4 and §7.6
are that caller's half; everything else serves both unless it says otherwise. The surface also
carries the **live exercise**'s pacing and live picture (§7.4).
**Audience:** An engineer implementing the launcher and the run-configuration schema, and a reviewer
deciding whether the shape recommended in §3 is the right one. It assumes the fork but not the
conversation that produced this plan.
**Grounding:** Claims about existing behaviour cite `path:line` against the working tree as read on
2026-09-18. Numbers labelled **measured** were produced by a script run during this pass; the scripts
are described where they are used so they can be re-run. Anything labelled **inference** is reasoning.

**Out of scope, deliberately.** The semantics of the solar epoch and of the freeze/advance policy —
those belong to [`11_Time_And_Illumination.md`](11_Time_And_Illumination.md), whose author owns them;
this section owns only how an operator *expresses* and *records* the choice, and states in §4.6 what
it needs from that section. Also out of scope: the scenario specification's own schema
([`07`](07_Scenario_Authoring.md)), the manifest's supervision content
([`06`](06_Truth_And_Annotation.md) §8.4), the sensor rig's optics ([`08`](08_Collection_And_EPoL.md)
§3), and the parameter *values* in [`10`](10_Scale_And_Performance.md) §8 — this section decides where
each value is set and how it is recorded, not what it should be.

**Three more things are out of scope and are named because the live and unattended cases invite them
in.** The **pacing ruling** — what a live run does when the external chain cannot keep up — is
[`08`](08_Collection_And_EPoL.md) §11.1 and §11.3's; this section owns how the choice is *expressed*
and what is *shown*, and §7.5 states the five properties it needs back. **The cadence itself** — what
decides that a corpus should be regenerated, what trains on it afterwards, and **when a run should
stop** — is entirely outside this plan. §3d puts termination in the caller's hands, so this section
specifies what the tool does *when it is stopped*, never when it should stop; there is no scheduler
here, and §3.10 designs an invocation, not a loop. **The verdict is outside too:** this surface
publishes facts about its own data and never an aggregate judgement of whether a corpus is fit for a
purpose it does not know; [`04`](04_Contracts.md) `C10` owns the result artifact's fields on the same
principle (§3.10.3, §7.2). And **the external
detect-and-track and model services**: this section specifies a socket a run writes to and a blob store
a transcript lands in, and nothing about what is on the other end
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c).

---

## 1. The present surface, measured

The user's words were *"we're building a tool suite that is going to have to have some easy way to
control it all."* Before proposing anything, this section measures what exists. The measurement is the
argument; nothing below rests on taste.

### 1.1 Method

`CarlaControlArgumentParser` (`CarlaControl/src/carlacontrol/CarlaControlArgumentParser.py:12`) is a
self-contained module — it imports only `argparse`, `os` and `random` (`:7-9`) — so it can be loaded
in isolation and its `argparse.ArgumentParser` walked directly. Three read-only scripts were run:

1. **Surface census** — load the parser, enumerate `_action_groups` and `_group_actions`, and count.
2. **Consumer trace** — for every `dest`, search the live Python sources
   (`CarlaControl/src/carlacontrol`, `CarlaControl/scripts`, `CarlaNet/python/carlanet`; build output
   excluded) for `args.<dest>`, `getattr(args, "<dest>", …)`, `self.args.<dest>` and dictionary forms.
3. **Duplicated-default scan** — find every `getattr(args, "<dest>", <fallback>)` outside the parser
   and compare `<fallback>` against the parser's own `default=`.

### 1.2 The shape

| | Measured |
|---|---|
| Arguments defined (excluding `-h`) | **86** |
| Distinct option strings | **86** — no argument has a short form or an alias, except the `--fade` / `--no-fade` pair |
| Distinct `dest` values | **85** — `--fade` and `--no-fade` share `fade` |
| Argument groups | **9** |
| `store` actions / `store_true` / `store_false` | **70 / 12 / 4** |
| Mutually exclusive groups | **1** (`--fade` ∥ `--no-fade`, `:317`) |
| Sub-commands, modes or verbs | **0** |
| Total help text | **12,009 characters**; median 71, longest 719 (`--road-offset-east`, `:149-164`) |

Group by group, with the group titles exactly as the parser declares them:

| Group | Count | Declared at |
|---|---:|---|
| `options` (the unnamed root group) | 1 | `:492-499` |
| `connection / mode` | 5 | `:49-74` |
| `world build (phase 1)` | 23 | `:76-221` |
| `EO observer (phase 2)` | 13 | `:223-285` |
| `staging traffic (phase 3)` | 19 | `:287-449` |
| `scenario` | 1 | `:451-460` |
| `CoT telemetry (phase 4)` | 7 | `:462-490` |
| `recording (F hotkey)` | 9 | `:501-569` |
| `orbit` | 8 | `:571-615` |

Alongside it sits a second, undeclared surface: **13 runtime hotkeys** registered in
`PygameInterface.py:289-311`, plus `Escape` and `Space` handled inline (`:347-349`). Four of the
thirteen — `K` (solar advance), `X` (storyboard), `O` (orbit), `P` (orbit pause) — are absent from the
control list in the entry point's own module docstring (`run_SCTMV.py:19-42`); three of those four are
mentioned only inside the help text of an unrelated argument (`--time-advance` at `:256-263`,
`--scenario` at `:453-459`, `--orbit` at `:573-577`), and **`P` is documented nowhere at all**.

### 1.3 What is inert

Two examples were already recorded in the plan. Both are confirmed, and the first is worse than
recorded.

**`--ev` can never have an effect on this fork.** The argument is defined at `:237-242` with
`default=0.0`. Its only consumer is `SensorRig.py:66-67`:

```python
if args.ev is not None and bp.has_attribute("exposure_compensation"):
    bp.set_attribute("exposure_compensation", str(args.ev))
```

The first clause is always true (the default is `0.0`, not `None`), so the guard is entirely the
second. **Measured:** `MakeCameraDefinition`
(`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Actor/ActorBlueprintFunctionLibrary.cpp:313-410`)
declares eleven attributes — `fov`, `image_size_x`, `image_size_y`, `lens_circle_falloff`,
`lens_circle_multiplier`, `lens_k`, `lens_kcube`, `lens_x_size`, `lens_y_size`,
`enable_postprocess_effects`, `post_process_profile` — plus `sensor_tick` from
`AddVariationsForSensor` (`:244-254`) and the `role_name` recommendation (`:241`). A grep for
`exposure_compensation` across the whole plugin returns nothing. The RGB camera therefore has **no
exposure attribute of any kind**, and `--ev` is a no-op for every value, silently.

That is a larger finding than "one dead flag". The engine class has the setters —
`SetExposureCompensation`, `SetShutterSpeed`, `SetISO`, `SetAperture`, `SetExposureMinBrightness`,
`SetExposureMaxBrightness`, `SetExposureMethod` and six more
(`Carla/Sensor/SceneCaptureSensor.h:237-378`) — but nothing publishes them as blueprint attributes, so
no client can reach them. [`08`](08_Collection_And_EPoL.md) open question 8 records the same gap from
the collection side. **Consequence for this section:** **no numeric exposure control exists**, so a
night window's brightness is whatever the engine's auto-exposure produces under the post-process
profile the camera spawned with — which is the one exposure control that does exist, and §5.2 makes it
a first-class toggle. A surface that offers `--ev` today is telling the operator a lie, and §6 check 16
turns that lie into a refusal that names the field that works.

**`--fade` was demoted to `default=False`** (`:318-328`), with `--no-fade` retained at `:329-334` so
existing command lines keep working. The stated reason is in the help text itself: the opacity is
"pushed to the server one blocking RPC per vehicle per reconcile, which is the heaviest load this
client puts on the server's per-frame RPC budget". Confirmed as read. This is the one place in the
present surface where a default was changed on measured grounds and the reason was written down where
the operator will see it — the pattern §3 generalises.

**`--time-rate` is silently inert unless `--time-advance` is given.** `WorldBuilder.py:246-247` reads
`args.time_rate` only inside `if args.time_advance:`. So `--time-rate 3600` alone parses, validates,
is recorded nowhere, and does nothing. There is no warning.

### 1.4 What is conditional

Every argument has at least one consumer — the trace found **86 of 86** reachable, once
`getattr(self.args, …)` forms are included (a first pass missed `--same-side-exit-rate`,
`--occlusion-margin` and `--occlusion-samples`, which are read through `getattr` at
`TrafficController.py:583,877` and `NativeRecorder.py:110-111`). Reachability is not the problem.
The problem is that **most arguments do nothing unless something outside their own group is on**:

| Count | Group | Inert unless |
|---:|---|---|
| 23 | `world build (phase 1)` | `--build` is in force; `--no-build` (`:80-86`) makes all 23 ignored |
| 19 | `staging traffic (phase 3)` | traffic is enabled — `--start-traffic` or the `T` hotkey |
| 9 | `recording (F hotkey)` | recording is enabled — the `F` hotkey; the group title says so, no other does |
| 8 | `orbit` | orbit is active — `--orbit` or the `O` hotkey |
| 7 | `CoT telemetry (phase 4)` | telemetry is enabled — the `Y` hotkey |
| **66 of 86** | | **conditional on a switch or hotkey declared outside their own group** |

Only **20 of 86** are unconditionally in force: the six connection/logging arguments, the thirteen EO
observer arguments, and `--scenario`.

A further three declare a second-order condition inside their own help text and enforce none of it:
`--fixed-delta` ("synchronous mode only", `:71-74`), `--terrain-res` and `--drape-cache-dir` ("'drape'
only", `:130-148`). Passing `--terrain-res 0.5` with `--height-align none` is accepted in silence.

**What this measures.** `argparse` can reject an unknown option and a bad type. It cannot reject an
option that is well-formed, correctly typed, and meaningless in the configuration it was given. With
66 of 86 arguments in that category, the dominant failure mode of the present surface is *a run that
starts, proceeds, and does not do what was asked*. That is the failure this plan cannot afford: a
windowed capture costs hours ([`10`](10_Scale_And_Performance.md) §4.1 measures 19–24 wall-clock days
for the un-windowed case) and the corpus records the configuration it *ran*, not the one that was
intended.

### 1.5 What is ambiguous or undocumented

| Finding | Measured |
|---|---|
| **No help text at all** | 5 arguments: `--osm`, `--ion-token`, `--fov`, `--width`, `--height` |
| **Single-word names carrying no subsystem** | 34 of 86 — including `--time`, `--date`, `--rate`, `--speed`, `--max`, `--step`, `--save`, `--filter`, `--seed`, `--z`, `--print`, `--stale` |
| **Name collisions across subsystems** | `--rate` is telemetry Hz (`:477`), `--record-hz` is capture Hz (`:512`), `--time-rate` is sun-clock gain (`:264`), `--fixed-delta` is the simulation step (`:68`) — four different rates, no shared prefix. `--speed` is camera fly speed (`:271`) while `--speed-scale`, `--speed-spread` and `--speed-bias` are traffic (`:376-419`). `--step` is reference-line sample spacing (`:96`), not a time step |
| **Mixed units inside one group** | The `orbit` group takes `--orbit-x` / `--orbit-y` in **metres** and `--orbit-radius` / `--orbit-altitude` in **FEET** (`:579-609`). `--z` is FEET (`:226-228`) while `--x` / `--y` are metres (`:229-235`) |
| **A parse with a side effect** | `parse_args` seeds the process-global RNG (`:640-641`) while `parse` (`:626-627`) does not. Two entry points, two behaviours, from the same class |
| **Duplicated defaults** | 11 `getattr(args, …, fallback)` sites outside the parser; **3 disagree with the parser's own default** — see below |

The three divergent defaults, measured:

| Knob | Parser default | Second default | Where |
|---|---|---|---|
| `depth_max_range` | `20000.0` (`:274-285`) | `1000.0` | `SensorRig.py:78` |
| `lane_spawn_spacing` | `15.0` (`:365-375`) | `0.0` | `TrafficController.py:647` |
| `speed_bias` | `2.0` (`:376-387`) | `0.0` | `TrafficController.py:700` |

These fallbacks only fire when the caller's `args` object lacks the attribute — that is, when the
consumer is driven by something other than this parser. **Today that never happens, so the divergence
is latent.** It stops being latent the instant a second front end exists, which is precisely what this
section proposes. `depth_max_range` is the sharpest case: the same run would measure depth to 20 km
through one front end and 1 km through the other, and the second silently caps the occlusion
measurement that [`08`](08_Collection_And_EPoL.md) D8.19's denominator depends on.

### 1.6 The trigger case, measured: every run overwrites the sun to noon

`run_SCTMV.py:138` calls `WorldBuilder.setup_solar_time(world, args)` **unconditionally** — after the
`--no-build` branch has already logged "attach mode … using the world already on the server"
(`:131-132`). Inside (`WorldBuilder.py:214-255`):

- `:226-230` — with no `--date`, the date becomes `datetime.now()` on the **host machine**.
- `:231-232` — with no `--time`, `hours = 12.0`.
- `:238-239` — `set_solar_date` then `set_solar_time` are called every time, and `set_solar_date`'s
  return value is discarded.
- `:246-247` — advance is configured only if `--time-advance` was passed.

So **attaching to an existing world resets its sun to 12:00 local on today's host date**, and a
scenario whose civil time is 23:00 renders in full daylight unless the operator remembers `--time`.
This is exactly the failure [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a describes, and the record makes it
worse rather than catching it: `FrameRecorder.cs:162` reads `GetCachedSolarState()`, `CotWriter.cs:50-65`
writes it into every sidecar as `<_solar solar_time= … sun_elevation_deg= …>`, and `SolarMetadata.cs:6-14`
embeds the same block in every PNG's `tEXt` chunk (`PngEncoder.cs:44`). **The truth record faithfully
reports noon.** Nothing anywhere compares it against what the scenario asserts, because nothing
machine-readable states what the scenario asserts.

Two further details of the same function matter for §4. There is no range check on `--time`: `25:70`
parses to 26.17 and is wrapped by the server (`carlanet/__init__.py:1500-1503`) with no complaint. And
`set_solar_time` returning `False` — which is how the server reports "this world has no CesiumSunSky"
— produces a `logger.warning` (`:244-245`) and the run continues.

### 1.7 What the measurement establishes

1. **The surface is flat and large, and most of it is conditional.** 86 arguments, 9 groups, 0 modes,
   66 of 86 inert unless something else is on.
2. **`argparse` cannot express the constraints that actually matter here.** One mutually exclusive
   pair guards a demoted cosmetic feature; nothing guards the combinations that cost a run.
3. **Defaults are not a recorded artifact.** They live in the parser, are duplicated in three places
   with divergent values, and have changed at least once (`--fade`). A recorded command line is
   therefore *not* a reproducible run description: the same argv on a different build resolves
   differently, silently.
4. **The one axis this plan added — time of day — is the worst-served part of the existing surface.**
   It is four flags in a group named "EO observer", one of which is inert without another, applied
   unconditionally to a world the operator may not have built, defaulting to a value that is wrong for
   any scenario that is not about noon.

This is not a criticism of `CarlaControlArgumentParser`. It is a correct and well-documented parser
for an *interactive single-client viewer*, which is what `run_SCTMV.py` is. §2 shows that a capture
run is a different object.

---

## 2. What a capture run has to carry

Gathered from across the plan. Each row names what must be decided before a run can start, and who
already owns it. The point of the table is its size and its heterogeneity: this is what a flat
argument list would have to express.

| Axis | What must be decided | Owner |
|---|---|---|
| **Mode** | Which of the four modes, exclusively | [`01`](01_Architecture.md) §5.1 |
| **World package** | Which world, and the binding that proves the scenario was authored against it | [`07`](07_Scenario_Authoring.md) §2, D7.15 |
| **Scenario package** | Which scenario, at which lock, with which supervision plan | [`07`](07_Scenario_Authoring.md) §5.1 |
| **Capture window** | `[begin_s, end_s]` in simulated time, plus prewarm — **or a begin with no end**, which runs until the scenario ends or the caller stops us (D12.4) | [`10`](10_Scale_And_Performance.md) §4.2, D10.3 |
| **Camera rig** | N channels, each `(sensor_id, pattern, track, optics, depth?, seg?)` | [`08`](08_Collection_And_EPoL.md) §3.2, §3.3, D8.4 |
| **Clock** | SUMO step, world delta, capture rate — an integer-ratio contract | [`01`](01_Architecture.md) D1.13 |
| **Solar epoch and policy** | The civil instant `t = 0` means, and whether the sun freezes or advances | [`11`](11_Time_And_Illumination.md); this section for expression |
| **Seeds** | Four of them, separately | [`07`](07_Scenario_Authoring.md) D7.11 |
| **Supervision and export roots** | Two roots, one writer each | [`08`](08_Collection_And_EPoL.md) D8.17 |
| **Telemetry sinks** | Whether, where, and on which thread | [`10`](10_Scale_And_Performance.md) D10.11; [`08`](08_Collection_And_EPoL.md) D8.23 |
| **Occlusion estimator** | On/off, margin, sample density | [`08`](08_Collection_And_EPoL.md) §5.4, open question 1 |
| **Radiometry** | Which post-process profile each channel spawns with, and the digest of the one the server actually loaded | [`08`](08_Collection_And_EPoL.md) §2.9, D8.27, D8.28 |
| **Pacing** | As fast as the machine allows, or against a wall clock at a stated factor | [`08`](08_Collection_And_EPoL.md) §11.1; this section for expression |
| **Handover and transcript** | Whether frames leave the process live, on which channels, and whether what comes back is recorded | [`08`](08_Collection_And_EPoL.md) §11.2, §11.3; [`02`](02_Use_Cases.md) UC-8 |
| **Caller** | Attended or unattended, and — if unattended — how each warning is adjudicated in advance. An unattended caller also decides when the run ends, which is not a field (§3.10.2) | §3.10, §6.4 |

Fifteen axes, of which four are *bindings to artifacts* (world, scenario, epoch, supervision plan),
two are *contracts between numbers* (clock ratio, seeds), eight are *choices*, and one — the caller —
decides **who is allowed to adjudicate the other fourteen**. A flat argument list can
express the choices. It cannot express a binding — there is nothing for a flag to bind *to* — it cannot
validate a contract between three numbers that live in three different groups, and it has no place to
put an adjudication that has to survive the run that used it.

---

## 3. The shape of the new surface

### 3.1 The requirements, restated as tests

The brief states five. Each is restated here as something a candidate either passes or fails, so §3.3
is an evaluation rather than a preference.

**These five were written for a human at a terminal**, and §3.10 adds the five a machine needs. They
are deliberately kept as separate lists rather than merged: three of the machine's five turn out to be
already satisfied by what R1–R5 forced, and that is worth being able to see rather than asserting.

| | Requirement | Test |
|---|---|---|
| **R1** | Every run is reproducible from a recorded artifact | Take the artifact the run recorded, give it to the tool on a different machine and a later build, and get the same effective configuration — **including every value that came from a default** |
| **R2** | The effective configuration is written into the run manifest | Open `manifest.json` and read the value of any knob, without knowing what the tool's defaults were |
| **R3** | A human can launch a common case in one short line | The commonest case — "capture the night window of this scenario" — fits on one line and names nothing that the scenario or the world already knows |
| **R4** | An assistant can generate a configuration reliably | There is a machine-readable schema; an unknown key is a refusal, not a silent no-op; and every refusal names the field and the candidates |
| **R5** | Nothing important can be left implicit | For every field, either it has a default that is correct in every case the tool accepts, or the tool refuses until it is stated |

### 3.2 Candidate — an extended flat command line

Extend `CarlaControlArgumentParser`'s pattern: add groups for SUMO drive, the window, the rig, the
solar policy and the roots; add cross-argument validation in code after `parse_args`.

| Test | Result |
|---|---|
| R1 | **Fails.** The recorded artifact would be the argv, and argv + defaults is not reproducible because defaults change. *Measured:* `--fade`'s default flipped from true to false (`:318-328`), and `--height-align` defaults to `none` (`:120-129`) while every world this plan describes is built with `drape`. An argv recorded before a default change replays differently and nothing says so |
| R2 | Fails unless the tool separately materialises every default into the manifest — at which point the artifact of R1 is that materialisation, not the argv, and this candidate has become the next one |
| R3 | **Fails today and would get worse.** *Measured:* the zero-argument invocation does not attach and capture; it builds a world from `Import/Lakeview_Carson.osm` (`:87-89`, file present, 730,905 bytes). The default behaviour of the viewer is the most expensive operation it has. Adding twelve axes to 86 arguments does not produce a short line |
| R4 | **Fails.** There is no schema. `argparse`'s only failure modes are unknown option and type coercion; the 66-of-86 conditional structure of §1.4 means the assistant's likely mistakes are all silent |
| R5 | **Fails structurally.** An omitted flag and a deliberately defaulted flag are indistinguishable in argv, so "the operator did not think about the sun" and "the operator chose noon" produce identical records |

This candidate is rejected on R1 and R5, which are the two the corpus depends on.

### 3.3 Candidate — a run-configuration file with a thin command line

One document per run describing the whole run; the command line names the file and little else.

| Test | Result |
|---|---|
| R1 | **Passes**, provided the file is complete — i.e. provided the tool writes back a fully-materialised copy rather than trusting the hand-written one |
| R2 | **Passes** |
| R3 | **Partly fails.** A common case still costs a file. *Measured consequence:* [`10`](10_Scale_And_Performance.md) D10.3 recommends 4–8 windows per seven-day scenario and [`07`](07_Scenario_Authoring.md) D7.12 makes counterfactual pairing a sweep mode that doubles the run list — so one scenario produces 8–16 near-identical files whose diffs are two fields each |
| R4 | **Passes** with a schema |
| R5 | **Passes** |

Rejected only on R3, and R3 matters: a surface that makes the ordinary case laborious gets worked
around, and the workaround is copy-paste, which is how 16 files acquire 16 different values for a
field nobody meant to vary.

### 3.4 Candidate — layered resolution: artifact bindings, then declared defaults, then the run, then the operator

A scenario declares what it knows (its epoch, its named windows, its step); a run
configuration states the run's own choices; the operator overrides individual fields on the command
line. All of it resolves into one **effective run configuration** before anything starts, and that
object — not the file, not the argv — is the recorded artifact.

| Test | Result |
|---|---|
| R1 | **Passes.** The recorded artifact is the resolved object, which contains no defaults-by-reference |
| R2 | **Passes** — it is the same object |
| R3 | **Passes.** `run_capture --scenario bahonar --window night_shift` is the common case, because the scenario already declares `night_shift` |
| R4 | **Passes**, same schema as the previous candidate |
| R5 | **Passes only if every resolved field carries its provenance.** Layering is exactly where implicitness creeps back in: "where did this value come from?" must be answerable from the artifact, not by re-deriving it |

**This is the recommendation**, with the R5 caveat promoted to a hard property (§3.6).

The decisive argument is not that layering is elegant. It is that **this plan already contains the
mechanism, one level down, and building a second one would be the duplication the brief forbids.**
[`07`](07_Scenario_Authoring.md) §5 compiles a scenario specification against a world package into a
`.lock.json` plus a `.resolution.json`, with a stated refuse/warn vocabulary and a report that states
*what it resolved*, not only what it rejected (D7.6). A run configuration is the same kind of object
one layer up: a document, bound to artifacts, compiled, locked and reported. §6 therefore does not
invent a validator; it adds a phase to that one.

### 3.5 The layers, named

Strictly increasing precedence. Every layer is optional except the first and the bindings.

| # | Layer | What it supplies | Override by a higher layer |
|---|---|---|---|
| 1 | **Tool defaults** | Values compiled into the schema and versioned with the tool. Every one is materialised into the effective configuration with `provenance: "tool_default"` and the tool version | Permitted |
| 2 | **Site profile** | Facts about *this machine*, not about the science: server host and port, SUMO install, Cesium ion token, the two export roots' base paths. Separated so a run configuration is portable between machines unedited | Permitted |
| 3 | **World package binding** | Origin latitude/longitude, staging rectangle, netconvert argument vector and version, world digest, network fingerprint | **Refused.** These are bindings, not defaults; an operator override here is a check failure naming both values |
| 4 | **Scenario package declaration** | Solar epoch, named capture windows, SUMO step, the supervision plan and the scenario's own seeds | Permitted, and every override is recorded against the value it replaced |
| 5 | **Run configuration** | The run's choices: mode, window selection, rig, solar policy, sinks, roots, seeds | Permitted |
| 6 | **Operator override** | One command-line flag per field, in `--set <path>=<value>` form plus short aliases for the handful in §3.8 | — |

Three properties make the layering safe rather than merely convenient:

- **A field that no layer supplies and that has no tool default is a refusal.** This is R5's mechanism.
- **A field whose correct value depends on a condition has no default under that condition.** Named
  the **conditional requirement**, it is what lets the file stay short in the ordinary case while
  still forbidding an important implicit choice. §4.4 and §5.2 use it twice; both uses are stated.
- **Layer 2 is materialised exactly like every other layer, and a field it resolved from the host
  environment names the variable it read.** A site profile is where host dependence is *allowed* to
  live; it is not where host dependence is allowed to hide. *Measured:* five environment variables
  already reach a run's behaviour — `CESIUM_ION_TOKEN` (`CarlaControlArgumentParser.py:105`),
  `SUMO_HOME` (`SumoInstallation.py:36`), and `CARLA_NETCONVERT` / `PROJ_LIB` / `PROJ_DATA`
  (`run_SCTMV.py:66-78`) — and none of them is written into any artifact a run produces. §3.10
  requirement M2 is what turns that into a rule.

### 3.6 Configuration resolution

```mermaid
flowchart TB
    subgraph INPUT["Inputs"]
        TD["Tool defaults<br/>schema + tool version"]
        SP["Site profile<br/>host, ports, install paths,<br/>export root bases"]
        WP["World package<br/>world.json - map.xodr -<br/>map.net.xml - bareearth.bin"]
        SC["Scenario package<br/>.sumocfg - .rou.xml -<br/>.supervision.json - .lock.json"]
        RC["Run configuration<br/>&lt;run&gt;.run.json"]
        OV["Operator overrides<br/>--window, --set path=value"]
    end

    TD --> R
    SP --> R
    WP -->|"BINDINGS<br/>not overridable"| B["Binding set<br/>world digest - network fingerprint -<br/>origin - staging rect - netconvert argv"]
    SC -->|"DECLARATIONS<br/>overridable, override recorded"| R
    RC --> R
    OV --> R

    B --> R["RunConfigurationResolver<br/>strict precedence 1 to 6<br/>one provenance record per field"]

    R --> EFF["EffectiveRunConfiguration<br/>every field materialised<br/>every field carries<br/>value + layer + overridden_value"]

    EFF --> V["RunConfigurationCompiler<br/>phase 7 of 07 section 5.4<br/>same refuse / warn vocabulary"]

    V -->|"refuse"| X(["REFUSE<br/>field named, both values named,<br/>candidates listed"])
    V -->|"warn"| WRN[/"WARN<br/>collected into the report"/]
    V -->|"accept"| LOCK[("&lt;run&gt;.lock.json<br/>digest of the effective configuration<br/>+ every input artifact's digest")]
    V --> REP[("&lt;run&gt;.resolution.json<br/>what resolved, from which layer,<br/>with every warning in full")]
    WRN -.-> REP

    LOCK --> SESS["CaptureSession starts"]
    REP --> SESS
    SESS --> MAN[("manifest.json<br/>session.effective_configuration<br/>= the EffectiveRunConfiguration verbatim")]

    MAN -.->|"a later run re-reads it<br/>as layer 5 and reproduces<br/>the run exactly"| RC

    classDef refuse fill:#7a2020,stroke:#d06060,color:#fff
    class X refuse
```

The dashed edge at the bottom is R1's test made structural: **the manifest's effective configuration
is itself a valid run configuration.** Reproducing a run is reading it back, not reconstructing it.

### 3.7 What the effective configuration looks like

Four fields, in full, to fix the shape. The document keys each field by its dotted path; this is
`EffectiveRunConfiguration.to_document()` for a run given `--solar advance --set capture.prewarm_s=600`:

```jsonc
"capture.prewarm_s": {
  "value": 600, "layer": "operator_override", "provenance": "--set capture.prewarm_s=600",
  "tool_default": 300, "overridden": null
},
"solar.policy": {
  "value": "advance", "layer": "operator_override", "provenance": "--solar advance",
  // no "tool_default" key: the policy has no tool default (§4.4)
  "overridden": [ { "layer": "scenario_package", "value": "freeze_at_window_start",
                    "provenance": "scenario gardnerville_fixture@d0bffc2099d8 :: illumination.policy" } ]
},
"solar.rate_sun_s_per_sim_s": {
  "value": 1.0, "layer": "pinned_by_policy",
  "provenance": "advance pins the rate to one sun-second per simulated second (12 D12.8)",
  "tool_default": null, "overridden": null
},
"solar.freeze_date_advances": {
  "value": null, "layer": "not_applicable",
  "provenance": "--solar advance set policy 'advance', which does not take it",
  "dropped": [ { "layer": "scenario_package", "value": false,
                 "provenance": "scenario gardnerville_fixture@d0bffc2099d8 :: illumination.freeze_date_advances" } ]
}
```

A field's `layer` is one of the six of §3.5, or `derived` (the world package's path, from the site
profile's root and the name the scenario lock records), `pinned_by_policy`, `not_applicable` (a field
the chosen mode or policy does not take) or `unsupplied` (no layer gave a value; check 2 refuses it).
A value read from an environment variable carries `environment_variable`.

Three properties, each doing a job:

- `layer` answers "where did this come from" without re-deriving it — R5 under layering.
- `tool_default` is carried **beside** the value even when the value came from elsewhere, so a later
  reader can tell a deliberate match from an accident, and a default change is visible in a diff.
- `overridden` records what an operator override replaced, so an override is an event in the record
  rather than an absence.

This costs nothing at run time: the whole object is written once at session start and never read again
by the session.

### 3.8 The short line

R3 is a real requirement and it is met by making the scenario carry what the scenario knows. The
common cases, in full:

```
run_capture --scenario bahonar_pattern_of_life --window night_shift
run_capture --scenario bahonar_pattern_of_life --window night_shift --solar freeze_at_window_start
run_capture --run configs/bahonar_night_sweep.run.json
run_capture --run configs/bahonar_night_sweep.run.json --set capture.prewarm_s=600
run_capture --run runs/cap-20260105-230000-1f2e3d/run.effective.json
```

`run_capture` is `CarlaControl/scripts/run_capture.py`, launched on either platform by
`Scripts/Windows/RunCapture.ps1` or `Scripts/Linux/RunCapture.sh` (§10). The short options are
generated from the fields that carry an alias — `--scenario` (`scenario_package`), `--window`
(`capture.window`), `--solar` (`solar.policy`), `--caller`, `--caller-label` and `--result`
(`result_path`) — and are applied before `--set`, which is applied in the order given. A run is
reproduced by handing the effective configuration the run wrote beside its result back to `--run`
(§3.6): it names the scenario by id, carries every binding, and resolves to the same digest.
`--emit-run-configuration` (§9.5) and `--run-list` (§13 question 1) are not built.

Everything else is either declared by the scenario (the epoch, the windows, the step, the seed, the
illumination default), fixed by the site profile (host, port, the paths), or bound by the world package
(origin, digest). `--window` takes a name the scenario declared or an explicit `begin_s:end_s` pair — which is
[`01`](01_Architecture.md) open question 3's and [`02`](02_Use_Cases.md) open question 2's recommended
answer, adopted here and recorded as D12.4 so those two can be closed together.

There is deliberately **no** `--dry-run`. Validation is not optional and not a mode: §6's phase 0 runs
on every launch, and a `--validate-only` flag stops after it — writing the resolution report and the
lock, exiting 0 when the offline checks accept, and writing a `refused_offline` result when they
do not. The
difference matters because a `--dry-run` that people forget to use is theatre.

**The unattended forms are the same line with the caller declared and a result path given**, because
the machine's needs are two fields and a contract, not a second program (§3.10):

```
run_capture --run configs/bahonar_night.run.json --caller unattended \
            --result out/bahonar_night.result.json
run_capture --run-list sweeps/bahonar_illumination.sweep.json --caller unattended \
            --result out/bahonar_illumination.result.json --caller-label ingest-38
```

`--caller unattended` is not a convenience: it changes what the tool is allowed to do without being
told (§3.10 M1, §6.4), and it is recorded in the corpus (§5.2), because a corpus whose warnings were
adjudicated by a file rather than by a person is a different thing from one that was watched.
`--caller-label` is optional, opaque, and never interpreted — it exists so a caller can recognise its
own run in a directory of them (§3.10.4).

**No form of the command takes a run length.** There is no `--duration` and no `--frames`: a run ends
when it reaches the end its window declared, or when the caller stops it (§3.10.2). A window may
declare no end at all, in which case the run continues until the scenario itself ends or the caller
stops us, and nothing in this surface depends on either happening.

### 3.9 The classes

Under [`AGENTS.md`](../../../../AGENTS.md): one public class per file, the file named for the class in
PascalCase, modern union hints, absolute imports outside the package, all imports at the top,
`logging` rather than `print`, and a thin `main` that parses, constructs and calls.

| File | Public class | Responsibility |
|---|---|---|
| `RunConfiguration.py` | `RunConfiguration` | **Built.** The parsed, unresolved document (layer 5), and the one field table every layer is checked against: each field's path, shape, tool default, mutability class and help. Publishes `CarlaControl/schemas/run_configuration.schema.json` and generates `run_capture --help`. Knows nothing about worlds or servers |
| `SiteProfile.py` | `SiteProfile` | **Built.** Layer 2. Machine facts, read from a site-profile file when one is named, otherwise derived from the layout the tool runs from; every value names its source and, where it was read from an environment variable, the variable |
| `RunConfigurationResolver.py` | `RunConfigurationResolver` | **Built.** Applies layers 1–6 in order, records provenance per field, refuses an override of a binding |
| `EffectiveRunConfiguration.py` | `EffectiveRunConfiguration` | **Built.** The resolved, immutable object of §3.7. Serialises itself; is re-readable as layer 5 |
| `RunConfigurationValidator.py` | `RunConfigurationValidator` | **Built.** §6's checks, split by phase; emits refusals and warnings in [`07`](07_Scenario_Authoring.md) §5.2's vocabulary — the compiler's own `CompileFinding` |
| `LaunchEcho.py` | `LaunchEcho` | **Built.** §6.4's pre-commit statement: computes the block, renders it for a human, and serialises it into the resolution report for a machine. One computation, two renderings |
| `RunConfigurationCheckCatalogue.py` | `RunConfigurationCheckCatalogue` | **Built.** §6.2's checks by their stable numbers, each with the phase it runs in and where it is carried out (§6.2.1) |
| `RunConfigurationFindings.py` | `RunConfigurationFindings` | **Built.** One launch's findings: the compiler's `CompileFinding`, cited against this catalogue, with a warning's `on_warning` code |
| `ScenarioPackage.py` | `ScenarioPackage` | **Built.** A compiled scenario re-bound by its lock rather than recompiled: the lock's layer-4 declarations, and a refusal of any file the lock no longer digests (check 49) |
| `SessionMonitor.py` | `SessionMonitor` | **Built.** §7.1's live view and §7.4's `pace` row; formats `RunCloseoutReport`'s snapshot and holds no reference to the session or a recorder, and degrades to line-oriented logging when standard output is not a terminal |
| `RunCloseoutReport.py` | `RunCloseoutReport` | **Built.** The one computation of what a run has established — a snapshot of the session and the recorders at any instant — and §7.2's gate records read from it. With no run manifest to append to, the records reach disk in `RunResult` at the terminal outcome (§7.2) |
| `RunResult.py` | `RunResult` | **Built.** §3.10.3's result artifact. Written in **every** terminal outcome the tool survives, including a refusal that produced no session, to a temporary name renamed into place; the process exit status is read from its outcome rather than computed beside it |
| `RunTerminationSequence.py` | `RunTerminationSequence` | **Built.** §3.10.2's ordered flush. Installed as the handler for `SIGINT`, `SIGTERM` and, on Windows, `SIGBREAK` *and* run as the session's `finally`, so one code path serves a stop, a signal and a fault. Idempotent, time-boxed at every step that can block on the server, and re-entrant: a second signal abandons the remaining steps |
| `WorldBuildConfiguration.py` | `WorldBuildConfiguration` | §9.2's single definition of the 24 world-build inputs, produced by both front ends |
| `ChannelDescription.py` | `ChannelDescription` | **Built.** One camera channel: §5.2's per-channel fields and their defaults, defined once and validated on construction. §9.2's typed channel description |
| `StareAim.py` | `StareAim` | **Built.** The pose a stare channel holds, from a look-at point or an explicit pose, or around the point a stare aimed at the rendered traffic resolves to |
| `RenderedTrafficCentre.py` | `RenderedTrafficCentre` | **Built.** The centre of the vehicles a prewarm step's last frame rendered, from the session's `on_pose` records of poses written to bodies: what a stare aimed at the rendered traffic follows through the prewarm, and the point it resolves to (§5.2) |
| `CameraFollower.py` | `CameraFollower` | **Built.** A viewer that places one camera from a `ChannelDescription` and shows its picture live; a camera-follower process in [`01`](01_Architecture.md) D1.1's sense — it never cues, never writes episode settings, and never records ([`08`](08_Collection_And_EPoL.md) §3.4). Its camera, window and frame-stall notice are `FollowerCamera`, `FollowerWindow` and `FrameStallWatch` |
| `CaptureSession.py` | `CaptureSession` | **Built.** One capture run, [`01`](01_Architecture.md) §2.3's component in the process that drives the world: it resolves and validates the invocation, prints the echo, writes the resolution report and the lock, starts `SumoDriveSession`, places each channel's cameras, prewarms, starts one recorder per channel under one session id, advances the window, and ends through `RunTerminationSequence` |
| `scripts/run_capture.py` | — | **Built.** Thin `main`: parses the command line, discovers the site profile, constructs `CaptureSession`, runs it, and returns the exit status its `RunResult` carries |
| `scripts/run_camera_follower.py` | — | **Built.** Thin `main` for `CameraFollower` |

`PlaybackClock`, `RenderSetSelector` and `SumoSession` are [`01`](01_Architecture.md) §2.3's
components; in the tree they are one object, `CarlaNet.CoSim.SumoDriveSession`, which
`CaptureSession` constructs with the effective configuration's values. `RunManifestWriter` is not
built: no run manifest is written, so the monitor, the gate records and the run result read the
session and the recorders directly (§7.1, §7.2).

### 3.10 The caller that starts us and stops us

Cyclic generation is **not ours to control** ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d). External
processes drive it and are in full control of when to terminate, what to do with the data, and what
comes next. They may kill the SUMO or CARLA server — or this client — at any instant, deliberately, or
query through CarlaNet and the Python shim to decide when enough is enough.

So the caller is not a scheduler we serve with a verdict. It is a process that **starts us, watches us
through interfaces that already exist, and stops us when it decides to.** Three things land on this
surface, and only three:

1. **Non-interactive, parameterised, reproducible invocation** — a caller that cannot answer a question
   still cannot answer one.
2. **Clean termination under a deliberate kill**, as a first-class property of the surface rather than
   an error path (§3.10.2).
3. **An honest record of whether we stopped or finished**, and of what exists on disk either way
   (§3.10.2, §3.10.3) — plus, while the run is still going, a surface the caller can watch (§7.6).

What does **not** land here: a cadence, a run-length policy, an aggregate verdict on the corpus, or any
assumption that we are asked politely to stop.

[`02`](02_Use_Cases.md) UC-12's *scripted launch* alternate flow states the governing rule — "the
composition step cannot be bypassed, not that a person must sit in front of it" — so the machine is not
a second surface. It is the same surface with a caller that cannot be asked a question and that decides
when we are done.

#### 3.10.1 Six machine requirements, checked against what already exists

The brief says this "mostly falls out; say so rather than inventing machinery". It does, for three of
the six. Each row states the requirement, whether §3.1–§3.9's design already meets it, and the evidence
either way.

| | Requirement | Met by §3.1–§3.9? | Evidence, and what this section requires |
|---|---|---|---|
| **M1** | **No interactive prompt, ever** | **Yes, and it was never at risk** | *Measured:* there is no `input()`, `getpass` or console prompt anywhere in `CarlaControl/src/carlacontrol` or `CarlaControl/scripts` — the two textual matches are `SumoScenarioBuilder.py:461` and `:632`, both the string `_additional_input`. §3.8 refuses a `--dry-run` on the grounds that an optional step people forget is theatre, and §6 makes validation a phase rather than a question. **The one blocking construct this section specifies is §6.4's echo, and §6.4 resolves it.** **Required:** `run_capture` constructs no display — `run_SCTMV.py:172, :198` build `PyGameSensorController` and `PygameInterface`, and the latter calls `pygame.init()` and `pygame.display.set_mode` at `PygameInterface.py:75, :79`, so a capture launcher that reused it would need a window server — and `SessionMonitor` degrades to line-oriented logging when standard output is not a terminal |
| **M2** | **No hidden host-dependent default** | **Partly** | The *mechanism* holds: layer 2 exists precisely to separate machine facts from the science (§3.5), and D12.3 makes every field carry the layer that set it. What does not hold is coverage. *Measured:* five environment variables reach behaviour today and none is recorded (§3.5); `--ion-token` defaults to the **empty string** when `CESIUM_ION_TOKEN` is unset (`CarlaControlArgumentParser.py:105`), which is a silent misconfiguration rather than an absence; `--date` defaults to `datetime.now()` on the **host** (`WorldBuilder.py:226-230`, §1.6); and `--osm` defaults to a path under the repository root (`:87-89`). **Why it is load-bearing:** a run that is killed leaves only what it wrote, so a value that came from the host environment and was never written down is unrecoverable and nobody can afterwards say what ran. **Required:** every layer-2 field names the variable it resolved from, an empty secret is a refusal rather than an empty string, and under `caller: unattended` a field resolving from an environment variable the site profile does not name is a refusal (checks 36 and 37) |
| **M3** | **The record says whether we stopped or finished** | **No. This is the real gap** | *Measured:* `run_SCTMV.py` has exactly two statuses — `1` when the world build fails (`:130`) and `0` otherwise (`:339`) — and `KeyboardInterrupt` is swallowed at `:291-292`, so **a run killed halfway through exits 0 and is indistinguishable from one that finished**. A deliberate kill is the expected path, so the one distinction the record must carry is precisely the one the tree cannot make. The plan has been bitten by a coarse exit code once already: [`07`](07_Scenario_Authoring.md) §5.5 measured `duarouter` exiting `0` regardless under `--ignore-errors`, and concluded "exit code alone is a gate that stops at the first error". **Required:** §3.10.2's outcome set, whose live distinction is *finished* against *stopped* |
| **M4** | **A machine-readable record of what was produced** | **Mostly** | §3.6 emits `<run>.resolution.json` and `<run>.lock.json` on every path including a refusal, in [`07`](07_Scenario_Authoring.md) §5.2's vocabulary; §7.2's gate records are facts about our own data. *Measured:* the one object in the tree shaped like a run result — `RunReport` (`SumoCotBridge.py:155-168`) — is **returned and never written**. `sumo_cot_telemetry.py:158` receives it, `:165-166` logs three of its six fields, and nothing persists it; `achieved_real_time_factor`, the field a live run exists to watch, has no reader at all. **Why it is load-bearing:** a returned object dies with the process, and this process is expected to be killed. **Required:** §3.10.3's `RunResult` — whose *fields* are [`04`](04_Contracts.md)'s `C10` — written to a path the caller gave, in every outcome the tool survives, and **carrying no aggregate verdict** |
| **M5** | **The same configuration produces the same corpus** | **Mostly, and the residue is worth naming** | R1's test *is* this requirement, and D12.3 (every field materialised with its provenance) and D12.11 (no nondeterministic seed default) are its mechanism. Three residues are named and closed in §3.10.4: a drawn seed, a rebuilt world, and a tool whose defaults moved. **Why it is load-bearing:** reproducibility here is not about repeating a cadence, which is the caller's business — it is about a killed run still being explicable afterwards from what it wrote |
| **M6** | **A kill at an arbitrary instant leaves valid artifacts and an honest record** | **No, and nothing in the tree is built for it** | *Measured:* `SIGTERM` is handled nowhere; the XML telemetry sink is well-formed only if its `finally` runs; an interrupted `RunReport` reports zero vehicles; the shutdown despawns the world before it drains the recorder; and frames still in the encode queue are lost without being counted. All five are in §3.10.2 with citations. **Required:** §3.10.2's termination sequence, its prohibitions, and its statement of what holds when there is no chance to flush at all |

**None of that is a new subsystem.** M1 is a property to preserve and two small refusals; M2 is coverage
over a layer that already exists; M4 is a serialisation of objects already specified; M3 is eight names
and a rule. M6 is an ordering — and the expensive part of M6 is that the ordering has to be the
*opposite* of the one the tree has today.

#### 3.10.2 Termination is the expected path, and how a run ends

##### What a deliberate kill does today, measured

Every line below was read from the live tree on 2026-09-18.

| Finding | Evidence |
|---|---|
| **`SIGTERM` is handled nowhere.** `run_SCTMV.py:246` installs a handler for **`SIGINT` only**, and the comment at `:243` says why — pythonnet can swallow `KeyboardInterrupt` inside a .NET call. *Measured:* a grep for `import signal`, `signal.signal`, `atexit`, `SIGINT` and `SIGTERM` across `CarlaControl/src/carlacontrol`, `CarlaControl/scripts` and `CarlaNet/python/carlanet` returns four lines, all in `run_SCTMV.py`, none of them `SIGTERM`. **So the ordinary way one process stops another — `SIGTERM` on Linux, `taskkill` without `/F` on Windows — terminates this client with no `finally` block at all.** The path the brief calls expected is exactly the path with no cleanup on it | `run_SCTMV.py:243-247` |
| **An interrupted run exits 0.** `KeyboardInterrupt` is swallowed and `main` returns `0` | `run_SCTMV.py:291-292, :339` |
| **The shutdown puts the world before the corpus.** Order in the `finally`: stop the background threads, join the worker (2 s), stop the orbit updater, disable the storyboard, **`traffic.disable()` — despawn every vehicle** — *then* `recorder.stop()`, then `telemetry.close()`, then restore asynchronous mode, then `sensors.cleanup()`. Every second spent despawning is a second the capture pipeline is not being drained, and the despawn mutates the world the unflushed captures describe | `run_SCTMV.py:294-338`; despawn `:317`, recorder `:321-325` |
| **One sink survives a kill and the one beside it does not.** The CoT telemetry XML is a single document: the `<events …>` root is opened at the top of the run and the closing `</events>` is written **only in the `finally`**, so a run killed without its `finally` leaves an unterminated document that no conforming parser will read. The CSV sink beside it writes one record per line and is complete to its last whole row. Same data, same run, two survivabilities | `SumoCotBridge.py:208-211`, `:283`; CSV `:274-276` |
| **The only run-summary object in the tree is valid only if the loop completed, and is never written.** `RunReport` assigns `vehicles` and `wall_seconds` *after* the loop, so an interrupted run reports zero for both; the object is returned to `sumo_cot_telemetry.py:158`, three of its six fields are logged at `:165-166`, and nothing writes it to disk | `SumoCotBridge.py:155-168`, `:277-278` |
| **Frames in flight are lost without being counted.** The recorder's encode queue is a bounded channel of `max(4, n × 2)` jobs with `n = max(2, ProcessorCount / 2)`, and `Dropped` is incremented **only** when `TryWrite` fails. A frame accepted into the queue and never encoded is counted nowhere | `FrameRecorder.cs:115-121`, `:184-185` |
| **A capture is two files and the pair is not atomic.** The worker writes the PNG straight to its final path and then the sidecar straight to its final path | `FrameRecorder.cs:222-227`, `:228-230` |
| **One bound already exists and is the right shape.** `FrameRecorder.Dispose` unsubscribes, completes the channel and waits **at most ten seconds** for the workers. A bounded drain is exactly what a signal handler can afford to run | `FrameRecorder.cs:243-249` |

Together those say one thing: **nothing in the current tree is built to be killed.**

##### What the tool does when it is stopped

One sequence — `RunTerminationSequence` (§3.9) — installed both as the handler for every signal the
platform delivers *and* as the session's `finally`, so an operator stop, a signal and a fault take the
same path and there is only one order to get right.

| Step | What happens | Why it is in this position |
|---:|---|---|
| 1 | Set the stop flag. **No new work starts**: no further tick is cued, no vehicle is admitted, no capture is scheduled | Everything after this is bounded because nothing is being added to it |
| 2 | Let the tick in flight finish, bounded by the client's own frame-wait timeout, which logs and returns rather than deadlocking (`CarlaClient.cs:292-299`, `:432-440`) | A half-applied tick is the one state in which poses and truth disagree |
| 3 | Unsubscribe every capture stream, then drain the encode queues under their existing ten-second bound (`FrameRecorder.cs:243-249`) | Unsubscribe first, drain second: the reverse races new jobs into a queue being emptied |
| 4 | **Append** the closing record to the manifest: last tick, last simulated and civil instant, `closed_by`, the gate records as of now, and per-channel *captured* and *written* counts | The manifest is already being appended to, so this is one more append rather than a document being finished. Nothing about it may be what makes the earlier appends readable |
| 5 | Write `RunResult` | Small, and by now it can state what exists |
| 6 | **Only now touch the world**: release the render set, release the authority lease, restore asynchronous mode — each best-effort, each time-boxed, each tolerating a server that is already gone | The corpus is safe before anything is spent on tidiness. This is the inverse of the order at `run_SCTMV.py:317` before `:321-325` |
| 7 | Close the handover socket and the transcript listeners without draining them | Waiting on the external chain is waiting on something the caller may have killed first |

**A second signal abandons the remaining steps and exits immediately, with the artifacts as they are.**
A caller sending a second signal is saying *now*; a shutdown that ignores it is a hang, and a hang is
what makes a caller reach for `SIGKILL`.

##### What it must never do

- **Never delete or rewrite anything already written.** There is no "clean up the partial run": a
  corpus interrupted mid-window is a shorter corpus, and shortening it is the caller's prerogative.
- **Never stage artifacts and publish them at the end.** Anything that becomes readable only on a clean
  exit is a corpus that a kill destroys.
- **Never write an artifact whose readability depends on a closing token** — the XML sink measured
  above does, and the CSV sink beside it does not, and they carry the same data.
- **Never block the corpus flush on the server, on SUMO, or on the external chain.** All three may
  already be dead, because the caller may kill them, so every call to any of them *during termination*
  is best-effort and time-boxed.
- **Never report a kill as a fault.** It is a state to record, not an error to raise: `internal_error`
  is for an unhandled fault and a signal is not one.
- **Never hold the corpus back until it can be judged.** There is nothing to judge (§3.10.3).

##### When it is killed with no chance to flush at all

`SIGKILL`, `taskkill /F`, a host that loses power, or a server that dies underneath us. This is normal,
and it is stated rather than hoped against.

- **What is on disk is what exists**, and the last complete record in each artifact is the authority.
  That is why step 4 is an append and not a close, and it is why this section needs
  [`04`](04_Contracts.md)'s per-artifact guarantee to be *valid at every instant* rather than *valid
  once closed*.
- **A bounded number of captured frames is lost, and the loss must not be silent.** At most
  `max(4, n × 2)` captures per channel are in flight (`FrameRecorder.cs:115-121`) and none of them is
  counted by `Dropped` (`:184-185`). The fix belongs in the record rather than in the queue: the
  manifest carries **captured** and **written** per channel, so the difference at the last append *is*
  the loss, and a reader sees it without being told.
- **The last capture may be torn.** A kill between the two files leaves an image with no truth record;
  a kill during either leaves a truncated file under its real name. Needed from
  [`04`](04_Contracts.md): atomic publication — write beside, rename into place — and a stated
  publication order. *Recommendation, labelled as this section's:* **publish the sidecar first**, so an
  image without truth is impossible and the only torn state is a sidecar with no image, which a reader
  can detect and disregard.
- **There may be no result artifact at all**, and its absence means *the tool was stopped before it
  could write one* — nothing more. §3.10.3's K2 states that as a property this section needs from
  [`04`](04_Contracts.md) `C10`, because a judgement inferred from our own death would make a normal
  operating mode look like a fault.

##### How a run ends

The valuable distinction is **stopped against finished**, honestly recorded, plus what a reader needs
to know about what exists on disk. Numbers alone would be conversational jargon
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §4), so each outcome carries a name that stands alone, and the name
is what appears in the result. **The process exit status is read from `RunResult.outcome`, never
computed alongside it** — the same rule D12.14 applies to the monitor, and for the same reason: two
computations of one fact eventually disagree, and the caller believes the cheaper one.

| Status | Name | What it says | What exists on disk |
|---:|---|---|---|
| 0 | `run_finished` | The run reached the end it was given — the window's declared end, or the scenario's own end where the window declares none — and closed itself | The corpus, with the manifest's last record naming the end it reached |
| 1 | `usage_error` | The invocation itself was malformed: unknown key, unreadable file, an override of a binding. Nothing was resolved | The result, and nothing else |
| 2 | `refused_offline` | Phase 0 refused (checks 1–19, 34–42, 46–48, 51). No server was contacted | The resolution report and the result |
| 3 | `refused_server` | Phase 1 refused (checks 22–27, 43). Nothing was acquired, nothing spawned | The resolution report and the result |
| 4 | `refused_authority` | Phase 2 refused (checks 28, 29): an authority is held by someone else, **named in the result** | The resolution report and the result |
| 5 | `refused_preroll` | Phase 3 refused (checks 30–32, 44, 45, 50). The lease was acquired and released; the manifest carries `closed_by: aborted_at_preroll` | A manifest with no window, the report and the result |
| 6 | `run_stopped` | The run ended before that end: a signal, an operator stop, a loud condition (§7.1), or the tool stopping itself because write headroom ran out (check 46). `closed_by` names which | A shorter corpus, complete to its last append |
| 7 | `internal_error` | An unhandled fault — **not** a signal | Whatever had been appended, plus the result if the fault left the tool able to write it |
| — | *no result at all* | The tool was stopped before it could write one | Whatever had been appended. **Absence is absence**, and says nothing about the data |

Three things about that table are deliberate.

- **`run_finished` and `run_stopped` are the only pair the tool is entitled to distinguish.** Which of
  them is *better* depends on what the caller wanted, and the caller did not tell us.
- **Nothing here is a verdict.** A refusal is a statement about a configuration we were given; a stop is
  a statement about how the run ended. Neither says whether the data is useful.
- **There is no retry advice.** `refused_authority` is the one outcome whose cause is outside the
  configuration — the world is busy — and that is legible because the result names the holder, not
  because this section recommends anything.

**As built** (`RunResult`, `CaptureSession`):

- `usage_error` is every refusal raised while the invocation is read and resolved — checks 1, 3, 16,
  38 and 49 (§6.2.1): an unknown key, a malformed override, an override of a binding, a scenario or
  world package that cannot be bound. A missing `scenario_package` is check 2, `refused_offline`.
- `refused_server` also covers a server that cannot be reached, and a refusal the session raises at
  its `Validation` or `Launch` stage, before it takes the lease (§6.3). `refused_preroll` covers the
  session's `PreRoll` stage, whether its start or a prewarm tick raised it, and, from `run_capture`
  itself, a camera that could not be placed, the prewarm's pace (check 44), a stare aimed at the
  rendered traffic with no vehicle to aim at (§5.2), and a channel's view not ready as the window
  opens, or a witness past its ceiling before then (check 50).
- A run that stops for any reason carries `closed_by`, one of: `window_end` and `scenario_end`
  (with `run_finished`); `signal:SIGINT`, `signal:SIGTERM`, `signal:SIGBREAK`, `operator_stop`,
  `loud:recorder_dropped`, `loud:pace_below_floor`, `write_headroom`, and `fault:<exception>` for a
  refusal the session raises at its `Window` stage — the world producing no frame, the solar audit
  failing, SUMO failing — (with `run_stopped`); `aborted_at_preroll` (with `refused_preroll`). A run whose window
  declares no end closes with `scenario_end`, whether SUMO ran out of vehicles or the scenario's
  declared end was reached.
- A signal is acted on at the next SUMO step boundary: the Python handler runs when the blocking
  `Advance` call returns, so at most one step is rendered after it arrives. On Windows, `taskkill`
  without `/F` delivers nothing a Python process can catch; a caller stops a run with Ctrl+C or with
  `CTRL_BREAK_EVENT` sent to the run's process group.

```mermaid
stateDiagram-v2
    direction LR
    [*] --> Invoked
    Invoked --> Resolve : argv + layers 1..6
    Resolve --> usage_error : malformed / override of a binding
    Resolve --> Phase0
    Phase0 --> refused_offline : checks 1-19, 34-42, 46-48, 51
    Phase0 --> Phase1
    Phase1 --> refused_server : checks 22-27, 43
    Phase1 --> Phase2
    Phase2 --> refused_authority : world is busy, holder named
    Phase2 --> Phase3 : lease held - first irreversible step
    Phase3 --> refused_preroll : checks 30-32, 44, 45, 50
    Phase3 --> Window
    Window --> run_stopped : signal, operator stop,<br/>loud condition, headroom gone
    Window --> internal_error : unhandled fault
    Window --> run_finished : reached the end it was given
    Window --> no_result : killed with no chance to flush

    run_finished --> [*] : corpus, gate records published
    run_stopped --> [*] : shorter corpus, closed_by named
    no_result --> [*] : the artifacts on disk are the authority
    usage_error --> [*]
    refused_offline --> [*]
    refused_server --> [*]
    refused_authority --> [*]
    refused_preroll --> [*]
    internal_error --> [*]

    note right of no_result
        Not an error path. The caller
        kills us on purpose; absence of
        a result means only that we were
        stopped before we could write one.
    end note
```

#### 3.10.3 The result artifact

**[`04`](04_Contracts.md) `C10` owns the artifact's fields. This section owns the tool's behaviour
around it**, which is four rules:

1. It is written **in every terminal outcome the tool survives**, including a refusal that produced no
   session, and including `run_stopped`.
2. It is written **to the path `--result` names** — a separate path rather than one derived from the
   session identity, because a phase-0 refusal never reaches [`08`](08_Collection_And_EPoL.md) D8.4's
   session assignment and so has no session root to be written under, and a path refused inside either
   corpus root (check 40), because a result must survive an outcome that produced no corpus.
3. It is a **pointer set over artifacts that already exist**, never a second description of the corpus.
   The corpus describes itself in the manifest ([`06`](06_Truth_And_Annotation.md) §8.4); a second copy
   is a second thing that can be stale.
4. It carries **no aggregate verdict**. **Individual gate records stay in full** — this check ran, this
   is what it observed, this is the threshold it compared against — because those are facts about our
   own data (§7.2). What it does not carry is a single field saying *therefore this corpus is fit*,
   because fitness is relative to a purpose the caller never told us. [`04`](04_Contracts.md) `C10`
   holds the same line for the same reason.

```jsonc
{
  "spec_version": 2,
  "outcome": "run_stopped",              // the name from §3.10.2; the exit status is its index
  "exit_status": 6,
  "caller": "unattended",
  "caller_label": "…",                   // opaque to us; see §3.10.4
  "tool_version": "…", "schema_version": 3,
  "effective_configuration_digest": "…", // the same digest the lock carries
  "resolution_report": "out/bahonar_night.resolution.json",
  "lock": "out/bahonar_night.lock.json",
  "launch_echo": { … },                  // §6.4 — the same block a human would have read
  "refusals":  [ { "check": 14, "field": "solar.vehicle_lights", "message": "…" } ],
  "warnings":  [ { "code": "lighting_honours_no_epoch", "message": "…",
                   "adjudication": "proceed", "adjudicated_by": "configs/bahonar_night.run.json" } ],
  "produced": {
    "manifest": "/data/truth/cap-20260308-2300/manifest.json",   // null on a refusal
    "roots": { "observation": "/data/obs/cap-…", "truth": "/data/truth/cap-…" },
    "closed_by": "signal:SIGTERM",
    "window": { "begin_s": 371700, "end_declared_s": 373500, "end_reached_s": 372480,
                "civil": ["2026-03-08T23:00:00+03:30", "2026-03-08T23:08:00+03:30"] },
    "channels": [ { "sensor_id": "OVERWATCH-1",
                    "captured": 1762, "written": 1749, "recorder_dropped": 0 } ],
    "gates": [ { "id": "capture.render_set_unpaired",
                 "observed": 3, "threshold": 0, "comparison": "equals", "met": false },
               { "id": "capture.recorder_dropped",
                 "observed": 0, "threshold": 0, "comparison": "equals", "met": true } ]
  }
}
```

**As built**, `RunResult` carries `result_version`, `outcome`, `exit_status`, `closed_by`, a one-line
`detail`, `caller`, `caller_label`, `session_id`, `tool_version`, `schema_version`,
`effective_configuration_digest`, the paths of the `resolution_report`, the `lock` and the replayable
`effective_configuration`, the `launch_echo`, `authority_holder`, `refusals`, `warnings` (each with
its `adjudication` and `adjudicated_by`), `expectations_declared` (§13 question 9's recommendation),
and `produced`: the capture directory, `closed_by`, the window (`begin_s`, `end_declared_s`,
`end_reached_s`, where the end came from, and the civil instants of the begin and the end reached),
each channel's counters, where every camera looked (`cameras`: a stare's pose and how it was declared,
an orbit's centre and the pose it opened on, and for a stare aimed at the rendered traffic the point
it resolved to, the vehicles and frame it was measured on, how far its camera moved at the last step,
where it began to hold, and the same point as the three look-at fields), how each channel's view became
ready before the window opened (`readiness`: the ceilings, where the wait began, and per channel the
frame, ticks and wall clock at which its tiles were in, the frame, ticks and frames at which its
picture settled and the residual, the blocks rendered vehicles took out of that comparison and the
share of the view judged, how many comparisons were judged and how many could not be and why, where a
witness stood if it did not finish, and any return of the tiles to streaming; §6.3), the window's
population (`admissions`: the vehicles SUMO had, every one drawn, at the pass the window opened on, and
over the passes inside the window how many there were and the largest population at one), the gate
records, the session's clock, SUMO release, pace,
sun and layers, its compile lock — whether the scenario was compiled, the SUMO release that routed it
and the world it was compiled for — and whether SUMO could teleport a blocked vehicle, the prewarm's
achieved factor, the run id every recorder was given, and each termination step as it ran. Three differences from the sketch above, each a fact about the tree:

- **One root, not two.** The recorder writes a capture's image and sidecar into one directory, so
  `produced` names `capture_directory`, with one directory per channel inside it.
- **`captured` is null.** The recorder counts captures written (`Saved`) and captures its queue had no
  room for (`Dropped`), and none accepted into the queue, so the difference a kill leaves (K4) cannot
  be stated; the gate `capture.captured_minus_written` is recorded as skipped with that reason.
- **The gate records are written once, at the terminal outcome.** There is no run manifest to append
  them to as they change (§7.2), so a run killed with no chance to flush leaves its lock, its resolution
  report and its captures, and no gate records.

`C10`'s `run_record.jsonl` is not written. `C10` makes it the run manifest's owner's to write, and
most of its required rows project the run manifest, which does not exist; its `run_closed.completion`
values also have no counterpart for a window's declared end or for a loud condition's self-stop, which
`closed_by` above carries.

Four properties, each the machine's version of something §7.2 does for a reader:

- **`produced` is a pointer set, not a copy** (rule 3 above).
- **`gates[]` is a list of observations, not a verdict.** Every entry names what it measured, what it
  compared against, and whether it met it. A caller that wants a single bit computes one from the
  subset it cares about; we do not compute it for them, because which subset matters depends on what
  they are building.
- **`channels[]` carries `captured` as well as `written`**, so the loss a kill causes is visible as
  their difference (§3.10.2). This is the field that makes an unflushed kill honest.
- **`warnings[]` carries its adjudication and the artifact that granted it** (§6.4). "Which warnings
  were accepted, and on whose authority" is the question [`07`](07_Scenario_Authoring.md) §5.3 says
  warnings exist to raise, and for an unattended run it is the only answer available.

**What this section needs from [`04`](04_Contracts.md) `C10`**, stated as properties in the manner of
§4.6:

| # | Property needed | Why this section needs it |
|---|---|---|
| **K1** | **No aggregate fitness field**, and `gates[]` shaped as observations — id, observed, threshold, comparison, met — rather than pass/fail labels | This surface must publish exactly the fields `C10` defines, and §7.2's gate records are the projection `C10` reads |
| **K2** | **An absent result means only that the tool was stopped before it could write one**; a caller that finds no result reads the artifacts on disk | The caller kills us deliberately. Deriving a judgement about the data from our death makes a normal operating mode look like a fault |
| **K3** | **`closed_by`, `end_declared_s` and `end_reached_s` are fields of the record**, so *stopped* and *finished* are legible without comparing timestamps | §3.10.2's only entitled distinction |
| **K4** | **`captured` and `written` are distinct per-channel counters** | Without both, an unflushed kill under-reports its own loss (`FrameRecorder.cs:184-185`) |

#### 3.10.4 What "the same configuration produces the same corpus" costs, precisely

R1's test holds for a *replay*: the manifest's effective configuration is itself a valid run
configuration (D12.3), so reading it back reproduces the run. Three residues are worth naming rather
than assuming away. **None of them is a cadence requirement** — how often the caller regenerates, and
whether it wants two runs to match, is its business.

| Residue | What it is | Ruling |
|---|---|---|
| **A drawn seed** | D12.11 keeps `random`, resolving to a drawn value that is then recorded. A recorded draw makes a run reproducible *afterwards*, which is the only reproducibility this surface owes anybody | **The requirement is survival, not repetition: a drawn seed is written into the lock and into the manifest's effective configuration before the first capture** (check 39), so a run killed one second later is still explicable. A caller that wants a reproducible draw supplies `caller_label` and gets one — the draw is then a deterministic function of the effective-configuration digest and that label — but nothing requires it, and **the tool never interprets the label** |
| **A rebuilt world** | World build carries nondeterminism no seed covers: [`09`](09_Toolchain_And_Packaging.md) D9.9 records `OsmClipper.py:154, :219` emitting nodes in `set` iteration order, which is a reproducibility hazard for anything that hashes the output (*carried forward from `09`, not re-measured here*). [`02`](02_Use_Cases.md) UC-6 refuses a swept parameter that changes the network, on the same grounds | **A capture run does not build a world** (check 38), for two reasons. The world package is an input bound by digest at layer 3; and a build is a long irreversible act whose half-written product a kill would leave behind, which is exactly the artifact the brief forbids. World building stays where it is — `run_SCTMV.py --build`, §9.1 — and is a separate, attended act |
| **A tool whose defaults moved** | D12.3 carries `tool_default` beside every value precisely so a default change shows in a diff, and the lock carries the tool version | **The tool reports; it never compares runs.** `RunResult` states `tool_version` and `schema_version`; deciding that one run is not comparable with another is the caller's judgement, and building a comparator here would be the scheduler the brief says is not ours |

---

## 4. Time of day

This is the axis that triggered the section, and §1.6 measured it as the worst-served part of the
existing surface. The mechanisms are complete; what is missing is expression, defaults, coupling and
recording.

### 4.1 What exists, verified

Re-resolved against the tree on 2026-09-18 rather than carried from the brief.

| Layer | Surface | Cited |
|---|---|---|
| Python shim | `set_solar_time(hours)`, `set_solar_date(y,m,d)`, `get_solar_state()`, `set_time_advance(enabled, rate)` | `carlanet/__init__.py:1500, :1506, :1511, :1535` |
| C# client | `SetSolarTimeAsync`, `SetSolarDateAsync`, `GetSolarStateAsync`, `SetTimeAdvanceAsync`, `GetCachedSolarState` | `CarlaClient.cs:1043, :1048, :1053, :1058, :1991` |
| Publication | Eleven doubles at offset 36 of the extended world-observer header, cached per tick, no RPC | `CarlaClient.cs:165-169, :1842-1855` |
| Capture record | `<_solar>` element on every sidecar, and a `carla:solar` JSON `tEXt` chunk in every PNG | `CotWriter.cs:50-65`; `SolarMetadata.cs:6-14`; `PngEncoder.cs:44`; read at `FrameRecorder.cs:162` |
| Vehicle lights | `Actor.set_light_state` / `get_light_state` over `VehicleLightStateFlags`; `SetVehicleLightStateCommand` is one of the batch commands | `carlanet/__init__.py:781, :786, :479, :487, :1147` |

**Per-capture recording is already solved.** Every frame already carries the sun that rendered it,
twice, in two independently-readable places. That is why §1.6's defect is dangerous rather than
merely annoying: the record is trustworthy and the value in it is wrong.

### 4.2 What is missing

| # | Gap | Evidence |
|---|---|---|
| 1 | **A scenario declares no civil time.** The mapping from simulated seconds to civil time exists only inside trip identifiers and in the author's head | [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a, measured on the sizing scenario |
| 2 | **Nothing couples the sun to the window.** `setup_solar_time` runs once, before any window exists | `run_SCTMV.py:138`; `WorldBuilder.py:214` |
| 3 | **The default is wrong for every scenario that is not about noon**, and applies even in attach mode | `WorldBuilder.py:231-232`; `run_SCTMV.py:131-138` |
| 4 | **The policy is not recorded at run level.** Per capture, `advancing` and `rate` ride in `<_solar>`; the *run's intent* — freeze or advance, and from which epoch — is nowhere. [`06`](06_Truth_And_Annotation.md) §8.4's manifest has no solar block at all | Read from the manifest schema |
| 5 | **A world with no sun is a warning, not a refusal** | `WorldBuilder.py:244-245` |
| 6 | **`rate` is unpinned and under-specified.** Its help says "wall-clock in `--async`, simulation time under synchronous ticking" but says nothing about which simulated second | `:256-270`; `carlanet/__init__.py:1535-1541` |
| 7 | **No exposure control exists**, so illumination cannot be compensated for even deliberately | §1.3, measured |

### 4.3 How an operator expresses it

The `solar` block of the run configuration is the scenario's `illumination` object
([`04`](04_Contracts.md) C9 §11.5), field for field, because that object is what the session reads
(`CarlaNet.CoSim.IlluminationPolicy`) and a second vocabulary beside it would be a second thing to
disagree with:

```jsonc
"solar": {
  "policy": "freeze_at_window_start",   // freeze_at_window_start | advance | freeze_at | ignore
  "rate_sun_s_per_sim_s": null,         // advance only; pinned to 1.0 in a capture run
  "freeze_at_civil_time": null,         // freeze_at only: HH:MM:SS
  "freeze_date_advances": false,        // a freeze only: whether the sun's date follows the calendar
  "require_sun": null,                  // null means required
  "note": "one lighting condition per window"
}
```

Every field resolves at layer 4 from the scenario's illumination default — the compiler refuses a
scenario that declares none ([`07`](07_Scenario_Authoring.md) check 39) — and a higher layer may
override it, the override recorded against the value it replaced (§3.7). One short alias exists,
`--solar <policy>`, because the policy is the one field an operator flips run to run — which is the
requirement [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a item 2 states. An illumination field that belongs
to the policy an override replaced is dropped and recorded as dropped (`RunConfigurationResolver`),
so `--solar advance` over a scenario that freezes does not carry `freeze_date_advances` into a policy
that would refuse it.

**The epoch is a binding, not a choice.** It comes from the scenario package (layer 4) as
`scenario.epoch` and an operator cannot override it, for the same reason they cannot override the
world digest: a scenario that asserts 23:00 and a run that renders 12:00 produce a corpus that
contradicts itself, and the whole point of §3.5's layer-3/4 distinction is to make that
unrepresentable. Its shape — civil date, time zone, and the civil instant `t = 0` denotes — is
[`11`](11_Time_And_Illumination.md)'s to define; §4.6 states what this section needs it to support.

**The four policies, and what each pins in a capture run.**

| Policy | Sun behaviour | `rate_sun_s_per_sim_s` | In a capture run |
|---|---|---|---|
| `freeze_at_window_start` | Held at the civil instant the window opens — `capture.window`'s begin, which `run_capture` gives the session as `window_opens_at` — with its date held or following the calendar per `freeze_date_advances`; the render prewarm before it is lit by the same sun | n/a | Permitted; the recommended policy ([`00`](00_Overview.md) §6) |
| `advance` | Tracks simulated time. The session writes the sun for every tick at that frame's instant, with the engine's own advance off ([`11`](11_Time_And_Illumination.md) D11.19) | **Pinned to 1.0** (`pinned_by_policy`), not operator-settable (check 3); a scenario default at another rate is refused (check 15) | Permitted |
| `freeze_at` | Held at `freeze_at_civil_time` whatever instant the window opens at | n/a | Permitted |
| `ignore` | Left as the world holds it; the run's lighting honours no epoch | n/a | Permitted with a warning, `lighting_honours_no_epoch` (check 15) |

An advancing sun at any rate other than 1.0 — `accelerated` in the interactive path, today's
`--time-rate` (`:264-270`) — is genuinely useful for look development, watching a site through a day in
a minute. It is **refused for a capture run, not removed**, because under it the recorded
`<_solar solar_time=…>` of successive frames no longer corresponds to the scenario's own clock, which
is the precise contradiction this whole requirement exists to prevent. Confining a capability to the
mode it is correct in is not losing it ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §4).

**What `rate = 1.0` means, pinned down.** Under [`01`](01_Architecture.md) D1.1 the session cues the
world exactly once per `world_delta_s` of simulated time and per D1.13 the SUMO step, the world delta
and the capture rate are in integer ratio. Therefore **one tick is one `world_delta_s` of simulated
time, and `rate = 1.0` makes one sun-second equal one simulated second — the same second the
scenario's `t` counts in.** Any other rate breaks that identity. This is the statement
[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a asks for, and it holds only because the clock contract holds;
§6 check 9 re-checks the ratio at launch for that reason.

### 4.4 No default, and why the policy is always stated

A frozen run and an unconfigured run are byte-identical ([`06`](06_Truth_And_Annotation.md)), so the
policy may never be absent: **there is no tool default** ([`00`](00_Overview.md) §6). The scenario
states one, the compiler refuses a scenario that does not, and a run takes it or overrides it — so
every run's policy has a provenance, and none is a default nobody chose.

The reason it must be stated rather than defaulted is the asymmetry between the two ways an unstated
policy fails:

- A run that wanted constant illumination and got an advancing sun: a 1,800 s window
  ([`10`](10_Scale_And_Performance.md) D10.3) advances the sun by 30 minutes, visibly at dawn or dusk,
  and every frame's `<_solar>` records it correctly — a small, self-describing variation.
- A run that wanted changing light and got a frozen one: 1,800 simulated seconds under a stationary
  sun — a state that cannot occur — and nothing flags it, because a constant `<_solar>` block is
  exactly what a freeze is supposed to produce.

`freeze_at_window_start` is the recommended value because a window is meant to be one lighting
condition; it is recommended, not silent.

**Vehicle lights are not offered.** The session writes no vehicle lamps — no light-state command is
issued anywhere in `CarlaNet.CoSim` — so there is no `solar.vehicle_lights` field to state and check 14
has nothing to compare. When lamps are built, the field is a conditional requirement: default `off`,
and no default in a window whose sun falls below −6°, so that a dark corpus with unlit vehicles and a
dark corpus with lit ones are both deliberate choices. *Inference, labelled:* brake and indicator state
is the most detectable vehicle signature available to a night EO detector, so the choice plausibly
dominates a night corpus's usefulness; it belongs to whoever captures the first night window.

### 4.5 What reaches the manifest

[`06`](06_Truth_And_Annotation.md) §8.4's `session` block gains one sibling. It is small because the
per-capture record already exists (§4.1); what is missing is the run's *intent* and the *confirmation*
that the intent took effect:

```jsonc
"solar": {
  "epoch": { "date": "2026-03-04", "time_zone": "+03:30", "t0_civil": "00:00:00",
             "source": "bahonar_pattern_of_life@a91c3f" },
  "policy": "advance",
  "rate": 1.0,
  "vehicle_lights": "from_sumo",
  "window_civil": ["2026-03-08T23:00:00+03:30", "2026-03-08T23:30:00+03:30"],
  "applied":   { "solar_time": 23.0, "date": "2026-03-08", "time_zone": 3.5,
                 "sun_elevation_deg": -37.2, "sun_azimuth_deg": 12.4, "advancing": true, "rate": 1.0 },
  "confirmed": { "at_tick": 7410000, "source": "get_solar_state" },
  "closing":   { "solar_time": 23.5, "sun_elevation_deg": -41.8 }
}
```

`applied` and `confirmed` are the part that matters and the part that does not exist today.
`WorldBuilder.py:238-247` sets the sun and logs *what it asked for*; it never reads back. The capture
surface reads `get_solar_state()` after applying, at the first cued tick, and records what the world
actually reports — which costs no RPC (`CarlaClient.cs:1991`). A disagreement between `applied` and
what was requested is a refusal at launch (§6 check 13), not a line in a log.

### 4.6 What this section needs from `11_Time_And_Illumination.md`

Stated as properties, per the brief's rule on cross-section dependencies.

| # | Property needed | Why this section needs it |
|---|---|---|
| **N1** | **An epoch type that carries a civil date, a time zone offset and the civil instant `t = 0` denotes**, and that supports a **half-hour offset** — the sizing scenario's site is at +03:30 | §4.3's binding; a time zone modelled as an integer would silently shift the sizing scenario's sun by 30 minutes |
| **N2** | **A total function from `(epoch, simulated_seconds)` to a civil instant and to the arguments of `set_solar_time` / `set_solar_date`** | §4.5's `window_civil` and `applied`; and §6 check 12 has to evaluate it for a window before any server exists |
| **N3** | **A statement of whether the epoch's time zone and the sun's time zone are the same quantity.** The sun's is derived from map longitude (`carlanet/__init__.py:1500-1502`), which for the sizing scenario is longitude ≈ 56°E ⇒ +03:44, against a civil +03:30 | If they differ, one of them is wrong in the record and this section needs to know which field carries which |
| **N4** | **A sun-elevation function** over `(epoch, simulated_seconds, latitude, longitude)` usable offline | §4.4's conditional requirement and §6 check 14 must evaluate darkness at launch, on a machine with no server and no GPU — [`02`](02_Use_Cases.md) D2.2's property |
| **N5** | **A ruling on daylight saving.** `setup_solar_time` takes a host-local date with no DST handling and the memory record notes DST was explicitly disabled at spawn | §4.5's `time_zone` is recorded as a number; whether it is fixed or seasonal changes what a corpus spanning a transition means |
| **N6** | **Confirmation that `rate` is sun-clock seconds per simulated second under synchronous ticking**, as §4.3 derives | Does not arise under `advance`, where the session writes the sun per tick and the engine's rate is not used. It remains open for `accelerated` in the interactive path, where the engine advances the sun |

---

## 5. The complete toggle inventory

### 5.1 Four mutability classes

Named descriptively, because "static" and "dynamic" do not say what is at stake.

| Class | Meaning | Recorded as |
|---|---|---|
| **Bound** | Fixed by an artifact, **or by a ruling in a sibling section that this surface expresses rather than re-offers.** Not the caller's to set; an attempt is a refusal naming both values | A binding in the effective configuration and the lock file |
| **Session-fixed** | The operator sets it; it is then fixed for the session's life. Changing it mid-run would make the corpus's own description of itself false | A field in the effective configuration, written once |
| **Degradation-only** | Never set by the operator. Changed only by [`10`](10_Scale_And_Performance.md) §7's degradation ladder, and always recorded at the instant it changes | A timestamped entry in the manifest, per window |
| **Run-mutable** | May change mid-run because it does not change what the corpus *is* | A `control_events[]` entry in the manifest: tick, field, old value, new value |

The dividing line between Session-fixed and Run-mutable is one question: **would a consumer reading
the corpus be wrong if this changed and they did not know?** If yes, it is Session-fixed. The
occlusion estimator is the instructive case: it looks like a toggle (it is one today, implicitly, via
`--no-occlusion` at `:542-552`), but [`08`](08_Collection_And_EPoL.md) D8.19 makes a capture whose
occlusion could not be paired *excluded from the unoccluded denominator entirely* — so turning it off
mid-run silently changes the meaning of the denominator. Session-fixed.

**Bound covers a sibling section's ruling as well as an artifact's value**, and three rows depend on
that: `synchronous` is Bound by [`10`](10_Scale_And_Performance.md) D10.10, `telemetry.on_tick_thread`
by D10.11, and the drop policy when the external chain falls behind by
[`08`](08_Collection_And_EPoL.md) §11.3 and D8.23, which rules it drop-oldest-and-count. This surface
expresses all three and re-offers none of them. A toggle for a decision another section has taken is a
way to contradict that section from a configuration file.

**The live and unattended axes earn no fifth class**, and that is worth stating because they look as
though they should. `caller`, `on_warning.*`, `caller_label` and `expect.*` never reach the world and
describe the *launch decision* rather than the corpus, which is a genuinely different kind of thing. But
the governing question places them anyway: a consumer reading a corpus **would** be wrong not to know
that its warnings were adjudicated by a file rather than by a person, and that nobody was watching while
it was produced — so `caller` and `on_warning.*` are Session-fixed on the existing test, with no new
machinery. `caller_label` and `expect.*` answer *no* to the question, and they are recorded anyway, in
the lock rather than in the corpus's description, because they are the only surviving evidence of what
the caller believed it was running — and under a caller that may kill us at any instant, evidence
written before the first capture is the only evidence guaranteed to exist.

### 5.2 The inventory

Each row's first cell is a field's path in the run configuration, as `RunConfiguration.FIELDS`
names it and the published schema (`CarlaControl/schemas/run_configuration.schema.json`) states it;
its second is the tool default, which a test holds equal to the field table. Defaults marked **—** have
no tool default: a package or the site profile supplies the value, or the run is refused until the
field is supplied (R5). Defaults marked **cond.** are conditional requirements (§3.5). A row whose
default reads *not offered* is a toggle this section specifies and the tree has nothing to set it on;
the reason is given, and check 1 refuses the key.

#### Mode and authority

| Toggle | Default | Class | Source |
|---|---|---|---|
| `mode` | **—** from the tool; a scenario package implies `sumo_driven_playback` (layer 4) | Session-fixed | [`01`](01_Architecture.md) §5.1. The schema names all four modes; check 4 refuses every one but `sumo_driven_playback`, and the traffic-manager path is `run_SCTMV.py` |
| `ambient_traffic` | *not a field in `sumo_driven_playback`* | Bound | [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3 item 4; the session's population lease |
| `storyboard` | *not offered*: `run_capture` runs `sumo_driven_playback` only, and storyboard execution under SUMO drive is refused until SUMO mirroring exists | — | [`01`](01_Architecture.md) §5.2 |
| `caller` | `attended` | Session-fixed — a corpus whose warnings were adjudicated by a file is not the same object as one that was watched (§5.1) | §3.10, §6.4 |
| `caller_label` | `null` | Launch provenance, recorded in the lock; **never interpreted by the tool** | §3.10.4 |
| `scenario_package` | **—** | Session-fixed (the choice); its contents are Bound | a scenario id, found as `<id>/<id>.lock.json` under `paths.scenario_root`; a package directory; or its `.lock.json` |
| `world_package` | `null` | Session-fixed (the choice); its contents are Bound | resolves, as `derived`, to the package the scenario lock names under `paths.world_package_root` |
| `world_build` | *not a field in a capture run*; refused (check 38) | **Bound** — a capture run binds a world package, it does not build one | §3.10.4; [`09`](09_Toolchain_And_Packaging.md) D9.9; [`02`](02_Use_Cases.md) UC-6 |

#### Pacing, live handover and transcript

The drop policy is deliberately absent from this table as a choice: it is Bound (§5.1).

| Toggle | Default | Class | Source |
|---|---|---|---|
| `pacing.mode` | `as_available` | Session-fixed | [`08`](08_Collection_And_EPoL.md) §11.1 |
| `pacing.real_time_factor` | `1.0` | Session-fixed. Applies under `wall_clock`; under `as_available` it resolves `not_applicable` and a value given is refused (check 41) | the session's `real_time_factor` (`RealTimePacer`, [`03`](03_CoSimulation_Runtime.md) D3.25) |
| `pacing.min_achieved_factor` | **cond.** — no default under `wall_clock` (§7.5 property L3); not a field under `as_available` | Session-fixed | checked against the prewarm (check 44) and loud while the run proceeds (§7.4.1) |
| `pacing.window_s` | `5.0` | Session-fixed | the session's `pacing_window_s`, the wall-clock span the achieved factor is measured over |
| `pacing.on_consumer_slow` | *not a field* | **Bound** to drop-oldest-and-count | [`08`](08_Collection_And_EPoL.md) §11.3, D8.23 |
| `handover.enabled`, `handover.endpoint`, `handover.transport`, `handover.channels`, `handover.queue_depth` | *not offered*: no handover transport exists | — | [`02`](02_Use_Cases.md) UC-8; [`08`](08_Collection_And_EPoL.md) §11.2, §11.3 |
| `transcript.enabled`, `transcript.root`, `transcript.sources` | *not offered*: no transcript writer exists | — | [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c; §7.4.3 |

#### Bindings

Every row here is supplied by a package and can be restated by a higher layer only with the package's
own value (check 3). A replayed effective configuration carries them, so a replay against a package
that has since changed is refused naming both values.

| Toggle | Default | Class |
|---|---|---|
| `world.map_name`, `world.network_fingerprint`, `world.opendrive_sha256`, `world.origin_latitude`, `world.origin_longitude`, `world.netconvert_version` | **—**: the world package's `world.json` | **Bound** |
| `scenario.scenario_id`, `scenario.lock_sha256`, `scenario.epoch`, `scenario.epoch_block_sha256`, `scenario.sumo_step_s`, `scenario.sumo_seed`, `scenario.end_s`, `scenario.catalogue_digest` | **—**: the scenario lock | **Bound** |
| the vocabulary and the supervision plan | not fields: the lock digests the supervision plan and the files that carry the vocabulary, and check 49 refuses a file it no longer digests | **Bound** |

#### Clock and window

| Toggle | Default | Class | Source |
|---|---|---|---|
| `capture.world_delta_s` | `0.05` | Session-fixed | today's `--fixed-delta` (`:68-74`) |
| `capture.capture_hz` | `2.0` | Session-fixed; **Degradation-only** downward when the degradation ladder exists | today's `--record-hz` (`:512-518`); [`10`](10_Scale_And_Performance.md) §7 row 6 |
| `synchronous` | *not a field in a capture run*: the session takes the world's clock | **Bound** | [`10`](10_Scale_And_Performance.md) D10.10 |
| `capture.window` | **—** (a scenario-declared name, an explicit `begin_s:end_s` pair, or `begin_s:` with no end) | Session-fixed | [`01`](01_Architecture.md) OQ3 / [`02`](02_Use_Cases.md) OQ2, resolved as D12.4. A window with no end resolves to the scenario's `end_s`, and the echo says so |
| `capture.prewarm_s` | `300` | Session-fixed | [`10`](10_Scale_And_Performance.md) §8. The session fast-forwards SUMO to `begin_s − prewarm_s`, renders from there, and the recorders start at `begin_s`; every channel's view is waited on inside it, and one not ready by `begin_s` refuses the run (checks 50 and 51, D12.38) |

#### Render set

By default every vehicle SUMO has in a capture window, or in its prewarm, is drawn, from the frame
SUMO first reports it in until SUMO removes it or the window closes, at any range from a camera. The
one exception is a vehicle of a type with no measured body, which is simulated and never drawn; the
compiler refuses a scenario whose vehicle class names a blueprint the catalogue did not measure
([`07`](07_Scenario_Authoring.md) check 14), and check 25 re-runs that against the server.

**Two optional performance controls trade fidelity for speed, and both are off unless set.**
`capture.render_set` with the `capture.render_*` fields limits which vehicles get a body at all: a
vehicle outside the limit is simulated by SUMO, so the traffic is the scenario's, and is not in
CARLA -- no body, no frame, no truth record -- and the echo says so before anything is acquired,
the closeout and the run result count what the limit left out ([`03`](03_CoSimulation_Runtime.md)
D3.41, [`06`](06_Truth_And_Annotation.md) §4.4). `capture.draw_distance_m` limits only how far from a
camera a body is drawn: every vehicle keeps its body, its pose and its truth, and each sidecar marks
the vehicles its camera did not draw ([`03`](03_CoSimulation_Runtime.md) D3.40,
[`06`](06_Truth_And_Annotation.md) §8.2). Neither is recommended for any scenario; each is a choice an
operator makes for a run, and each is recorded in the lock with every other field.

| Toggle | Default | Class | Source |
|---|---|---|---|
| `capture.render_set` | `all` — every vehicle SUMO has | Session-fixed. `circle` draws only the vehicles inside `capture.render_region`; `cameras` those inside or about to enter any channel camera's ground footprint, orbits included, with the region deciding until the cameras are placed where one is given and every vehicle where none is. Each RGB camera is registered with the session and let go before it is destroyed | the session's `RenderSet` policy ([`03`](03_CoSimulation_Runtime.md) §8.3); check 53 |
| `capture.render_region` | `null` | Session-fixed. `{x_m, y_m, radius_m}` in CARLA's frame, negated into SUMO's at the call; required under `circle`, read until the cameras are placed under `cameras`, refused under `all` (check 53) | `RegionRenderSetPolicy` |
| `capture.render_hysteresis_m` | `60.0` | Session-fixed. How much further out than the radius a vehicle keeps its body; under `cameras`, the band beyond a footprint's widest admission threshold | `RegionRenderSetPolicy`, `CameraFootprintRenderSetPolicy` |
| `capture.render_cap` | `null` — no limit on the count | Session-fixed. How many vehicles may hold a body at once, under any render set; the vehicles it declines are counted pass by pass | the policy's `Capacity` |
| `capture.render_min_pixels` | `2.0` | Session-fixed. Under `cameras`, the range cap: where the catalogue's longest body covers fewer than this many pixels along its length anywhere in the picture | `CameraFootprintRenderSetPolicy` |
| `capture.render_admit_lead_s` | `3.0` | Session-fixed. Under `cameras`, simulated seconds of a vehicle's own travel ahead of a footprint it is admitted at | `CameraFootprintRenderSetPolicy` |
| `capture.render_release_lag_s` | `5.0` | Session-fixed. Under `cameras`, simulated seconds a vehicle drawn is held after it last was within reach of a footprint | `CameraFootprintRenderSetPolicy` |
| `capture.road_layer_visible`, `capture.signal_layer_visible` | `false`, `false` | Session-fixed; written once by the session before the first tick and given back on every exit path | [`13`](13_Work_Breakdown.md) §10, `LayerVisibilityLease` |
| `capture.draw_distance_m` | `null` — every body drawn at any range | Session-fixed. An optional performance control: no camera draws a body farther than this from it, while every vehicle keeps its body, its pose and its truth; each channel's sidecars mark the vehicles its camera did not draw (`beyond_draw_distance`). Check 52 refuses one that does not reach the point a channel is aimed at | the session's `DrawDistanceMetres`, set once on each pooled body by `set_actors_max_draw_distance` ([`03`](03_CoSimulation_Runtime.md) §8.3, D3.40); [`06`](06_Truth_And_Annotation.md) §8.2 |
| `aoi_max_relations_per_vehicle` | *not offered*: areas of interest are not built | — | [`10`](10_Scale_And_Performance.md) §8, D10.8 |

#### Camera rig — per channel

| Toggle | Default | Class | Source |
|---|---|---|---|
| `capture.channels` | **—**: one object per channel, at least one | Session-fixed; every channel is recorded | the rows below are each object's fields |
| `sensor_id` | **—** when more than one channel | Session-fixed | [`08`](08_Collection_And_EPoL.md) D8.4 |
| `pattern` | `stare` | Session-fixed | [`08`](08_Collection_And_EPoL.md) §3.3 |
| `fov` / `width` / `height` | `90.0` / `1280` / `720` | Session-fixed | today's `:236, :272-273` |
| `capture_rgb` | `true` | Bound — a channel without it is not a channel | [`08`](08_Collection_And_EPoL.md) D8.2 |
| `capture_depth` | *not offered*: the recorder writes no depth imagery; a depth camera is spawned at a stare channel's pose only to measure occlusion (`occlusion.enabled`) | — | D8.2 |
| `capture_segmentation` | *not offered*: the recorder writes no segmentation | — | [`08`](08_Collection_And_EPoL.md) OQ2 |
| `depth_max_range_m` | *not offered per channel*: `occlusion.depth_max_range_m`, one range for every depth camera | — | `:274-285`; and see §1.5's divergent second default |
| `sensor_tick` | *not a field*: `1 / capture_hz` on every camera, so a camera renders only the frames the recorder keeps | Session-fixed | `ActorBlueprintFunctionLibrary.cpp:248`; check 10 holds by construction |
| `post_process_profile` | `Default` (EV100 +12.32, measured by [`08`](08_Collection_And_EPoL.md) §2.9) | Session-fixed. Set on the camera by name, spelt with the file's own case; the digest of the JSON the server loaded is not readable, so check 43 is not built | [`08`](08_Collection_And_EPoL.md) D8.27, D8.28; see the correction below |
| `exposure` (a numeric exposure value) | **not offered** — no numeric exposure attribute exists on any camera blueprint (§1.3) | — | measured; check 16 names `post_process_profile` as the field that does the job |
| `orbit_radius_m`, `orbit_altitude_m`, `orbit_period_s` | `200.0`, `518.2`, `240.0` | Session-fixed | today's `:598-615`, **converted to metres** (§1.5) |
| `orbit_centre_x_m`, `orbit_centre_y_m` | **cond.** — required when `pattern` is `orbit`; refused with `stare` | Session-fixed | today's `--orbit-x` / `--orbit-y`, which fall back to the start pose when absent (`OrbitSensorController.py:235-242`) |
| `orbit_centre_z_m` | `0.0` | Session-fixed | today's fixed `center_z` (`OrbitSensorController.py:215`) |
| `stare_look_at_x_m`, `stare_look_at_y_m` | **cond.** — a stare gives these, or `stare_look_at_target`, or the five `stare_*` pose fields below: exactly one of the three; refused with `orbit` | Session-fixed | [`08`](08_Collection_And_EPoL.md) §3.3 |
| `stare_look_at_z_m` | `0.0` | Session-fixed; not used with `stare_look_at_target`, whose point carries the vehicles' own height | the implicit look-at height of `camera_transform` in `CarlaNet/python/run_sumo_drive.py` |
| `stare_look_at_target` | **cond.** — `rendered_traffic`, in place of a look-at point or a pose; refused with `orbit`, and with a prewarm shorter than one SUMO step (check 47) | Session-fixed; the camera follows the traffic through the prewarm until one SUMO step and 120 ticks before the window opens (7 s at the defaults), then holds one pose while its view becomes ready and for the whole window | `run_sumo_drive.py`'s `--camera-aim traffic` (`RenderedVehicleCentre`); D12.37 |
| `stare_altitude_m`, `stare_standoff_m`, `stare_bearing_deg` | `304.8`, `0.0`, `0.0` | Session-fixed | altitude and standoff are today's start pose — `--z` 1000 ft **converted to metres**, looking straight down (`CarlaControlArgumentParser.py:232-234`, `SensorRig.py:62`); the bearing puts north at the top of the picture, where the start pose's `yaw=0.0` puts east there |
| `stare_x_m`, `stare_y_m`, `stare_z_m`, `stare_pitch_deg`, `stare_yaw_deg` | **cond.** — all five or none; the alternative to a look-at point or target | Session-fixed | §9.1: a stare is sited by flying there first, and what flying produces is a pose |

**How a stare and an orbit are placed.** All positions are in CARLA's frame — metres, x east, y south,
so north is −y — and angles are CARLA's: yaw 0 faces east and −90 faces north, and a negative pitch looks
down. A stare aimed at a point stands `stare_standoff_m` back from the look-at point, on the side
opposite `stare_bearing_deg`, and `stare_altitude_m` above it; the bearing is the compass direction the
camera looks along, clockwise from north, so the boresight passes through the point and dips by
atan(altitude / standoff). A standoff of `0.0` looks straight down with the bearing at the top of the
picture. An orbit circles its centre at `orbit_radius_m`, `orbit_altitude_m` above
`orbit_centre_z_m`, with the boresight held on the centre. It is held at the pose it opens on — angle
zero, east of the centre, where `OrbitSensorController` starts — through the pre-roll, so the view its
first capture is written from is the one whose readiness is waited on (§6.3), and it sweeps from the
window's opening. A field the chosen pattern would ignore is refused rather than dropped. The single
definition of every row in this table that is built is
`ChannelDescription` (`CarlaControl/src/carlacontrol/ChannelDescription.py`), whose defaults a test
holds equal to this table; the stare geometry is `StareAim`. `post_process_profile` is not yet in it:
it is a field of a run configuration's channel object, defined once in `RunConfiguration`, until
§9.2's `SensorRig` conversion moves it into the description.

**A stare aimed at the rendered traffic** (`"stare_look_at_target": "rendered_traffic"`) stands off
the same way from a point that is measured rather than given: the mean position, height included, of
the vehicles the session rendered on the last frame before its camera holds for the window, taken from
the poses the session wrote to bodies (`RenderedTrafficCentre`, fed by `on_pose`, which `run_capture`
binds only when a channel needs it). Its camera is spawned over the centre of the world's staging
bounds (`get_staging_bounds`) at CARLA's origin height and follows the traffic through the prewarm: after each step it is moved, with its depth
camera, to the pose around the centre of that step's last frame. It stops one SUMO step and the
picture's 120-tick ceiling before the window opens — seven one-second steps at the defaults
(`ViewReadiness.hold_lead_s`), and never before the prewarm's first step — and that step's centre is
the point, held through the rest of the prewarm and the whole window. The hold is where its view's readiness is waited on
([`03`](03_CoSimulation_Runtime.md) §9.5.1, check 50): a camera that moves between its frames never
reads settled, and the tiles' figures cover every registered view, so every channel's wait begins
there. A camera moved only as the window opened would spend the window's first captures settling, and
one followed to the last step would open the window on a view nobody had seen ready. The run result
records it (`produced.cameras[]`): the point, the vehicles and the frame it was measured on, the pose,
how far the camera moved at the last step, where it began to hold, and the point again as
`as_look_at_point`, the three look-at fields, so the view is reproducible from the record as an
ordinary look-at stare. It needs a prewarm of at least one SUMO step (check 47), and one SUMO step
more than check 51 asks of a still camera; a frame before the hold that rendered no vehicle refuses
the run at pre-roll, naming the channel; and a camera follower refuses the form, because only the
process driving the session sees the poses.

**`post_process_profile` is the exposure control, and its default is a hidden host-dependent value of
exactly the kind M2 forbids.** No camera blueprint publishes a *numeric* exposure attribute (§1.3), but
[`08`](08_Collection_And_EPoL.md) §2.9 followed the attribute that *is* there: exposure is selectable at
spawn through `post_process_profile`, over four shipped profiles spanning EV100 +12.32 to −1.06.
Verified here, and the verification found a second defect:

- The attribute's default is the **lowercase literal** `"default"`
  (`ActorBlueprintFunctionLibrary.cpp:1376`), and the path built from it is
  `Content/Carla/Config/PostProcess/<name>.json` (`PostProcessJsonUtils.h:49-52`) — *read*.
- *Measured,* the only content directory in the tree holds exactly four files, and the one that name
  must match is `Default.json`, with a capital D.
- `LoadAllPostProcessFromJsonToSceneCapture` returns `false` when the file does not load
  (`PostProcessJsonUtils.cpp:86-101`), and **the call site discards the return value**
  (`ActorBlueprintFunctionLibrary.cpp:1377-1380`) — *read*.
- *Inference, labelled, because this fork has not been run on Linux for this purpose:* the default name
  resolves on a case-insensitive Windows file system and does not resolve on a case-sensitive Linux one,
  so **the same run configuration produces a differently-exposed camera on the two platforms**, with no
  error on either. That is M5 broken by the host, and §10's whole premise is that the two platforms run
  the same program.

Three consequences for this section, all small because
[`08`](08_Collection_And_EPoL.md) D8.27 and D8.28 already own the collection side. `post_process_profile`
becomes a real toggle in the table above rather than an absence; check 16's refusal changes from *no
exposure control exists* to *no numeric exposure exists, and here is the field that does*; and phase 1
gains check 43, which reads the loaded profile's digest back from the server — the same
request-then-confirm shape §4.5 uses for the sun, and for the same reason: a subsystem that reports what
it was asked for rather than what it did will eventually be asked for something it cannot do.

#### Solar

The `solar` block is the scenario's `illumination` object, field for field (§4.3).

| Toggle | Default | Class | Source |
|---|---|---|---|
| `solar.policy` | **—** from the tool; the scenario's illumination default (layer 4) | Session-fixed; an override is recorded against the value it replaced | §4.3, §4.4, D12.7 |
| `solar.rate_sun_s_per_sim_s` | `null` | **Bound**: pinned to `1.0` under `advance`; not operator-settable (check 3); another rate refused (check 15) | §4.3, D12.8 |
| `solar.freeze_at_civil_time` | `null` | Session-fixed; `freeze_at` only | [`04`](04_Contracts.md) C9 §11.5 |
| `solar.freeze_date_advances` | `null` | Session-fixed; a freeze only | [`04`](04_Contracts.md) C9 §11.5 |
| `solar.require_sun` | `null` | Session-fixed; `null` means required | check 23 |
| `solar.note` | `null` | Session-fixed | [`04`](04_Contracts.md) C9 §11.5 |
| `scenario.epoch` | **—**: the scenario lock | **Bound** | §4.3, D12.9 |
| `solar.vehicle_lights` | *not offered*: the session writes no vehicle lamps | — | §4.4 |

#### Telemetry and occlusion

| Toggle | Default | Class | Source |
|---|---|---|---|
| `telemetry.enabled`, `telemetry.host`, `telemetry.port`, `telemetry.ttl`, `telemetry.rate_hz`, `telemetry.stale_s`, `telemetry.affiliation`, `telemetry.truth_endpoint` | *not offered*: `run_capture` starts no Cursor-on-Target feed; each capture's truth is its sidecar | — | `:465-484` |
| `telemetry.on_tick_thread` | `false`, and not a field | **Bound** | [`10`](10_Scale_And_Performance.md) D10.11 |
| `occlusion.enabled` | `true` | Session-fixed — **not** run-mutable (§5.1). Measured on stare channels only; an orbit with it on is refused (check 47) | `:542-552`; [`08`](08_Collection_And_EPoL.md) D8.19 |
| `occlusion.margin_m`, `occlusion.samples` | `1.0`, `24` | Session-fixed | `:553-569`; [`08`](08_Collection_And_EPoL.md) OQ1 |
| `occlusion.depth_max_range_m` | `20000.0` | Session-fixed | `:274-285`; the depth camera's `max_range` |

#### Roots, seeds, the machine, diagnostics

| Toggle | Default | Class | Source |
|---|---|---|---|
| `roots.observation`, `roots.truth` | *not offered*: the recorder writes a capture's image and its sidecar into one directory, so a run has one root, `paths.capture_root`, and the two-root split is stage K's (check 17 not built) | — | [`08`](08_Collection_And_EPoL.md) D8.17, [`04`](04_Contracts.md) D4.26 |
| `paths.capture_root`, `paths.runs_root`, `paths.scenario_root`, `paths.world_package_root`, `paths.catalogue` | **—**: the site profile (layer 2) | Session-fixed | §3.5; a session writes `<paths.capture_root>/<session id>/<sensor_id>/` |
| `server.host`, `server.port`, `server.timeout_s` | `127.0.0.1`, `2000`, `30.0` | Session-fixed; a site profile may set them | `run_sumo_drive.py`'s connection |
| `sumo.home` | `null` | Session-fixed; the site profile names it, or the session searches `SUMO_HOME` and then `PATH` (check 36 refuses that under `caller: unattended`) | [`09`](09_Toolchain_And_Packaging.md) D9.6 |
| `sumo.allow_version_mismatch` | `false` | Session-fixed; the session records an accepted mismatch | check 26 |
| `seeds.sumo` | *not a run field*: `scenario.sumo_seed`, bound by the scenario package, whose SUMO configuration carries it | **Bound** | [`07`](07_Scenario_Authoring.md) D7.11 |
| `seeds.appearance`, `seeds.admission` | *not offered*: nothing consumes them — appearance is drawn by SUMO's own seed, and admission chooses nothing: every vehicle SUMO has is drawn | — | [`07`](07_Scenario_Authoring.md) D7.11 |
| `log_path` | *not offered*: `run_capture` logs to standard output | — | today's `--log` (`:493-499`) |
| `result_path` | `null` | Session-fixed; `null` is `<paths.runs_root>/<session id>/run.result.json`; **—** under `caller: unattended`, and refused inside the capture root (check 40) | §3.10.3 |
| `on_warning` | `{}` | Session-fixed — which warnings a corpus proceeded past is a fact a consumer needs (§5.1); **—** under `caller: unattended` for every code actually raised | §6.4 |
| `expect` | `{}` | Launch provenance; recorded in the lock, never in the corpus description | §6.4 |
| `write_headroom_floor_s` | `600` | Session-fixed — the one bound the tool imposes on itself, in captured seconds (check 46) | §6.2 check 46, D12.35 |
| `diagnostics` | *not offered*: `run_capture` has no diagnostics output of its own | — | today's `]` hotkey; `:439-449` |
| `monitor` | `on` | **Run-mutable**; line-oriented rather than a panel when standard output is not a terminal | §7.1, §3.10.1 M1 |
| `run_configuration_version` | `1` | — | the schema version a document is written against |

**No run-level seed exists.** The only seed a run consumes is SUMO's, and it is the scenario's: the
compiler writes it into the SUMO configuration the lock digests, so it cannot change without the
traffic changing and the lock refusing the file. Checks 18 and 39 therefore hold by construction, and
reproducible traffic follows from reproducible inputs rather than from a seed a run could vary.

### 5.3 The structural exclusions

Three combinations must be impossible rather than rejected late.

| Exclusion | Where it is caught | Why both layers are needed |
|---|---|---|
| **SUMO drive × traffic-manager ambient** | (a) The schema: `mode` is one enum and a block belonging to a non-selected mode is an **unknown key**, which is a refusal. (b) The lease: `WorldDriveAuthority` denies population authority at session start, naming the holder | (a) catches "this configuration asks for both" at compile time, with no server. (b) catches "something else is already driving this world", which no configuration can know. Neither substitutes for the other |
| **SUMO drive × storyboard execution** | Compile-time refusal until SUMO mirroring exists; then a conditional permit | [`01`](01_Architecture.md) §5.2 — an unmirrored entity is invisible to every SUMO vehicle |
| **Anything × recorded replay** | `mode` is one enum | [`01`](01_Architecture.md) §5.2 |

The lockout being a *failed session start naming the holder* rather than a warning is
[`01`](01_Architecture.md) D1.7 and [`02`](02_Use_Cases.md) D2.6; this section adds only that the
configuration cannot express the request in the first place.

---

## 6. Validation and failure

**The obligation:** a configuration that cannot work fails at launch with the reason named, not at
minute forty. [`02`](02_Use_Cases.md) UC-7 already states the sharpest instance — "a capture window
falls outside the scenario's end time: refuse at session start rather than discovering an empty world
at hour 170".

### 6.1 One compiler, not two

[`07`](07_Scenario_Authoring.md) §5 defines a compile-and-validate step with a stated refuse/warn
vocabulary, a resolution report that states what it *resolved* (D7.6), and a lock file. **The run
configuration is compiled by the same compiler, as a further phase**, emitting `<run>.resolution.json`
and `<run>.lock.json` in the same format, rendered by the same reporter. Concretely: §3.6's
`RunConfigurationCompiler` is phase 7 of the pipeline drawn in [`07`](07_Scenario_Authoring.md) §5.4,
reached after phase 6 has emitted the scenario package. A scenario that was compiled earlier is
re-bound by its lock rather than recompiled.

Three things are inherited rather than re-specified: **refuse** means nothing is emitted and no session
starts; **warn** means it proceeds and the warning appears in full in the report; and the report states
what resolved, not only what was rejected.

**As built**, the reuse is of the vocabulary and the validators rather than of one class.
`RunConfigurationValidator` reports in the compiler's own finding record, `CompileFinding`, with its
two outcomes; checks a field's shape with the scenario schema's validator
(`ScenarioSchema.validate_against`); and calls the session's validators for the rules the session owns
(`CoSimClock.ForSession`, `IlluminationPolicy.FromJson`, `SolarEpoch`). Its checks carry this
section's numbers, in their own catalogue (`RunConfigurationCheckCatalogue`), because the compiler's
numbers are a different stable sequence. The compiler's `ResolutionReport` names the scenario's
sections only, so the run writes its own `<run>.resolution.json` — the findings, the launch echo, the
effective configuration and the site profile — and its own `<run>.lock.json`, beside its result. A
scenario is re-bound by its lock (`ScenarioPackage`), never recompiled.

### 6.2 The checks

Grouped by phase. **Phase 0 needs no server, no GPU and no SUMO** — [`02`](02_Use_Cases.md) D2.2's
property, extended to the run configuration so an operator can validate a night's worth of runs on a
laptop.

#### Phase 0 — offline

Checks 1–19 below (20 and 21 are withdrawn), plus 34–42, 46–49 and 51–53 in the table that follows
the later phases.

| # | Check | Outcome | Message shape |
|---:|---|---|---|
| 1 | Document parses against the schema at its declared `spec_version` | refuse | `run configuration: unknown key 'capture.prewarm_sec' at line 14; did you mean 'capture.prewarm_s'?` |
| 2 | Every required field is supplied by some layer | refuse | `'capture.window' has no value and no default. Supply it, or name one of the scenario's declared windows: morning_shift, night_shift.` |
| 3 | No operator override targets a layer-3 binding | refuse | `'world.origin_lat' is bound by world package Bahonar@3f91ac (56.3421); it cannot be overridden. Rebuild the world to change it.` |
| 4 | `mode` is one value, and no block of a non-selected mode is present | refuse | `mode is 'sumo_driven_playback'; block 'traffic_manager' is not valid in this mode. Ambient traffic and SUMO drive are mutually exclusive (01 D1.7).` |
| 5 | Scenario lock's world fingerprint equals the world package's | refuse on recipe mismatch, **warn** on digest-only mismatch with an explicit override | `scenario bahonar@a91c3f was compiled against network fingerprint net@88b1de; world package Bahonar@3f91ac carries net@2c40aa.` — the two-tier gate of [`07`](07_Scenario_Authoring.md) check 1 |
| 6 | Catalogue and vocabulary versions are ones this tool understands | refuse | `vocabulary_version 3 is newer than this tool's 2.` |
| 7 | `window` resolves — a declared name, a pair inside `[0, simulated_span_s]`, or a begin inside it with no end | refuse, listing the declared names | `window 620000:621800 ends after the scenario's end time 604800.` A window with no end resolves to the scenario's own end and is reported as such in the echo, because a run that nothing stops still has to stop somewhere |
| 8 | `window.begin_s − prewarm_s ≥ 0` | warn | `prewarm 300 s clipped to 180 s: the window begins at t=180.` |
| 9 | `sumo_step_s`, `world_delta_s` and `capture_hz` are in integer ratio | refuse | `clock ratio: sumo_step 1.0 s / world_delta 0.03 s is not an integer (33.33). Choose a world_delta that divides the SUMO step.` — [`01`](01_Architecture.md) D1.13 |
| 10 | `sensor_tick` is consistent with `capture_hz` | warn | `sensor_tick 0.0 renders every channel at world rate while the recorder keeps 1 frame in 10.` |
| 11 | `sensor_id` present and unique when channels > 1 | refuse | `two channels both name sensor_id 'OVERWATCH-1'.` |
| 12 | The scenario declares a solar epoch | refuse | `scenario bahonar@a91c3f declares no epoch. A capture cannot set a sun from simulated seconds without one (12 §4.3).` |
| 13 | The window's civil span is computable from the epoch | refuse | `epoch time zone '+3:3' is not a valid offset.` |
| 14 | If the window's sun elevation falls below −6°, `solar.vehicle_lights` is stated | refuse | `window night_shift is dark (sun elevation −37.2° to −41.8°). 'solar.vehicle_lights' has no default in a dark window: state 'from_sumo' or 'off'.` |
| 15 | `solar.policy` is not `accelerated` | refuse | `solar.policy 'accelerated' is not permitted in a capture run: a rate other than 1.0 makes recorded solar time disagree with the scenario's clock. Use the interactive viewer for look development.` |
| 16 | A numeric `exposure` is not requested | refuse, **naming the field that does exist** | `a numeric camera exposure is not settable on this build: sensor.camera.rgb declares no exposure attribute (ActorBlueprintFunctionLibrary.cpp:313-410). Exposure is chosen per channel by 'post_process_profile': Default, GoPro, Town10HD_Opt, Town_C.` — §1.3's dead flag made loud, and the refusal names the field that works rather than only a wall (R4) |
| 17 | The two export roots are distinct and neither contains the other | refuse | `roots.truth '/data/run7' contains roots.observation '/data/run7/obs'. The anti-leak split requires two disjoint roots (08 D8.17, 04 D4.26).` — there is no third root: model output is neither produced nor consumed here, and a named shelf for it would only invite it into the tree |
| 18 | Every seed has an explicit value or `random` | refuse | `seeds.sumo is unset. Give a value, or 'random' to draw and record one.` |
| 19 | Free space under `roots.observation`, expressed in **captured seconds** at the configured rate and channel count | refuse when the window declares an end that does not fit; **warn with the figure** when it declares none | `window needs ~52 GB at 2 Hz × 2 channels × 6.2 Mpx; 31 GB free at /data.` and, with no declared end, `at 24 GB/h, 31 GB free at /data is 1 h 17 m of capture. This window declares no end.` — derived from [`10`](10_Scale_And_Performance.md) §4.2.3's measured per-frame sizes. Check 46 is the same quantity re-evaluated while the run proceeds |
| 20 | **Withdrawn 2026-09-30** with the render region: there is no region to size. | — | — |
| 21 | **Withdrawn 2026-09-30** with the render cap: there is no cap for a population to stay under. | — | — |

#### Phase 1 — server-bound

| # | Check | Outcome | Message shape |
|---:|---|---|---|
| 22 | The loaded world's OpenDRIVE digest equals the world package's | refuse | `server holds xodr@2c40aa; the run names Bahonar@3f91ac. Load the right world or name the right package.` |
| 23 | `get_solar_state()` returns a state — the world has a CesiumSunSky | refuse | `world has no CesiumSunSky: a capture cannot set its sun. (Today this is a warning at WorldBuilder.py:244-245 and the run continues.)` |
| 24 | Every attribute the rig sets exists on the blueprint it targets | refuse, naming the attribute and the blueprint | `sensor.camera.depth has no attribute 'max_range' on this build; depth would silently use 1000 m.` |
| 25 | Every `vType` in the scenario binds to a blueprint present on this server | refuse | carried from [`07`](07_Scenario_Authoring.md) check 14, re-run against the live server |
| 26 | The resolved SUMO version equals the one that built the world | refuse, with an explicit override | `world built by netconvert 1.27.0; SUMO_HOME resolves 1.27.1.` — [`09`](09_Toolchain_And_Packaging.md) D9.6 |
| 27 | No other client is attached | warn | `2 clients attached; in synchronous mode each client's service time is added to the tick (10 D10.10).` |

#### Phase 2 — authority

| # | Check | Outcome | Message shape |
|---:|---|---|---|
| 28 | Population authority acquired | refuse | `population authority held by TrafficManagerAmbient (client 0x…, since tick 41200). Stop it before starting a SUMO capture.` — [`01`](01_Architecture.md) D1.7 |
| 29 | Motion authority free for every vehicle the run will drive | refuse, naming the holder | [`01`](01_Architecture.md) D1.8 |

#### Phase 3 — pre-roll

| # | Check | Outcome | Message shape |
|---:|---|---|---|
| 30 | SUMO reaches `window.begin_s − prewarm_s` | refuse on a SUMO error, naming its own message | [`10`](10_Scale_And_Performance.md) D10.2 |
| 31 | Applied solar state matches the requested one, read back from `get_solar_state()` | refuse | `requested solar_time 23.00, world reports 12.00.` — §4.5's `confirmed` |
| 32 | The first cued tick delivers a frame on every channel | refuse | `channel OVERWATCH-2 delivered no frame within 5 cues.` — [`02`](02_Use_Cases.md) UC-7's session fault, applied before the window rather than during it |
| 33 | **Withdrawn 2026-09-30** with the render cap: every vehicle SUMO has at the window's begin is drawn, so there is no eligible population to compare. | — | — |

#### Further checks, each naming its own phase

**Check numbers are stable and are never reused**, because siblings cite them by number —
[`08`](08_Collection_And_EPoL.md) §15 cites rule 17 specifically. The checks below therefore continue
the sequence rather than sitting inside a phase's table, and each states its own phase.

| # | Phase | Check | Outcome | Message shape |
|---:|---|---|---|---|
| 34 | 0 | Under `caller: unattended`, every warning code actually raised has an `on_warning` adjudication | refuse | `warning 'lighting_honours_no_epoch' was raised and the caller is unattended. Set on_warning.lighting_honours_no_epoch to 'proceed' — which is recorded against this configuration — or choose a solar.policy that honours the scenario's epoch. An unattended run does not proceed past an unadjudicated warning (§6.4).` |
| 35 | 0 | Every declared `expect.<path>` holds against the resolved value | refuse, naming both | `expect.solar.window_civil_begin was '23:00:00+03:30'; the configuration resolved '12:00:00+03:30'. The scenario's epoch is bahonar…@a91c3f and the window is night_shift.` — §6.4's substitute for a human reading the echo |
| 36 | 0 | Under `caller: unattended`, no field resolved from an environment variable the site profile does not name | refuse | `'sumo.install' resolved from SUMO_HOME='G:\Sumo', which this site profile does not declare. An unattended run does not inherit host state it was not given (§3.10 M2; 09 D9.6).` |
| 37 | 0 | No site-profile secret or path resolves to the empty string | refuse | `'cesium.ion_token' is empty: CESIUM_ION_TOKEN is unset and the parser's default is the empty string (CarlaControlArgumentParser.py:105). An absent token is a refusal, not a blank.` |
| 38 | 0 | No `world_build` block is present | refuse | `a capture run binds a world package; it does not build one. Build with run_SCTMV.py --build and name the resulting package (§3.10.4; 09 D9.9).` |
| 39 | 0 | A `random` seed resolves to its drawn value, and that value is written into the lock and into the manifest's effective configuration **before the first capture** | refuse if the lock is not writable at that point | `seeds.sumo resolved to 'random' but the lock at out/bahonar_night.lock.json is not writable. A drawn seed that is not written before the first capture is lost the moment the run is stopped (§3.10.4).` |
| 40 | 0 | `result_path` is writable and lies outside both corpus roots | refuse | `result_path '/data/truth/run7/run.result.json' is inside roots.truth. The result must survive a refusal that produces no corpus.` |
| 41 | 0 | Pacing fields are consistent with `pacing.mode` | refuse | `pacing.mode is 'as_available'; 'pacing.real_time_factor' is not valid in that mode.` and `pacing.min_achieved_factor 1.2 exceeds pacing.real_time_factor 1.0.` |
| 42 | 0 | `handover.channels[]` names only declared channels, and `transcript.root` lies outside both corpus roots | refuse | `transcript.root '/data/obs/cap-…/transcript' is inside roots.observation. Received data is not a corpus artifact (08 D8.38's precedent, team brief §3c).` |
| 43 | 1 | Each channel's `post_process_profile` loaded on the server, confirmed by digest | refuse | `channel OVERWATCH-1 asked for profile 'default'; the server loaded nothing and kept the component's construction-time settings. The load's return value is discarded (ActorBlueprintFunctionLibrary.cpp:1377-1380), so a name that does not resolve is silent. Profiles present: Default, GoPro, Town10HD_Opt, Town_C.` — §5.2's correction, and the platform case mismatch it measured |
| 44 | 3 | Under `pacing.mode: wall_clock`, the pre-roll's achieved real-time factor is measured and compared against `min_achieved_factor` | refuse | `pre-roll held 0.31 of real time against a requested 1.0 and a floor of 0.8. A live exercise that cannot hold its rate should not open its window.` — the rate is measured before the window is spent, not after |
| 45 | 3 | Under `handover.enabled`, the handover transport opens, and every `transcript.sources[]` listener binds | refuse | `handover transport could not open tcp://…: connection refused. Nothing has been captured.` |
| 46 | 0, then continuous | Write headroom under `roots.observation`, in **captured seconds** at the configured rate and channel count, stays above `write_headroom_floor` | refuse at launch when a declared window does not fit (check 19); **stop the run cleanly** when it falls below the floor while running | at launch `at 24 GB/h, 31 GB free at /data is 1 h 17 m of capture against a declared window of 8 h.`; while running `write headroom is 9 min of capture and the floor is 10 min; stopping cleanly at t=372 480 (closed_by: write_headroom).` |
| 47 | 0 | Every channel is a valid `ChannelDescription`, occlusion is measured only on a stare, and a stare aimed at the rendered traffic has a prewarm of at least one SUMO step to measure it over | refuse | `capture.channels[0]: channel description refused: a stare needs somewhere to look: give stare_look_at_x_m and stare_look_at_y_m, or stare_look_at_target 'rendered_traffic', or all of stare_x_m, …`, `occlusion is measured against a depth camera held at the channel's pose, and an orbit moves its camera with one call at a time … Set occlusion.enabled false for a run with an orbit, or make this channel a stare` and `this stare aims at the rendered traffic, which is measured on the last frame the prewarm renders before the window opens; the prewarm is 0 s … and one SUMO step is 1 s, so no frame would be rendered to measure it on` |
| 48 | 0 | The catalogue at `paths.catalogue` is the one the scenario was compiled against | refuse | `…/vehicles.catalogue.json has catalogue_digest 771f…; scenario gardnerville@d0bf… was compiled against 0771…. The session would seat bodies of other dimensions than the routes were built for` |
| 49 | resolution | The scenario package and the world package resolve, and the scenario's files are the ones its lock digests — a scenario compiled earlier is re-bound by its lock, not recompiled (§6.1) | refuse | `routes file gardnerville.rou.xml digests 5a1c…, not the 9c07… its lock recorded: it changed after the compile. Recompile the specification` |
| 50 | 3 | Every channel's view is ready as the window opens: its photoreal tiles in — the camera published on the last tick, every visible tileset at load progress 100, no failed tile in view — and its picture settled — a frame within 0.5 grey levels of the camera's newest frame at least ten ticks before it, in its worst 80-pixel block that no rendered vehicle covers in either frame, with at least half the view's blocks left to judge, counting frames rendered once the tiles were in — each within its ceiling, 90 s of wall clock and 120 ticks ([`03`](03_CoSimulation_Runtime.md) §9.5.1) | refuse; the window's first frame is not moved (D12.38) | `channel OVERWATCH-1: its photoreal tiles were not in within 90 s of wall clock (03 §9.5.1): at frame 12345, 1800 ticks and 90.0 s into the wait, the tiles were 87%, 3 failed in view (ion 2275207: progress 87.0, queued 4/0, kicked 0, failed in view 3, failed loaded 3)` and `channel OVERWATCH-1: its view was not ready when the window opened at t=25200 (03 §9.5.1): the tiles were in at frame 7000, … and the picture had not settled: 1 of its frames arrived since, none 10 ticks after another to compare it with` and `channel OVERWATCH-1: its picture did not settle within 120 ticks of its tiles being in at frame 419713 (03 §9.5.1): rendered vehicles covered 80 of its 144 80-pixel blocks in frame 419833 or frame 419823, leaving 44% of the view to judge against the 50% the witness needs, so the view could not be judged (12 comparisons: 0 judged, 12 with too few blocks left, 0 with vehicles that could not be placed)` |
| 51 | 0 | The prewarm leaves every camera, at the pose it holds as the window opens, enough ticks after its tiles are first asked about, one SUMO step into the hold, to render two frames at least ten ticks apart whatever the phase of its period: the fewest its picture can be witnessed settled on | refuse | `every channel's view is waited on inside the prewarm, and its picture is witnessed settled by comparing one of the camera's frames with its frame at least 10 ticks before it, … the prewarm is 1 s (capture.prewarm_s, clipped to the window's begin), which leaves 0 ticks against the 19 a camera rendering every 10 ticks may need for two such frames, so the run would be refused at pre-roll. Give a prewarm of at least 1.95 s` — check 50's certain refusal moved to phase 0 |
| 52 | 0 | `capture.draw_distance_m`, where set, reaches the point every channel's camera is aimed at: a stare's standoff and altitude, an orbit's radius and altitude, measured as a slant range; a stare given as an explicit pose names no point and is not judged | refuse | `capture.draw_distance_m is 250 m, and channel OVERWATCH-1's camera stands 304.8 m from the point it is aimed at, so no vehicle there would be drawn in its images: a body farther than the draw distance from a camera is not in that camera's picture. Raise the draw distance above 304.8 m, bring the camera nearer, or leave it unset to draw every body at any range` |
| 53 | 0 | An optional render-set limit is one the session can draw: `capture.render_set` `circle` has a `capture.render_region`, and a region is given only to a render set that reads it | refuse | `capture.render_set is circle and capture.render_region has no value: give x_m, y_m and radius_m in CARLA's frame, or leave capture.render_set at all to draw every vehicle SUMO has` and `capture.render_region is given and capture.render_set is all, which draws every vehicle SUMO has and reads no region. Set capture.render_set to circle or cameras to limit the render set to it, or drop the region` |

Checks 34, 35, 44 and 46 are the four that earn their place. **34 and 35 are §6.4's whole mechanism** —
the machine's substitute for a human reading an echo — and **44 moves the live run's dominant failure
from minute forty to the pre-roll**, using a measurement that already has to be taken because
[`08`](08_Collection_And_EPoL.md) §11.1 requires the achieved factor to be recorded per session anyway.

**46 is the one bound the tool imposes on itself, and it is physics rather than policy.** The caller
decides when a run has produced enough, so nothing here limits a run by time or by frame count. A disk
that fills is different: it produces truncated files, which is the one outcome §3.10.2 forbids
outright. A clean self-stop with `closed_by: write_headroom` is a convenience limit in the brief's
sense — **nothing depends on it**, a caller that stops us first never sees it, and its floor is a field
an operator can set (§5.2).

#### 6.2.1 Where each check is carried out

`RunConfigurationCheckCatalogue` holds every check above by its number, and a test holds this table equal to it. **resolution** is the first of the offline checks: a check there ends the launch before anything is
resolved, with outcome `usage_error` (§3.10.2). A check the co-simulation session runs as part of starting — `SumoDriveSession.Start` — is run there and nowhere else, because the session is its one validator; `run_capture` maps the session's refusal onto the run's outcome. A check marked *by construction* guards something the configuration cannot express; one marked **not built** compares a thing nothing in the tree publishes, and the last column says what; one marked **withdrawn** is retired with its number, which is never reused.

| # | Phase | Carried out by | Where, or what is missing |
|---:|---|---|---|
| 1 | resolution | `run_capture` | RunConfiguration, when a document or an override is read |
| 2 | offline | `run_capture` | RunConfigurationValidator |
| 3 | resolution | `run_capture` | RunConfigurationResolver |
| 4 | offline | `run_capture` | RunConfigurationValidator; only sumo_driven_playback is built here, and the traffic-manager path is run_SCTMV.py |
| 5 | offline | `run_capture` | RunConfigurationValidator |
| 6 | offline | `run_capture` | RunConfigurationValidator |
| 7 | offline | `run_capture` | EffectiveRunConfiguration.window |
| 8 | offline | `run_capture` | RunConfigurationValidator |
| 9 | offline | `run_capture` | CarlaNet.CoSim.CoSimClock.ForSession, the session's own validator, called offline |
| 10 | offline | by construction | sensor_tick is not a field; every camera's is set to 1 / capture_hz |
| 11 | offline | `run_capture` | RunConfigurationValidator |
| 12 | offline | `run_capture` | RunConfigurationValidator |
| 13 | offline | `run_capture` | ScenarioEpoch, which reads the epoch with the session's SolarEpoch |
| 14 | offline | **not built** | the session writes no vehicle lamps, so there is no field to state |
| 15 | offline | `run_capture` | RunConfigurationValidator |
| 16 | resolution | `run_capture` | RunConfiguration; the refusal names post_process_profile, the field that exists |
| 17 | offline | **not built** | the recorder writes each capture's image and sidecar into one directory; the two-root split is stage K's and no writer makes it |
| 18 | offline | by construction | the only seed the run consumes is SUMO's, bound by the scenario package |
| 19 | offline | `run_capture` | RunConfigurationValidator, from doc 10's measured capture sizes |
| 20 | — | **withdrawn 2026-09-30** | with the render region: there is no region to size |
| 21 | — | **withdrawn 2026-09-30** | with the render cap: there is no cap for a population to stay under |
| 22 | server | the session | SumoDriveSession.Start, LoadedWorldCheck, before SUMO is started |
| 23 | server | `run_capture` | RunConfigurationValidator.validate_against_server |
| 24 | server | `run_capture` | RunConfigurationValidator.validate_against_server |
| 25 | server | `run_capture` | RunConfigurationValidator.validate_against_server |
| 26 | server | the session | SumoDriveSession.Start; sumo.allow_version_mismatch runs anyway and is reported |
| 27 | server | **not built** | the server publishes no count of attached clients |
| 28 | authority | the session | SumoDriveSession.Start; PopulationAuthorityHeldException names the holder |
| 29 | authority | **not built** | the session holds a population lease and no per-actor motion lease |
| 30 | pre-roll | the session | SumoDriveSession.Start, its fast-forward |
| 31 | pre-roll | the session | SumoDriveSession.Start, SolarLease and the window-open audit; SolarAuditFailedException |
| 32 | pre-roll | **not built** | the recorder publishes no count of frames received |
| 33 | — | **withdrawn 2026-09-30** | with the render cap: every vehicle SUMO has at the window's begin is drawn |
| 34 | offline | `run_capture` | RunConfigurationValidator |
| 35 | offline | `run_capture` | RunConfigurationValidator, against the configuration and the launch echo |
| 36 | offline | `run_capture` | RunConfigurationValidator |
| 37 | offline | `run_capture` | RunConfigurationValidator |
| 38 | resolution | `run_capture` | RunConfiguration |
| 39 | offline | by construction | no seed is drawn; the SUMO seed is the scenario package's |
| 40 | offline | `run_capture` | RunConfigurationValidator |
| 41 | offline | `run_capture` | RunConfigurationValidator |
| 42 | offline | **not built** | no handover or transcript writer exists |
| 43 | server | **not built** | nothing reads back which profile a camera loaded |
| 44 | pre-roll | `run_capture` | CaptureSession, from the session's RealTimePacer |
| 45 | pre-roll | **not built** | no handover transport exists |
| 46 | offline, then continuous | `run_capture` | RunConfigurationValidator at launch; CaptureSession while the run proceeds |
| 47 | offline | `run_capture` | ChannelDescription, RunConfigurationValidator |
| 48 | offline | `run_capture` | RunConfigurationValidator |
| 49 | resolution | `run_capture` | ScenarioPackage, RunConfigurationResolver |
| 50 | pre-roll | `run_capture` | CaptureSession, ViewReadinessGate: world.get_view_readiness after every prewarm step, and the camera's own frames, each frame's rendered vehicles placed by SessionFrameVehicles |
| 51 | offline | `run_capture` | RunConfigurationValidator, from ViewReadiness.wait_begins_s |
| 52 | offline | `run_capture` | RunConfigurationValidator |
| 53 | offline | `run_capture` | RunConfigurationValidator |

### 6.3 Launch, from command to first capture

```mermaid
sequenceDiagram
    autonumber
    actor OP as Capture operator
    participant CLI as run_capture
    participant RES as RunConfigurationResolver
    participant VAL as RunConfigurationValidator
    participant SRV as CARLA server
    participant AUT as WorldDriveAuthority
    participant SUMO as SumoSession
    participant CLK as PlaybackClock
    participant MAN as RunManifestWriter
    participant REC as FrameRecorder per channel

    OP->>CLI: run_capture --scenario bahonar --window night_shift
    CLI->>RES: resolve layers 1..6
    RES->>RES: bind world package (layer 3)
    RES->>RES: read scenario declarations (layer 4)
    RES-->>CLI: EffectiveRunConfiguration<br/>+ provenance per field

    rect rgb(30,45,70)
    note over CLI,VAL: PHASE 0 - offline. No server, no GPU, no SUMO.
    CLI->>VAL: validate(effective)
    VAL-->>CLI: checks 1-19
    alt any refusal
        VAL-->>OP: REFUSE, field named, both values named,<br/>candidates listed. Nothing started.
    end
    end

    CLI->>SRV: connect
    rect rgb(30,45,70)
    note over CLI,SRV: PHASE 1 - server-bound
    CLI->>SRV: get OpenDRIVE digest, blueprint attributes,<br/>get_solar_state()
    SRV-->>CLI: world state
    CLI->>VAL: validate_against_server(...)
    VAL-->>CLI: checks 22-27
    alt refusal
        VAL-->>OP: REFUSE. Nothing acquired, nothing spawned.
    end
    end

    CLI->>OP: LAUNCH ECHO - civil span, sun elevation, cost,<br/>roots, warnings (6.4.1). Attended: blocks only if a<br/>warning was raised. Unattended: the same block is<br/>serialised to the result, warnings were adjudicated in<br/>the configuration and expectations checked in phase 0.

    rect rgb(30,45,70)
    note over CLI,AUT: PHASE 2 - authority. The first irreversible step.
    CLI->>AUT: acquire population authority (mode, session_id)
    alt held
        AUT-->>OP: REFUSE naming the current holder
    end
    AUT-->>CLI: granted
    end

    CLI->>MAN: open manifest, write session block<br/>+ effective_configuration verbatim
    CLI->>SRV: synchronous mode, world_delta_s
    CLI->>SRV: set_solar_date / set_solar_time (window civil instant)
    CLI->>SRV: set_time_advance(policy == advance, 1.0)

    rect rgb(30,45,70)
    note over CLI,SUMO: PHASE 3 - pre-roll
    CLI->>SUMO: launch, step from t=0 to begin_s - prewarm_s
    SUMO-->>CLI: warm state
    CLI->>VAL: validate_preroll(...)
    VAL-->>CLI: checks 30-32
    alt refusal
        CLI->>AUT: release
        VAL-->>OP: REFUSE. Manifest closed as aborted_at_preroll.
    end
    end

    CLK->>SRV: cue first tick of the window
    SRV-->>CLK: frame delivered
    CLK->>SRV: get_solar_state() (no RPC, cached)
    CLK->>MAN: solar.confirmed at this tick
    CLK->>REC: capture
    REC->>MAN: first capture recorded
    REC-->>OP: monitor shows first capture, clock ratio, population
```

The ordering is the substance. **Everything that can be checked without a server is checked without a
server; everything that can be checked before acquiring authority is checked before acquiring
authority; and the sun is applied and read back before the first capture, not after.** Phase 2 is
marked as the first irreversible step because it is the first one another operator can notice.

**As built**, the authority and pre-roll checks happen inside one call. `SumoDriveSession.Start` checks the world package
against the loaded world and the scenario's files against its compile lock, refuses a scenario that
lets SUMO teleport a blocked vehicle, resolves the SUMO installation and refuses a release other than
the world's converter, starts SUMO, takes the world's clock, hides the road and signal layers, checks
the clock ratio and the network, takes the population lease, fast-forwards SUMO to the prewarm's first
instant (`capture.window` begin less `capture.prewarm_s`), and binds the sun for the window's opening,
reading it back: `CaptureSession` gives it the window's begin as `window_opens_at`, so a frozen sun is
pinned there and the prewarm is lit by it. It refuses by exception, and **every refusal carries the
stage the session had reached** ([`03`](03_CoSimulation_Runtime.md) §11.10), which `CaptureSession`
maps onto the outcome, from `start_sumo_drive` and from `Advance` alike: `Validation` and `Launch` are
`refused_server`; `Authority` is `refused_authority`, with the holder a held lease names (`HeldBy`) in
`authority_holder`; `PreRoll` — the fast-forward, the sun's binding and read-back, or a refusal on a
prewarm tick — is `refused_preroll`, closed `aborted_at_preroll`; `Window` is `run_stopped`, closed
`fault:<exception>`. A SUMO failure is such a refusal, quoting SUMO's console, and the result's
`detail` names the stage. Anything else raised — a dropped CARLA connection among them, which the
session does not wrap — is `internal_error`. The session restores everything it took on every exit
path, so a start that failed leaves the world as it was found. Then `CaptureSession` places the
cameras — an orbit at the pose it opens on, held there — ticks the prewarm through the session with
nothing recording — moving each stare aimed at the rendered traffic after every step until its hold,
and recording the point it resolved to there (§5.2) — and waits inside the prewarm for every
channel's view to be ready ([`03`](03_CoSimulation_Runtime.md) §9.5.1, check 50). After each step it
asks the server whether each camera's photoreal tiles are in (`world.get_view_readiness`), and once
they are it compares the camera's own frames, which it listens to from the camera's placement until
the recorders start, until the picture has settled, leaving out every block a rendered vehicle
covers in either frame of a comparison — placed from the session's render set of the frame and the
client's snapshot of it — and judging only a comparison that leaves at least half the view; it asks
nothing between steps, so the wait ticks
only with the prewarm, and it begins once every camera holds the pose the window opens on. A
witness past its ceiling, or a view not ready when the prewarm's last step ends, refuses the run at
pre-roll, naming the channel and the witness; the window's first frame is never moved (D12.38). It
then checks the prewarm's pace under `wall_clock` (check 44) and every view as the window opens, stops
listening, starts the recorders at the window's begin and sets any orbit sweeping. The CLI never sets
the sun or the world's settings itself: the session is their one owner.

### 6.4 The echo before commit, and what replaces it for a machine

[`01`](01_Architecture.md) §10.2 item 3 asks this section for "an echo before a long run commits" —
the run telling the operator the civil instant and the sun elevation its first frame will be captured
under, before it starts — on the grounds that
[`10`](10_Scale_And_Performance.md) §4.2.3 sizes a capture plan at ten hours of wall clock and 313 GB,
and discovering afterwards that the sun was wrong is the most expensive failure available here.
[`02`](02_Use_Cases.md) UC-12 step 4 asks for the same thing from the actor's side, and
[`13`](13_Work_Breakdown.md) §8 carries it as a work item. It is specified here.

#### 6.4.1 What it is

`LaunchEcho` computes one block after phase 0 and before phase 2 — after everything checkable offline
has passed, and before the first irreversible step. It contains what a reader would have to be told to
notice that the run is not the run they meant:

```
bahonar_pattern_of_life @ a91c3f  ::  window night_shift        caller: attended
  simulated   371 700 - 373 500 s      (1 800 s, 3 600 captures at 2.0 Hz)
  civil       2026-03-08 23:00:00 +03:30  ->  23:30:00 +03:30    epoch: scenario
  sun         elevation -37.2 deg -> -41.8 deg   azimuth 12.4 -> 31.0    policy: advance, rate 1.0
              DARK for 1 800 s of 1 800.  solar.vehicle_lights = from_sumo  (stated, check 14)
  world       Bahonar@3f91ac   net@88b1de   origin 30.2891, 56.3421
  cost        ~52 GB under /data/obs      predicted 2 h 10 m wall at the measured tick rate
  roots       observation /data/obs/cap-20260308-2300     truth /data/truth/cap-20260308-2300
  warnings    0
```

Every figure in it already exists: the civil span and sun angles come from
[`11`](11_Time_And_Illumination.md)'s N2 and N4 functions that check 12 and check 14 already evaluate;
the size from check 19; the roots from the resolved layer 2. **The
echo is a rendering of a block, not a computation** — the same rule D12.14 applies to the monitor, one
layer earlier, and the block is serialised as `launch_echo` in `<run>.resolution.json` and in
`RunResult` (§3.10.3) whether anybody reads it or not.

**As built** (`LaunchEcho`), the block carries the simulated span and where its end came from, the
captures per channel and per hour, the civil span, the sun at the window's first and last captured
instants with their illumination bands (`IlluminationBand`), the world, the estimated disk cost and headroom (check 19's figures), where the run writes, the pacing, the
wait for every channel's view (`readiness`: the two witnesses, their ceilings, the blocks rendered
vehicles take out of a comparison, the instant the wait begins and the window's opening it must be met
by, where a stare aimed at the traffic stops to hold, and that a view not ready refuses at pre-roll
with the window unmoved), and the warning codes raised.
The sun is evaluated with the window opening at **its own begin**, because that
is the instant `run_capture` gives the session as `window_opens_at` and where the session pins a frozen
sun and anchors an advancing one; `held_at` states it, and says the prewarm before it is lit by the
same sun. Two figures are stated as not predicted: the wall-clock duration (no measured tick rate
exists for a configuration before it runs) and, where a stare is aimed at the rendered traffic, where
it will look.

#### 6.4.2 When it blocks, for a human

**It prints on every attended launch, and it blocks on exactly one condition: a phase-0 warning was
raised.** With no warnings it prints and the run proceeds.

**A warning raised at pre-roll is adjudicated by the same codes, and nobody is asked.** No pre-roll
check warns today, but one would be evaluated after the prewarm, with the lease held and the world's
clock the session's, so a prompt then would hold both, and an operator who has stepped away would hold
them indefinitely. `on_warning.<code>` `refuse` refuses and `proceed` proceeds, as in phase 0; with no
adjudication an unattended caller is refused at pre-roll, and an attended run proceeds with the warning said at once as
a loud condition and carried unadjudicated into the result, where the `launch.warnings_adjudicated`
gate records it.

The rule is not arbitrary. [`07`](07_Scenario_Authoring.md) §5.3 states the principle this section
inherits — warnings appear in the report in full "because warnings are the failures that a human has to
adjudicate". A refusal needs no human; it already stopped. A clean resolution needs no human; nothing is
in question. A warning is by construction the case where the tool has said *this is probably wrong and
I am not entitled to refuse it*, and that is precisely a judgement. Blocking there, and only there, is
what stops the echo from becoming the thing §3.8 rejects: an affordance people learn to scroll past.

A run killed while it is blocked at the echo costs nothing, because the echo sits after phase 0 and
before phase 2: nothing has been acquired, nothing has been spawned, and no artifact but the resolution
report exists to be left half-written.

#### 6.4.3 What the unattended path does instead

A machine cannot be blocked, and silently starting a multi-hour run on a misresolved configuration is
the failure the echo existed to prevent. The resolution is that **the two things the echo does get
separated, and each is given a mechanism a machine can satisfy.**

| What the echo does for a human | Attended | Unattended |
|---|---|---|
| **Shows the numbers** | printed to the terminal | the identical `launch_echo` block in `<run>.resolution.json` and `RunResult`. Nothing is lost; only the blocking is |
| **Collects a judgement on a warning** | the run blocks until the operator accepts, and the acceptance is recorded in the lock | **the judgement is made in advance, per warning code**: `on_warning.<code>` is `refuse` or `proceed`, an unadjudicated raised code is a phase-0 refusal (check 34), and the lock records the adjudication **and the artifact that granted it** |
| **Catches a configuration that resolved legally but not as intended** | the operator reads "civil 23:00, sun −37°" and knows whether that is the run they meant | **declared expectations**: `expect.<path>` states what the caller believed the field would resolve to, and a disagreement is a phase-0 refusal naming both values (check 35) |

**The third row is the one that matters, and it is the one an adjudication mechanism alone does not
cover.** A misresolved configuration need raise no warning at all. §1.6's measured defect is exactly
that shape: `setup_solar_time` sets the sun to 12:00 on the host's date, every value is legal, nothing
warns, the truth record faithfully reports noon, and the corpus contradicts its own scenario. A human
catches it by reading one line and applying knowledge that is nowhere in the configuration — *this
scenario is about the night shift*. **A machine has no such knowledge unless someone wrote it down, so
`expect` is the written-down form of the operator's raised eyebrow.** It costs one line in the run
configuration, it is checked offline with no server, and it reuses the refusal vocabulary whole:

```jsonc
"expect": {
  "solar.window_civil_begin": "2026-03-08T23:00:00+03:30",
  "solar.sun_elevation_deg_max": -6.0,
  "world.digest": "Bahonar@3f91ac",
  "capture.channels": 2,
  "capture.frames_per_hour": 14400
}
```

Every expectation names something that **resolves before the run starts**, which is what keeps the
mechanism honest under a caller that may stop the run at any instant: a declared total frame count
would be an expectation about how long we are allowed to run, and that is not ours to expect
(§3.10.2). A rate and a channel count are configuration; a total is a prediction about the caller.

Three properties keep this from being decoration:

- **An expectation is never a value.** It cannot supply a field, only disagree with one, so it can
  never become a seventh layer or a way to smuggle a default in. A failed expectation refuses; a passed
  one changes nothing.
- **Expectations are declared, not inferred.** The tool never generates them from a previous run, which
  would make one run silently assert the numbers of another — the comparison §3.10.4 keeps outside.
- **An attended run may declare them too**, and should whenever the configuration is a template
  something else will later run unattended, because that is the moment the human's knowledge is still
  present and can be written down. `expect` is worth more, not less, under a caller that stops us at a
  time of its choosing: it catches a configuration that resolves legally but not as intended **before**
  a long run starts, and a long run that is going to be killed at an unknown instant is exactly the kind
  whose first minutes cannot be inspected and re-run cheaply.

#### 6.4.4 Two shapes that were considered and refused

- **Block with a timeout, then proceed.** It is the worst of both: an unattended caller waits for
  nothing, and an operator who stepped away gets exactly the silent start the echo exists to prevent.
  A guard whose failure mode is "proceed anyway" is not a guard.
- **`--yes` as a blanket acceptance.** One flag that accepts every warning, present and future, records
  nothing about *which* warnings were accepted, and would be pasted into every cadence invocation on
  the first day. The per-code adjudication costs one line per warning class that actually fires, and it
  is the thing §13 open question 6 was reaching for: an acknowledgement that is attributable rather than
  ceremonial.

---

## 7. What is visible while a run proceeds, and what is recorded

[`02`](02_Use_Cases.md) open question 5 asks this directly and says nothing above specifies it. This
is the answer.

### 7.1 While it runs

One rule governs the design: **every field the monitor displays is a field the manifest also carries,
read from the same source.** A monitor that computes its own numbers is a second implementation that
can disagree with the record, and a disagreement between the screen and the corpus is the worst
outcome available — the operator believes the screen.

```
bahonar_pattern_of_life :: night_shift          cap-20260308-2300     [ADVANCING]
sim   t=371 240 / 371 700   window  61.7%   |  civil 23:12:20 +03:30  sun -39.1 deg
clock 0.41 ticks/wall-s  (ratio 0.29)       |  ETA 00:52 wall
sumo  population 139   rendered 139
chan  OVERWATCH-1   frames 1 482   dropped 0   occl paired 1 482/1 482
chan  OVERWATCH-2   frames 1 482   dropped 0   occl paired 1 481/1 482    1 unpaired
truth manifest flushed t=371 238 (2 s ago)   instances 7   intervals open 2
```

| Field | Why it is there | Manifest field |
|---|---|---|
| Window progress and civil time | The operator must be able to see that the sun matches the scenario, which is §1.6's defect made visible | `solar.window_civil`, `<_solar>` per capture |
| Sun elevation and the advancing flag | The one number that says the time-of-day coupling is working | `solar.applied`, `solar.confirmed` |
| **Achieved ticks per wall-second and the clock ratio** | Recorded nowhere today; recoverable only by differencing PNG metadata against file timestamps | [`10`](10_Scale_And_Performance.md) D10.12 |
| `population / rendered` | How many vehicles SUMO has and how many bodies the frame drew. Every vehicle SUMO has is drawn, so they differ only by a vehicle SUMO has just inserted, drawn from the frame SUMO first reports it in, one SUMO step after the pass that admits it ([`03`](03_CoSimulation_Runtime.md) D3.6), and by a vehicle of a type with no measured body | `render_states[]` ([`04`](04_Contracts.md) §4.5) |
| **Frames written and `Dropped`, per channel** | `FrameRecorder.Dropped` is incremented at `FrameRecorder.cs:184` and has **no reader anywhere in the tree** | [`10`](10_Scale_And_Performance.md) D10.7 |
| Occlusion pairing successes and failures | An unpaired capture is excluded from the unoccluded denominator entirely | [`08`](08_Collection_And_EPoL.md) D8.19 |
| Manifest last-flush tick | The manifest is written incrementally; a stalled writer is a silent loss of supervision | [`06`](06_Truth_And_Annotation.md) §8.4 |

**One condition is loud** — it interrupts rather than appearing in a column, because it means the
corpus is no longer what was asked for: the **recorder's** `Dropped` becomes non-zero on any channel →
[`10`](10_Scale_And_Performance.md) D10.7. §7.4 names the second, unrelated drop counter that a live
run introduces and explains why it is not loud, and adds a second loud condition for a live run only.
A participant in an open annotated interval is not a loud condition: every vehicle SUMO has is drawn,
so a participant always is ([`04`](04_Contracts.md) D4.6), and §7.2's
`render_accounting.intervals_rendered` records it.

**As built** (`SessionMonitor`, `RunCloseoutReport`), the panel shows the simulated time, the window's
progress, the newest frame's declared civil instant, declared sun elevation and policy — read from the
session's illumination source, the same declaration every capture's `<_illumination>` carries — the
requested and achieved real-time factor and the last pacing window's, the vehicles SUMO has and the
vehicles rendered now, read off the session's report between advances, the ticks, the SUMO steps and
the batch failures, and per channel the captures written, the recorder's
`Dropped`, the captures without their illumination declaration, and occlusion measured and unmatched;
until the recorders start, each channel's view instead, where its tiles and its picture stand in their
wait (`view  OVERWATCH-1   tiles in at frame 1100 after 100 ticks, 0.5 s; picture settling, last 1.30
grey levels with 81% of its blocks judged`), read from the same `readiness` block the run result
carries.
Every figure is in the snapshot the run result's `produced` block is taken from. Both loud
conditions are observable — a recorder's `Dropped` becoming non-zero, and in a live run the achieved
factor falling below its floor. The manifest's last flush has no source and is not shown.

Nothing else interrupts. Diagnostics verbosity stays Run-mutable (§5.2) precisely so that the loud
conditions are not buried, which is the reason `--traffic-diagnostics` is off by default today
(`:439-449`).

**Under `caller: unattended` there is nobody to interrupt, and the conditions do not become
advisory.** A loud condition ends the run through §3.10.2's termination sequence: the manifest's
closing record carries the `closed_by` reason, the outcome is `run_stopped` (exit 6), and the condition
is named in `RunResult.produced`. The alternative — an unattended run continuing past a condition that
would have stopped an attended one — would make the corpus a function of who was watching, which is the
one thing §5.1's governing question exists to forbid.

**The caller may also stop the run for reasons of its own, at any instant, and that takes the same
path.** A signal is not a loud condition and is not a fault: it enters the termination sequence at step
1 from wherever the session was, and the only difference in the record is the `closed_by` value.

### 7.2 The gate records, published as they change

Every gate below is a **fact about our own data**: this check ran, this is what it observed, this is
the threshold it compared against, and whether it met it. **There is no aggregate.** Whether a corpus
is fit for a purpose depends on the purpose, and the caller never told us what it is
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d); a caller that wants a single bit computes one from the subset
it cares about. [`04`](04_Contracts.md) `C10` carries these records into the result artifact unchanged
(§3.10.3 K1).

**The records are computed continuously and appended to the manifest as they change, not calculated at
the end.** That is what makes them survive a kill — a run stopped at minute nine has already published
every gate record it had at minute nine — and it is the same property that lets an external process
read them while the run is still going (§7.6). `RunCloseoutReport` renders the latest values; it is a
rendering of the manifest, never a second computation, for the same reason the monitor is not one.

| Section | Contents | Gate record |
|---|---|---|
| **Identity** | session, run, scenario, plan, world digest, network fingerprint, seeds, effective-configuration digest | — |
| **What was asked for** | the effective configuration, with every field that came from an override or a non-default layer flagged | — |
| **What ran** | window in simulated and civil time; the end declared, the end reached and `closed_by`; achieved ticks per wall-second per window; wall-clock elapsed | `clock.ratio_recorded` — observed: windows with a recorded ratio; threshold: all of them |
| **Capture** | per channel: captured, written, `Dropped`, capture rate including any degradation step, occlusion pairing | `capture.recorder_dropped` — observed `Dropped`, threshold 0 (D10.7). `capture.captured_minus_written` — observed the difference, threshold 0, which is non-zero exactly when a kill left frames in the encode queue (§3.10.2). `capture.rate_changes_recorded` — observed rate changes carrying a record, threshold: all |
| **Render accounting** | simulated / rendered / never rendered; admissions and releases; vehicle types refused a body, by reason (`no_blueprint`, `unknown_extent`) | `render_accounting.intervals_rendered` — observed annotated intervals never rendered, threshold 0 |
| **Solar** | requested policy, epoch, applied state, confirmed state, closing state | `solar.applied_equals_confirmed` — observed the difference, threshold 0 |
| **Radiometry** | per channel: the profile asked for and the digest of the profile the server loaded | `radiometry.profile_digest_present` — observed channels carrying a digest, threshold: all ([`08`](08_Collection_And_EPoL.md) D8.28) |
| **Pacing** | requested mode and real-time factor; achieved factor per window | `pacing.factor_recorded` — under `wall_clock`, observed: recorded or not. `pacing.achieved_factor` — observed the achieved factor, threshold `min_achieved_factor` |
| **Handover** | per channel: frames offered, handover drops, last delivered tick; transcript sources, blobs received, last stamp | **no gate.** A handover drop is expected by design ([`08`](08_Collection_And_EPoL.md) §11.3) and the coverage record already carries it as *covered but not delivered* |
| **Launch provenance** | caller, caller label, every warning with its adjudication and the artifact that granted it, every declared expectation and that it held | `launch.warnings_adjudicated` — observed warnings with no adjudication, threshold 0; an attended run's pre-roll warning that nobody adjudicated in writing misses it (§6.4.2), and otherwise only a bug in §6.4 can |
| **Supervision** | instances, intervals, per-interval observability, prevalence in all three units | `supervision.manifest_closing_record` — observed: present or absent. A run killed with no chance to flush has none, and this record is what says so |
| **Corpus-affecting events** | SUMO collisions, teleports, emergency stops, reconciliation refusals | recorded, not compared — [`01`](01_Architecture.md) OQ6 |

**A gate record that did not meet its threshold deletes nothing, hides nothing and downgrades
nothing.** The corpus exists, the record says what was observed and against what, and what to do about
it is the reader's decision — which is [`08`](08_Collection_And_EPoL.md) open question 6's
recommendation applied to more than the closed flag. A corpus is described the same way whether the run
finished or was stopped; the difference between those two is `closed_by`, not a change of tone.

**As built**, the gate records `RunCloseoutReport` evaluates are `capture.recorder_dropped[<sensor>]`
(threshold 0), `capture.illumination_unpaired[<sensor>]` (captures written without their frame's
illumination declaration, threshold 0), `capture.render_set_unpaired[<sensor>]` (captures written
with no vehicle list because their frame's render set was no longer held, threshold 0;
[`06`](06_Truth_And_Annotation.md) §8.2), `capture.sensor_pose_header_disagreed[<sensor>]` (captures
whose image header placed the camera elsewhere than the snapshot of their own frame, threshold 0;
§9.6), `capture.depth_pose_header_disagreed[<sensor>]` (the same for the depth captures occlusion is
measured against, threshold 0; skipped for a channel with no depth camera), `clock.ratio_recorded`,
`pacing.achieved_factor` under
`wall_clock`, `solar.applied_equals_confirmed` (the solar audit's worst angle against its tolerance;
skipped where the policy binds no sun) and `launch.warnings_adjudicated`. Three are recorded as
`skipped`, each with its reason, so that *not measured* never reads as *met*:
`capture.captured_minus_written`, `radiometry.profile_digest_present` and
`supervision.manifest_closing_record`. A record carries `id`,
`name`, `owner`, `status` (`evaluated` or `skipped`), `observed`, `threshold`, `comparison` and `met`.
**With no run manifest in the tree, the records are not appended as they change**: they are computed
from the live session and recorders at any instant, rendered by the closeout, and written into the run
result at the terminal outcome. Appending them as they change waits for `RunManifestWriter`. Beside
them, recorded and not compared, the run result carries whether SUMO could teleport a blocked vehicle
and whether that was accepted (`produced.session.teleporting`) and the scenario's compile lock
(`produced.session.compile_lock`).

### 7.3 The two silent failures this closes

Both are recorded in [`00_Overview.md`](00_Overview.md) §5 and neither is a new mechanism; they are
readers for values that already exist.

- **`FrameRecorder.Dropped` is incremented at `FrameRecorder.cs:184`, and outside a capture run
  nothing reads it but the log line `run_sumo_drive.py` prints at its end.** `RunCloseoutReport`
  reads it per channel in every snapshot: the monitor shows it, a non-zero value is a loud condition, and the gate record
  `capture.recorder_dropped` carries it into the run result. A thin corpus stops looking like a normal
  one.
- **The clock ratio is recorded in no capture artifact.** [`10`](10_Scale_And_Performance.md) §7
  measures it as non-constant across sessions — 84%, 99%, 29.5% — so a window that ran at 15%
  produces the same imagery as one that ran at 90% at six times the cost, and nothing in the corpus
  says which. The
  session measures it (`RealTimePacer`, whether or not the run is paced), the monitor shows it live,
  and the run result records the achieved factor over the run and over the prewarm.

### 7.4 The live run: how pacing is expressed, and what is shown while it runs

A live exercise is paced against a wall clock with a human watching
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c). **The pacing ruling is not this section's** —
[`08`](08_Collection_And_EPoL.md) §11.1 fixes the mechanism (a real-time factor on the world tick,
against an absolute target in the manner of `SumoCotBridge.py:243-248`, so an overrunning step is
absorbed rather than accumulating drift) and §11.3 fixes what happens when the consumer falls behind
(drop the oldest pending frame and count it; never back-pressure the world, because that would move the
sun). This section owns two things: the expression, which is §5.2's `pacing.*` block and its one Bound
row, and the picture.

#### 7.4.1 The panel

It is §7.1's monitor with four rows replaced, not a second display. Everything D12.14 requires still
holds: every field shown is a field the manifest carries, read from the same source.

```
bahonar_pattern_of_life :: night_shift          exr-20260308-2300     [ADVANCING]
sim   t=371 240 / 371 700   window  61.7%   |  civil 23:12:20 +03:30  sun -39.1 deg
pace  requested 1.00  achieved 0.97  floor 0.80   |  sim clock 4.1 s behind wall clock
sumo  population 139   rendered 139
chan  OVERWATCH-1  captured 1 482  recorder-dropped 0  |  offered 1 482  handover-dropped 37
chan  OVERWATCH-2  captured 1 482  recorder-dropped 0  |  offered 1 482  handover-dropped 41
recv  DETECT-A  last 0.4 s ago  312 blobs  application/octet-stream   (not parsed)
      EPOL-1    last 6.1 s ago   18 blobs  application/octet-stream   (not parsed)
truth manifest flushed t=371 238 (2 s ago)   truth feed OFF (08 D8.23)
```

| Row | Why it is there | Manifest field |
|---|---|---|
| **`pace`** — requested factor, achieved factor, floor, and how far simulated time has fallen behind wall clock | The one number a live operator is there to watch. [`02`](02_Use_Cases.md) UC-8's failure flow requires the degradation to be *displayed* rather than silent | `pacing.achieved_factor` per window ([`08`](08_Collection_And_EPoL.md) §11.1) |
| **Two drop counters per channel, side by side and never summed** | §7.4.2 | recorder: `FrameRecorder.Dropped`; handover: [`08`](08_Collection_And_EPoL.md) §11.3's per-sensor counter |
| **`recv`** — per transcript source: last-received age, blob count, content type, and the words *not parsed* | The operator must be able to see that the external chain is alive without the display implying we understood anything it said | `transcript.sources[].last_tick`, `.count` |
| **`truth feed OFF`** | [`08`](08_Collection_And_EPoL.md) D8.23 makes truth's own endpoint off by default in a live handover and turning it on a recorded choice. Showing the state stops it from being discovered afterwards | `telemetry.truth_endpoint` |

**Two displays, two audiences, and they must not be merged.** This panel is the *capture* operator's:
it shows the sun's elevation and the `ADVANCING` flag because those are how §1.6's defect is made
visible. The *exercised* operator's picture is [`08`](08_Collection_And_EPoL.md) §11.4's, and D8.23
specifically forbids `advancing` and `rate` from riding a feed an exercised operator sees, for the same
reason truth does not: they describe the simulation's configuration, and an exercise is not supposed to
show its own scaffolding. A single merged display would have to satisfy both rules and could not.

**A second loud condition, in a live run only:** the achieved real-time factor falls below
`pacing.min_achieved_factor`. It is loud rather than a column because a live exercise that has stopped
keeping time is no longer the thing anybody is watching, and check 44 has already proved at pre-roll
that the machine *could* hold the rate — so a shortfall inside the window is a change, not a
misconfiguration.

#### 7.4.2 Two drop counters, and why summing them would be a lie

They count different losses and they have different verdicts.

| | Recorder drop | Handover drop |
|---|---|---|
| What was lost | a frame that never reached disk | a frame that reached disk but not the consumer |
| Where | the encode queue's `DropWrite` channel, counter incremented at `FrameRecorder.cs:184-185` (declared at `:46`), which §7.3 measures as having **no reader anywhere in the tree** | the handover socket's drop-oldest queue, per sensor ([`08`](08_Collection_And_EPoL.md) §11.3) |
| What it does to the corpus | **a hole.** The imagery is short and the coverage record says the camera saw something that no file holds | **nothing.** The corpus is complete; the coverage record marks that `(sensor, tick)` *covered but not delivered* |
| Verdict | **loud** — §7.1's loud condition, and D10.7 makes a non-zero value a gate record that misses its threshold | **a counted column.** [`08`](08_Collection_And_EPoL.md) §11.3 rules it the correct behaviour, so interrupting on it would be interrupting on the design working |

A single "dropped" figure would be a number that is neither: non-zero on a healthy live run, and unable
to distinguish a corpus with holes from a consumer that reads slowly. They are separate fields in the
manifest, separate columns on the panel, and separate rows in the closeout.

#### 7.4.3 The transcript is stored, never interpreted

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c and [`02`](02_Use_Cases.md) UC-8 step 4 fix what a transcript is:
what crossed the interface and when, recorded verbatim and tick-stamped, "an opaque blob with a
timestamp, a source id and a content type". This section adds only where it goes and how it is switched
on: `transcript.root`, which **is refused inside either corpus root** (check 42).

That is not a third corpus root. It is the same ruling
[`08`](08_Collection_And_EPoL.md) D8.38 makes for a probe's workspace — outside both roots, never
released with the corpus, never digested into a manifest, never cited by a corpus artifact. The reason
here is the one D8.17 gives for the two-root split in the first place: a directory inside the corpus
that holds somebody else's model output is one refactor away from being read as a label. There are two
roots, and received data is not in either of them.

### 7.5 What this section needs from `08_Collection_And_EPoL.md`

Stated as properties, in the manner of §4.6.

| # | Property needed | Why this section needs it |
|---|---|---|
| **L1** | **The handover drop counter's identity and scope** — per sensor, per session, incremented at the socket — and a statement that it is a distinct field from `FrameRecorder.Dropped`, with distinct names in the manifest | §7.4.2. If the two land in one field, the closeout's gate on a recorder drop (D10.7) fires on a healthy live run, and the first thing anybody does about that is switch the gate off |
| **L2** | **At what granularity the achieved real-time factor is published** — per tick, per window, or per session — and by which component | D12.14 forbids the monitor from computing its own figures, so the `pace` row can only show a field something else already publishes. §11.1 says the achieved factor must be recorded per session; the panel needs it at least per window and ideally as a rolling value |
| **L3** | **A ruling on the pacing floor**: below what fraction of the requested real-time factor has a live exercise failed, or an explicit statement that there is no such number and the operator supplies one | §5.2 gives `pacing.min_achieved_factor` no tool default on the assumption the answer is *the operator supplies it*. If `08` fixes a number, it becomes a tool default and check 44's message changes |
| **L4** | **Confirmation that a transcript is not a corpus artifact** and belongs outside both roots on D8.38's precedent, or a ruling that overrules it | §7.4.3. `08` owns the root structure and the release attestation (D8.17); this section should not place a new directory near it without that section agreeing |
| **L5** | **Whether a live handover session also records to disk**, which §16 open question 7 recommends with the session assigned to the held-back release partition by default | It decides whether `pacing.mode: wall_clock` implies a corpus at all, and therefore whether §7.2's gate records and §3.10.3's `produced` block describe a live run or are empty for it |

### 7.6 What a caller can see while a run is in progress

An external process decides "when enough is enough" by **querying**, not by reading a verdict at the
end ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d). Two surfaces already exist, they answer different
questions, and neither is new.

#### 7.6.1 The server, through CarlaNet and the Python shim

An observer attaches as a second client. Three properties, all measured on 2026-09-18:

- **The world-observer subscription is opt-in and must be started explicitly.** `Client.__init__` does
  not start it and the comment at `carlanet/__init__.py:2224-2226` records why — auto-starting races the
  first RPCs and times them out — so an observer calls `Client.start_observer()` (`:2233-2239`) first.
  Without it `get_actor_ids()` returns an empty set and `get_solar_state()` silently falls back to an
  RPC.
- **Three reads are free**: `World.get_sim_time()` (`:2017-2025`), `World.get_actor_ids()`
  (`:2027-2036`) and `World.get_solar_state()` (`:1511-1533`) are all cache reads against the frame the
  observer stream last delivered. No RPC, and therefore no service time added to the tick.
- **One read is not**: `World.get_actors()` (`:2038-2050`) is a blocking RPC whose handler serialises a
  full description and bounding box for every actor, which its own docstring says outright. An observer
  that polls it is paying for data `get_actor_ids()` gives away.

That distinction is the whole of the cost question. [`10`](10_Scale_And_Performance.md) D10.10 measured
why: in synchronous mode the server drains every client's pending requests on the game thread before
advancing (`CarlaEngine.cpp:331-343`), so a polling client's service time is added directly to the
tick, while "a consumer needing world state reads the world-observer push stream, which costs no RPC".
**An observer reads the stream; it does not poll.** §6 check 27 warns when other clients are attached,
and it stays a warning rather than becoming a refusal precisely because an observing caller is a normal
condition under this brief — the warning tells an operator that the tick has company, which remains
worth knowing.

**What this surface answers:** how far simulated time has got, whether the world is still ticking, what
the sun is doing, and how many actors exist right now.

**What it cannot answer, and this is a boundary rather than a gap to design around:** anything about the
corpus. **The server holds no capture state.** Frames written, annotated intervals closed, area covered
and gate records are in no RPC and in no snapshot field, and no amount of client-side work reaches them.
They are in the artifacts, which is the second surface.

Two shim defects an observer would hit, both already recorded:
`World.wait_for_tick` returns a synthetic `Timestamp(0, 0.0, 0.0, …)`, discarding the timestamp its own
handler was given (`carlanet/__init__.py:2190-2209`; [`05`](05_CarlaNet_Capability_Audit.md) §5.3), so a
caller that keys liveness on `ts.frame` reads zero for ever — `on_tick` (`:2158-2163`) and
`get_sim_time` are correct and are what an observer should use. And the opt-in observer is documented
only in a constructor comment, which is where a caller will not look.

#### 7.6.2 The artifacts, read as they are written

The manifest is appended to while the run proceeds (§3.10.2 step 4, §7.2), and the two roots fill as
captures are published. Tailing them is how every corpus question is answered, and it costs the running
session nothing at all.

| Question a caller asks | Answered by | Surface |
|---|---|---|
| How far has simulated time got? | `World.get_sim_time()` | free cache read |
| Is the world still ticking? | the observer frame's tick number advancing, via `on_tick` | push stream |
| What is the sun doing? | `World.get_solar_state()` — `solar_time`, `sun_elevation_deg`, `advancing`, `rate` | free cache read |
| How many vehicles are rendered right now? | `World.get_actor_ids()` | free cache read |
| How many frames have been written? | the manifest's per-channel `written` | artifact |
| How many were captured but not yet written? | the manifest's per-channel `captured`; the difference is what a kill would lose | artifact |
| How many annotated intervals have closed? | the manifest's supervision block ([`06`](06_Truth_And_Annotation.md) §8.4) | artifact |
| How much of a declared area has been covered? | the coverage record ([`08`](08_Collection_And_EPoL.md)) | artifact |
| Which gate records do not currently meet their threshold? | the manifest's gate records (§7.2) | artifact |
| Is the run still alive, and did it stop? | the manifest's last append instant; then `RunResult` appearing, or not appearing (§3.10.2) | artifact |

**None of that is a new channel.** D12.14 already requires the monitor to display only fields the
manifest carries, read from the same source — so the panel an operator watches and the file an external
process tails are **the same numbers**, and a run describes itself in exactly one place.

#### 7.6.3 What this needs from `04_Contracts.md`

Stated as properties, in the manner of §4.6 and §7.5. [`04`](04_Contracts.md) is specifying the
queryable surface and owns the per-artifact guarantees; these are what this section's monitor, its gate
records and an external observer all rest on.

| # | Property needed | Why this section needs it |
|---|---|---|
| **Q1** | **A stated flush cadence for the manifest, and a stated maximum staleness** | Without it a caller cannot tell *nothing has happened* from *the writer has stalled*, and §7.1's manifest-flush row is the monitor's only liveness indicator for the truth path |
| **Q2** | **An append-only manifest shape whose truncated tail is discardable**, so that reading it while it is being written, or after a kill, yields the records up to the last complete one and no error | §3.10.2. The measured counter-example is in the tree: the CoT XML sink needs its closing token and the CSV beside it does not (`SumoCotBridge.py:208-211`, `:283`, `:274-276`) |
| **Q3** | **The in-progress counters live in the manifest**, not in a side-channel | D12.14. A second place to read a number is a second number |
| **Q4** | **Atomic publication of a capture's two files, and a stated publication order** | §3.10.2. A reader tailing the observation root while the run proceeds sees the same torn states a killed run leaves behind, so one rule covers both |

---

## 8. The operator's session

Partitions are the operator, the control surface, the capture session and the corpus.

```mermaid
flowchart TB
    subgraph OP["Capture operator"]
        direction TB
        O1["Pick the scenario and read its<br/>declared windows and epoch"]
        O2["Choose a window:<br/>a declared name, or an explicit span"]
        O3["Set the solar policy:<br/>advance (default) or freeze"]
        O4{"Is the window dark?"}
        O5["State vehicle_lights:<br/>from_sumo or off"]
        O6["Launch"]
        O7["Read the refusal;<br/>fix the named field"]
        O8["Watch the monitor"]
        O9{"Loud condition?"}
        O10["Stop the session"]
        O11["Read the gate records"]
        O12{"Did every record meet<br/>its threshold?"}
        O13["Use the corpus"]
        O14["Use it, knowing which records<br/>did not; or fix and re-run<br/>from the manifest"]
    end

    subgraph CS["Control surface"]
        direction TB
        C1["Resolve layers 1-6;<br/>record provenance per field"]
        C2["PHASE 0 offline checks 1-19<br/>no server, no GPU"]
        C3["PHASE 1 server checks 22-27"]
        C4["PHASE 2 acquire population authority"]
        C5["PHASE 3 pre-roll checks 30-32"]
        C6["Emit resolution report + lock"]
        C7["Render the live monitor<br/>from manifest fields only"]
        C8["Render the gate records<br/>as last appended"]
    end

    subgraph SESS["Capture session"]
        direction TB
        S1["Write session block +<br/>effective configuration verbatim"]
        S2["Synchronous mode; solar applied;<br/>read back and confirm"]
        S3["SUMO from t=0 to begin - prewarm"]
        S4["Window: cue, apply poses,<br/>capture, reconcile, append"]
        S5["Drain recorders; append the<br/>closing manifest record;<br/>write RunResult; THEN release<br/>vehicles, lease, sync mode"]
    end

    subgraph CORP["Corpus"]
        direction TB
        P1[("OBSERVATION root<br/>imagery + collect.json")]
        P2[("TRUTH root<br/>sidecars + labels + depth<br/>+ manifest + coverage")]
    end

    O1 --> O2 --> O3 --> O4
    O4 -->|"yes"| O5 --> O6
    O4 -->|"no"| O6
    O6 --> C1 --> C2
    C2 -->|"refuse"| O7 --> O2
    C2 -->|"accept"| C3
    C3 -->|"refuse"| O7
    C3 -->|"accept"| C4
    C4 -->|"held by another"| O7
    C4 -->|"granted"| C6 --> S1 --> S2 --> C5
    C5 -->|"refuse"| O7
    C5 -->|"accept"| S3 --> S4
    S4 --> C7 --> O8 --> O9
    O9 -->|"yes"| O10
    O9 -->|"no, window continues"| S4
    S4 -->|"reached the end it was given"| S5
    O10 --> S5
    KILL(["Caller stops us:<br/>signal, at any instant"]) --> S5
    S4 --> P1
    S4 --> P2
    S5 --> C8 --> O11 --> O12
    O12 -->|"yes"| O13
    O12 -->|"no"| O14

    classDef refusal fill:#7a2020,stroke:#d06060,color:#fff
    class O7 refusal
```

Three things the diagram asserts:

- **Every refusal returns the operator to choosing the window, not to a restart.** The refusal names a
  field; the fix is an edit to that field; nothing that was acquired has to be released because
  nothing was acquired.
- **The darkness branch is in the operator's lane, before the launch.** It is a conditional
  requirement (§4.4), so it is a decision the operator makes rather than a default they inherit.
- **A capture run writes exactly two roots, and there is no third.** Model output is neither
  produced nor consumed by this pipeline, so nothing here holds it
  ([`04`](04_Contracts.md) D4.26). The `OBSERVATION`/`TRUTH` separation is the whole of the
  anti-leak boundary's physical form. A live run's transcript and
  a run's `RunResult` are written **outside both**, and neither is a corpus artifact (§7.4.3, §3.10.3).

**The same diagram is the unattended traversal with one lane replaced.** Every decision in the operator
lane is made in advance and in writing: the window is named in the run configuration, the darkness
branch is answered by `solar.vehicle_lights` before launch, a refusal is returned as an exit status and
a named field in `RunResult` rather than read on a screen, the loud-condition branch terminates the run
instead of asking, and the closing branch reads the gate records rather than a verdict. **The
control-surface and capture-session lanes are unchanged**, which is the whole claim of §3.10: the
machine is the same surface with a caller that cannot be asked a question.

**One edge has no attended equivalent, and it is drawn deliberately.** The caller may stop the run at
any instant and for reasons of its own, entering §3.10.2's termination sequence from wherever the
capture-session lane had got to. It is not a branch out of a decision node because there is no decision:
it arrives from outside, it is expected, and the sequence it enters is the same one an ordinary close
uses.

---

## 9. Coexistence with `run_SCTMV.py`

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3 item 6 keeps the traffic-manager path and the OpenSCENARIO
executor; §4 forbids losing a capability. This section adds a second front end; it removes nothing.

### 9.1 What stays exactly as it is

`run_SCTMV.py` and `CarlaControlArgumentParser` remain the interactive path: world building, free
flight and orbit, ambient staging traffic, storyboard execution, live telemetry, ad-hoc recording, and
the thirteen hotkeys. Every one of its 87 arguments — the 86 §1 measured and `--aoi` — keeps working. In particular, three capabilities
live **only** there and must not be assumed to migrate:

| Capability | Why it stays interactive |
|---|---|
| **Free-flight camera control** (`PyGameSensorController`) | A capture run's camera track is declared, not flown. Siting a stare or an orbit is done by flying there first. Under SUMO drive the same controls fly a camera inside `run_sumo_drive.py` and record from it (§9.6); `run_capture` offers none |
| **`solar.policy: accelerated`** (`--time-rate`) | Refused for a capture run (§4.3), and genuinely useful for look development |
| **`--async` free-running mode** | A capture run is synchronous by [`10`](10_Scale_And_Performance.md) D10.10 |

### 9.2 What is shared, and how

Three concerns are genuinely common. Each gets **one definition and two front ends**, never two copies.

| Concern | Single definition | Consumed by |
|---|---|---|
| **World build inputs** — the 24 arguments of `world build (phase 1)`, `--aoi` among them | `WorldBuildConfiguration` (§3.9). `CarlaControlArgumentParser`'s build group is refactored to *construct* one; the run configuration's `world_build` block deserialises one | `WorldBuilder`, both front ends |
| **Sensor rig construction** | `SensorRig` already takes an `args`-shaped object; it takes a typed channel description instead, and the argument parser builds a one-channel description from `--x/--y/--z/--fov/--width/--height/--depth-max-range` | both front ends |
| **Recorder construction** | `NativeRecorder` already takes `args`; it takes a typed capture description instead | both front ends |

The rule that makes this work: **a default is defined exactly once, in the schema, and the argument
parser's `default=` reads it from there.** §1.5 measured why this is not optional — three
`getattr(args, …, fallback)` sites already disagree with the parser, and they disagree *because* they
were written to survive a caller that does not use this parser. A second front end is exactly that
caller. `depth_max_range` would ship 20 km through one path and 1 km through the other, silently
truncating the occlusion measurement that the observed denominator rests on.

### 9.3 What must not be duplicated

| Must not be duplicated | Because |
|---|---|
| Defaults | §1.5, measured: three already divergent |
| The world-build argument set | 24 arguments, 23 of them carrying a measured 2,832 characters of hard-won help text, including the measurement instructions in `--road-offset-east` (`:149-164`) |
| The solar application path | §1.6's defect exists once; duplicating it doubles it. `WorldBuilder.setup_solar_time` is replaced by a shared `SolarApplication` that both front ends call, with the capture path supplying an epoch and the interactive path supplying `--time`/`--date` as it does today |
| Validation | §6.1: one compiler, one refuse/warn vocabulary, one report renderer |
| The session identity | [`08`](08_Collection_And_EPoL.md) D8.4: assigned once per session and handed to every channel; `FrameRecorder`'s wall-clock fallback (`FrameRecorder.cs:98-103`) survives only for a single-channel interactive run |

### 9.4 Repairs the interactive path needs anyway

These are not migration work; they are defects §1 measured, and they should be fixed in
`run_SCTMV.py`'s own surface whether or not the new one is built.

1. **`--ev` is refused or removed** (§1.3). A flag that cannot work must not parse.
2. **The three divergent defaults are reconciled** (§1.5), starting with `depth_max_range`.
3. **`--time-rate` without `--time-advance` warns** rather than being silently inert (§1.3).
4. **`setup_solar_time` does not overwrite the sun in attach mode** unless `--time` or `--date` was
   given (§1.6). This is a behaviour change and is the right one: attaching to a world should not
   silently relight it.
5. **The four undocumented hotkeys are documented** in the module docstring, `P` in particular
   (§1.2).
6. **`parse` and `parse_args` behave identically**; the RNG side effect at `:640-641` moves to the
   caller.
7. **The post-process profile default resolves on both platforms, and a profile that did not load is
   not silent** (§5.2). The interactive viewer spawns its RGB camera through the same
   `UActorBlueprintFunctionLibrary::SetCamera` path, so the case mismatch between the default name
   `"default"` (`ActorBlueprintFunctionLibrary.cpp:1376`) and the file `Default.json` reaches it too:
   its own imagery is exposed differently on Windows and on Linux with no message either way. The
   engine-side half — publishing the setters — is
   [`08`](08_Collection_And_EPoL.md) D8.27's; the half owed here is that a discarded `false`
   (`:1377-1380`) stops being discarded.

### 9.5 The conversion, stated as a property

There is no cut-over. The run configuration schema is a superset of what the interactive path can
express for the overlapping concerns, so `run_capture --emit-run-configuration` can render a
`run_SCTMV.py` command line into a run configuration, and the resolution report shows what each flag
resolved to. That is the migration path for an operator with a command line they trust: run it once
through the converter, read the report, keep the file.

### 9.6 Flying a camera inside the drive

`CarlaNet/python/run_sumo_drive.py --view free` puts the free-move camera inside the process that
drives the world. `--view fixed`, the default, is the drive as it was: one camera, aimed once,
recorded from the window's opening to the end. `--view free` spawns no fixed camera; it opens the
window, flight controls and heads-up display of `CarlaControl/scripts/run_free_move_camera.py` --
`PygameInterface` opened read-only, `SensorRig` (the RGB camera and its depth camera) and
`PyGameSensorController`, the same classes, not copies — and gives the window's F key a recorder
that writes the flown camera's captures with the session's render set and illumination, as the
fixed camera's are written. It is `carlacontrol.FreeView` for the window and
`carlacontrol.SpanRecorder` for the key. `run_free_move_camera.py` stays the separate viewer that
records nothing, and `run_capture` offers no flown camera: a capture run's camera track is declared.

**What another process reads during a drive (2026-10-01).** The drive's render set is carried on every
world-observer snapshot ([`03`](03_CoSimulation_Runtime.md) §8.9, D3.39), so the truth any other
process reads -- `world.get_vehicle_telemetry()`, the CoT feed `run_SCTMV.py` toggles with Y,
`cot_telemetry.py`, a recorder started outside the drive -- lists only the bodies each frame drew, each
named by its SUMO vehicle, with `role_name` `sumo`, and no body parked below the ground. The drive's
report prints a `render set` line: how many changes the session named to the server, any body the
server did not find, and, against a server built before this, the server's refusal, after which other
processes list every vehicle actor as they did before and the drive's own captures are unaffected.

| Option | Default | What it does |
|---|---|---|
| `--view` | `fixed` | `free` opens the flight window in place of the fixed camera |
| `--camera-z` | `300.0` | the free camera starts over the centre of the world's staging bounds at this height, looking straight down |
| `--flight-speed` | `60.0` | the free camera's starting speed, m/s; the mouse wheel changes it |
| `--width`, `--height`, `--fov` | fixed `1920`, `1080`, `60`; free `1280`, `720`, `90` | the camera's image, which is also the window's size; a value given applies to either view |
| `--record-dir` | `Build/captures` | a free view writes each span to a folder of its own under it |
| `--no-record` | off | a free view opens and flies, and F records nothing |

`--camera-standoff`, `--camera-yaw` and `--camera-aim` are the fixed camera's and do nothing in a
free view.

| Key | In the free view |
|---|---|
| RMB + mouse | look around |
| W/S, A/D, E/Q | fly forward and back, strafe, up and down |
| Mouse wheel, Shift | flight speed; Shift triples it |
| Ctrl + LMB | measure the latitude, longitude and elevation of a point |
| B / M | the perimeter and margin overlays, drawn in the window only |
| **F** | ask for a recording span; F again ends it, or cancels the wait for tiles |
| Space | back to the start pose |
| Esc, or closing the window | ends the drive: the loop stops at the next step, the report is printed and everything is given back, as at the end of a scenario |

The keys that toggle a layer, the ground's collision, the road mesh or the sun's advance (C, G, V,
R, L, K) are not bound: the window is read-only, and the drive owns all of those.

**A recording span.** F asks for a span; nothing is recorded until the capture window has opened --
before then F is refused with the instant it opens — and a span starts only once the camera's
photoreal tiles are in. Each span is written to `<record-dir>/CARLA-SENSOR-<camera id>-<UTC>`, the
UTC instant as `yyyymmddThhmmssZ` with a numbered suffix for a second span begun in the same second,
so a folder names the platform uid its sidecars carry and the instant it began; the file names
inside are the recorder's own. `SpanRecorder.span_directory` is the one place that name is made.
Every span of one drive carries the drive's run id (`run-<UTC>`, logged at start), so the spans can
be gathered back into the run. The recorder is the fixed camera's --
`world.start_recording(camera, span folder, --record-hz, fov, run_id, depth_camera=the rig's depth
camera, illumination=session.Illumination, render_set=session.RenderSet)` — through a world object
of its own, so the drive's other recording calls never touch it. When a span ends it is flushed and
its counts are logged, as the fixed camera's are at the end of a drive: captures written and
dropped, illumination paired and not, render-set paired and unpaired and bodies missing, and
occlusion measured and not with the reason.

**The tiles.** A flown camera renders new ground while Cesium is still streaming it
([`03`](03_CoSimulation_Runtime.md) §9.5.1), so a span waits for `world.get_view_readiness(camera)`
to say the tiles are in — the camera published on the tick answered for, and every visible tileset
at load progress 100 with no failed tile in view — on a frame later than the first answer of the
wait, which is a frame rendered after the key was pressed. The ceiling is §9.5.1's 90 s of wall
clock, and it only fails: past it the span is not started, the window's note says so with how far
the tiles got, and F tries again. Readiness covers every registered view, so a camera still being
flown holds the progress below 100 until it stops. A server that cannot answer is said so, and the
span starts without the wait. The picture's own settling, §9.5.1's second witness, is not waited on:
the operator is watching the view the span writes. While a span records, readiness is asked once per
capture period, the heads-up display shows it, and the log says when the view's tiles stop being in
and when they are in again, with the frame. It is not written into a capture: it describes the view
as of the last tick, and an image reaches the recorder several ticks after its frame with the camera
possibly moved in between. Recording each capture's own readiness needs the server to publish it per
frame, on the observer snapshot, which is not built. Every readiness call is timed, and the log
gives the count, median and worst when a wait ends and when a span closes.

**The heads-up display's record field** reads `off`; `waiting for tiles, 87% (12 s)` during the
wait; and while recording `REC 24@2Hz -1 dropped  set 23 paired -1 unpaired  tiles in` — the
captures written at the rate, the recorder's `Dropped` when non-zero, the captures listing their own
frame's rendered vehicles and those that could not, and the tiles. `NativeRecorder`'s field in
`run_SCTMV.py` reads exactly as it did.

**The pose a capture records is the image's.** The sidecar's platform point and boresight, and the
pose the capture's occlusion is measured from, are the camera's pose in the client's world snapshot of
the image's own frame (`CarlaClient.GetSnapshotFrame`, the snapshot the capture's truth records are
read from), never the camera actor's current transform, which for a camera flown between the frame and
the image's arrival is somewhere else. The transform in the image's sensor header is checked against it
and not trusted, because on this path it was wrong (measured 2026-09-30):

- **What the header carried.** This section said, until this correction, that the server takes the
  header's transform in the same game-thread call that captures the frame, citing
  `SendPixelsInRenderThread` in `PixelReader.h`. That function does, but nothing calls it. Every camera
  -- RGB, depth, semantic and instance segmentation, normals, optical flow -- reads its image back from
  the GPU asynchronously (`ImageUtil::ReadSensorImageDataAsyncFColor`, `ReadImageDataAsync`) and sends
  it from the read-back callback through `ASensor::SendDataToClient`, which built the header there,
  from the camera's transform and the episode's clock as they stood at that moment, and put back only
  the frame number. The callback runs on the render thread up to a frame behind the game thread, and by
  then the client's next `set_transform` had been served. Measured on Bahonar with a camera moved
  before every synchronous tick (0.05 s) through a three-pose cycle told apart by sky colour: the pixels
  of the image labelled F were F's pose in 90 of 90 images; its header's transform was the pose
  commanded for F+1 in 89 of 90; the client's snapshot of F held the camera at F's pose in 59 of 60,
  the other frame not held exactly. `test_moving_camera_pose.py` failed live, 5 of 34 captures carrying
  their own frame's pose and 29 a later frame's. `FrameRecorderSensorPoseTests` had passed because it
  fed the recorder an image whose header held the right pose, which was the premise in question.
- **The header's clock.** Read in the same callback, so late whenever the callback ran after the next
  tick had begun: the frame counter and the episode's clock advance together as the tick cue is
  received (`FCarlaEngine::OnPreTick`), and the frame number was put back while the clock was not.
  Reasoned, not measured: in the measurement above the header's transform was already the next frame's,
  so the callback ran after the client's `set_transform` for F+1 had been served, and the client sends
  its cue for F+1 one round trip later; for the clock to have been on time the callback would have had
  to fall in that sub-millisecond gap in 89 of 90 frames, so wherever the pose was late the clock almost
  certainly was too, and that holds for a still camera as well, whose pose cannot show it. Where the
  client waits between ticks, as a paced drive does, the callback can fall before the cue and the clock
  be on time. The captures on disk do not settle which: in each of five recorded runs (336 captures)
  `sim_time_s` stays locked to `tick` at exactly 0.05 s, so the clock fell on the same side every time
  within a run, and nothing recorded says which side. The live check now measures it.
- **The server fix.** `ASensor::MakeCaptureHeader` takes the frame, the episode's clock and the
  sensor's world transform -- and, for ROS2, its transform relative to the actor it is attached to --
  in the game-thread call that captures the frame, and `SendDataToClient` writes all three into the
  header from it (`SetFrameNumber`, `SetTimestamp`, `SetTransform`, as `SendPixelsInRenderThread`
  does). All six cameras use it. None of the other sensors was late: the DVS camera reads its image
  synchronously and builds its header in the call that captured it, and the ray-cast lidars, radar,
  GNSS, IMU, collision and obstacle sensors build and send theirs on the game thread in the tick they
  measured. The G-buffer path (`SendGBuffer` in `SceneCaptureSensor.h`) builds its header the late way
  too, but compiles only against an engine with `GBufferView.h`, which 5.7.4 does not have. A ROS2
  camera message now carries the capture's transform; its stamp is still ROS2's own clock at publish
  time, as before.
- **The recorder's check.** `SensorPoseCheck` takes the pose from the snapshot of the image's own
  frame whenever the client holds that frame exactly and the camera is in it, and compares the
  header's with it: 1 cm, and 0.01° on each of the forward and up axes, compared as axes so that a yaw
  of 180 and one of −180, or yaw traded for roll looking straight down, are not a disagreement. It
  counts `SensorPoseFromSnapshot`, `SensorPoseHeaderDisagreed` among those, and `SensorPoseFromHeader`
  for a frame the client no longer holds, which is written with the header's pose because the nearest
  frame held is the camera at another instant. The depth capture occlusion is projected from goes
  through the same check for the depth camera (`OcclusionDepthPose*`): projected from a late header,
  this frame's vehicles would be measured against where the depth camera went next. `start_recording`
  hands the recorder both camera ids. `run_sumo_drive.py`'s summary and each span's closing report say
  the counts, louder when a header disagreed; `run_capture` gates `capture.sensor_pose_header_disagreed`
  and `capture.depth_pose_header_disagreed` at 0 (§7.2); `run_SCTMV.py`'s recorder says them when it
  stops. The sidecar's `sim_time_s` still comes from the header's clock and is not checked; the server
  fix is what makes it the frame's.
- **Exercised by** `FrameRecorderSensorPoseTests`, which streams a real recorder the defect as it was
  measured -- an image of frame 100 whose header holds the pose the camera was moved to by frame 103,
  while the snapshot of frame 100 holds where it was -- and checks that the sidecar's point, hae,
  azimuth and elevation and the PNG's `carla:sensor` chunk carry the snapshot's, with one disagreement
  counted; that a header that agrees counts none; that a frame the client no longer holds is written
  from its header, not from the nearest frame, and counted apart; and that a depth capture is projected
  from its frame's snapshot in both cases. `SensorPoseCheckTests` covers the lookup, the tolerances and
  the equivalent angles. Each was seen failing against the recorder reading the header, against the
  nearest frame taken for the image's, against a disagreement left uncounted, against the depth capture
  projected from its header, and against angles compared as numbers. `CarlaNet/python/test_moving_camera_pose.py`
  is the live check. It reports separately the snapshots against the commanded poses (the control), the
  headers' poses and timestamps against their own frame's (the server fix), each sidecar against the
  pose commanded for its own frame and the three after it, and the recorder's counts (the check). With
  the wheel rebuilt and the plugin not, the sidecar lines pass and the header lines fail, the recorder
  counting the disagreements; with both rebuilt, every line passes and the count is 0.

**Threads, and what the window costs the drive.** The drive keeps its thread: the session's steps,
the world's ticks and the pacer's waits. The window is created, pumped and drawn on a thread of its
own, which SDL requires on Windows and which keeps every frame it draws off the drive's thread; the
flight controller moves the two cameras from its mover thread in one batch; the span recorder starts,
stops and asks about the tiles on a fourth. The drive hands the session no per-vehicle callback: the
worst divergence it logs is read off the session's report, which already names the vehicle and tick,
so no Python runs inside the tick loop for another thread to hold up. **Measured offline**
(pythonnet 3.2, Python 3.14, Windows; a 1280×720 frame converted and drawn at 20 fps on a pygame
thread beside two threads copying 3.7 MB frames at 20 Hz, as the rig's listeners do): the interpreter
is released for the whole of a .NET call; a thread coming back from one waits for it at most 4.5 ms
(p99 0.08 ms), a switch interval; 128 Python callbacks inside one .NET call took a worst 2.8–5.7 ms
against under 1 ms with no window thread, which is the wait the divergence callback would have put in
the tick loop. pygame also sets the system timer's resolution for the process: a 20 ms .NET wait ends
a median 0.5 ms late with the window open against 11 ms without, so the pacer's cues
([`03`](03_CoSimulation_Runtime.md) §9.9) go out closer to their due instants while the view is up.
What the server pays is the rig's two 1280×720 cameras rendering every tick, the same as
`run_free_move_camera.py` beside a drive; the drive's achieved pace with and without the window at
`--real-time-factor 1.0` is not yet measured.

**Every vehicle is drawn wherever the camera is flown.** The session draws every vehicle SUMO has, so
a flown camera finds the traffic wherever it goes, and nothing the camera does changes which vehicles
have bodies; the same holds for the fixed camera. The free camera starts over the centre of the
world's staging bounds (`get_staging_bounds`).

**Exercised by** `test_span_recorder.py` (the wait, the first answer not trusted, the ceiling, a
failed tile, cancelling, the capture window, a server with no answer, the folders and their suffix,
the flush before the report, the counts, the readiness asked per capture period, and the recorder's
own thread doing the starting and stopping), `test_free_view.py` (one thread that is not the
caller's makes, pumps and draws the window; no write to the world whatever is pressed; Esc; the
note; a window that cannot open; the record field) and `test_run_sumo_drive_free_view.py` (the span
recorded as the fixed camera records, the rig's start and depth range, and no divergence callback in
either view). Each was seen failing against a wrong implementation: the first answer trusted, no
ceiling, the capture window unchecked, a failed tile ignored, a stop left recording, the window not
read-only, the divergence callback bound, no depth camera, and the pairing not shown.

---

## 10. Both platforms

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §4: any change to `Scripts/Windows/*.ps1` requires its
`Scripts/Linux/*.sh` counterpart in the same change, help text and documentation included.

**The parity is already broken, measured.** `Scripts/Windows/MakeDistribution.ps1:237` copies
`CarlaNet\python\SCTMV.py`, a file deleted in `d2c666c23`; the copy only warns, so the build succeeds,
and `:301` then writes a `run-sctmv.ps1` that executes the absent script. The Linux script was
migrated correctly: `Scripts/Linux/MakeDistribution.sh:117` copies
`CarlaControl/scripts/run_SCTMV.py` and `:196` executes it. A second, unrecorded half of the same
break: `MakeDistribution.sh:112-113` bundles **both** the `carlanet` and `carlacontrol` wheels, while
the Windows script bundles only `carlanet` (`:230-234`) and mentions `carlacontrol` nowhere — so even
with the script path fixed, a Windows distribution could not import `carlacontrol`.

What this section's work requires, on both platforms in the same change:

| Artifact | Windows | Linux |
|---|---|---|
| Capture launcher | `Scripts/Windows/RunCapture.ps1` | `Scripts/Linux/RunCapture.sh` |
| Distribution launcher written into the bundle | `run-capture.ps1` beside `run-sctmv.ps1` | `run-capture.sh` beside `run-sctmv.sh` |
| Distribution contents | the `carlacontrol` wheel, the run-configuration schema, the site-profile template, the vehicle catalogue and the annotation vocabulary ([`09`](09_Toolchain_And_Packaging.md) §5.5) | the same set |
| `MakeDistribution` header comment describing the tree | `:8-20` updated | `:9-12` updated |
| Distribution README | the capture quick-start | the capture quick-start |

Three properties are required of the launchers, all because the underlying tool is the same program:

- **The `--help` output is generated from the schema, not hand-written**, so the two platforms cannot
  drift and an assistant reading `--help` gets the same field names the schema validates.
- **A parity check runs in CI**: the two launchers are invoked with `--help` and their option sets
  compared. The measured break above survived because nothing compared them.
- **The process exit status propagates identically through both launchers**, and the CI parity check
  compares that too: each launcher is invoked with a configuration that refuses in phase 0 and the
  status compared against D12.22's table. This is new with the unattended caller and it is exactly the
  kind of thing a wrapper script loses silently — the whole value of the status set is that a machine
  can act on it, and a launcher that returns 0 for a refusal is worse than one that returns nothing.
  The `run-capture` launchers must also pass `--result` through unchanged, since it is the only path on
  which a refusal leaves a readable artifact.

**As built.** `Scripts/Windows/RunCapture.ps1` and `Scripts/Linux/RunCapture.sh` run
`CarlaControl/scripts/run_capture.py` with every argument but their own `--python-exe` passed through
unchanged — `--result` included — and return its exit status unchanged. `--help` (and `-h`, `-Help`)
prints the launcher's one option and then `run_capture --help`, whose field list is generated from
`RunConfiguration.FIELDS`. The interpreter is the platform's Python 3: `python3` then `python` on
Linux, `python` then the `py` launcher on Windows, where `python3` is usually the Microsoft Store's
installer stub. The Linux launcher `exec`s `run_capture`, so a signal sent to the launcher reaches the
run directly; the Windows launcher runs it as a child in the same console, which Ctrl+C and
`CTRL_BREAK_EVENT` reach. `CarlaControl/test/test_run_capture_launchers.py` is the parity check: it runs
both launchers with `--help` and compares their option sets, and runs both with a configuration the offline
checks refuse and with a malformed override, comparing each exit status (2 and 1) with the status in the
result written at the `--result` path passed through. It runs wherever PowerShell 7 and bash are both
installed; whether CI runs it is the CI's owner's to decide. `Scripts/Linux/RunCapture.sh` must be
committed with mode `100755`, as the other Linux scripts are.

The distribution launchers (`run-capture.ps1`, `run-capture.sh`), the distribution's contents and the
`MakeDistribution` header comments of the table above are not built. What `MakeDistribution` has to do
for them, on both platforms in the same change: bundle the `carlacontrol` wheel (the Windows script
bundles only `carlanet` today); copy `run_capture.py`; write a launcher beside `run-sctmv` that runs it
exactly as the source-tree launchers do; ship `CarlaControl/schemas/run_configuration.schema.json`, a
site-profile template (`run_capture --write-site-profile`), the vehicle catalogue and the staged SUMO
installation; and either lay the distribution out so the site profile's derived paths hold —
`Build/scenarios`, `Build/world-packages`, `CarlaControl/catalogue/vehicles.catalogue.json`,
`Build/captures`, `Build/runs` and `Build/sumo-install` under the directory two levels above
`run_capture.py` — or ship a site profile naming the distribution's own paths, with
`CARLANET_SUMO_HOME` declared in its `environment` list if the distribution sets it.

The site profile (§3.5 layer 2) is what keeps the run configuration itself platform-neutral: paths,
the SUMO install, the ion token and the export root bases live there, so a run configuration authored
on Windows runs unedited on Linux.

---

## 11. What this section does not cover

- **The epoch's and the policy's semantics.** [`11`](11_Time_And_Illumination.md) owns them; §4.6
  states the six properties this section needs.
- **The scenario specification's schema.** [`07`](07_Scenario_Authoring.md) §3.5 owns it. This section
  requires only that it can declare an epoch and named windows.
- **The manifest's supervision content.** [`06`](06_Truth_And_Annotation.md) §8.4 owns it; §4.5 adds
  one sibling block and §7.2 reads it.
- **The values of the capture parameters**, `prewarm_s` among them. [`10`](10_Scale_And_Performance.md)
  §8 owns them; §5.2 records where each is set and which class it is in.
- **The optics and coverage of the rig.** [`08`](08_Collection_And_EPoL.md) §3 owns them.
- **A graphical interface.** Nothing above needs one. If one is built it is a producer of run
  configurations and a reader of manifests, and it changes nothing in §3 or §6.
- **The live exercise's operator picture.** [`08`](08_Collection_And_EPoL.md) §11.4 owns it; §7.1 and
  §7.4.1 are the capture monitor and the three are different displays for different jobs — and D8.23
  makes two of them structurally incompatible with a merge.
- **The pacing ruling and the drop policy.** [`08`](08_Collection_And_EPoL.md) §11.1 and §11.3 own them.
  This section expresses them (§5.2), shows them (§7.4.1) and states in §7.5 what it needs back. The one
  place it declines to offer a toggle at all is the drop policy, because that section has already ruled.
- **Numeric exposure control.** Measured absent from every camera blueprint (§1.3); exposure is
  selectable at spawn through `post_process_profile` instead (§5.2). Publishing the engine-side setters
  (`SceneCaptureSensor.h:237-393`) is [`08`](08_Collection_And_EPoL.md) D8.27's; §6 check 16 refuses a
  numeric exposure and names the field that works, and check 43 makes a profile that did not load
  loud.
- **The cadence, the training, the models — and when a run should stop.** What decides that a corpus
  should be regenerated, what trains on it, what any of it is worth, and when enough has been produced
  are all outside this plan ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b, §3d). §3.10 specifies an
  invocation, an honest record of whether we stopped or finished, and what the tool does when it is
  stopped; it does not schedule, does not loop, does not compare one run with another, and publishes no
  aggregate judgement of a corpus at all.
- **The per-artifact durability guarantees.** What each artifact guarantees after a kill at an
  arbitrary point is [`04`](04_Contracts.md)'s; §3.10.2 owns the tool's behaviour, and §3.10.3 K1–K4 and
  §7.6.3 Q1–Q4 state the properties this section needs back.
- **The queryable surface's contract.** [`04`](04_Contracts.md) specifies it; §7.6 states how it is
  exposed at the tool level and what the live panel shows, and names the one boundary that is not a gap:
  the server holds no capture state.
- **The external chain's formats, transports and failure modes.** A handover endpoint and a transcript
  source are configuration fields whose values this section never interprets. A reader must be able to
  substitute a completely different detector without any field above changing
  ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c).

---

## 12. Decisions

Decision numbers and validation-check numbers are **stable and never reused**, because siblings cite
them by number ([`08`](08_Collection_And_EPoL.md) §15 cites check 17).

| # | Decision |
|---|---|
| **D12.1** | **The present surface is measured, not characterised: 86 arguments in 9 groups, 0 modes, 1 mutually exclusive pair, 5 with no help text, 34 single-word names, and 66 of 86 inert unless a switch or hotkey outside their own group is on.** The redesign rests on those numbers and on three measured defects — `--ev` cannot work, `--time-rate` is silently inert without `--time-advance`, and three defaults are duplicated outside the parser with divergent values (§1) |
| **D12.2** | **The control surface is layered resolution, not an extended flat command line and not a bare configuration file.** Six layers in strict precedence: tool defaults, site profile, world-package bindings, scenario declarations, run configuration, operator overrides. Rejected: the flat command line, because a recorded argv is not a reproducible run description once a default changes — *measured:* `--fade` already flipped; and the bare file, because one scenario's 4–8 windows times a counterfactual sweep produces 8–16 near-identical documents (§3.2–3.4) |
| **D12.3** | **The recorded artifact is the `EffectiveRunConfiguration`, and every field in it carries its value, its layer, the tool default it would have had, and what any override replaced.** The manifest's copy is itself a valid run configuration, so reproducing a run is reading it back rather than reconstructing it. This is what makes layering safe: "where did this come from" is answered by the artifact, not by re-derivation (§3.6, §3.7) |
| **D12.4** | **A capture window is chosen by name from the scenario's declarations, or given explicitly by the operator, and the manifest records which.** A window may also declare a begin and **no end**, in which case the run continues until the scenario ends or the caller stops it. This closes [`01`](01_Architecture.md) open question 3 and [`02`](02_Use_Cases.md) open question 2 together, in the way both recommended (§3.8, §5.2) |
| **D12.5** | **Run-configuration validation is a further phase of [`07`](07_Scenario_Authoring.md) §5's compiler, not a second validator.** Same refuse/warn vocabulary, same resolution report, same lock-file shape. Phase 0 — 33 of the 48 checks in force — needs no server, no GPU and no SUMO, preserving [`02`](02_Use_Cases.md) D2.2's property one layer up (§6.1, §6.2) |
| **D12.6** | **Two mutual-exclusion mechanisms, both required.** A configuration naming a block of a non-selected mode is refused at compile time with no server involved; a world whose population authority is held refuses the session start naming the holder. The first catches a wrong request, the second catches a busy world; neither substitutes for the other (§5.3) |
| **D12.7** | **The solar policy has no tool default: the scenario states it, and a run takes it or overrides it with the override recorded.** A frozen run and an unconfigured run are byte-identical, so absence is made impossible rather than defaulted ([`00`](00_Overview.md) §6). The failure modes of an unstated policy are asymmetric: a run that wanted constant illumination and got an advancing sun records a small, correct, self-describing variation, while a run that wanted changing light and got a frozen one records a physically impossible constant that nothing flags. `freeze_at_window_start` is the recommended value, not a silent one (§4.3, §4.4) |
| **D12.8** | **Under `advance`, `rate` is pinned to 1.0 and is not operator-settable**, because the session writes each tick's own instant ([`11`](11_Time_And_Illumination.md) D11.19) and [`01`](01_Architecture.md) D1.1/D1.13 make one tick exactly `world_delta_s` of simulated time — so 1.0 is the only value under which one sun-second is one scenario-second. `accelerated` (any other rate) is **refused for a capture run and retained in the interactive path**, where it is useful and harmless (§4.3) |
| **D12.9** | **The solar epoch is a binding, not a choice.** It comes from the scenario package and an operator override of it is a refusal, for the same reason the world digest is: a scenario asserting 23:00 rendered at 12:00 is a self-contradicting corpus, and the surface must not be able to express the request (§4.3) |
| **D12.10** | **A field whose correct value depends on a condition has no default under that condition** — the *conditional requirement*. Built: `pacing.min_achieved_factor` has no default under `pacing.mode: wall_clock` and is not a field under `as_available`. Specified for when vehicle lamps exist: `solar.vehicle_lights` defaults to `off`, but in a window whose sun elevation falls below −6° it has no default and the run is refused until it is stated. This is how the surface stays short in the ordinary case without letting an important choice be implicit (§3.5, §4.4) |
| **D12.11** | **Seeds have no nondeterministic default.** Today `--seed` defaults to `None`, documented "nondeterministic" (`:310-316`), which is incompatible with reproducing a run from its record. `random` is still available and resolves to a drawn value that is then recorded (§5.2) |
| **D12.12** | **The solar state is read back from the world and recorded before the first capture, and a disagreement with what was requested refuses the run.** Today `WorldBuilder.py:238-247` logs what it asked for and never reads back, and a world with no CesiumSunSky produces a warning and a run that continues (`:244-245`) (§4.5, §6.2 checks 23 and 31) |
| **D12.13** | **Four mutability classes — Bound, Session-fixed, Degradation-only, Run-mutable — decided by one question: would a consumer reading the corpus be wrong if this changed and they did not know?** The occlusion estimator is Session-fixed rather than Run-mutable for exactly this reason, although it is a runtime toggle today. **Bound** reads *fixed by an artifact, or by a ruling in a sibling section that this surface expresses rather than re-offers*, which is what `synchronous` (D10.10), `telemetry.on_tick_thread` (D10.11) and the external-chain drop policy ([`08`](08_Collection_And_EPoL.md) §11.3) all need. **There is no fifth class**: `caller` and `on_warning.*` are Session-fixed by the governing question, and `caller_label` and `expect.*` are recorded in the lock as launch provenance rather than given a class of their own (§5.1, §5.2) |
| **D12.14** | **The live monitor displays only fields the manifest also carries, read from the same source.** A monitor that computes its own numbers can disagree with the record, and the operator believes the screen (§7.1) |
| **D12.15** | **One condition interrupts the operator in every run, and nothing else does:** a non-zero `Dropped` on any channel; a live run adds the achieved real-time factor falling below its floor (§7.4.1). A participant in an open annotated interval is always drawn, because every vehicle SUMO has is drawn, so it is a gate record rather than an interruption (§7.2). Everything else is a column (§7.1) |
| **D12.16** | **This surface publishes gate records and never an aggregate verdict.** Each record names what the check observed, the threshold it compared against and whether it met it; nothing rolls them into a single field saying the corpus is fit, because fitness is relative to a purpose the caller never told us ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d). The records are **appended to the manifest as they change**, so a run stopped at minute nine has already published everything it knew at minute nine, and the closeout is a rendering rather than the moment they come into existence. A record that did not meet its threshold deletes nothing and hides nothing (§7.2, §3.10.3) |
| **D12.17** | **`run_SCTMV.py` and its parser keep every one of their 87 arguments (§1's 86 and `--aoi`) and all thirteen hotkeys.** The new surface is a second front end over shared definitions — `WorldBuildConfiguration`, the channel description, the capture description, and one solar application path — with **defaults defined exactly once in the schema**. *Measured justification:* three `getattr` fallbacks already disagree with the parser and exist precisely to survive a caller that is not this parser (§9.2, §9.3, §1.5) |
| **D12.18** | **Seven repairs to the interactive surface are owed regardless of whether the new one is built:** refuse or remove `--ev`; reconcile the three divergent defaults; warn on `--time-rate` without `--time-advance`; stop overwriting the sun in attach mode; document the four undocumented hotkeys; make `parse` and `parse_args` behave identically; and fix the post-process profile default, whose name is the lowercase literal `"default"` (`ActorBlueprintFunctionLibrary.cpp:1376`) against a file named `Default.json` — so it resolves on a case-insensitive file system and silently does not on a case-sensitive one, with the failure discarded at `:1377-1380` (§9.4) |
| **D12.19** | **The launcher ships on both platforms in the same change, its `--help` generated from the schema, with a CI parity check comparing the two option sets.** *Measured:* the existing parity break survived because nothing compared them — `MakeDistribution.ps1:237` copies a file deleted in `d2c666c23` and only warns, `:301` then writes a launcher that runs it, and the Windows script additionally omits the `carlacontrol` wheel that `MakeDistribution.sh:112-113` bundles (§10) |
| **D12.20** | **Withdrawn 2026-09-30.** There is no render cap or render region to check a population against: every vehicle SUMO has is drawn, and a heavier scenario runs slower, never thinner (§6.2 checks 20 and 21, withdrawn) |
| **D12.21** | **The external caller is a first-class consumer of this surface, not a scripted human.** It is declared (`caller: unattended`), it is recorded in the corpus, and it changes what the tool may do without being told. **Three of its six requirements already hold and are not rebuilt:** no interactive prompt (*measured:* no `input()` anywhere in `CarlaControl/src/carlacontrol` or `CarlaControl/scripts`), a provenance-carrying effective configuration (D12.3), and a reproducible artifact (R1, D12.11). Three are specified here: a record that distinguishes stopped from finished (D12.22), a result artifact written in every outcome the tool survives (D12.23), and clean termination under a kill (D12.33). What this surface does **not** contain is a scheduler, a loop, a cadence, a run length or a comparison between runs — all of those belong to the caller (§3.10) |
| **D12.22** | **Eight named terminal outcomes whose live distinction is *stopped* against *finished*, and the process exit status is read from the result artifact rather than computed beside it.** *Measured justification:* `run_SCTMV.py` has two statuses — `1` on a failed world build (`:130`), `0` otherwise (`:339`) — and swallows `KeyboardInterrupt` at `:291-292`, so a run killed halfway is indistinguishable from one that finished; and [`07`](07_Scenario_Authoring.md) §5.5 measured `duarouter` exiting 0 regardless under `--ignore-errors`, concluding that an exit code alone is a gate that stops at the first error. The set is small on purpose, and **none of it is a verdict**: `run_finished` and `run_stopped` differ in how the run ended, not in whether the data is useful, and the refusals differ in how far the launch got. `refused_authority` is the one outcome whose cause is outside the configuration, and the result names the holder (§3.10.2) |
| **D12.23** | **`RunResult` is written in every terminal outcome the tool survives, at a path the caller gives, outside both corpus roots — and its absence means only that the tool was stopped before it could write one.** It carries the outcome and its status, `closed_by`, the end declared and the end reached, the launch echo, every refusal, every warning with its adjudication, per-channel *captured* and *written*, the gate records, and a pointer to the manifest or `null`. It carries **no aggregate verdict**. The path is separate because a phase-0 refusal never reaches [`08`](08_Collection_And_EPoL.md) D8.4's session assignment and so has no session root to write under. Its fields are [`04`](04_Contracts.md)'s `C10`; this section owns when and where the tool writes it, and §3.10.3 K1–K4 state the properties it needs back (§3.10.3) |
| **D12.24** | **The echo before commit is specified, and it blocks on exactly one condition: a phase-0 warning was raised.** [`01`](01_Architecture.md) §10.2 item 3 asked for it and [`13`](13_Work_Breakdown.md) §8 carries it. It prints on every attended launch; with no warnings it prints and proceeds. The rule follows [`07`](07_Scenario_Authoring.md) §5.3 — a refusal needs no human because it already stopped, a clean resolution needs no human because nothing is in question, and a warning *is* by construction the case the tool is not entitled to decide. **The echo is a rendering of a `launch_echo` block, not a computation**, so the block exists on both paths (D12.14's rule, one layer earlier) (§6.4.1, §6.4.2) |
| **D12.25** | **For an unattended caller the echo's two jobs are separated, and each gets a mechanism a machine can satisfy.** The judgement on a warning is made **in advance and per warning code** — `on_warning.<code>`, an unadjudicated raised code is a refusal (check 34), and the lock records the adjudication *and the artifact that granted it*. The catch on a configuration that resolved legally but not as intended is **declared expectations** — `expect.<path>`, checked offline, a disagreement refused naming both values (check 35). **Refused: block-with-timeout-then-proceed** (an unattended caller waits for nothing and an absent human gets the silent start the echo existed to prevent) and **a blanket `--yes`** (it records nothing about which warnings were accepted and would be pasted into every invocation on the first day) (§6.4.3, §6.4.4) |
| **D12.26** | **`expect` is the written-down form of the knowledge a human applies when reading the echo, and it can never supply a value.** §1.6's measured defect is the shape that motivates it: every value legal, nothing warning, the sun at noon, the corpus contradicting its own scenario. A human catches it by knowing the scenario is about the night shift; a machine cannot unless someone wrote that down. An expectation only ever disagrees — it is never a seventh resolution layer, it is never generated from a previous run, and a passing one changes nothing (§6.4.3) |
| **D12.27** | **A capture run never builds a world, and a drawn seed is written down before the first capture.** World build carries nondeterminism no seed covers ([`09`](09_Toolchain_And_Packaging.md) D9.9's `set`-ordered node emission, carried forward), the world is already a layer-3 binding, and a build is a long irreversible act whose half-written product a kill would leave behind — so `world_build` is refused in a capture run (check 38) and building stays where it is, attended, in `run_SCTMV.py --build`. `random` seeds stay available; the drawn value is written into the lock and the manifest's effective configuration **before the first capture** (check 39), so a run stopped a second later is still explicable. A caller that wants a repeatable draw supplies `caller_label` and gets one — the draw is then a deterministic function of the effective-configuration digest and that label — and the tool never interprets the label (§3.10.4) |
| **D12.28** | **Layer 2 is where host dependence is allowed to live, not where it is allowed to hide.** Every layer-2 field is materialised like any other and names the environment variable it resolved from; a secret or path that resolves empty is a refusal rather than a blank (*measured:* `--ion-token` defaults to `""` at `CarlaControlArgumentParser.py:105`); and under `caller: unattended` a field resolved from an environment variable the site profile does not declare is a refusal (checks 36, 37). *Measured:* five variables reach behaviour today — `CESIUM_ION_TOKEN`, `SUMO_HOME`, `CARLA_NETCONVERT`, `PROJ_LIB`, `PROJ_DATA` — and none is recorded in anything a run produces (§3.5, §3.10.1) |
| **D12.29** | **Pacing is expressed here and ruled elsewhere.** `pacing.mode` and `pacing.real_time_factor` are Session-fixed and recorded, `pacing.min_achieved_factor` has no tool default pending [`08`](08_Collection_And_EPoL.md)'s ruling, and **`pacing.on_consumer_slow` is not a field at all** — [`08`](08_Collection_And_EPoL.md) §11.3 and D8.23 rule it drop-oldest-and-count, so this surface expresses that and does not re-offer it. A toggle for a decision another section has taken is a way to contradict that section from a configuration file. Check 44 moves the live run's dominant failure to the pre-roll, using a measurement §11.1 already requires (§5.2, §7.4) |
| **D12.30** | **Two drop counters, displayed side by side and never summed.** The recorder's (`FrameRecorder.cs:46, :184-185`) counts a frame that never reached disk — a hole in the corpus, loud, and D10.7 fails the gate on it. The handover socket's counts a frame that reached disk but not the consumer — no hole, expected by design, and the coverage record already carries it as *covered but not delivered*. A single figure would be non-zero on a healthy live run and unable to distinguish the two (§7.4.2) |
| **D12.31** | **The capture monitor and the exercised operator's picture are different displays and cannot be merged.** §7.1 and §7.4.1 show sun elevation and the advancing flag because that is how §1.6's defect is made visible; [`08`](08_Collection_And_EPoL.md) D8.23 forbids `advancing` and `rate` — and truth — from any feed an exercised operator sees. One display cannot satisfy both rules. The live panel additionally shows the truth feed's own state, so its being off is a visible fact rather than one discovered afterwards (§7.4.1) |
| **D12.32** | **A transcript is stored and never interpreted, and it is not a third corpus root.** `transcript.root` is refused inside either corpus root (check 42) on exactly [`08`](08_Collection_And_EPoL.md) D8.38's precedent for a probe workspace: outside both roots, never released, never digested into a manifest, never cited by a corpus artifact. A source is `{source_id, listen, content_type}` and a record is an opaque blob with a timestamp — the brief's §3c wording, adopted verbatim because widening it is how a schema for somebody else's output gets designed by accident. **There are still two roots** (§7.4.3) |
| **D12.33** | **Clean termination under a deliberate kill is a property of the surface, not an error path.** One sequence — `RunTerminationSequence` — serves an operator stop, a signal and a fault, in a fixed order: stop starting work; let the tick in flight finish under the client's own frame-wait bound (`CarlaClient.cs:292-299`); unsubscribe the capture streams and drain the encode queues under their ten-second bound (`FrameRecorder.cs:243-249`); **append** the closing manifest record; write `RunResult`; and only then release the render set, the lease and synchronous mode, each best-effort against a server that may already be dead. A second signal abandons the rest. It never deletes or rewrites what is written, never stages artifacts for publication at the end, never writes an artifact whose readability depends on a closing token, never blocks the corpus flush on the server or the external chain, and never reports a kill as a fault. **This is the inverse of the order in the tree**, where `traffic.disable()` despawns the world at `run_SCTMV.py:317` before `recorder.stop()` at `:321-325`, and where `SIGTERM` is handled nowhere at all (§3.10.2) |
| **D12.34** | **A kill with no chance to flush is normal, and the record makes its cost visible.** What is on disk is what exists and the last complete record is the authority. At most `max(4, n × 2)` captures per channel are lost from the encode queue (`FrameRecorder.cs:115-121`) and `Dropped` counts none of them (`:184-185`), so the manifest carries **captured** and **written** per channel and their difference is the loss. A capture is two files written to their final paths in sequence (`:222-227`, `:228-230`), so atomic publication and a stated publication order are required from [`04`](04_Contracts.md), with the sidecar published first so that the only torn state is one a reader can detect and disregard (§3.10.2, §3.10.3 K2–K4) |
| **D12.35** | **A run has no length of ours.** There is no `--duration` and no `--frames`; a window may declare no end; and nothing in this surface depends on a run reaching an end. The single bound the tool imposes on itself is **write headroom**, expressed in captured seconds rather than bytes and re-evaluated while the run proceeds (check 46), because a disk that fills produces truncated files — the one outcome D12.33 forbids outright. A clean self-stop carries `closed_by: write_headroom`, its floor is an operator-settable field, and a caller that stops us first never sees it (§3.8, §5.2, §6.2 check 46) |
| **D12.36** | **What a caller can watch while a run proceeds is two surfaces that already exist, and one boundary that is not a gap.** Through CarlaNet and the Python shim, after an explicit `Client.start_observer()` (`carlanet/__init__.py:2233-2239`), three cache reads are free and cost the tick nothing — `get_sim_time` (`:2017`), `get_actor_ids` (`:2027`) and `get_solar_state` (`:1511`) — while `get_actors` (`:2038`) is a blocking RPC per call; [`10`](10_Scale_And_Performance.md) D10.10 is why the distinction matters, and an observer reads the push stream rather than polling. **The server holds no capture state**, so frames written, intervals closed, area covered and gate records are answerable only from the incrementally written artifacts — the same fields, from the same source, that D12.14 already binds the monitor to (§7.6) |
| **D12.37** | **A stare can aim at the rendered traffic instead of at coordinates, and the point it resolves to is recorded.** `stare_look_at_target: rendered_traffic` is a third stare form beside a look-at point and a pose — exactly one of the three — with the look-at form's altitude, standoff and bearing. The point is the mean position, height included, of the vehicles the session rendered on the last frame before its camera holds for the window, measured from the poses it wrote to bodies, because the middle of the world's staging bounds, where its camera starts, is not where a corridor scenario's traffic is. It is declared as a named target rather than a flag so that the look-at fields name what the boresight passes through in one place, and a second target is a new value rather than a new field. It is resolved one SUMO step and the picture's 120-tick ceiling before the window opens (seven one-second steps at the defaults) because the window is what it frames and the view the window holds has to be seen ready first: the camera follows the traffic through the prewarm until then and holds from there, so the view whose tiles and picture are waited on (D12.38) is the view the window holds; a camera followed to the last step would open the window on a view nobody had seen ready, because a camera that moves between its frames never reads settled. The run result records the point as look-at fields, so a run is reproducible from its record by an ordinary look-at stare, and a process with no session — a camera follower — refuses the form (§5.2) |
| **D12.38** | **No capture is written before its camera's view is ready, and a view not ready by the window's opening refuses the run at pre-roll; the window's first frame is never moved and the prewarm is never lengthened while a run is under way.** Readiness is [`03`](03_CoSimulation_Runtime.md) §9.5.1's two witnesses — the server's word that the tiles are in, the camera's own frames that the picture has settled — waited on inside the prewarm, asked once after every step the session renders and never between, from the point every capture camera holds the pose the window opens on: an orbit is held at its opening pose until the window opens, and a stare aimed at the rendered traffic holds for the last SUMO step and 120 ticks (D12.37). The picture is judged with every block a rendered vehicle covers, in either frame of a comparison, left out, because the witness asks whether the world's rendering has settled and a vehicle driving through the view answers a different question (measured on Bahonar: 56 rendered vehicles held the worst block at 1.8–2.0 grey levels where the same view with none settled); a comparison that leaves less than half the view to judge, or whose vehicles could not be placed, does not count. The prewarm is the lead — `capture.prewarm_s`, Session-fixed and recorded — and it is not lengthened at run time: SUMO has already been fast-forwarded to its first instant and cannot be taken back, and a window opened late is not the window whose sun was bound ([`03`](03_CoSimulation_Runtime.md) D3.21). So a witness past its ceiling — 90 s of wall clock for the tiles, 120 ticks for the picture — the renderer settles on the world's ticks, not on the frames a camera renders — neither a count a caller supplies — or a view not ready when the window opens is check 50, `refused_preroll`, naming the channel, the witness and where it stood, as check 44 refuses a live exercise that cannot hold its rate; a prewarm that could never hold the fewest frames the picture can be compared on is check 51, in phase 0. The run result records per channel how its view became ready. A capture's own readiness is not recorded, because the server answers only for the last tick and publishes nothing per frame, and an orbit's readiness as the window opens says nothing of the ground it sweeps afterwards (§6.3) |

---

## 13. Open questions

1. **What does a run list do when one member's gate records miss their thresholds?**
   [`10`](10_Scale_And_Performance.md) D10.3 puts 4–8 windows on a seven-day scenario and
   [`07`](07_Scenario_Authoring.md) D7.12 doubles that for counterfactual pairs, so a night's work is
   8–16 sessions. `run_capture --run-list` walks the sweep artifact
   [`07`](07_Scenario_Authoring.md) §7.2 already defines, one session per entry — that much is settled,
   because a caller that cannot answer a question cannot launch sixteen invocations by hand either, and
   §3.8 shows the form. What is unsettled is the continuation rule. An attended operator is usually
   better served by stopping — one wasted window instead of twelve — while an unattended list that stops
   on entry 1 of 16 produces almost nothing, and nobody is there to notice. Options: stop always;
   complete always; or `on_gate_miss: stop | continue`, with `stop` the attended default and `continue`
   the unattended one. **Recommend the third**, with `RunResult` carrying one entry per member so a
   partly-missed list is still a usable set with named holes — D12.16's reasoning applied to a list
   instead of a run. Note that the caller can stop the list at any instant regardless (§3.10.2), so this
   rule governs only what we do unprompted. Separately unsettled: whether a run list shares one `sumo`
   process across its windows, which is [`01`](01_Architecture.md) open question 5 and should be
   answered with it. **Needs the user** only if they would rather an unattended list fail fast than
   produce a partial set.

2. **Where does the site profile live, and who writes it?** It is the layer that makes a run
   configuration portable, and it is also the layer nobody will maintain. Options: a file beside the
   distribution, discovered by convention; environment variables, as `CARLA_NETCONVERT` and `PROJ_LIB`
   already are (`run_SCTMV.py:60-78`); or derived from the distribution's own layout with no file at
   all. **Recommend the third with the first as an override**, since a distribution already knows
   where its own `tools/sumo` is — but it interacts with [`09`](09_Toolchain_And_Packaging.md) D9.6's
   `SUMO_HOME` precedence and should not be decided without it. *Built as recommended, pending that decision:*
   `SiteProfile` derives every path from the layout `run_capture` runs from, a file named by
   `--site-profile` overrides it, and `sumo.home` falls back to `CARLANET_SUMO_HOME` and then the
   layout's staged SUMO — the session's own order — before leaving the session to search `SUMO_HOME`.

3. **Should `solar.vehicle_lights` default to `from_sumo` in a dark window rather than being a
   conditional requirement?** D12.10 refuses to choose and forces the operator to. The argument for
   choosing `from_sumo` instead is that brake and indicator state is plausibly the dominant detectable
   signature at night, so `off` produces a corpus that is wrong rather than merely conservative. The
   argument against is that nobody has captured a night window yet and the choice would be made from
   an argument rather than an image. **Recommend keeping the conditional requirement until one night
   window exists**, then revisiting with the imagery in hand. **Needs the user** only if they would
   rather not be asked each time.

4. **What is the full `closed_by` vocabulary?** §3.10.2 fixes the mechanism — every early end takes the
   same termination sequence and the manifest's closing record names the reason — and this section needs
   at least `window_end`, `scenario_end`, `operator_stop`, `signal:<name>`, `loud:<condition>`,
   `write_headroom` and `aborted_at_preroll`. A half-window is a corpus whose authored intervals were
   cut by something other than the scenario, which [`01`](01_Architecture.md) open question 4 also needs
   a value for. **Recommend that [`06`](06_Truth_And_Annotation.md) own the vocabulary** and name these
   alongside its other `closed_by` values rather than each section inventing its own; a refusal to close
   early without an acknowledgement is **not** an option here, because the caller may stop us without
   asking.

5. **Is there an operator-facing preview of what a window will look like before it costs a run?**
   §6's check 19 predicts corpus size, cheaply. A second prediction — the sun's elevation and
   azimuth across the window, and the fraction of it that is dark — is equally cheap once
   [`11`](11_Time_And_Illumination.md)'s N4 exists, and would let an operator choose a window by its
   light. **Recommend adding it to the
   resolution report** rather than building a separate tool; noting that it is exactly the information
   that would have prevented the defect in §1.6 from ever mattering.

6. **Should the effective configuration be signed, or merely digested?**
   [`07`](07_Scenario_Authoring.md) open question 3 raises the same question for the scenario's
   resolution report — that an author should record having read it. The run-level analogue is stronger,
   because a capture is expensive and the operator's acceptance of a warning is a judgement nobody
   else can reconstruct. **Recommend a field in the run lock recording which warnings were
   acknowledged and by whom**, populated from an explicit flag — but process that is not enforced is
   theatre, and whether this one would be enforced is a question about how the team works rather than
   about the tool.

   For an unattended caller this is settled by D12.25: `on_warning.<code>` records the adjudication *and
   the artifact that granted it*, and check 34 refuses a run with an unadjudicated warning, so the
   acknowledgement is neither optional nor ceremonial — it is a field without which the run does not
   start. **What remains open is the attended half**, where the acknowledgement is a keystroke at
   §6.4.2's block and the recorded actor is whatever identity the launcher can obtain. That is still a
   question about how the team works.
7. **Does the monitor belong in the same process as the capture session?** §7.1 assumes it does, which
   is simplest and guarantees it reads the same fields. §7.6 settles the remote case — anything watching
   from outside tails the manifest and reads the world observer's push stream, never polls — so the
   residual question is only whether the *operator's* panel is in-process. **Recommend in-process for
   the default single-process deployment ([`01`](01_Architecture.md) D1.12), and the same
   manifest-tailing path as the remote option**, because the manifest is already the single source, it
   costs the server nothing, and one reader implementation then serves the operator and the external
   caller alike.

8. **Should `caller: unattended` be permitted together with `pacing.mode: wall_clock`?** A live exercise
   has a human watching by definition ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c), which argues for
   refusing the combination outright. But [`02`](02_Use_Cases.md) UC-8's *integration test rather than
   demonstration* alternate flow is precisely an unattended live run — does the stage accept our frame,
   is the tick preserved, does latency stay inside the budget — and refusing it would delete a named use
   case to enforce a tidiness rule. **Recommend permitting it**, with two conditions: `handover.enabled`
   must be true (an unattended live run with nothing on the other end is a corpus run paced slowly for
   no reason), and §7.1's loud conditions terminate rather than merely display, which D12.21's unattended
   rule already requires. **Needs the user** only if they consider an unwatched live run a contradiction
   in terms.

9. **Should `expect` be mandatory under `caller: unattended`?** §6.4.3 argues that without a declared
   expectation a cadence has no guard at all against the one failure mode checks cannot reach — a
   configuration that resolves legally and not as intended. Mandating it would close that. Against:
   a mandatory field gets filled in mechanically, and a guard people satisfy without thinking is exactly
   what §3.8 refuses to build. **Recommend not mandatory, but visible**: the closeout and `RunResult`
   report the count of declared expectations, so `expectations declared: 0` is a line somebody reads
   rather than an absence nobody notices — and revisit once one unattended run has happened. The case
   for mandating it is stronger than it looks, because a run the caller will stop at an unknown instant
   cannot be cheaply inspected in its first minute and re-launched. *Built as recommended:* the run result carries
   `expectations_declared`, and nothing makes an expectation mandatory.
10. **What `pattern` is the interactive viewer's camera once §9.2 converts it?** run_SCTMV's one camera
   starts as a stare and becomes an orbit on the `O` key or under `--orbit`, with the orbit's centre
   defaulting to the start pose (`OrbitSensorController.py:235-242`). `ChannelDescription` holds one
   `pattern` per channel and refuses an orbit centre on a stare, so a description built from today's
   flags would be refused for exactly the camera D12.17 keeps. Either the interactive path builds
   an orbit description whenever orbiting is possible, or the description admits a channel whose
   pattern the operator switches. Nothing changes for run_SCTMV until §9.2's conversion is made.
