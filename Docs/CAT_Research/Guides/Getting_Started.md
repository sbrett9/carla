# Getting started

This page is for the person who installs the tool suite and for the developer who sets up cameras with it.\
It covers what the distribution holds, what the machine needs, how to set it up, the settings to make once, and how to check that it works.

Two more pages follow on from this one:

- [Running a capture](Running_A_Capture.md): capture a window of a compiled scenario with `carla-capture`.
- [Cameras and missions](Cameras_And_Missions.md): drive cameras from your own code, and watch a drive live.

## What the distribution holds

The distribution is one archive: `Carla-<version>-Win64-<config>.zip` on Windows, or `Carla-<version>-Linux-<config>.tar.gz` on Linux.\
`Scripts/Windows/MakeDistribution.ps1` and `Scripts/Linux/MakeDistribution.sh` make it.\
Unpacked, it is one folder of the same name, and it runs from where you unpack it.

| Item | What it is |
|---|---|
| `CarlaServer/` | The CARLA server, cooked and ready to run. `run-server` starts it. |
| `wheels/` | Two Python wheels: `carlanet`, the client library, and `carlacontrol`, which installs the `carla-*` commands. |
| `setup-venv.ps1` or `setup-venv.sh` | Makes a Python virtual environment, `venv/`, and installs both wheels into it. Run it once. |
| `carla-env.ps1` or `carla-env.sh` | Sets up the environment. Run it in each new terminal before you use a `carla-*` command. |
| `run-server.ps1` or `run-server.sh` | Starts the server without a window. |
| `tools/sumo/` | The SUMO toolchain, laid out as a SUMO installation: `netconvert`, `sumo` and `duarouter` in `bin/`, SUMO's `typemap` and `xsd` data, its `traci` and `sumolib` Python modules, and the PROJ data. On Linux, `lib/` holds the shared libraries the programs load. There is no `sumo-gui`. |
| `catalogue/` | The measured vehicle catalog, `vehicles.catalogue.json`, and the SUMO vehicle types made from it, `vehicles.vtypes.rou.xml`. |
| `osm/` | Example OpenStreetMap extracts to build worlds from. |
| `skills/` | The scenario-authoring skills, which describe how to write scenarios for a generated world. |
| `world-tools/` | `PackageWorld` and `InstallWorld`, which package a world for a distribution and install a packaged world into one. |
| `VERSION` | Which build of CARLA this is. |
| `MANIFEST.md` and `licenses/` | Every component in the archive, where it came from, its license, and the license texts. |
| `README.md` | A short version of this page. |

The commands `carlacontrol` installs include these:

| Command | What it does |
|---|---|
| `carla-build-world` | Builds a world on the running server from an OpenStreetMap extract and writes its world package (`.cwp`). |
| `carla-sctmv` | Builds a world, then lets you fly, drive and record in it. |
| `carla-compile-scenario` | Compiles a scenario against a world package. |
| `carla-capture` | Captures a window of a compiled scenario. |
| `carla-drive` | Drives a world's vehicles from SUMO, and records. |
| `carla-camera-follower` | Shows one camera of your own on a running world, live. |
| `carla-free-camera` | Flies a camera of your own around a running world, live. |
| `carla-validate` | Checks the files the tools write against their schemas. |
| `carla-audit-sidecars` | Checks the truth sidecars of a capture. |
| `carla-check-sumo` | Says which SUMO the commands use, and checks that it is complete. |
| `carla-cot-telemetry` | Makes Cursor-on-Target telemetry for every vehicle in a SUMO scenario, to a UDP socket, an XML file or a CSV. |
| `carla-diff-manifests` | Checks that two runs of one scenario name the same supervision rows in their run manifests. |
| `carla-check-label-leaks` | Checks that a telemetry dataset does not give away which vehicles a scenario planted. |
| `carla-publish-reference-set` | Refreshes a world package's areas of interest and place index. |

Every command takes `--help`.

## What the machine needs

- **A GPU.**\
  The server renders even when it runs without a window.\
  On Windows, 64-bit Windows 10 or 11 with current graphics drivers.\
  On Linux, a 64-bit system compatible with RHEL 8 (glibc 2.28 or newer) and a GPU with Vulkan drivers.
- **Python 3.11 or newer**, on the `PATH`, for the virtual environment.
- **The .NET 10 runtime.**\
  `carlanet` runs .NET assemblies.
- **SUMO 1.27.0, only if you want `sumo-gui`.**\
  The distribution's own SUMO has no `sumo-gui`.\
  If you want to watch a drive in SUMO's window, install SUMO 1.27.0 yourself.\
  It must be the release that converted the world, which for the worlds this release builds is 1.27.0.

