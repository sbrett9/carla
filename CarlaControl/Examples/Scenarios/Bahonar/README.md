# Shahid Bahonar Port pattern of life

`Shahid_Bahonar_Port_PatternOfLife` is a week of traffic at Shahid Bahonar Port, in Bandar Abbas, Iran.\
SUMO drives the traffic and CARLA draws every vehicle SUMO moves.\
The week lays down a daily routine around the port.\
Six planted events stand out against it.\
The world is `Shahid_Bahonar_Port`.

## The scenario

Simulated second zero, t = 0, is 07:00 local time, UTC+03:30, on 29 September 2026.\
That is the first morning shift change.\
The run ends at t = 604800 s, 07:00 on 6 October 2026.\
The calendar moves on at each midnight.\
SUMO steps every 1.0 s, with seed 42.\
The same specification, seed and world give the same traffic.

### The place

The port holds a naval base, with an airfield inside a guarded fence.\
It also has a ferry terminal and a ship repair drydock.\
A public coastal corridor runs past them.\
The airfield perimeter, the apron and the drydock approach are mapped as service roads.\
SUMO opens a service road only to delivery vans, pedestrians and bicycles.\
This world's type map also opens them to the `army` and `authority` classes.\
So naval traffic and port-cleared traffic drive inside the wire.\
Civilian traffic cannot.

### The routine

| Pattern | When | Who |
|---|---|---|
| Coastal corridor | All week, in three streams: west to east, west to the northern exit and east to the northern exit. The rate follows the time of day, as the next table shows. | Civilian cars, pickups, taxis, trucks and buses |
| Ferry sailings | Every 2 hours from 06:00 to 18:00. On the first day the first sailing is at 08:00. A pulse drives in to the ferry for 12 minutes at 600 vehicles per hour. A pulse drives out 30 minutes later, for 12 minutes at the same rate. | Port-cleared cars and freight |
| Shift changes | At 07:00, 15:00 and 23:00. Traffic drives in from the west end of the corridor to the apron for 20 minutes at 240 vehicles per hour. From 15 minutes past, the same rate drives out from the apron to the northern exit for 20 minutes. | Naval jeeps and trucks |
| Guard postings | At 07:00, 15:00 and 23:00. Sixteen guards drive from the apron, one to each tower. Each parks at its tower for 8 hours. | One naval jeep for each tower |
| Freight hauls | At 09:00, 13:00 and 17:00. One truck hauls air freight from the apron to the port. | Naval trucks |

Each corridor stream carries this many vehicles per hour:

| From | To | Vehicles per hour |
|---|---|---|
| 00:00 | 06:00 | 20 |
| 06:00 | 10:00 | 180 |
| 10:00 | 16:00 | 120 |
| 16:00 | 20:00 | 200 |
| 20:00 | midnight | 50 |

The guard schedule sends 335 guards: 16 towers, 3 shifts a day, 7 days, less the one posting it skips.\
The hauls number 21.\
There are 49 ferry pulses in and 49 out.

Before the compiler writes anything, it runs the scenario in SUMO alone.\
In that run, 69246 vehicles enter over the 604800 s and none is dropped.\
All 366 vehicles the supervision plan names get in.

### The six planted events

