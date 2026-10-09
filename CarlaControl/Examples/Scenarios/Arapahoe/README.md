# Arapahoe I-25 underpass dwell

`Arapahoe_I25_UnderpassDwell` is 45 minutes of morning traffic where Interstate 25 crosses East Arapahoe Road, in Arapahoe County, Colorado.\
SUMO drives the traffic and CARLA draws every vehicle SUMO moves.\
Among heavy freeway and arterial traffic, one vehicle leaves I-25 and waits 30 minutes under the Yosemite Street bridge.\
Then it drives on.\
The world is `Arapahoe_I25`.

## The scenario

Simulated second zero, t = 0, is 07:00 Mountain Daylight Time on 29 September 2026, in the morning peak.\
The run ends at t = 2700 s, 07:45.\
SUMO steps every 0.05 s, with seed 42.\
The same specification, seed and world give the same traffic.

### The traffic

51 flows run for the whole 2700 s:

- The freeway.\
  3300 vehicles per hour drive north on I-25 and 3000 drive south.\
  Eight smaller flows join or leave it at the Arapahoe Road interchange, at 110 to 150 vehicles per hour each.
- Arapahoe Road and the side streets.\
  350 vehicles per hour drive west on East Arapahoe Road and 330 drive east.\
  Fourteen more flows use South Yosemite Street and the side streets, at 40 to 130 vehicles per hour each.
- The underpass.\
  Three more arterial flows drive South Yosemite Street under the bridge, at 120, 110 and 80 vehicles per hour.\
  The road there has one lane each way.
- The commuters.\
  22 residential flows, at 20 to 32 vehicles per hour each, link the west and south edges with the freeway and Arapahoe Road.

Each kind of flow draws its own mix of vehicles:

| Vehicle class | Freeway | Arterial | Residential |
|---|---|---|---|
| Ordinary cars | 35% | 45% | 58% |
| Quick cars | 26% | none | none |
| Sport utilities | 20% | 24% | 33% |
| Vans | 8% | 20% | 9% |
| Box trucks | 7% | 11% | none |
| Heavy goods vehicles | 4% | none | none |

Quick cars are a kind of driver, not a body.\
They draw the same nine cars as the ordinary ones and work their way to the left lanes.\
Arapahoe Road is lined with businesses.\
So the arterial mix has more vans and trucks than the freeway.

Before the compiler writes anything, it runs the scenario in SUMO alone.\
In that run, 7433 vehicles enter over the 2700 s and none is dropped.

### The marked vehicle

One actor, `marked`, is the vehicle that matters.\
Its body is the Jeep Wrangler Rubicon, `vehicle.jeep.wrangler_rubicon`.\
No other class in the scenario draws that body.\
Its class holds the posted limit exactly: its `speedFactor` is 1.00 and its `speedDev` is 0.

In order:

1. At t = 120 s, 07:02, it enters northbound on I-25 at the south edge of the map.
2. It leaves at the Arapahoe Road interchange and drives west on East Arapahoe Road.
3. It turns north up South Yosemite Street.
4. Under the Yosemite Street bridge, it pulls off the running lane and waits 1800 s, 30 minutes.
5. Then it drives on to I-25 and leaves north.

The stop is a parking stop, `"parking": true`.\
A parking stop leaves the running lane free.\
SUMO decides the time the vehicle reaches the stop.\
The route is 5251.8 m long.\
At the speed limit it takes 257.9 s, without the stop.

These are its times in a SUMO-only run of the compiled scenario:

| Event | t (s) |
|---|---|
| Enters I-25 | 120.00 |
| Reaches its stop | 450.60 |
| Leaves its stop | 2250.60 |
| Leaves the map | 2485.05 |

The same specification, seed and world give the same times.

The stop is the point 39.600357, -104.88649.\
The compiler placed it on lane `218965860#0_0` of South Yosemite Street, 88.63 m along.\
In CARLA's frame, the spot is at about (-171.6, -672.0).

### The incident

An incident closes five of the six northbound lanes of I-25 past Arapahoe Road.\
It lasts from t = 900 s to t = 1080 s, 07:15 to 07:18.\
The closed lanes are lanes 0 to 4 of edge `1001791386`, counted from the right.\
Drivers learn of the closure upstream, on edges `907700111` and `1342047649`.\
A closed lane admits only the `authority` class.