## Set up

Unpack the archive, open a terminal in its folder, and run these steps.

On Windows, in PowerShell:

```powershell
.\setup-venv.ps1        # once: makes venv\ and installs the wheels and the commands
. .\carla-env.ps1       # in each new terminal: note the dot and the space at the start
```

On Linux:

```sh
./setup-venv.sh         # once
. ./carla-env.sh        # in each new shell
```

**`setup-venv`** makes `venv/` beside the scripts and installs both wheels from `wheels/`.\
It installs `carlacontrol`'s own dependencies too (numpy, lxml and pygame-ce), and puts the `carla-*` commands in the virtual environment.

**`carla-env`** must be run with a dot in front, so that it changes the terminal you are in.\
It does two things:

- It activates `venv/`, which puts the `carla-*` commands on the `PATH`.
- It points every command at the distribution's own SUMO toolchain, whatever the machine had set before:

  | Variable | Set to |
  |---|---|
  | `SUMO_HOME` | `tools/sumo`, the SUMO installation scenarios are compiled and driven with |
  | `CARLA_NETCONVERT` | `tools/sumo/bin/netconvert`, the converter worlds are built with |
  | `PROJ_LIB` and `PROJ_DATA` | `tools/sumo/proj`, the coordinate data `netconvert` uses |

**`run-server`** starts the server.\
Run it in a terminal of its own:

```powershell
.\run-server.ps1                                      # Windows
.\run-server.ps1 /Arapahoe_I25/Maps/Arapahoe_I25      # start in an installed world
```

```sh
./run-server.sh &                                     # Linux
```

It runs `CarlaServer` with `-RenderOffScreen -nosound`, so there is no window.\
A first argument that does not start with `-` is the map to start in.\
Anything else you add is passed to the server.\
The server answers on port 2000.

A capture needs the world loaded on the server.\
`carla-build-world --osm osm/<extract>.osm` builds a world on the running server, loads it, and writes its world package to `world-packages/`.\
A world installed with `world-tools/InstallWorld` is loaded by starting the server in its map, as above.

## Settings to make after installing

### A Cesium ion token, for building worlds

`carla-build-world` and `carla-sctmv` stream terrain and imagery from Cesium ion, and need an access token.\
Set `CESIUM_ION_TOKEN` to it, or pass `--ion-token <token>`:

```powershell
$env:CESIUM_ION_TOKEN = "<your token>"     # Windows, this terminal only
```

```sh
export CESIUM_ION_TOKEN="<your token>"     # Linux, this shell only
```

### Your own SUMO, for `sumo-gui`

`carla-drive --sumo-gui` shows the simulation it is stepping in SUMO's own window.\
The distribution has no `sumo-gui`, so point the drive at your own SUMO 1.27.0 installation, the folder that holds `bin/sumo-gui`.\
Either pass it each time:

```sh
carla-drive --sumo-gui --sumo-home <your SUMO 1.27.0 folder> ...
```

or set `CARLANET_SUMO_HOME` to that folder.\
The drive looks at `CARLANET_SUMO_HOME` before `SUMO_HOME`, so `SUMO_HOME` can stay on the distribution's toolchain for building worlds and compiling scenarios.

`carla-capture` reads `CARLANET_SUMO_HOME` too, when no site profile names a SUMO installation (see below).\
The capture then runs your SUMO, so it must be 1.27.0 as well.\
An unattended capture refuses a value read from `CARLANET_SUMO_HOME` unless the site profile lists the variable in its `environment`.

### The site profile

A site profile is a small JSON file that holds the facts about this machine that a capture needs: the server's address, the SUMO installation, and the folders for scenarios, world packages, the vehicle catalog, captures and run records.\
Keeping them in their own file lets the same run file move between machines unchanged.\
The [Site profile](../Schemas/Site_Profile.md) page describes every field.

You need one for two reasons:

- **An unattended capture refuses without one.**\
  An installed `carla-capture` run with `--caller unattended` is refused by run check 36 until the site profile names the SUMO installation.\
  Without a profile nothing names it, so the capture would search `SUMO_HOME` and then the `PATH`.\
  An unattended run does not take machine state it was not given, so it refuses.\
  The refusal reads: `'sumo.home' names no installation, so the session will search SUMO_HOME, then PATH for one, which the site profile does not declare.`
- **Without one, the folders depend on where you run the command.**\
  An installed `carla-capture` looks for world packages in `world-packages/` under the folder it is run from, and for compiled scenarios in `scenarios/` there.\
  A profile fixes those folders whatever folder you are in.

