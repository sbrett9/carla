# SUMO traffic integration — driving ambient traffic with a real traffic simulator

**Date:** 2026-08-21 · **Status:** scouting complete. No tracked file changed — the only side effect is
three extra binaries in the gitignored `Build/` tree (§1.2). Plan in §8.
**Question asked:** ambient traffic is not good enough to be the background an OpenSCENARIO storyboard
runs against — vehicles under-populate the freeway on Arapahoe, left-turners do not yield to oncoming
traffic on a permissive green, and speed does not respond to terrain. SUMO source is already pulled
for `netconvert`; what would it take to build the rest of the toolchain and connect it to a running
server with a level loaded, and what stands in the way?
**Answer in one line:** the toolchain was one `cmake --build` away — `sumo` and `duarouter` are now built
and verified (§1.2), and **TraCI is a wire protocol over TCP** that .NET can speak directly (§1.1), so no
Python need enter the tick path — but the integration upstream CARLA ships would *regress* capabilities
this fork depends on, so the recommended shape is SUMO as the **decision layer** with CARLA physics kept
as the **execution layer**, and the real work is four data problems, not the bridge.

**Relates to:** [10_Intersection_Navigation_Traffic_Control.md](10_Intersection_Navigation_Traffic_Control.md)
(the three-layer gap this supersedes for ambient behaviour),
[07_RoadNetwork_Filtering.md](07_RoadNetwork_Filtering.md) (the netconvert flag set and the type map),
[18_Scenario_Fabrication_For_EPoL_Training.md](18_Scenario_Fabrication_For_EPoL_Training.md)
(what the ambient traffic is a background *for*),
[04_DynamicWorld_DataPipeline.md](04_DynamicWorld_DataPipeline.md) §3 (netconvert and elevation),
[21_Road_Elevation_Profile_Continuity.md](21_Road_Elevation_Profile_Continuity.md) (the elevation
profile this proposes to reuse for grade).
**Issues subsumed or unblocked:** [#27](https://github.com/sbrett9/carla/issues/27) (vehicles stop dead
inside junctions), [#19](https://github.com/sbrett9/carla/issues/19) (routed vehicles run reds),
[#7](https://github.com/sbrett9/carla/issues/7) (umbrella), [#12](https://github.com/sbrett9/carla/issues/12)
(clip discards turn restrictions — **prerequisite**, see §6.6),
[#18](https://github.com/sbrett9/carla/issues/18) (two owners destroy a vehicle — becomes three).
**Measurement basis:** `carla/Build/sumo-src` at the pinned commit; `Import/Arapahoe_I25.osm` run
through the exact flag set `OsmConverter.BuildArguments` emits, 2026-08-21.

**Change history**

| Date | Change |
|---|---|
| 2026-08-21 | Initial scouting: toolchain, discarded network, recommended shape, hurdles, plan. |
| 2026-09-21 | §6.3: the TraCI client is a C# socket client; SWIG binding costs measured (§6.3). |

---

## 1. What is already on disk

Everything below was verified on this machine, not inferred from the setup script.

| Thing | Where | State |
|---|---|---|
| SUMO source, **complete** | `Build/sumo-src` | v1.27.0, pinned `e238ea04b7` — `src/microsim`, `src/traci-server`, `src/libsumo`, `src/libtraci`, `src/duarouter`, … all present |
| Windows dependency bundle | `Build/SUMOLibraries` | pinned tag `1.27.0` — Xerces, PROJ, FOX, Boost, GDAL, and `swigwin-4.3.1` |
| CMake build tree | `Build/sumo-build` | **already configured for the whole project** — `sumo.vcxproj`, `duarouter.vcxproj`, `libtracics.vcxproj`, `libsumocs.vcxproj` all generated |
| Staged binaries | `Build/sumo-install/bin` | **`netconvert.exe` only**, plus its DLLs and PROJ data — `sumo`/`duarouter`/`libtracics` now exist in `Build/sumo-src/bin` but are not staged (§1.2) |
| Python tools | `Build/sumo-src/tools` | `traci/`, `sumolib/`, `randomTrips.py`, `routeSampler.py`, `tls/` — pure Python, nothing to build |
| Type maps / data | `Build/sumo-src/data` | present but **unstaged**; `SUMO_HOME` is set nowhere, so netconvert falls back to its compiled-in OSM type map (already noted in [doc 07 §1.3](07_RoadNetwork_Filtering.md)) |
| Distribution slot | `Scripts/Windows/MakeDistribution.ps1:237` | already bundles `tools\sumo\` |

`CarlaSetup.ps1:677` configures the entire SUMO project and then builds exactly one target:

```powershell
cmake --build $sumoBuild --target netconvert --config Release -- -m
```

**The rest of the toolchain is a target-list edit.** Nothing about the pin, the dependency bundle, or
the CMake configure has to change.

### 1.1 The finding that shapes everything else

**TraCI is a wire protocol, and every TraCI client is a socket client.** `sumo --remote-port N` is a
server; a client connects over TCP and exchanges length-prefixed command and response frames. SUMO
ships its own reference client for that protocol, in Python, under `tools/traci` — and the transport
half of it is small:

| File | Lines | What it is |
|---|---|---|
| `tools/traci/connection.py` | 407 | the socket, the frame header, command packing, response dispatch. `socket.socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)` at `:78`; the 4-byte big-endian length prefix at `:105-127`; the type-tagged value packer at `:154-207` |
| `tools/traci/storage.py` | 103 | reading typed values back out of a response frame. `import struct` and nothing else |
| `tools/traci/domain.py` | 283 | the shared getter / setter / subscription machinery every domain sits on |
| `tools/traci/_vehicle.py` | 1 633 | the vehicle domain. Each call is one line over `domain.py` — `moveToXY` is `self._setCmd(tc.MOVE_TO_XY, …)` (`:1499`), `getPosition` is `self._getUniversal(tc.VAR_POSITION, vehID)` |
| `tools/traci/_simulation.py` | 685 | the simulation domain — step, time, the departure and arrival lists |

There is no native module anywhere in it. **The consequence is that any language with a TCP socket can
drive SUMO**, so CarlaNet can do it in .NET, off `CarlaClient.OnTick`, with **no Python anywhere in the
tick path** — which satisfies the architecture constraint recorded for
[CarlaNet.Scenario](18_Scenario_Fabrication_For_EPoL_Training.md) rather than fighting it, and is the
single biggest reason this integration is cheaper than it looks. **Which** C# client — a port of the
reference client, or a binding to SUMO's own C++ one — is §6.3.

SUMO's build also generates C# bindings, at `src/libsumo/CMakeLists.txt:147` and
`src/libtraci/CMakeLists.txt:109`:

```cmake
SWIG_ADD_LIBRARY(libsumocs  LANGUAGE CSharp SOURCES libsumo.i)   # namespace Eclipse.Sumo.Libsumo
SWIG_ADD_LIBRARY(libtracics LANGUAGE CSharp SOURCES libtraci.i)  # namespace Eclipse.Sumo.Libtraci
```

`ENABLE_CS_BINDINGS` defaults **ON**, SWIG is already in the pinned bundle, and both projects are
**already generated in our build tree**.

`libsumo` and `libtraci` expose the **same API** over **different mechanisms**: `libsumo` runs SUMO
in-process, `libtraci` is a C++ socket client to a separate `sumo` process. That is measurable rather
than documentary — `libtracics.dll`'s import table is the MSVC runtime, `kernel32` and **`ws2_32`**
(Winsock), with nothing of SUMO's own in it. So `libtracics` is a wrapper around the same protocol
`tools/traci` speaks, not an alternative to it.

### 1.2 Verified, not predicted (2026-08-21)

The claim in §1 was tested rather than left as an inference. Against the **existing, unmodified**
CMake configuration:

```powershell
$env:SUMO_LIBRARIES = 'carla\Build\SUMOLibraries'
cmake --build carla\Build\sumo-build --target sumo libtracics duarouter --config Release -- -m
```

Exit 0, no errors, ~13 minutes. Produced in `Build/sumo-src/bin`:

| Artifact | Result |
|---|---|
| `sumo.exe` (6.9 MB) | `Eclipse SUMO sumo 1.27.0`, build features `… Proj FMT Intl SWIG Parquet Eigen` |
| `duarouter.exe` (2.6 MB) | `Eclipse SUMO duarouter 1.27.0` |
| `libtracics.dll` (2.1 MB) + `libtracics-sources.zip` | the native SWIG C# binding |
| `Build/sumo-build/src/libtraci/Eclipse.Sumo.Libtraci/` | **94 generated C# files**, including `Vehicle.cs`, `Simulation.cs`, `TrafficLight.cs`, `Lane.cs`, `libtraciPINVOKE.cs` |

`libtracics` is in that invocation because §6.3 weighs it; the toolchain the plan requires is the three
binaries — `netconvert`, `sumo`, `duarouter`.

The two calls §4.1 and §6.11 depend on are present on both clients: `moveToXY` and
`subscribe` / `getAllSubscriptionResults` in the generated C# API, and the same two at
`tools/traci/_vehicle.py:1485` and `tools/traci/domain.py:188,223` in the reference client.

So **Phase 0's build step carries no remaining technical risk**; what is left of Phase 0 is staging and
`SUMO_HOME`. Note the binaries land in the *source* tree's `bin/` (SUMO's convention, already handled
for `netconvert` at `CarlaSetup.ps1:684`), not in the CMake build dir.

---

## 2. What our own pipeline already computes and then throws away

`OsmConverter.ConvertFileWithNetworkAsync` already asks netconvert for the SUMO network
(`--output-file`) so `TrafficLightInjector` can read the `<tlLogic>` phase programs — and then
**deletes it** (`OsmConverter.cs:135`, `finally { … TryDelete(netPath); }`).

Running our exact flag set over `Import/Arapahoe_I25.osm` and measuring what is in that discarded file:

| Measured on the Arapahoe network | Value |
|---|---|
| `netOffset` | `0.00,0.00` — origin pinning + `--offset.disable-normalization` means **SUMO (x, y) ≡ CARLA (x, −y)**, with no offset arithmetic |
| Normal edges / internal edges | 2 420 / 5 819 |
| Junctions | 1 431 — **437 `priority`, 372 `right_before_left`, 45 `traffic_light`**, 212 `dead_end`, 365 internal |
| **`<request>` right-of-way rows** | **5 855** |
| `tlLogic` programs | 45 |
| Distinct `z` values in any lane shape | **0** (the network is flat) |
| Edge type histogram | 1 464 `highway.service`, 368 `footway`, 200 `residential`, 131 `secondary`, 27 `motorway_link`, **11 `motorway`** |
| Motorway edges beginning at a fringe (`dead_end`) junction | **2**, six lanes each, 29.06 m/s |
| Turn-restriction relations netconvert saw | 8 `Ignoring restriction relation` warnings (on the *unclipped* OSM — see §6.6) |

The third row is the important one. **A complete, compiled right-of-way table for every junction on
the map — 5 855 rows of who yields to whom — is produced by our own pipeline on every world build and
discarded.** That table is precisely what the .NET traffic manager does not have and what
[issue #27](https://github.com/sbrett9/carla/issues/27) is about.

---

## 3. Mapping the three complaints to causes

### 3.1 Freeway under-population — a spawn-model problem, not a driver-model problem

The staging controller draws spawn sites from the map's recommended spawn points filtered to the
inward edge ring (`SCTMV.py:760`–`779`). CARLA generates those points in
`AOpenDriveGenerator::GenerateSpawnPoints` (`OpenDriveGenerator.cpp:161`) from
`GenerateWaypointsOnRoadEntries()` — **one point per drivable lane at each road entry, and nowhere
else**. On Arapahoe the fringe has 212 dead-end junctions' worth of entries and exactly **two** of them
are I-25. A uniform draw therefore puts ~1 % of spawns on the freeway, and those vehicles clear a
~3 km sandbox at 29 m/s in under two minutes. On top of that, `spawn_actor` *fails* when the point is
occupied — there is no queue, so a busy site silently yields nothing.

SUMO's insertion model is the direct answer, and it is not an approximation of one: `<flow>`
definitions carry a per-edge rate, `departLane="best"`/`"free"` picks a lane with a gap,
`departSpeed="desired"` inserts at traffic speed instead of from rest, and a vehicle that cannot be
inserted **waits in a queue and retries** rather than being dropped. `tools/randomTrips.py` even has
`--fringe-factor`, which biases trip origins and destinations toward the network fringe — the staging
ring concept, already implemented, with rates you set per road class.

### 3.2 Left turns that do not yield — the data exists, nothing consumes it

`test_left_turn_yield.py` already states the mechanism exactly: *"Nothing in the traffic manager
encodes right of way for a turn across oncoming traffic. Two vehicles whose paths cross inside a
junction resolve purely on the geometry of their swept paths, and that decision can reverse as they
move."* That is [issue #27](https://github.com/sbrett9/carla/issues/27).

SUMO compiles right-of-way into the network at conversion time — the 5 855 `<request>` rows of §2, one
per connection, with a `response` and `foes` bitmask — and the runtime enforces it: a minor-road or
permissive-left movement blocks until it has a gap of at least `jmTimegapMinor`, and internal-junction
blocking stops a vehicle entering a junction it cannot clear. **This is the largest behavioural win
available and it requires no new modelling from us** — only that we stop discarding the network.

### 3.3 Speed variation with terrain — SUMO does **not** solve this out of the box

Stating this plainly because it is the one complaint SUMO does not answer: the Krauss/IDM/EIDM
car-following models do not read road grade, and in any case **our network is flat** (§2). Nothing in
stock SUMO slows a car for an undulation or a crest.

What SUMO *does* give is the machinery to express it cheaply, because it decelerates smoothly and
model-consistently into a slower lane:

- **Bake grade and curvature into per-lane speed.** We already compute the full elevation profile
  (`ElevationInjector.ExtractCenterlineSamples` + the C¹ fit from
  [doc 21](21_Road_Elevation_Profile_Continuity.md)) and we have the plan geometry. A pass that turns
  (grade, curvature) into a lane `speed` — written into the network, or set at runtime with
  `lane.setMaxSpeed` — makes cars brake for the crest and the sharp drop on Hormuz *through the
  car-following model*, which is what makes it look right rather than scripted.
- **Driver-to-driver variation for free.** vType `speedFactor` with a distribution
  (`--default.speeddev`) means not every car takes the same line at the same speed — an EO-realism win
  that today's uniform TM does not produce.

This is additional work, but it lands *inside* the SUMO framing rather than beside it, and it reuses
data we already produce.

---

## 4. Why upstream's co-simulation is the wrong shape for this fork

Upstream CARLA's `Co-Simulation/Sumo` (absent from our tree — we have no `Co-Simulation/` directory)
works by **teleporting**: SUMO owns every ambient pose, CARLA actors get `set_simulate_physics(False)`
and a `set_transform` every tick. Adopting that shape here would cost capabilities this fork has
already built and validated:

| Capability | What teleporting does to it |
|---|---|
| **Truth telemetry velocity** | `WorldObserver.cpp:373` serializes `View->GetActor()->GetVelocity()`. For a non-simulating body a teleport does not update component velocity, so **every ambient vehicle reports speed 0** — into CoT truth telemetry, the TM collision stage, and the occlusion/arrival gating of [doc 17](17_Photoreal_Occlusion_Metric.md) |
| **Seating on the draped terrain** | The SUMO network is flat (§2); poses would arrive with no usable Z, discarding the whole drape/DTM decoupling result |
| **Suspension, pitch, wheel rotation** | Gone — and pitch over undulations is exactly the EO cue §3.3 is trying to add |
| **Vehicle fade / staging ring** | Built around a client-side registry keyed to vehicles the staging controller owns |

That collides with the standing rule that an existing capability is never lost. Teleporting is still
worth *having* as a comparison mode (§8, Phase 4) — it is the reference implementation and the honest
oracle for "is our control loop tracking SUMO?" — but it should not be the production path.

### 4.1 Recommended shape: SUMO decides, CARLA physics executes

Per world tick, in .NET, off `CarlaClient.OnTick`:

1. **Push reality into SUMO** — for each bridged vehicle, `vehicle.moveToXY(edge, lane, x, −y, angle, keepRoute=1)` plus its measured speed. SUMO's ghost is corrected to where the car physically *is*.
2. **`simulationStep()`** — SUMO applies car-following, lane-changing, junction right-of-way, and insertion to that corrected state.
3. **Read back the decision** — target speed, target lane, next edge (batched through a TraCI *subscription*, so this is a constant number of calls per step, not per vehicle).
4. **Actuate with our own controller** — feed the target through `CarlaNet.TrafficManager/Stages/PIDController.cs` and `MotionPlanStage` and apply a `VehicleControl`. Physics, wheels, terrain seating and real velocity all survive.

Two consequences worth naming:

- The loop is **tick-driven by construction**, so it sidesteps the recorded gap that the .NET TM
  produces no motion under synchronous world ticking — the SUMO bridge advances *because* the world
  ticked, which is exactly what that gap is missing.
- The existing .NET TM is **not replaced**. It stays the path for maps with no SUMO network and for
  everything already validated on stock content. SUMO becomes a mode.

---

## 5. Where the seam goes

```
                    build time (once per world)                 run time (per tick)
  .osm ──netconvert──┬──► .xodr ──elevation/sign/TL injection──► CARLA world
                     │
                     └──► .net.xml ──(persist; patch z + lane speeds)──► sumo ──TraCI/TCP──►
                                                                              CarlaNet.CoSim
                                                                                    │
                                     CarlaClient.OnTick ─────────────────────────────┘
                                                                                    │
                                                                   VehicleControl ──► CARLA actors
```

New .NET project `CarlaNet.CoSim`, modelled on `CarlaNet.Scenario` (which already proves the pattern:
subscribe to `CarlaClient.OnTick` at `ScenarioExecutor.cs:78`, advance, unsubscribe at `:561`).
Python's role stays what it is for scenarios — a toggle, never a per-tick participant.

---

## 6. Hurdles

Ordered by when they bite, not by size. "Cost" is relative effort, not a schedule.

### 6.1 Build the rest of the toolchain — *low*
Add `sumo` and `duarouter` (and optionally `jtrrouter`, `polyconvert`) to the target list in
`CarlaSetup.ps1` and `CarlaSetup.sh`, and stage them into `Build/sumo-install/bin`. The idempotence
guard currently keys on `netconvert.exe` existing; it must key on the *newest* required binary or a
returning developer silently keeps a half-toolchain. The build needs nothing beyond what `netconvert`
already needs — Xerces-C and PROJ — on either platform.

### 6.2 `SUMO_HOME` and the data directory — *low, but a real trap*
`sumo` needs `data/` (type maps, XSDs) and the Python tools need `tools/` on `PYTHONPATH`. We set
`SUMO_HOME` nowhere today, which is already why netconvert silently uses its compiled-in type map
([doc 07 §1.3](07_RoadNetwork_Filtering.md)). Stage `data/` and `tools/` beside the binaries and set
`SUMO_HOME` in the same place `CARLA_NETCONVERT`/`PROJ_LIB` are set (`SCTMV.py:102`–`106`,
`MakeDistribution.ps1:287`). Fixing this also lets us stop relying on the compiled-in type map.

### 6.3 The TraCI client — *low* (given §1.1)

**`CarlaNet.Sumo` speaks the TraCI wire protocol directly, over a managed TCP socket, to an
out-of-process `sumo` started with `--remote-port`.** It is a port of SUMO's own reference client
(§1.1), written in C#, so nothing native is loaded and nothing is generated.

**Out of process, and now structurally so.** A SUMO assertion cannot take the CarlaNet client down and
`sumo` can be restarted without restarting the world — the properties `libsumo` (in-process) would give
up. `libsumo` is not reachable from a socket client at all, so moving in-process later would mean a
different mechanism rather than a swap — worth knowing, because a swap is what it would have been
through SUMO's own bindings, where the two share an API.

**What the port costs, measured.** `connection.py` (407 lines) and `storage.py` (103) are the whole
transport. `domain.py` (283) is the shared getter/setter/subscription machinery, and a domain call on
top of it is one line — `moveToXY` is `_setCmd(tc.MOVE_TO_XY, …)` (`_vehicle.py:1499`), `getPosition` is
`_getUniversal(tc.VAR_POSITION, …)`. Only the calls §4.1 and §6.11 name have to be ported, not the
1,633-line vehicle domain; §6.11's subscription path reaches `domain.py:188,223` and nothing deeper.
The exchange is that the wire format becomes code in this repository — readable, steppable and
version-checkable — and this repository owns it.

**What the generated C# binding costs, also measured.** SUMO's build produces one
(`libtracics` + `Eclipse.Sumo.Libtraci`, §1.1), and it was built and driven as far as a working
subscription reader against the Arapahoe network before the client shape was settled, so these are
readings rather than predictions (branch `feature/carlanet-sumo`, 2026-09-21):

| | |
|---|---|
| **No speed to buy.** 10.43 ms per step through C# at 388 live vehicles, against **8.10 ms** for the same seven subscribed variables through Python, 1.77 ms of the C# figure spent marshalling values out of SWIG's containers | `libtraci` is itself a socket client (§1.1's `ws2_32` import), so there is no in-process saving to have. Any client pays SUMO's step and the same round trip |
| **A version mismatch is undefined behaviour, not a divergence.** 1.27.1's `libtracics` against proxies generated from 1.27.0 loads, and the simulation steps | Entry-point names and struct layouts belong to one build. The protocol answers `CMD_GETVERSION` (0x00) with an API version and SUMO's version string, and the reference client asks on every connect (`connection.py:381-388`, called from `main.py:119`); a P/Invoke surface has nothing to ask |
| **Subscription results carry no downcast.** SUMO's `SWIGJAVA_CAST` helpers sit inside `#ifdef SWIGJAVA`, so `(TraCIDouble)result` throws for every variable | The type-safe alternative, `getString()`, formats through `std::ostringstream` at six significant digits — for a projected coordinate, metres |
| **Ownership varies by call site**, and SUMO's own exceptions arrive wrapped | `getAllSubscriptionResults` returns an owned map; indexing it returns a borrowed view that dies with the map; indexing one vehicle's results returns an owned copy. `FatalTraCIError` is a data class, and what is actually thrown is `ApplicationException` or `ArgumentOutOfRangeException` |
| **35,767 lines**, 35,690 of them generated across 93 proxy files, plus a native library to resolve at runtime | Regenerable rather than maintained, but reviewed, committed and shipped all the same |

**Python is out of the per-tick path either way, and that is a constraint on the language the bridge is
written in rather than on the transport.** The reference client's own correctness is not in question
here: `CarlaControl/src/carlacontrol/SumoCotBridge.py:267-365` drives SUMO through it in production
today.

### 6.4 Persisting the network — *low*
`OsmConverter.ConvertFileWithNetworkAsync` deletes the `.net.xml` (`OsmConverter.cs:135`). It must be
written next to the `.xodr` and kept, and the pair must always come from the **same netconvert run** —
which today it already does. This matters more than it sounds: upstream's `netconvert_carla.py`
re-derives a network from the `.xodr`, and every geometry or ID mismatch it introduces becomes a
co-simulation registration failure. We get an exactly-corresponding pair for free.

### 6.5 The network is flat — *medium*
Zero `z` in any lane shape (§2). Two distinct consequences:
- **Grade-aware speed (§3.3)** needs z, or at least a per-lane derived speed. The cleaner fix is to
  patch lane shape `z` from the same centreline samples the elevation injection already consumes — SUMO
  reads 3D shapes fine.
- **Teleport mode** would additionally need Z per pose. In the recommended shape this mostly
  disappears (CARLA physics owns Z), and where it is still needed `CarlaClient.SampleDrapeGroundElevation`
  (`CarlaClient.cs:204`) already resolves ground height in .NET with no RPC.

Note `--osm.elevation` is not a way out: [doc 04 §3](04_DynamicWorld_DataPipeline.md) already
established that OSM elevation is too sparse to matter.

### 6.6 Turn restrictions are discarded before netconvert sees them — *prerequisite*
[Issue #12](https://github.com/sbrett9/carla/issues/12): `osm_clip.py` drops all OSM *relations*. On
the raw Arapahoe extract netconvert reported 8 restriction relations; after clipping there are none.
Today that costs little because nothing enforces turn legality. Under SUMO it costs a great deal —
SUMO will happily route traffic through banned turns and the resulting behaviour will look *worse*
than today's, in a way that is easy to misattribute to the bridge. **Fix #12 before or with Phase 2.**

### 6.7 Pose conventions — *low, but silently wrong if skipped*
Three conversions, each a known source of a quiet 2–4 m error: CARLA's Y is negated relative to SUMO's;
CARLA yaw is `sumoAngle − 90` (SUMO measures clockwise from north); and **SUMO's reference point is the
front bumper centre while CARLA's is the body centre**, so every pose needs a half-length shift along
the heading. Upstream's `BridgeHelper` is the reference for all three.

### 6.8 Lifecycle ownership — *medium*
[Issue #18](https://github.com/sbrett9/carla/issues/18) already records two subsystems independently
destroying a vehicle with different signals. SUMO's arrival/teleport/collision-removal makes a third.
One owner must be named before this ships, and the staging controller's fade-in/fade-out and
`RED_CLEAR` despawn have to be reconciled with SUMO's fringe insertion and arrival — otherwise we
re-create the clipped-edge spawn bug in a new place. Also needed: a **blueprint ↔ vType map** with
matching dimensions (upstream ships `vtypes.json`; our blueprint set differs, so ours is new work) —
if a vType's length disagrees with the blueprint's bounding box, SUMO's gaps and our rendering
disagree by that difference everywhere.

### 6.9 Scenario interaction — *medium*
For a storyboard to run *inside* believable traffic, ambient traffic must react to the scenario actors.
That means mirroring every `CarlaNet.Scenario` actor into SUMO (`vehicle.add` + `moveToXY(keepRoute=2)`
for off-network manoeuvres) so SUMO's car-following yields to them, while SUMO never issues control for
them. The ownership handshake — what happens when a storyboard seizes a vehicle the bridge was driving,
and hands it back — needs to be designed, not discovered.

### 6.10 Tick rate and ownership — *low/medium*
SUMO's default step is 1 s (0.1 s in co-simulation practice); the world runs at `fixed_delta_seconds`.
Either set `--step-length` equal to the world delta (supported, and what upstream does) or define an
integer ratio and hold SUMO's decision across sub-steps. One owner of the tick, stated explicitly.

### 6.11 Throughput — *medium, and already contended*
Per-tick work scales with vehicle count. TraCI **subscriptions** (`subscribe`/`getAllSubscriptionResults`)
turn per-vehicle reads into a constant number of calls per step and are non-optional at scale. Note
[issue #14](https://github.com/sbrett9/carla/issues/14) already warns the tick thread is contended by
telemetry emission — the bridge lands in the same budget.

**A subscription is charged inside `simulationStep`, whether or not the client ever reads it.** Measured
at 388 live vehicles on Arapahoe: the step costs **3.73 ms** with nothing subscribed and **9.26 ms** with
the seven-variable set subscribed and never read. SUMO fills the results as part of advancing; the read
is what it costs to collect them afterwards. So **what is subscribed is a cost in its own right** — the
set of vehicles the bridge subscribes to has to be governed, not just the set it renders — and this is a
property of SUMO, true of any client.

### 6.12 Packaging and CI — *low*
`MakeDistribution.ps1` already creates `tools\sumo\`; extend it to `sumo`, `duarouter`, `data/` and
`tools/`, and set `SUMO_HOME` in the generated launcher. Linux equivalent in the `.sh` path. SUMO is
EPL-2.0; we already redistribute `netconvert`, and keeping SUMO out of process behind the TraCI socket
keeps the boundary exactly where it is today — the recipient gets the same compiled binaries and no
SUMO source.

---

## 7. What this does *not* fix

Named so they are not quietly assumed away:

- **Terrain-responsive speed** is our work, not SUMO's (§3.3).
- **Pedestrians.** SUMO has a person model; CARLA has walkers and `CarlaNet.Nav`. Out of scope for a
  first cut, and a second bridge when it comes.
- **Vehicle dynamics.** SUMO is a point-mass mesoscopic-to-microscopic model. It decides *what* a
  driver does; it says nothing about how the body rolls doing it. That stays CARLA's job — which is
  precisely the argument for §4.1.
- **Determinism** is preserved only if both halves are deterministic: fixed SUMO seed *and* the world
  in synchronous mode.

---

## 8. Plan

Each phase ends in something observable. Nothing after Phase 1 is worth starting if Phase 1's
measurement does not show the headroom it predicts.

**Phase 0 — finish the toolchain (§6.1, §6.2, §6.3).**
The build itself is **already proven** (§1.2): `sumo` and `duarouter` compile clean from the unmodified
configuration. What remains is to make it reproducible and shipped — add the targets to
`CarlaSetup.ps1`/`.sh`, re-key the idempotence guard off the newest required binary rather than
`netconvert.exe`, stage the binaries plus `data/` and `tools/`, set `SUMO_HOME` where
`CARLA_NETCONVERT`/`PROJ_LIB` are set today, and extend `MakeDistribution`. *Done when* `sumo --version`
runs from the **staged install** (not the build tree) and a C# test steps an empty simulation over
TraCI, against a `sumo` it started itself.

**Phase 1 — measure the ceiling offline, before touching the engine.**
Persist the Arapahoe `.net.xml` (§6.4), generate fringe-weighted demand with
`randomTrips.py --fringe-factor`, run `sumo` headless, and report per-edge throughput on I-25 against
what the staging ring produces today. This is the cheap, controlled probe that answers "would SUMO
actually put cars on the freeway?" without any integration risk — and it is the right order given how
often a systemic explanation offered ahead of a measurement has been wrong on this project.
*Done when* we have both numbers side by side.

**Phase 2 — the bridge, read-only.**
New `CarlaNet.CoSim` on `CarlaClient.OnTick`: launch/attach `sumo`, register existing CARLA vehicles
into SUMO, push their real poses each tick, step, and **log** SUMO's decisions without applying them.
Fix [#12](https://github.com/sbrett9/carla/issues/12) here. *Done when* SUMO's ghost tracks the real
vehicles within a stated tolerance for a full staging run, and its logged decisions at
`test_left_turn_yield.py`'s junction 117 show the left-turner being told to wait.

**Phase 3 — close the loop.**
Apply SUMO's target speed/lane through the existing PID and motion-plan stages; hand insertion and
removal to SUMO with the staging fade reconciled (§6.8). *Done when* `test_left_turn_yield.py` passes
(the left-turner waits outside and goes after the through-vehicle clears), the freeway populates, and
a stock-content regression run is unaffected — Town10HD has no `.net.xml`, so the bridge must no-op
cleanly there, and a co-simulable Town network can be produced with
`netconvert --opendrive-files` (confirmed available) when we want one.

**Phase 4 — terrain-responsive speed, and the teleport oracle.**
Patch lane `z` and derive per-lane speeds from grade and curvature (§3.3, §6.5); add `speedFactor`
spread. Add teleport mode behind a flag purely as the comparison oracle (§4). *Done when* vehicles
visibly brake for the Hormuz drop and the speed histogram across a run is no longer a spike.

**Phase 5 — scenario coupling (§6.9).** Mirror `CarlaNet.Scenario` actors into SUMO; define the
ownership handshake. *Done when* a `.xosc` storyboard runs with ambient traffic reacting to the ego.

---

## 9. Open questions for the next session

1. **Where does demand come from long-term?** Random fringe-weighted trips are a good default, but
   `routeSampler.py` can match measured counts — real AADT per road class would make density itself a
   modelled quantity rather than a knob, which matters for EO realism.
2. **How far can the control loop lag SUMO before its guarantees stop meaning anything?** The
   `moveToXY` feedback push keeps SUMO honest about where cars *are*, but a car that cannot make the
   commanded gap is a car SUMO thinks is safe and physics does not. Phase 2's tracking tolerance is
   the number that decides this, and it should be measured, not assumed.
3. **How much of the reference client does `CarlaNet.Sumo` carry, and how does it grow?** §6.3 ports the
   calls §4.1 and §6.11 name. A later section wanting a domain that is not there — `trafficlight`,
   `lane`, `person` — should find the transport already able to carry it and only a thin domain call
   missing; whether that holds is worth confirming against the second domain added rather than assumed
   from the first.