### What the truth labels mark

The scenario labels nothing.\
Its supervision plan, the file that holds a scenario's labels, is `Arapahoe_I25_UnderpassDwell.supervision.json`.\
The plan names no instance and no series.\
Every flow and the `marked` vehicle are `unlabelled`: the plan makes no assertion about them.

A capture's truth still names every vehicle.\
Each vehicle record in a truth sidecar, the XML truth file beside each still, carries the vehicle's SUMO id.\
The marked vehicle's records carry `sumo_id="marked"`.\
A flow's vehicles are named `<flow id>.<n>`, such as `i25_north_through.0`.

## What you need

- A CARLA distribution: `Carla-0.10.0-Win64-Development`.\
  It is CARLA 0.10.0, built from commit `954765e92`, as its `VERSION` file says.\
  The level pack was cooked against it.
- The distribution set up.\
  Run `setup-venv` once.\
  In each new terminal, run `carla-env` with a dot in front.
- A Cesium ion access token in `CESIUM_ION_TOKEN`.\
  The server streams the photoreal imagery from Cesium ion.\
  Without the token, the level loads with no imagery.
- This folder.\
  In a distribution, it is `Scenarios/Arapahoe/`.

This folder holds the rest:

| File | What it is |
|---|---|
| `Arapahoe_I25.tar.xz` | The level pack, 46.9 MB. It is the world as an Unreal level, cooked for the distribution above. |
| `Arapahoe_I25.cwp` | The world package the level was made from. A world package is one generated world in one file. The compiler, `carla-drive` and `carla-capture` read it. |
| `Arapahoe_I25_UnderpassDwell.scenario.json` | The scenario specification. |
| `Arapahoe_I25_UnderpassDwell/` | The compiled scenario, ready to run. |