| Event | Vehicles | Departs | What happens | Label |
|---|---|---|---|---|
| Ferry stay-behind | `staybehind`, a port-cleared car | t = 90000 s, 30 September 08:00 | It arrives from the east end of the corridor with a ferry sailing's traffic. It parks at the ferry berth until the run ends. | `bahonar:arrival_without_departure` |
| Gate probe | `probe_d2`, a civilian car | t = 187474 s, 1 October 11:04:34 | It drives in from the west end of the corridor and halts short of the port gate for 300 s. Then it drives on east without passing the gate. | `bahonar:standoff_dwell_at_access_point` |
| Escort to the drydock | `escort_0` to `escort_4`, five naval jeeps | t = 270000 s to t = 270016 s, 2 October 10:00:00 to 10:00:16 | They leave the apron 4 s apart. They drive as one group to the drydock. Routine freight never goes there. | `bahonar:coordinated_group_transit` and `bahonar:destination_off_pattern` |
| Guard who parks elsewhere | `offpost_d4_h7_t3`, a guard | t = 345600 s, 3 October 07:00 | It is due to relieve tower 3. It parks for 8 hours on a spur west of the apron instead. The schedule skips that posting. So tower 3 stands empty for the shift. | `bahonar:posting_not_taken_up` |
| Gate probe | `probe_d5`, a civilian car | t = 447085 s, 4 October 11:11:25 | The same as `probe_d2`. | `bahonar:standoff_dwell_at_access_point` |
| Perimeter shadow | `shadow`, a civilian car body in the `army` class | t = 502200 s, 5 October 02:30 | It leaves the apron and follows the fence past the guard posts at 0.45 of each limit. It stops at none of them. | `bahonar:perimeter_transit_off_cadence` |

The civil times are local, UTC+03:30.\
The escort's route is 13720.5 m long.\
Each gate probe's route is 9903.83 m long, 356 s at the speed limit without the halt.\
SUMO decides the time each vehicle reaches its stop.

These are the planted vehicles' times in a SUMO-only run of the compiled scenario, in seconds:

| Vehicle | Departs | Reaches its stop | Leaves its stop | Ends its trip |
|---|---|---|---|---|
| `staybehind` | 90000 | 90087 | never, still parked at t = 604800 | never |
| `probe_d2` | 187474 | 187791 | 188091 | 188219, off the map at the east end |
| `escort_0` to `escort_4` | 270000 to 270016 | no stop | no stop | 271808 to 271852, at the drydock |
| `offpost_d4_h7_t3` | 345600 | 346897 | 375697 | 376839, back at the apron |
| `probe_d5` | 447085 | 447370 | 447670 | 447791, off the map at the east end |
| `shadow` | 502200 | no stop | no stop | 512872, back at the apron |

The same specification, seed and world give the same times.

### What the truth labels mark

The supervision plan, the file that holds a scenario's labels, is `Shahid_Bahonar_Port_PatternOfLife.supervision.json`.\
Its terms are in the `bahonar` namespace, version 2.\
Each label row has one of three states:

- `annotated`: the vehicle carries out the labeled behavior.\
  The ten vehicles of the six planted events are annotated.\
  So is every vehicle of the 98 ferry pulses, as `bahonar:cleared_gate_transit`.
- `nominal`: a matched negative, a vehicle that looks like a labeled behavior and is not it.\
  The 21 freight hauls are nominal, as `bahonar:routine_freight_haul`.\
  They are hard negatives for the escort's two terms.\
  The 335 guard postings are nominal, as `bahonar:tower_posting`.\
  They are hard negatives for the stay-behind, the gate probes and the guard who parks elsewhere.
- `unlabelled`: no assertion.\
  The 150 corridor and shift-change flows are unlabelled.

A planted event's interval starts at the vehicle's own departure or at its stop.\
The run records the times each interval opened and closed.\
The label of the guard who parks elsewhere carries two values.\
Its `expected_tower` is `tower_03`, the tower it was due at.\
Its `expected_shift_start` is `2026-10-03T07:00:00+03:30`, the start of that shift.\
So the empty tower reaches the record as a value on that guard's label.

Each vehicle record in a truth sidecar, the XML truth file beside each still, carries the vehicle's SUMO id and its supervision state.\
[Behavioral annotations](../../../../Docs/CAT_Research/EPOL/Behavioral_Annotations.md) explains how to read them.

## What you need

- A CARLA distribution: <<FILL: distribution folder name>>.\
  The level pack was cooked against it.
- The distribution set up.\
  Run `setup-venv` once.\
  In each new terminal, run `carla-env` with a dot in front.
- A Cesium ion access token in `CESIUM_ION_TOKEN`.\
  The server streams the photoreal imagery from Cesium ion.\
  Without the token, the level loads with no imagery.