**Write a profile to edit.**\
Run this from the folder you will run captures from, usually the distribution's folder:

```sh
carla-capture --write-site-profile site.json
```

It writes the values this machine would use now, as absolute paths.\
Installed, that is the current folder's `scenarios/`, `world-packages/`, `captures/` and `runs/`, the catalog installed with `carlacontrol`, and `"sumo": {"home": null}` unless `CARLANET_SUMO_HOME` names an installation.\
It writes no `server` block.

**Edit it.**\
Set `sumo.home` to the SUMO installation, the folder that holds `bin/sumo`.\
Don't leave it `null`: with `null`, the capture searches `SUMO_HOME` and the `PATH` of whatever machine runs it.\
Add a `server` block if the server is not on this machine at port 2000.\
A relative path is relative to the profile's own folder, so a profile kept in the distribution's folder can be short:

```json
{
  "site_profile_version": 1,
  "note": "The capture machine. Relative paths are relative to this file.",
  "environment": [],
  "server": {"host": "127.0.0.1", "port": 2000, "timeout_s": 30.0},
  "sumo": {"home": "tools/sumo"},
  "paths": {
    "scenario_root": "scenarios",
    "world_package_root": "world-packages",
    "catalogue": "catalogue/vehicles.catalogue.json",
    "capture_root": "captures",
    "runs_root": "runs"
  }
}
```

| Field | What to put in it |
|---|---|
| `sumo.home` | The SUMO installation captures run: the distribution's `tools/sumo`, or your own SUMO 1.27.0. |
| `server` | The server's `host`, `port` and `timeout_s` (seconds to wait for an answer). |
| `paths.scenario_root` | Where compiled scenarios are, one folder per scenario id (`<id>/<id>.lock.json`). |
| `paths.world_package_root` | Where the world packages (`.cwp` files) are. |
| `paths.catalogue` | The vehicle catalog. It must be the one the scenario was compiled against (run check 48). |
| `paths.capture_root` | Where captures are written, one folder per run. Put it on a disk with room: the launch estimates about 2.25 MiB per 1280 x 720 still. |
| `paths.runs_root` | Where run results and their records go when `--result` is not given. |
| `environment` | The environment variables the profile allows a value to come from. Leave it empty when every value is in the file. |

The `catalogue/vehicles.catalogue.json` in the distribution is the same file as the one installed with `carlacontrol`.

**Use it** on every capture:

```sh
carla-capture --site-profile site.json --run my_capture.run.json
```

The capture refuses a profile that is not JSON, has another `site_profile_version`, holds a block or field it does not know, or does not fit its schema.\
That refusal ends the launch with exit status 1, before a run begins, so no run result is written.\
Every value the capture uses is recorded, with where it came from, in the run's lock and resolution report.\
To check the file without starting a run, give `carla-validate` the folder that holds it.\
It checks every site profile and run file in the folder against its schema.\
It does not take a single `.json` file.

### World packages

`carla-capture` binds the world package (`.cwp`) the scenario was compiled against.\
It finds it by the name the scenario's lock records, under `paths.world_package_root`.\
`carla-build-world` writes the package to `world-packages/` under the folder it is run from.\
A world installed from a `PackageWorld` zip does not bring its `.cwp` with it, so copy the `.cwp` the world was built as into that folder.

## Checking the install

### Which SUMO the commands use

`carla-check-sumo` says which SUMO installation the commands find, its release, and which rule found it.\
Then it runs `netconvert`, `sumo` and `duarouter`, and imports `traci`, to check that the installation is complete.\
Give it the release you expect:

```sh
carla-check-sumo --expect-version 1.27.0
```

After `carla-env`, it finds `tools/sumo` through `SUMO_HOME`:

```text
SUMO installation: <distribution>\tools\sumo (version 1.27.0, matched by SUMO_HOME)
Staged tools:
  netconvert   Eclipse SUMO netconvert 1.27.0
  sumo         Eclipse SUMO sumo 1.27.0
  duarouter    Eclipse SUMO duarouter 1.27.0
  traci        <distribution>\tools\sumo\tools\traci\__init__.py
SUMO toolchain at <distribution>\tools\sumo is complete.
```

It exits 1 when the installation is incomplete or is another release.\
Its options:

| Option | What it does |
|---|---|
| `--sumo-home SUMO_HOME` | The installation to check. Otherwise `SUMO_HOME`, then the repository's own build when run from a checkout, then the `PATH`. |
| `--expect-version EXPECT_VERSION` | The release the installation must be, such as `1.27.0`. |
| `--allow-version-mismatch` | Warn instead of refusing when `--expect-version` does not match. |