The level pack is a Windows pack, for a Win64 Development distribution.\
A Linux distribution needs a pack cooked on Linux with `PackageWorld.sh`.\
[How the level pack was made](#how-the-level-pack-was-made) gives the steps.

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
.\world-tools\InstallWorld.ps1 -Package Scenarios\Arapahoe\Arapahoe_I25.tar.xz
```

`InstallWorld` takes this `.tar.xz` in the same way as the `.zip` that `PackageWorld` writes.\
The script installs into the distribution whose `world-tools` folder holds it.\
So the command needs no `-Into`.

The world interface version says what a delivered world can rely on.\
First the script makes sure that the distribution offers the version the pack needs.\
This pack needs version 1.x, minor 0 or later.\
The distribution declares 1.0.

Then the script copies the world into `CarlaServer/CarlaUnreal/Plugins/GeneratedWorlds/`.\
It replaces a copy of the world that is already installed.\
At the end, it prints the command that loads the world.\
If a server from this distribution has the world loaded, stop the server before you install.\
A refusal means the pack was not cooked for this distribution (see [When it refuses](../../../../Docs/CAT_Research/Guides/Making_A_Level.md#when-it-refuses-1)).

The pack is for Windows only: its `world.json` names the platform `Win64`.\
The install scripts do not check the platform.\
So do not install this pack into a Linux distribution.

The pack does not bring the world package.\
The commands on this page read `Arapahoe_I25.cwp` from this folder.

Start the server on the world's map, in a terminal of its own.\
Set `CESIUM_ION_TOKEN` in that terminal first.

```powershell
.\run-server.ps1 /Arapahoe_I25/Maps/Arapahoe_I25
```

On Linux, with a pack cooked on Linux installed:

```sh
./run-server.sh /Arapahoe_I25/Maps/Arapahoe_I25
```

The server runs headless, with no window.\
Give the map's full path.\
A packaged server does not find a world installed later by its short name.\
If the server does not find the map, it loads its default map instead.\
To make sure that it loaded this world, read the log lines in [Checking it loaded](../../../../Docs/CAT_Research/Guides/Making_A_Level.md#checking-it-loaded).

This pack was installed with the command above and loaded in `Carla-0.10.0-Win64-Development`.\
The server log showed `[GeneratedWorld] applied: origin ...` with `per-cell surface field`.\
The origin in that line must be the one in the `.cwp`, 39.59431, -104.88449.\
The road network loaded from the level's own asset.

## Running it

### Compiling the scenario

A run needs the compiled scenario: the SUMO files, the supervision plan and a lock that names them all.\
This folder holds it, in `Arapahoe_I25_UnderpassDwell/`.\
It was compiled at commit `a2baed579` from the specification in this folder, against `Arapahoe_I25.cwp`.\
So a run needs no compile first.

Read `Arapahoe_I25_UnderpassDwell.resolution.md` in that folder.\
It says what each place and time resolved to.\
The compile gives six warnings.\
Check 17 warns about each of the five classes that draw one body.\
Check 41 warns that the scenario has only one supervision state.

To compile it again, run this command.\
The new compiled scenario replaces the delivered one.

```
carla-compile-scenario Scenarios/Arapahoe/Arapahoe_I25_UnderpassDwell.scenario.json --out-dir Scenarios/Arapahoe/Arapahoe_I25_UnderpassDwell
```

The specification names its world package as `Arapahoe_I25.cwp`, the file beside it.\
It names the vehicle catalog as `../../catalogue/vehicles.catalogue.json`.\
The compiler reads both paths from the specification's own folder.\
In a distribution, the catalog path leads to `catalogue/` in the distribution's folder.\
So the specification compiles there as it is.

From a source checkout, compile the copy in `Import/` instead.\
Its paths lead to `Build/world-packages/` and `CarlaControl/catalogue/`.

The compiler makes sure that the specification fits the world package.\
It routes every vehicle with SUMO's `duarouter`.\
It runs the scenario in SUMO alone over the whole 2700 s.\
Then it writes the compiled scenario into the folder `--out-dir` names.\
The exit status is 0 for a compiled scenario and 1 for a refused one.\
If check 1 refuses, the package's road network differs from the one the specification names.

### Watching it with carla-drive

`carla-drive` drives the vehicles from SUMO in the world loaded on the server.\
Start it in a terminal of its own:

```
carla-drive --scenario Scenarios/Arapahoe/Arapahoe_I25_UnderpassDwell/Arapahoe_I25_UnderpassDwell.sumocfg --world-package Scenarios/Arapahoe/Arapahoe_I25.cwp --epoch Scenarios/Arapahoe/Arapahoe_I25_UnderpassDwell.scenario.json --no-record --real-time-factor 1.0 --steps 0
```

- `--epoch` names the specification.\
  The drive reads the epoch and the sun's policy from it.\
  Do not also give `--illumination`.
- `--no-record` spawns no camera and writes no frames.
- `--real-time-factor 1.0` holds the traffic to the pace of the wall clock.
- `--steps 0` runs until the scenario ends.

Run `carla-env` in the drive's terminal first.\
If `SUMO_HOME` names a SUMO of another release than the one that converted the world, the drive refuses to start.\
`carla-env` sets `SUMO_HOME` to the distribution's own SUMO.

Before it starts, the drive makes sure that the package describes the loaded world.\
The marked vehicle enters at t = 120 s.\
To start while it waits at its stop, add `--warm-up 600`.\
SUMO then runs ahead to t = 600 s before the first tick and draws nothing on the way.

To see the traffic, place a camera of your own from another terminal:

```
carla-camera-follower --sensor-id Underpass_Orbit_1 --pattern orbit --orbit-centre -171.6 -672.0
```

It orbits the dwell spot at a 200 m radius, 518.2 m up, 240 s per lap.\
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

The capture finds a compiled scenario by its id, as `<id>/<id>.lock.json` under `paths.scenario_root`.\
It finds the world package by the name the lock records, under `paths.world_package_root`.\
Here both are in this folder.\
So the capture commands below set both paths to `Scenarios/Arapahoe` with `--set`.\
A relative path in `--set` is read from the folder you run the command in.\
Run them from the distribution's folder.

This run file, `underpass.run.json`, is the one [Running a capture](../../../../Docs/CAT_Research/Guides/Running_A_Capture.md#an-orbit-flown-by-the-server) uses for this scenario.\
It records 240 s from t = 600 s.\
In the SUMO-only run, the marked vehicle waits at its stop through the whole window.\
Before that, it renders a 60 s prewarm: simulated time that gets the views ready and is not recorded.\
One camera orbits the dwell spot.\
The other stares at it from 150 m south and 150 m up, with its own exposure.

```json
{
  "run_configuration_version": 1,
  "caller_label": "underpass orbit",
  "scenario_package": "Arapahoe_I25_UnderpassDwell",
  "capture": {
    "window": "600:840",
    "prewarm_s": 60.0,
    "channels": [
      {
        "sensor_id": "Underpass_Orbit_1",
        "pattern": "orbit",
        "orbit_centre_x_m": -171.6,
        "orbit_centre_y_m": -672.0,
        "orbit_radius_m": 200.0,
        "orbit_altitude_m": 518.2,
        "orbit_period_s": 240.0,
        "fov": 30.0,
        "width": 1920,
        "height": 1080
      },
      {
        "sensor_id": "Underpass_Stare_1",
        "stare_look_at_x_m": -171.6,
        "stare_look_at_y_m": -672.0,
        "stare_altitude_m": 150.0,
        "stare_standoff_m": 150.0,
        "stare_bearing_deg": 0.0,
        "fov": 40.0,
        "exposure_iso": 200.0,
        "exposure_shutter_s": 0.002,
        "exposure_fstop": 5.6
      }
    ]
  }
}
```

Run the offline checks first.\
They do not touch the server:

```
carla-capture --site-profile site.json --run underpass.run.json --set paths.scenario_root=Scenarios/Arapahoe --set paths.world_package_root=Scenarios/Arapahoe --validate-only
```

The launch echo says what the run will do, with the window's civil times and the sun.\
The scenario's sun policy, `freeze_at_window_start`, holds the sun at the civil time the window opens.\
Then run it for real:

```
carla-capture --site-profile site.json --run underpass.run.json --set paths.scenario_root=Scenarios/Arapahoe --set paths.world_package_root=Scenarios/Arapahoe
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
| `Arapahoe_I25.osm` | The OpenStreetMap extract the world is built from. The build cuts the roads at its `<bounds>`. |
| `Arapahoe_I25_UnderpassDwell.scenario.json` | The scenario specification: the traffic, the marked vehicle and the incident. |

The world was built with no areas file and no type map.\
The vehicle catalog comes with the distribution, in `catalogue/`.\
In a source checkout, `CarlaControl/scripts/make_arapahoe_scenario.py` writes the specification.\
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

The build writes `Scenarios/Arapahoe/Arapahoe_I25.cwp`, beside the specification.\
It replaces the delivered package.\
To keep the delivered package, copy it out of this folder first.\
Then build:

```
carla-build-world --osm Scenarios/Arapahoe/Arapahoe_I25.osm --height-align drape --drape-cache-dir drape-cache --emit-world-package Scenarios/Arapahoe
```

- `--emit-world-package Scenarios/Arapahoe` writes the package into this folder.\
  The specification finds it there.\
  Without the option, the package goes to `world-packages/`.
- `--height-align drape` seats the roads on the photoreal imagery point by point.\
  It writes the ground height grids into the package.\
  A drive and a capture refuse a package without them.
- `--drape-cache-dir drape-cache` keeps the sampled heights.\
  A later build of the same area reads them instead of sampling again.
- The road filter stays on, as it was for the delivered package.\
  It keeps only the roads open to passenger cars.

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
python -c "import json, zipfile; print(json.loads(zipfile.ZipFile('Scenarios/Arapahoe/Arapahoe_I25.cwp').read('world.json'))['NetworkFingerprint'])"
```

If it differs from `world.network_fingerprint` in the specification, the compiler refuses the scenario (check 1).\
Copy the new fingerprint into the specification.\
Then compile again, as [Compiling the scenario](#compiling-the-scenario) shows.

Read the resolution report.\
This specification names most of its places by edge id.\
If an edge has a new id, check 7 refuses the place that names it.\
In a source checkout, run `make_arapahoe_scenario.py` with `--world-package` instead.\
It writes the specification against that package and compiles it.

[Writing a scenario](../../../../Docs/CAT_Research/Guides/Writing_A_Scenario.md) explains every field of the specification.

## How the level pack was made

The level pack is the world `Arapahoe_I25` as an Unreal level.\
It is cooked as add-on content (DLC) for one distribution.\
[Making a level](../../../../Docs/CAT_Research/Guides/Making_A_Level.md) gives every step in full.\
Run its commands from the top of the CARLA checkout.\
For this world, the steps were:

1. Set up the engine, Visual Studio 2022 and a CARLA checkout at the distribution's commits.\
   [Section 1 of Making a level](../../../../Docs/CAT_Research/Guides/Making_A_Level.md#1-what-you-need) lists what each needs.\
   This pack was cooked from a checkout at a later CARLA commit, as step 9 says.
2. Copy `Arapahoe_I25.cwp` into the checkout's `Build\world-packages\` folder.\
   Do not rename it.\
   The importer names the level and the plugin after the file name.
3. Open the editor with `.\Scripts\Windows\OpenCarlaEditor.ps1`.
4. Open the World Package Importer.\
   In the Content Browser, open "Settings" and turn on "Show Plugin Content".\
   Go to "Plugins > CarlaTools Content > GeneratedWorld".\
   Right-click WBP_WorldPackageImporter and choose "Run Editor Utility Widget".
5. Choose `Arapahoe_I25.cwp` and click "Import".\
   Leave "Make this world available to packaged builds" on.\
   Leave the Cesium ion token empty.\
   A token typed there ships inside the level pack.\
   The import writes the level `/Game/Carla/Maps/Generated/Arapahoe_I25`.\
   It also exports the level as a plugin, in `Unreal\CarlaUnreal\Plugins\GeneratedWorlds\Arapahoe_I25\`.
6. Clean up the level: none, for this level.\
   It ships as the import made it.\
   In a clean-up of your own, leave the georeference, the road network and the bare-earth data as the import made them.\
   A drive or a capture compares them with the `.cwp` before it starts.
7. After any edit, export the level again.\
   Save your work first.\
   Open a level that is neither the imported level nor the plugin's copy.\
   Then run this line in the Output Log, with its command box set to "Python":

   ```python
   import unreal; r = unreal.GeneratedLevelExporter.export_level_as_plugin("/Game/Carla/Maps/Generated/Arapahoe_I25", "Arapahoe_I25"); print(r.succeeded, r.assets_exported, r.failure_reason)
   ```

8. Mark the world for separate delivery:

   ```powershell
   New-Item -ItemType File Unreal\CarlaUnreal\Plugins\GeneratedWorlds\Arapahoe_I25\DeliverSeparately.txt
   ```

9. Cook it as DLC against the distribution.\
   With the checkout at the distribution's commit, the usual command names the distribution's folder:

   ```powershell
   .\Scripts\Windows\PackageWorld.ps1 -World Arapahoe_I25 -Distribution <distribution folder>
   ```

   `<distribution folder>` is a placeholder for the path of `Carla-0.10.0-Win64-Development`.\
   This pack was not cooked that way.\
   The checkout was at a later commit than the distribution.\
   `-Distribution` refuses such a checkout.\
   So this command made the pack:

   ```powershell
   .\Scripts\Windows\PackageWorld.ps1 -World Arapahoe_I25 -BasedOnRelease 954765e92
   ```

   `-BasedOnRelease` used the release record of the base cook that produced the distribution.\
   That record's asset registry is byte for byte the distribution's.\
   `PackageWorld` writes the level pack to `Build\WorldPackages\Arapahoe_I25.zip`.\
   For a Linux distribution, run `PackageWorld.sh` on Linux.
10. Repack the zip as a `.tar.xz`.\
    Unpack `Build\WorldPackages\Arapahoe_I25.zip` into a folder.\
    Then pack its contents again with LZMA:

    ```
    tar -cJf Arapahoe_I25.tar.xz -C <unpacked folder> world.json Arapahoe_I25
    ```

    `<unpacked folder>` is a placeholder for the folder the zip went into.\
    Any xz at level 9 gives about half the zip's size.\
    The contents stay the same: `world.json` beside the `Arapahoe_I25` plugin folder.\
    The repack is only for the examples shipped here.\
    `PackageWorld` writes a zip.
