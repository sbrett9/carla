# CARLA DIST_VERSION distribution for DIST_PLATFORM

This bundle holds a cooked CARLA server, the `carla-*` commands that drive it and the SUMO toolchain they use.\
It runs from the folder you unpack it into.\
Start with [Getting started](docs/Guides/Getting_Started.md).

## Two modes of operation

The bundle works in two modes.\
In the primary mode, SUMO drives the vehicles.\
In the secondary mode, CARLA's own traffic manager drives them.\
Each mode has its own commands.\
Every command takes `--help`.

### The SUMO-driven mode

SUMO moves every vehicle.\
CARLA draws each vehicle where SUMO puts it.\
You describe the traffic in a scenario: the vehicles, their routes and their departure times.\
Use this mode for captures of planned traffic, with truth files that record where every vehicle was.

| Command | What it does |
|---|---|
| `carla-build-world` | Builds a world on the running server from an OpenStreetMap extract. Writes its world package (`.cwp`), one generated world in one file. |
| `carla-compile-scenario` | Compiles a scenario against its world package. |
| `carla-drive` | Drives a world from a compiled scenario. Records while it drives. |
| `carla-capture` | Captures a window of a compiled scenario. Writes a still and a truth file for every capture of every camera. |
| `carla-camera-follower` | Shows one camera of your own on a running world, live. |
| `carla-free-camera` | Flies a camera of your own around a running world, live. |
| `carla-cot-telemetry` | Makes Cursor-on-Target telemetry for every vehicle in a SUMO scenario. |
| `carla-validate` | Checks the files the tools write against their schemas. |
| `carla-audit-sidecars` | Checks the truth files of a capture. |
| `carla-diff-manifests` | Checks that two runs of one scenario name the same supervision rows. |
| `carla-check-label-leaks` | Checks that a telemetry dataset does not give away which vehicles a scenario planted. |
| `carla-publish-reference-set` | Refreshes the areas of interest and the place index of a world package. |

A first scenario goes in this order.\
Build a world with `carla-build-world`.\
Compile the scenario against its world package with `carla-compile-scenario`.\
Then drive it with `carla-drive` or capture it with `carla-capture`.\
`Scenarios/` holds ready examples to start from.

### The traffic-manager mode

This mode has one command, `carla-sctmv`, the `run_SCTMV.py` program.\
It builds a world on the running server from an OpenStreetMap extract.\
Then you fly a camera through the world, watch the traffic and record what the camera sees.\
CARLA's own traffic manager drives the vehicles.\
The mode needs no compiled scenario.\
Use it to explore a world by hand.

```
carla-sctmv --osm osm/Lakeview_Carson.osm
```

### Shared by both modes

Both modes build worlds with the bundled SUMO toolchain.\
`carla-check-sumo` says which SUMO installation the commands use.\
It also checks that the installation is complete.

## What ships

| Item | What it is |
|---|---|
| `CarlaServer/` | The cooked CARLA server. |
| `wheels/` | The `carlanet` and `carlacontrol` Python wheels. `carlacontrol` installs the `carla-*` commands. |
| `setup-venv.DIST_EXT` | Makes the Python virtual environment. Run it once. |
| `carla-env.DIST_EXT` | The environment step. Run it in each new terminal. |
| `run-server.DIST_EXT` | Starts the server with no window. |
| `tools/sumo/` | SUMO DIST_SUMO: `netconvert`, `sumo` and `duarouter`, with the PROJ data. There is no `sumo-gui`. |
| `catalogue/` | The measured vehicle catalog that a scenario names. |
| `osm/` | Example OpenStreetMap extracts to build worlds from. |
| `Scenarios/` | Ready example scenarios. |
| `docs/` | The user guides and the reference pages. |
| `skills/` | The scenario-authoring skills. |
| `world-tools/` | `PackageWorld` and `InstallWorld`, for worlds made elsewhere. |
| `VERSION` | Which build of CARLA this is. |
| `MANIFEST.md` and `licenses/` | Every component, where it came from, its license and the license texts. |

## What the machine needs

- A 64-bit machine with a GPU and current graphics drivers.\
  It runs DIST_SYSTEM.\
  The server renders even with no window.
- Python 3.11 or newer, on the `PATH`.
- The .NET 10 runtime.\
  `carlanet` runs .NET assemblies.
- A Cesium ion access token.\
  Every user needs one, in both modes.\
  The server streams terrain and imagery from Cesium ion with it.\
  Set `CESIUM_ION_TOKEN` to the token in the terminal that starts the server.\
  Set it in the terminal that runs the commands too.
- Your own SUMO DIST_SUMO, only to watch a drive in `sumo-gui`.\
  The bundled SUMO has no `sumo-gui`.\
  Give the folder of your SUMO to `carla-drive --sumo-gui` with `--sumo-home <folder>`.\
  Or set `CARLANET_SUMO_HOME` to that folder.

## Setting up

Unpack the bundle.\
Open a DIST_SHELL terminal in its folder.\
Run these steps:

```
./setup-venv.DIST_EXT     # once
. ./carla-env.DIST_EXT    # in each new terminal
./run-server.DIST_EXT     # in a terminal of its own
```

- `setup-venv`\
  Makes a Python virtual environment, `venv/`, in the bundle's folder.\
  Installs both wheels into it, with the `carla-*` commands.\
  Run it once.
- `carla-env`\
  The environment step.\
  Activates `venv/` to put the `carla-*` commands on the `PATH`.\
  Points every command at the bundled SUMO toolchain through `SUMO_HOME`, `CARLA_NETCONVERT`, `PROJ_LIB` and `PROJ_DATA`.\
  Run it in each new terminal, with a dot and a space in front.\
  The dot lets it change the terminal you are in.
- `run-server`\
  Starts the server with no window.\
  A first argument that does not start with `-` names the map to start in.\
  The server answers on port 2000.

A command reads and writes under the folder you run it from: `world-packages/`, `scenarios/`, `captures/` and `runs/`.\
A site profile or an option can name other folders.\
[Getting started](docs/Guides/Getting_Started.md) explains both.

## Guides

The guides are in `docs/Guides/`.\
Start with [Getting started](docs/Guides/Getting_Started.md).\
The other guides follow on from it:

- [Building a world](docs/Guides/Building_A_World.md)
- [Writing a scenario](docs/Guides/Writing_A_Scenario.md)
- [Running a capture](docs/Guides/Running_A_Capture.md)
- [Cameras and missions](docs/Guides/Cameras_And_Missions.md)
- [Making a level](docs/Guides/Making_A_Level.md)

The rest of `docs/` is reference:

- `docs/Schemas/`\
  One page for each kind of file the tools write and read.
- `docs/EPOL/`\
  What a capture folder holds, for people who build pattern-of-life models from captures.
- `docs/Tracking/`\
  How to match the tracks of your own detector to the truth files.
- `docs/Skills/`\
  What the authoring skills in `skills/` are.

## Worlds made elsewhere

A world can come from another machine as a level package, the `.zip` that `PackageWorld` makes.\
`world-tools/` holds the two scripts for it:

- `InstallWorld.DIST_EXT`\
  Installs a level package into this bundle.\
  It finds this bundle by itself from `world-tools/`.
- `PackageWorld.DIST_EXT`\
  Makes a level package from a world.\
  It needs the Unreal editor and a CARLA checkout at the commit of this bundle.

[Making a level](docs/Guides/Making_A_Level.md) covers both scripts and their options.

## Licenses

`MANIFEST.md` lists every component in the bundle, where it came from and its license.\
The license texts are in `licenses/`.\
The packaging script writes both from the files it copied into this bundle.