Check your own SUMO the same way before you use it for `sumo-gui`: `carla-check-sumo --sumo-home <your SUMO> --expect-version 1.27.0`.

### Which server build you are talking to

With the server running, ask it what it was built from.\
`get_build_identity` never raises for an old server or one it cannot reach; it says so in `available` and `reason` instead.

```python
import carlanet as carla

client = carla.Client("127.0.0.1", 2000)
client.set_timeout(30.0)
print("carlanet", carla.__version__)
print("server", client.get_server_version())
print(client.get_build_identity())
```

The identity is a dictionary.\
A capture run records it in its result, under `producer.server`.\
This one came from a run on a development editor build:

```json
{
  "available": true,
  "release": "0.10.0",
  "world_interface": "1.0",
  "build": "editor",
  "configuration": "Development",
  "carla_commit": "90c0f69d29a6c5b8fd593d51ce9f82f4d165551a",
  "content_commit": "unknown",
  "engine_commit": "unknown",
  "commits_from": "compiled"
}
```

| Key | Meaning |
|---|---|
| `available` | False for a server built before the call, or one that could not be reached. A `reason` then says why. |
| `release` | The release compiled into the server. |
| `world_interface` | What a separately delivered world can rely on this server providing, as `Major.Minor`. |
| `build` | `package` for a cooked server, `editor` for the editor. |
| `configuration` | The build configuration, such as `Development` or `Shipping`. |
| `carla_commit`, `content_commit`, `engine_commit` | The commits the build came from. A value the server cannot know is `unknown`. |
| `commits_from` | Where the commits were read: `version_file` (the package's `VERSION`), `compiled`, or `none`. |

The release of the client is `carlanet.__version__`.\
In a build that is not the tagged release it ends in the short CARLA commit, such as `0.10.0+g31bc08de3`.

## Where things are written

An installed command has no repository to write into, so it uses the folder you run it from instead.\
`carlacontrol.ToolLayout` decides this.\
It looks at where `carlacontrol` itself was imported from, not at the current folder: a copy imported from a checkout's `CarlaControl/src/` uses the checkout's folders, and an installed copy uses the current folder.

| What | Installed | From a checkout |
|---|---|---|
| World packages (`.cwp`) | `world-packages/` | `Build/world-packages/` |
| Compiled scenarios, found by id | `scenarios/` | `Build/scenarios/` |
| Captures | `captures/` | `Build/captures/` |
| Run results and their records | `runs/` | `Build/runs/` |
| `carla-sctmv` recordings | `SCTMV_recordings/` | `Build/SCTMV_recordings/` |
| Inputs, such as OpenStreetMap extracts | the current folder, or named by argument | `Import/` |
| Vehicle catalog | the copy installed with `carlacontrol` | `CarlaControl/catalogue/` |
| Schemas | the copies installed with `carlacontrol` | `CarlaControl/schemas/` |
| SUMO toolchain | whatever `SUMO_HOME`, `CARLA_NETCONVERT` and `PROJ_LIB` name, which `carla-env` sets | `Build/sumo-install/` |

So run the commands from one folder, usually the distribution's folder, or give a site profile and explicit paths.\
A command run from another folder looks for its inputs, and writes its outputs, there.

## Running the commands from a checkout

From a source checkout, each command is a script you run with Python, with the checkout's sources ahead of any installed copy.\
Most are under `CarlaControl/scripts/`, but the names differ from the command names:

| Command | From a checkout |
|---|---|
| `carla-capture` | `python CarlaControl/scripts/run_capture.py` |
| `carla-drive` | `python CarlaNet/python/run_sumo_drive.py` |
| `carla-camera-follower` | `python CarlaControl/scripts/run_camera_follower.py` |
| `carla-free-camera` | `python CarlaControl/scripts/run_free_move_camera.py` |
| `carla-validate` | `python CarlaControl/scripts/validate_capture.py` |
| `carla-audit-sidecars` | `python CarlaControl/scripts/audit_truth_sidecars.py` |
| `carla-check-sumo` | `python CarlaControl/scripts/test_sumo_toolchain.py` |
| `carla-build-world` | `python CarlaControl/scripts/build_world.py` |
| `carla-compile-scenario` | `python CarlaControl/scripts/compile_scenario.py` |
| `carla-sctmv` | `python CarlaControl/scripts/run_SCTMV.py` |

These pages use the command names.\
From a checkout, `carlanet` also needs its .NET assemblies: build them with `CarlaNet/python/build_wheel.ps1` or `build_wheel.sh`, or set `CARLANET_PUBLISH_DIR` to a folder `CarlaNet.Python` was published to.