- The level pack, <<FILL: pack file name>>.
- The world package the level was made from, `Shahid_Bahonar_Port.cwp`, from <<FILL: where the world package is published>>.\
  A world package is one generated world in one file.\
  The compiler, `carla-drive` and `carla-capture` read it.
- This folder.\
  In a distribution, it is `Scenarios/Bahonar/`.

On Windows, in PowerShell:

```powershell
.\setup-venv.ps1                         # once
. .\carla-env.ps1                        # in each new terminal
$env:CESIUM_ION_TOKEN = "<your token>"   # in each new terminal
```

On Linux:

```sh
./setup-venv.sh
. ./carla-env.sh
export CESIUM_ION_TOKEN="<your token>"
```

Unless a section says otherwise, run the commands on this page from the distribution's folder.\
[Getting started](../../../../Docs/CAT_Research/Guides/Getting_Started.md) explains the distribution and its commands.

## Installing the level pack

Install the pack with the distribution's own `InstallWorld`:

```powershell
.\world-tools\InstallWorld.ps1 -Package <<FILL: pack file name>>
```

On Linux:

```sh
./world-tools/InstallWorld.sh --package <<FILL: pack file name>>
```

The script installs into the distribution whose `world-tools` folder holds it.\
The world interface version says what a delivered world can rely on.\
First the script makes sure that the distribution offers the version the pack needs.\
Then it copies the world into `CarlaServer/CarlaUnreal/Plugins/GeneratedWorlds/`.\
At the end, it prints the command that loads the world.\
If a server from this distribution has the world loaded, stop the server before you install.\
A refusal means the pack was not cooked for this distribution (see [When it refuses](../../../../Docs/CAT_Research/Guides/Making_A_Level.md#when-it-refuses-1)).

Put `Shahid_Bahonar_Port.cwp` in the distribution's `world-packages/` folder.\
The pack does not bring it.

Start the server on the world's map, in a terminal of its own.\
Set `CESIUM_ION_TOKEN` in that terminal first.

```powershell
.\run-server.ps1 <<FILL: map path>>
```

On Linux:

```sh
./run-server.sh <<FILL: map path>>
```

The server runs headless, with no window.\
Give the map's full path.\
A packaged server does not find a world installed later by its short name.\
If the server does not find the map, it loads its default map instead.\
To make sure that it loaded this world, read the log lines in [Checking it loaded](../../../../Docs/CAT_Research/Guides/Making_A_Level.md#checking-it-loaded).

## Running it

### Compiling the scenario

A run needs the compiled scenario: the SUMO files, the supervision plan and a lock that names them all.\
You compile it from the specification in this folder.

The specification names its world package as `../../world-packages/Shahid_Bahonar_Port.cwp`.\
It names the vehicle catalog as `../../catalogue/vehicles.catalogue.json`.\
The compiler reads both paths from the specification's own folder.\
In a distribution, they lead to `world-packages/` and `catalogue/` in the distribution's folder.\
So the specification compiles there as it is.\
From a source checkout, compile the copy in `Import/` instead.\
Its paths lead to `Build/world-packages/` and `CarlaControl/catalogue/`.

Compile it:

```
carla-compile-scenario Scenarios/Bahonar/Shahid_Bahonar_Port_PatternOfLife.scenario.json --out-dir scenarios/Shahid_Bahonar_Port_PatternOfLife
```

The compiler makes sure that the specification fits the world package.\
It routes every vehicle with SUMO's `duarouter`.\
It runs the scenario in SUMO alone over the whole 604800 s.\
Then it writes the compiled scenario into `scenarios/Shahid_Bahonar_Port_PatternOfLife/`.\
`carla-capture` finds it there by the scenario id.\
The exit status is 0 for a compiled scenario and 1 for a refused one.

Read `Shahid_Bahonar_Port_PatternOfLife.resolution.md` in that folder.\
It says what each place, time and label resolved to.\
The compile gives ten warnings.\
Check 17 warns about each of the nine classes that draw one body.\
Check 41 gives 0.142 for how much the light band tells about the label.\
In the nautical and astronomical twilight bands, every entry is annotated.\
There the light band alone gives the label away.\
If check 1 refuses, the package's road network differs from the one the specification names.

### Watching it with carla-drive

`carla-drive` drives the vehicles from SUMO in the world loaded on the server.\
At the pace of real traffic, the whole week takes a week.\
So start the drive near the part you want to see.\
This drive starts at the day 2 gate probe's departure:

```
carla-drive --scenario scenarios/Shahid_Bahonar_Port_PatternOfLife/Shahid_Bahonar_Port_PatternOfLife.sumocfg --world-package world-packages/Shahid_Bahonar_Port.cwp --epoch Scenarios/Bahonar/Shahid_Bahonar_Port_PatternOfLife.scenario.json --no-record --real-time-factor 1.0 --steps 0 --warm-up 187474
```

- `--epoch` names the specification.\
  The drive reads the epoch and the sun's policy from it.\
  Do not also give `--illumination`.
- `--no-record` spawns no camera and writes no frames.
- `--real-time-factor 1.0` holds the traffic to the pace of the wall clock.
- `--steps 0` runs until the scenario ends.
- `--warm-up 187474` runs SUMO ahead to t = 187474 s before the first tick.\
  It draws nothing on the way.

Before it starts, the drive makes sure that the package describes the loaded world.

To see the gate, place a camera of your own from another terminal:

```
carla-camera-follower --sensor-id Gate_Stare_1 --stare-look-at 1998.4 -1035.0 --stare-altitude-m 150 --stare-standoff-m 150 --stare-bearing-deg 0 --fov 40
```

The package's `areas.resolved.json` gives the port gate area's center as (1998.376, -1034.966) in CARLA meters.\
The camera stands 150 m south of it and 150 m up.\
It looks north at the gate.\
It records nothing.\
Esc closes its window.\
Ctrl+C in the drive's terminal stops the drive.

The distribution has no `sumo-gui`.\
To watch SUMO's own window, add `--sumo-gui --sumo-home <your SUMO 1.27.0 folder>` to the drive.

### Capturing a window with carla-capture

`carla-capture` renders one window of simulated time through the cameras a run file describes.\
For every capture of every camera, it writes a PNG still and its truth sidecar.

The scenario declares no capture windows.\
So the run file gives the window in seconds, as `<begin_s>:<end_s>`.

The capture needs a site profile: a JSON file of the facts about this machine.\
Write one in the distribution's folder:

```
carla-capture --write-site-profile site.json
```

Then edit it as [The site profile](../../../../Docs/CAT_Research/Guides/Getting_Started.md#the-site-profile) shows.\
Set its `sumo.home` to `tools/sumo`.

This run file, `gate_probe.run.json`, stares at the port gate on day 2.\
The window opens at t = 187474 s, as `probe_d2` departs.\
It lasts 900 s.\
In the SUMO-only run, the probe's halt and its exit from the map both fall inside the window.\
Before it, the run fast-forwards SUMO to the start of a 60 s prewarm and draws nothing on the way.\
The prewarm is simulated time that gets the view ready and is not recorded.\
The camera values are a starting point for your own view.

```json
{
  "run_configuration_version": 1,
  "caller_label": "gate probe day 2",
  "scenario_package": "Shahid_Bahonar_Port_PatternOfLife",
  "capture": {
    "window": "187474:188374",
    "prewarm_s": 60.0,
    "channels": [
      {
        "sensor_id": "Gate_Stare_1",
        "stare_look_at_x_m": 1998.4,
        "stare_look_at_y_m": -1035.0,
        "stare_altitude_m": 150.0,
        "stare_standoff_m": 150.0,
        "stare_bearing_deg": 0.0,
        "fov": 40.0
      }
    ]
  }
}
```

Run the offline checks first.\
They do not touch the server:

```
carla-capture --site-profile site.json --run gate_probe.run.json --validate-only
```

The launch echo says what the run will do, with the window's civil times and the sun.\
The scenario's sun policy, `freeze_at_window_start`, holds the sun at the civil time the window opens.\
Then run it for real:

```
carla-capture --site-profile site.json --run gate_probe.run.json
```

The last line names the run result.\
The result names the capture folder, under `captures/`.\
Then make sure that the files keep their schemas and the vehicle records keep their rules:

```
carla-validate captures/<session id>
carla-audit-sidecars captures/<session id>
```

[Running a capture](../../../../Docs/CAT_Research/Guides/Running_A_Capture.md) describes the run file, the timing and the results in full.

## Making the scenario from its parts

This folder holds every input a rebuild needs:

| File | What it is |
|---|---|
| `Shahid_Bahonar_Port.osm` | The OpenStreetMap extract the world is built from. The build cuts the roads at its `<bounds>`. |
| `Shahid_Bahonar_Port.typ.xml` | The type map. It opens the service roads to the `army` and `authority` classes. |
| `Shahid_Bahonar_Port.aoi.geojson` | The areas of interest, named places a label can point to. They are the 16 guard towers, the port gate, the drydock and the ferry terminal. |
| `Shahid_Bahonar_Port_PatternOfLife.scenario.json` | The scenario specification: the routine, the planted events and the labels. |
| `Shahid_Bahonar_Port_PatternOfLife.display.json` | The display convention for `carla-cot-telemetry`. It gives each vehicle class its Cursor-on-Target affiliation: civilian and port traffic neutral, the naval base's traffic friendly. It is a run setting, not part of the scenario. |

The build finds the type map and the areas file beside the extract, by name.\
`carla-cot-telemetry` finds the display convention beside the compiled `.sumocfg`, by name.\
So copy it into `scenarios/Shahid_Bahonar_Port_PatternOfLife/` after you compile.\
The vehicle catalog comes with the distribution, in `catalogue/`.\
In a source checkout, `CarlaControl/scripts/make_bahonar_scenario.py` writes the specification.\
Change the script, not the specification.

### Building the world

`carla-build-world` builds the world on a running server from the extract.\
It writes the world package and leaves the world loaded on the server.\
The build needs the server running, on any map.\
To start it on its default map, in a terminal of its own:

```powershell
.\run-server.ps1
```

On Linux:

```sh
./run-server.sh
```

Set `CESIUM_ION_TOKEN` in the build's terminal too.\
The build streams the imagery and the terrain heights from Cesium ion.

The build writes `world-packages/Shahid_Bahonar_Port.cwp`.\
To keep the delivered package, move it out of `world-packages/` first.\
Then build:

```
carla-build-world --osm Scenarios/Bahonar/Shahid_Bahonar_Port.osm --height-align drape --no-road-filter --netconvert-arg "--remove-edges.by-type highway.footway,highway.path,highway.steps,highway.cycleway,highway.pedestrian,highway.bridleway" --drape-cache-dir drape-cache
```

- `--height-align drape` seats the roads on the photoreal imagery point by point.\
  It writes the ground height grids into the package.\
  A drive and a capture refuse a package without them.
- `--no-road-filter` keeps the roads closed to passenger cars.\
  With the filter on, the build removes every service road.\
  The inside of the wire goes with them.\
  The delivered package was built with the filter off.
- With the filter off, netconvert also keeps the ways for walking, cycling and riding.\
  `--netconvert-arg` removes them by type.\
  The delivered package records the same argument in its `world.json`, as `NetconvertExtraArgs`.
- `--drape-cache-dir drape-cache` keeps the sampled heights.\
  A later build of the same area reads them instead of sampling again.

Before it starts, the build prints what it found beside the extract.\
Read those lines.\
Without the type map, the compiler refuses the guard schedule's trips: their roads are closed to their class (check 10).

A build takes minutes.\
Its last line names the package: `world built; its package is <path of the .cwp>`.\
If the world was built and its package written, the exit status is 0.\
If either was not, it is 1.

The installed level was made from the delivered package.\
A rebuilt package can differ from it.\
A drive or a capture refuses a package that does not describe the loaded world.\
So use the rebuilt package on the world the build left loaded.

[Building a world](../../../../Docs/CAT_Research/Guides/Building_A_World.md) explains every part of the build.

### Compiling against the new package

Each build writes a new network.\
A scenario names its network by its fingerprint, a hash of the network's content.\
Read the new package's fingerprint:

```
python -c "import json, zipfile; print(json.loads(zipfile.ZipFile('world-packages/Shahid_Bahonar_Port.cwp').read('world.json'))['NetworkFingerprint'])"
```

If it differs from `world.network_fingerprint` in the specification, the compiler refuses the scenario (check 1).\
Copy the new fingerprint into the specification.\
Then compile again, as [Compiling the scenario](#compiling-the-scenario) shows.

Read the resolution report.\
This specification names its places by edge and by lane.\
If an edge has a new id, check 7 refuses the place that names it.\
In a source checkout, run `make_bahonar_scenario.py` with `--world-package` instead.\
It writes the specification against that package and compiles it.

[Writing a scenario](../../../../Docs/CAT_Research/Guides/Writing_A_Scenario.md) explains every field of the specification.

## How the level pack was made

The level pack is the world `Shahid_Bahonar_Port` as an Unreal level.\
It is cooked as add-on content (DLC) for one distribution.\
[Making a level](../../../../Docs/CAT_Research/Guides/Making_A_Level.md) gives every step in full.\
Run its commands from the top of the CARLA checkout.\
For this world, the steps were:

1. Set up the engine, Visual Studio 2022 and a CARLA checkout at the distribution's commits.\
   [Section 1 of Making a level](../../../../Docs/CAT_Research/Guides/Making_A_Level.md#1-what-you-need) lists what each needs.
2. Copy `Shahid_Bahonar_Port.cwp` into the checkout's `Build\world-packages\` folder.\
   Do not rename it.\
   The importer names the level and the plugin after the file name.
3. Open the editor with `.\Scripts\Windows\OpenCarlaEditor.ps1`.
4. Open the World Package Importer.\
   In the Content Browser, open "Settings" and turn on "Show Plugin Content".\
   Go to "Plugins > CarlaTools Content > GeneratedWorld".\
   Right-click WBP_WorldPackageImporter and choose "Run Editor Utility Widget".
5. Choose `Shahid_Bahonar_Port.cwp` and click "Import".\
   Leave "Make this world available to packaged builds" on.\
   Leave the Cesium ion token empty.\
   A token typed there ships inside the level pack.\
   The import writes the level `/Game/Carla/Maps/Generated/Shahid_Bahonar_Port`.\
   It also exports the level as a plugin, in `Unreal\CarlaUnreal\Plugins\GeneratedWorlds\Shahid_Bahonar_Port\`.
6. Clean up the level: <<FILL: the clean-up done on this level, or none>>.\
   Leave the georeference, the road network and the bare-earth data as the import made them.\
   A drive or a capture compares them with the `.cwp` before it starts.
7. After any edit, export the level again.\
   Save your work first.\
   Open a level that is neither the imported level nor the plugin's copy.\
   Then run this line in the Output Log, with its command box set to "Python":

   ```python
   import unreal; r = unreal.GeneratedLevelExporter.export_level_as_plugin("/Game/Carla/Maps/Generated/Shahid_Bahonar_Port", "Shahid_Bahonar_Port"); print(r.succeeded, r.assets_exported, r.failure_reason)
   ```

8. Mark the world for separate delivery:

   ```powershell
   New-Item -ItemType File Unreal\CarlaUnreal\Plugins\GeneratedWorlds\Shahid_Bahonar_Port\DeliverSeparately.txt
   ```

9. Cook it as DLC against the distribution:

   ```powershell
   .\Scripts\Windows\PackageWorld.ps1 -World Shahid_Bahonar_Port -Distribution <<FILL: distribution folder>>
   ```

   `PackageWorld` writes the level pack to `Build\WorldPackages\Shahid_Bahonar_Port.zip`.\
   For a Linux distribution, run `PackageWorld.sh` on Linux.
