# Making a level

This guide is for the Unreal tech artist who makes the level a scenario takes place in. You start from
a world package (`.cwp`) that the world build wrote. You import it into a level in the editor, clean up
its road meshes and the detail around them, and ship the level as add-on content (DLC). Anyone who has
the matching CARLA distribution can then install the level and load it, without being sent a new
distribution.

The steps, in order:

1. Set up the engine, Visual Studio and a CARLA checkout that match the target distribution.
2. Get the world package.
3. Import it into a level.
4. Clean up the level.
5. Export it as a plugin, so packaged builds can load it.
6. Mark it for separate delivery.
7. Cook it as DLC with `PackageWorld`.
8. Deliver it, and install it with `InstallWorld`.
9. Load it and check that it loaded.

The commands are for PowerShell on Windows. Run them from the top of the CARLA checkout unless a step
says otherwise. Each script prints its options with `-Help`. For `PackageWorld.ps1` and
`InstallWorld.ps1`, run `Get-Help <script> -Detailed` instead: their `-Help` first asks for their
required option.

**On Linux.** Every script in this guide except `OpenCarlaEditor.ps1` has a Linux twin with the same
name ending in `.sh`. `CarlaSetup.sh` is at the top of the checkout, and the others are in
`Scripts/Linux/`. A Linux distribution has `run-server.sh` and the `.sh` world tools. The Linux scripts
spell their options in lower case with two dashes, such as `--world` and `--distribution`. A level
cooked on Windows goes into a Windows distribution, and a level cooked on Linux goes into a Linux one.

## Words used here

- **Distribution**: the CARLA bundle made by `Scripts/Windows/MakeDistribution.ps1`. It is a folder named
  `Carla-<version>-Win64-<configuration>`, such as `Carla-0.10.0-Win64-Development`. It holds the cooked
  server in `CarlaServer\`, the world tools in `world-tools\`, `run-server.ps1`, and a `VERSION` file.
- **World package**: one generated world in one `.cwp` file, written by the world build.
- **Exported world**: the level and its assets, copied into a content-only plugin under
  `Unreal\CarlaUnreal\Plugins\GeneratedWorlds\<World>\`. This is the copy that ships.
- **Level package**: the `.zip` that `PackageWorld` makes. It holds the cooked plugin and a `world.json`
  that says what the level needs.

`<World>` stands for the world's name. The examples use `Arapahoe_I25`.

## 1. What you need

### The target distribution

You need the distribution your level is for, extracted, with its folder name unchanged.
`PackageWorld` reads three things from it:

- its `VERSION` file, which names the commits it was built from;
- `CarlaServer\CarlaUnreal\AssetRegistry.bin`, the list of everything the distribution already holds;
- its folder name, which gives the build configuration (`Development`, `Shipping` or `Debug`).

A `VERSION` file looks like this:

```
Carla version:          0.10.0
World interface:        1.0
Carla git hash:         025443a834b1f3c9d1e2a4b5c6d7e8f901234567
Content git hash:       6bcd042a91a54d9a2f2f002869fbf1c75f3768f4
UnrealEngine git hash:  e5e266de195a2400a6a74180402fb1a3e8f75472
```

The steps below use its three commits. `World interface` is explained in section 8.

### The engine

Use the team's engine distribution: an Unreal Engine Installed Build packaged by
`Scripts/Windows/MakeEngineDistribution.ps1`. You do not build the engine from source.

1. Get the archive. It is named `UnrealEngine-<engine version>-<branch>-<commit>-Win64.zip` and comes
   with a `.sha256` file and a `.metadata.txt` file.
2. Check that it is the right engine. The `<commit>` in the name, and `source_commit` in the
   `.metadata.txt` file, are the short form of the engine commit. They should match the start of the
   distribution's `UnrealEngine git hash`.
3. Check that the download is whole. The output of `Get-FileHash -Algorithm SHA256 <archive>` should
   match the hash in the `.sha256` file.
4. Extract it, and set `CARLA_UNREAL_ENGINE_PATH` to the extracted folder, the one that holds `Engine\`:

   ```powershell
   [Environment]::SetEnvironmentVariable('CARLA_UNREAL_ENGINE_PATH', 'D:\UnrealEngine-5.7.4-<branch>-<commit>-Win64', 'User')
   ```

   Open a new PowerShell window afterwards, so it sees the variable. Each script also takes
   `-UnrealEngineRoot <folder>` instead.

### Visual Studio 2022

An Installed Build does not include a compiler on Windows. CARLA's editor modules are compiled with
your own Visual Studio. Install Visual Studio 2022 with:

- the "Desktop development with C++" workload;
- the MSVC v143 build tools, version 14.44. `CarlaSetup.ps1` and `BuildCarla.ps1` stop when this
  version is missing;
- a Windows SDK.

You also need Git, and CMake 3.21 or later, on `PATH`. The first step of `CarlaSetup.ps1` installs
Ninja and Python when they are missing.

### A checkout at the distribution's commits

`PackageWorld` cooks your checkout's content and leaves out everything the distribution already holds.
Both must come from one build, so check out **the exact commit the distribution was built from**, and
the content repository at its matching commit. Both commits are in the distribution's `VERSION` file.

```powershell
git clone <CARLA repository URL> carla
cd carla
git checkout <Carla git hash>
.\CarlaSetup.ps1
git -C Unreal\CarlaUnreal\Content\Carla checkout <Content git hash>
.\Scripts\Windows\BuildCarla.ps1 -SkipCarlaNet -SkipCarlaControl
```

What these steps do:

- `CarlaSetup.ps1` sets up the checkout. It installs the prerequisites, clones the content repository
  into `Unreal\CarlaUnreal\Content\Carla` (on its `ue5-dev` branch, which is why you check out the
  content commit next), builds the SUMO toolchain, builds Cesium for Unreal from source, and then
  configures and builds CARLA. The first run takes a long time. `-SkipPrerequisites` skips the
  prerequisites on a machine that already has them. `-Vs 2022` picks Visual Studio 2022 when more than
  one version is installed.
- `BuildCarla.ps1` compiles the CARLA editor (`CarlaUnrealEditor`). `-SkipCarlaNet` and
  `-SkipCarlaControl` skip the Python client packages, which you do not need to make a level.

`PackageWorld` refuses a checkout that is not at the distribution's CARLA commit. It does not check the
content or engine commits, so check those yourself. These two commands should print the distribution's
`Carla git hash` and `Content git hash`:

```powershell
git rev-parse HEAD
git -C Unreal\CarlaUnreal\Content\Carla rev-parse HEAD
```

Open the editor with `.\Scripts\Windows\OpenCarlaEditor.ps1`. It starts the editor on
`Unreal\CarlaUnreal\CarlaUnreal.uproject` with the engine `CARLA_UNREAL_ENGINE_PATH` names.

## 2. A world package to start from

You start from a world package: one `.cwp` file. [Building_A_World.md](Building_A_World.md) explains
how to build one. Its format is described in [World package](../Schemas/World_Package.md).

The importer reads three of its entries: `world.json` (where the world sits on the Earth),
`map.xodr` (the road network) and `bareearth.bin` (the bare-earth grids, when the world has them).

- **The file name is the world's name.** The importer names the level, the plugin and the map path
  `/<World>/Maps/<World>` after the file name, without `.cwp`. Do not rename the file.
- **Keep the `.cwp`.** Scenarios are compiled against it, and a SUMO drive or a capture checks the
  loaded level against it before it starts. Whoever runs scenarios on your level needs this same file.
- **Put it where the importer looks first:** `Build\world-packages\` in the checkout. The importer
  fills in the first package it finds there.
- If you have the `carla-*` commands installed, `carla-validate <World>.cwp` checks the package before
  you import it.

## 3. Importing it into a level

### Open the World Package Importer

The importer is an editor utility widget in the CarlaTools plugin.

1. In the Content Browser, open **Settings** and turn on **Show Plugin Content**.
2. Go to **Plugins > CarlaTools Content > GeneratedWorld**.
3. Right-click **WBP_WorldPackageImporter** and choose **Run Editor Utility Widget**.

The panel's code is in `Unreal/CarlaUnreal/Plugins/CarlaTools/Source/CarlaTools/`, in
`Private/GeneratedWorld/WorldPackageImporterWidget.cpp` and `WorldPackageImporter.cpp`.

### The panel

| Field | What it does |
|---|---|
| World package | The `.cwp` to import. **Choose...** opens a file dialog. Choose a `.cwp` file. |
| Replace a level built from a different source (discards edits made to it) | Off by default. With it off, the importer refuses to overwrite a level of the same name that was built from a different OpenStreetMap extract. Re-importing a package built from the same extract always goes ahead, with or without it. |
| Make this world available to packaged builds (writes it to Plugins/GeneratedWorlds) | On by default. After the import, it exports the level as a plugin (section 5). Leave it on. |
| Cesium ion token | Optional. **Leave it empty for a level you will ship.** A token typed here is saved in the level's imagery layers, so it is in the exported plugin and in the DLC, where anyone who installs the level can read it. With the field empty, the editor streams imagery with the project's own token, and a server uses the `CESIUM_ION_TOKEN` environment variable. |

Click **Import**. The line at the bottom of the panel reports the result, such as
`Imported to /Game/Carla/Maps/Generated/Arapahoe_I25. ... Packaged builds will include it (...)`.
The level is written but not opened. Open it from the Content Browser.

The importer refuses to run:

- on the level the editor has open. Open a different level and import again;
- while a play session is running;
- on a `.cwp` that is missing an entry it needs, or that a newer release wrote;
- over a level built from a different OpenStreetMap extract, unless you tick the replace box.

### What the import creates

| What | Where |
|---|---|
| The level | `/Game/Carla/Maps/Generated/<World>` |
| The world settings | `/Game/Carla/Maps/Generated/<World>_WorldSettings`: where the world is on the Earth (origin latitude, longitude and height, and the projection), how the road surface was matched to the bare earth, the imagery layers, the extent of the area traffic uses, and where the world came from |
| The bare-earth field | `/Game/Carla/Maps/Generated/<World>_BareEarthField`: the bare-earth grids. Only for a world whose road surface was fitted to the imagery point by point |
| The road network | `/Game/Carla/Maps/Generated/<World>_RoadNetwork`: the OpenDRIVE document, as an asset |
| The road network file | `Unreal\CarlaUnreal\Content\Carla\Maps\Generated\OpenDrive\<World>.xodr` |
| The road meshes | `/Game/Carla/Static/Road/<World>/SM_RoadSurface_<n>` for driving lanes and junctions, with the material `MI_Road_Asphalt_A`; `/Game/Carla/Static/SideWalk/<World>/SM_Sidewalk_<n>` for sidewalks, with `MI_CurbDirty01`, when the road network has sidewalks |

The level is a copy of `/Game/Carla/Maps/OpenDriveMap`, so it starts with that map's OpenDRIVE
generator, player start and lighting. It has no large-map manager. The importer adds:

- **GeneratedWorldInitializer**, an actor that points at the world settings. Each time the level plays,
  it applies them: the georeference, the imagery layers, the collision surface and the bare-earth data.
  A level loaded by name needs no client to set it up.
- **The road pieces**, as Static Mesh Actors labeled with their mesh names and tagged `road`. Only
  driving lanes, junctions and sidewalks are baked. Other lane types in the road network, such as bike
  lanes, get no mesh.
- **The Cesium georeference and imagery tilesets**, set up from the settings, so the globe streams in
  the editor. The bare-earth ground layer is not drawn in the simulation, so the editor hides it when
  the level opens. Use the eye icon in the Outliner to show it while you work.
- **GeneratedWorldExposure**, an unbound post process volume with a fixed daylight exposure (ISO 100,
  1/125 s, f/16). Without it the level looks white in the editor. Camera sensors set their own
  exposure, so this volume does not change captured images.

Traffic lights and signs are not in the level. They are placed from the road network when the level
plays.

## 4. Cleaning up

Work in the imported level, `/Game/Carla/Maps/Generated/<World>`. The exported plugin is a copy that
is replaced each time you export (section 5), so do not edit the plugin's copy of the level.

### Safe to edit

- **The road meshes.** Fix seams, gaps, holes, normals, UVs, LODs and materials on the
  `SM_RoadSurface_<n>` and `SM_Sidewalk_<n>` meshes, in `/Game/Carla/Static/Road/<World>/` and
  `/Game/Carla/Static/SideWalk/<World>/`. Keep the surface where it is. Vehicles are placed from the
  road network and the bare-earth data, not from these meshes. A surface that is raised, lowered or
  moved sideways leaves vehicles floating above it or sunk into it.
- **The road materials.** Another stock CARLA material is fine. A new material must be saved inside
  the world's plugin (see below).
- **Detail around the roads**: props, vegetation, buildings, barriers, decals. Keep it off the
  driving lanes. Vehicles follow the road network, so anything on a lane ends up inside the vehicles
  that drive through it.
- **GeneratedWorldExposure**, to change how the level looks in the editor. It does not change camera
  images.

### Where to save new assets

Everything the level uses must be in one of two places when it is cooked: inside the world's plugin,
or already in the distribution. The cook fails on anything else (section 7).

- The export (section 5) copies into the plugin only the static meshes under `/Game` that Static Mesh
  Actors in the level use. It does not copy materials, textures, Blueprints, foliage types or any other
  kind of asset.
- So save every new asset you make inside the world's plugin, in a top-level folder of your own, such
  as `/<World>/Detail/`. The plugin's content appears under **Plugins > `<World>` Content** in the
  Content Browser once the world has been exported.
- Do not save anything under `/<World>/Carla/`. Each export deletes that folder and writes it again.
- Stock CARLA content that the distribution already holds can be used where it is.

**The folder decides a mesh's segmentation label.** CARLA labels a mesh by the fourth folder of its
path. `/Game/Carla/Static/Road/<World>/SM_RoadSurface_0` is labeled `Road`, and so is its exported copy
`/<World>/Carla/Static/Road/<World>/SM_RoadSurface_0`. Keep the road pieces in their `Road` and
`SideWalk` folders. To give a new mesh a label, put the label in the fourth folder of its path, for
example `/<World>/Detail/Static/Vegetation/SM_Hedge_01` for `Vegetation`. The folder names CARLA
recognizes are in `ATagger::GetLabelByFolderName`, in
`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Game/Tagger.cpp`. A mesh whose fourth folder is not one
of those names has no label.

### Must not change

A SUMO drive or a capture compares the loaded level with the world's `.cwp` before it starts. It
refuses to start when any of the following differ, and names what differs.

- **The georeference.** Leave the Datum fields of `<World>_WorldSettings` alone: `OriginLatitude`,
  `OriginLongitude`, `OriginHeightMeters` and `GeoReferenceString`. Leave the Cesium georeference actor
  alone too. The initializer sets it from the settings each time the level plays. The run allows the
  origin to differ from the `.cwp`'s by no more than about a millimeter. Do not move or rotate the
  level's content as a whole either: everything in it is placed in the coordinates the road network
  uses.
- **The road network the scenarios bind to.** Leave `<World>_RoadNetwork` and
  `OpenDrive\<World>.xodr` alone. Scenarios are compiled against the SUMO network that was built with
  this road network. The run compares the OpenDRIVE the server serves with the `.cwp`'s `map.xodr`.
- **The bare-earth data.** Leave `<World>_BareEarthField` alone, and the Surface fields of
  `<World>_WorldSettings`: `HeightAlignMode`, `DrapeActive`, `HeightAlignOffsetMeters` and
  `OffsetField`. The run compares the grids' size, position and contents with the `.cwp`'s
  `bareearth.bin`.

Leave these alone as well, because the level does not work without them:

- the **GeneratedWorldInitializer** actor and its `Settings` reference;
- the **OpenDRIVE generator** actor (class `OpenDriveGenerator`). It reads the road network when the
  level plays and places the traffic lights;
- the `road` tag on the road pieces. The server finds the road surface by this tag;
- the names: the level, the `<World>_WorldSettings`, `<World>_RoadNetwork` and `<World>_BareEarthField`
  assets, and the `<World>` folders. The export and the server find them by name.

Do not add a large-map manager. It turns on origin rebasing, which a generated world does not support.

The imagery layers in `<World>_WorldSettings` are applied again each time the level plays. Changes
made to the tileset actors in the level do not carry into a run.

### Re-importing loses your work

**Re-importing a world replaces its whole road surface, so every hand fix is lost.** The importer
deletes the road meshes and builds new ones from the `.cwp`. It also deletes the level and makes it
again from the template, so everything you placed in the level is lost too. With "Make this world
available to packaged builds" ticked, the export that follows replaces the plugin's copy as well.

Re-importing the same world goes ahead without asking. The replace box only guards against a package
built from a different OpenStreetMap extract. A `.cwp` rebuilt from the same extract is not stopped.

Neither the imported level nor the exported plugin is tracked in git. Keep your own backup of these,
and copy them before any re-import:

- in `Unreal\CarlaUnreal\Content\Carla\Maps\Generated\`, the `<World>` files and
  `OpenDrive\<World>.xodr`;
- `Unreal\CarlaUnreal\Content\Carla\Static\Road\<World>\` and
  `Unreal\CarlaUnreal\Content\Carla\Static\SideWalk\<World>\`;
- `Unreal\CarlaUnreal\Plugins\GeneratedWorlds\<World>\`, which holds your own assets.

## 5. Exporting it so packaged builds can load it

A world reaches a packaged build only as a plugin of its own. "Make this world available to packaged
builds" exports the level as a content-only plugin under
`Unreal/CarlaUnreal/Plugins/GeneratedWorlds/<World>/`. The exporter's code is in
`Private/GeneratedWorld/GeneratedLevelExporter.cpp` in the CarlaTools plugin.

The export copies the level and its assets into the plugin. The imported level stays where it is, for
you to keep editing.

```
Unreal\CarlaUnreal\Plugins\GeneratedWorlds\<World>\
  <World>.uplugin
  Content\
    <World>_WorldSettings.uasset
    <World>_RoadNetwork.uasset
    <World>_BareEarthField.uasset           only for a world with bare-earth grids
    Maps\<World>.umap
    Maps\OpenDrive\<World>.xodr
    Carla\Static\Road\<World>\SM_RoadSurface_<n>.uasset
    Carla\Static\SideWalk\<World>\SM_Sidewalk_<n>.uasset     only when the world has sidewalks
```

- The plugin's content is mounted at `/<World>/`, so the level becomes `/<World>/Maps/<World>`. That is
  the name a server loads it by.
- The export points the copied settings at the copied road network and field, and points the copied
  level's road pieces at the copied meshes.
- The plugin is marked as explicitly loaded. CARLA mounts every exported world when the editor or a
  server starts.
- The world's name is fixed once it is exported. Every cooked reference names `/<World>/`.

### Exporting again after you edit

The panel exports only as part of an import, and an import replaces your work (section 4). To export
the edited level without importing, use the editor's Python console:

1. Save all your work.
2. Open a level that is neither the imported level nor the plugin's copy, for example with
   **File > New Level > Empty Level**. The export replaces the plugin's copy of the level, which must not
   be the level the editor has open.
3. In the **Output Log**, set the command box to **Python** and run this line, with your world's name:

   ```python
   import unreal; r = unreal.GeneratedLevelExporter.export_level_as_plugin("/Game/Carla/Maps/Generated/Arapahoe_I25", "Arapahoe_I25"); print(r.succeeded, r.assets_exported, r.failure_reason)
   ```

   On success the log shows a line starting `[GeneratedLevelExporter] exported`.

Each export replaces the plugin's level, settings, road network and field, and deletes and rewrites
`/<World>/Carla/`. It leaves other files in the plugin folder alone, such as `DeliverSeparately.txt`
and your own folders.

## 6. Deciding how it ships

A generated world ships in one of two ways, never both:

- **inside the base distribution.** Whoever makes the distribution cooks it with the rest of CARLA;
- **on its own, as DLC.** You cook it by itself against an existing distribution, and it is installed
  into that distribution.

A level for others to install ships on its own. Mark it by creating an empty file named
`DeliverSeparately.txt` in the world's plugin folder:

```powershell
New-Item -ItemType File Unreal\CarlaUnreal\Plugins\GeneratedWorlds\Arapahoe_I25\DeliverSeparately.txt
```

Only the file's presence counts, not its contents.

Why it cannot be both, from `Unreal/Package/CookGeneratedWorlds.cmake.in`:

- When a base distribution is cooked, every exported world in `Plugins/GeneratedWorlds/` is cooked into
  it, except a world with a `DeliverSeparately.txt`.
- A DLC cook leaves out everything the base already holds. For a world that is already in the base,
  that is everything, so the cook produces a plugin with no level and no assets. The cook still reports
  success, and the empty level would fail at whoever installed it.
- So the base cook skips a marked world, and `PackageWorld` refuses an unmarked one.

**The marker is not tracked in git.** The whole `Unreal/CarlaUnreal/Plugins/GeneratedWorlds/` folder is
in `.gitignore`, so the marker, like the exported world, stays on the machine where it was made. A
fresh checkout has neither. If you give the exported world to someone who makes distributions, give
them the marker too, or their base cook will include the world.

A distribution that already contains a world of the same name cannot take your world as DLC. The
installed worlds are the folders in
`<distribution>\CarlaServer\CarlaUnreal\Plugins\GeneratedWorlds\`.

## 7. Cooking it as DLC with `PackageWorld`

`Scripts/Windows/PackageWorld.ps1` cooks one exported world on its own and packages it as one `.zip` of
about 100 MB. The recipient does not need the whole distribution again, which is over 30 GB. Save your
work first. The cook reads the files on disk.

```powershell
.\Scripts\Windows\PackageWorld.ps1 -World Arapahoe_I25 -Distribution D:\Carla-0.10.0-Win64-Development
```

A distribution also carries `PackageWorld.ps1` in its `world-tools\` folder. Run from the distribution's
folder, it needs to be told where the checkout is:

```powershell
.\world-tools\PackageWorld.ps1 -World Arapahoe_I25 -CarlaRoot D:\carla -Distribution .
```

### The base release record

A DLC cook needs a list of everything the base distribution already holds, so that it can leave all of
that out and cook only what the world adds. That list is the base release record.
`PackageWorld -Distribution <folder>` takes it from the distribution:

1. It reads the CARLA commit from the distribution's `VERSION` file.
2. It refuses unless the checkout is at that commit.
3. It names the release the way the base cook names it, by the short form of that commit.
4. It copies the distribution's `CarlaServer\CarlaUnreal\AssetRegistry.bin` to
   `Unreal\CarlaUnreal\Releases\<release>\Windows\AssetRegistry.bin` in the checkout. If the same file is
   already there, it is used as it is.
5. It takes the build configuration from the distribution's folder name, `Carla-<version>-Win64-<configuration>`.
   If you renamed the folder, pass `-Config` yourself: without it the configuration is `Development`.

It then cooks the world as DLC against that release, checks that the cook produced a level and assets,
writes `world.json`, and makes the zip.

### Options

From the script's help text:

| Option | Meaning |
|---|---|
| `-World <name>` | Exported world to package (required). |
| `-BasedOnRelease <name>` | Release to cook against (default: current short Carla commit). |
| `-Distribution <folder>` | Record the release from this CARLA distribution, then cook against it. The checkout must be at the distribution's CARLA commit. |
| `-CarlaRoot <path>` | The CARLA checkout (default: the one this script is in; required when run from a distribution's world-tools folder). |
| `-OutputDirectory <path>` | Where to write the .zip (default: `Build\WorldPackages`). |
| `-Config <cfg>` | Development (default) \| Shipping \| Debug. |
| `-SkipCook` | Package an existing cook without re-cooking. |
| `-UnrealEngineRoot <path>` | Engine root (default: `CARLA_UNREAL_ENGINE_PATH` or `<repo-parent>\UE_5_7_4`). |

With `-Distribution`, you do not need `-BasedOnRelease` or `-Config`.

`PackageWorld.ps1` cooks for Windows. For a Linux distribution, run `PackageWorld.sh` on Linux.

### When it refuses

Each refusal says what is wrong. The ones you are likely to see:

| The message starts with | What it means | What to do |
|---|---|---|
| `No exported world named '<World>'` | There is no plugin folder for the world. | Export it (section 5). |
| `'<World>' has no <World>.uplugin` | The export did not finish. | Export it again. |
| `'<World>' is not marked for separate delivery` | There is no `DeliverSeparately.txt`. | Create it (section 6). The message also describes re-cooking a base; that applies only to whoever makes distributions. |
| `<folder> has no VERSION file` | `-Distribution` does not name the distribution's top folder. | Name the folder that holds `VERSION` and `CarlaServer\`. |
| `... is a Linux distribution` | | Use `PackageWorld.sh` on Linux. |
| `... carries no CarlaServer\CarlaUnreal\AssetRegistry.bin` | The distribution has no list of what it holds. | No world can be cooked against it. Ask for a complete distribution. |
| `-Config <x>, but ... is a <y> distribution` | `-Config` contradicts the folder name. | Leave out `-Config`. |
| `This checkout is not at the commit the distribution was built from` | | Check out the commit it names, and the content commit too (section 1). |
| `Release '<release>' is already recorded here, from another build` | `Releases\<release>\Windows\` already holds a record from another build: another distribution, or a base cooked on this machine. | Delete the folder it names, and run again. |
| `Release '<release>' holds a registry from a cook on this machine` | `Releases\<release>\Windows\` holds part of a record from a base cooked on this machine. | Delete the folder it names, and run again. |
| `Unreal Engine root not found` | | Set `CARLA_UNREAL_ENGINE_PATH`, or pass `-UnrealEngineRoot`. |
| `Cook failed (exit <n>)` | The cook itself failed. | Read the cook's output above it. If it says content is `being referenced by DLC`, the level uses an asset that is neither in the plugin nor in the distribution. Move that asset into the plugin (section 4), export again, and cook again. |
| `The cook produced no content for '<World>'` | Every part of the world is already in the distribution. | The distribution already contains this world. It cannot also take it as DLC (section 6). |

### What comes out

`Build\WorldPackages\<World>.zip`, or the folder `-OutputDirectory` names:

```
<World>.zip
  world.json
  <World>\
    <World>.uplugin
    AssetRegistry.bin
    Content\...            the cooked level (.umap) and assets (.uasset, .uexp, .ubulk)
```

`world.json` says what the world is and what it needs from the distribution it is installed into: the
world's name, the map to load, the world interface version, the configuration and the platform. Its
fields are described in [Level package manifest](../Schemas/Level_Package_Manifest.md).

At the end, `PackageWorld` prints the file, its size, what it needs, and the command to install it:

```
Packaged Arapahoe_I25
  file    : ...\Build\WorldPackages\Arapahoe_I25.zip
  size    : 115.4 MB
  needs   : world interface 1.x, minor 0 or later; Development, Win64
```

## 8. Delivering and installing it with `InstallWorld`

### Delivering

Send two files:

- the level package, `<World>.zip`;
- the world package, `<World>.cwp`. The level package holds only the level. Scenarios are compiled
  against the `.cwp`, and a drive or a capture checks the level against it.

Any way of copying files works. To check that the zip arrived whole, compare
`Get-FileHash -Algorithm SHA256 <World>.zip` on both machines.

### Installing

From the distribution's folder:

```powershell
.\world-tools\InstallWorld.ps1 -Package D:\Downloads\Arapahoe_I25.zip
```

Run from its `world-tools\` folder, `InstallWorld` installs into that distribution. From anywhere else,
name the distribution with `-Into`:

```powershell
.\Scripts\Windows\InstallWorld.ps1 -Package Build\WorldPackages\Arapahoe_I25.zip -Into D:\Carla-0.10.0-Win64-Development
```

From the script's help text:

| Option | Meaning |
|---|---|
| `-Package <world.zip>` | The `.zip` written by `PackageWorld.ps1` (required). |
| `-Into <package directory>` | The CARLA package to install into: a cooked package's root (the directory holding `CarlaUnreal\` and `VERSION`), or a CARLA distribution's root (the one holding `CarlaServer\` and `VERSION`). Run from a distribution's world-tools folder, it defaults to that distribution. |
| `-Force` | Installs despite a world interface version that does not allow it; the world may then fail to load. A `world.json` of a newer format than this script reads is refused regardless. |

`InstallWorld` unpacks the zip, checks the world interface version, and copies the world's folder to
`CarlaServer\CarlaUnreal\Plugins\GeneratedWorlds\<World>\`. A copy of the world that is already
installed is deleted first. If a server from that distribution is running with the world loaded, stop
it before you install. At the end, `InstallWorld` prints the command that loads the world.

### The world interface version

The world interface version is a promise a CARLA build makes about what a delivered world can rely on:
the content, the asset classes and the cooked format that a world's files refer to. It is written by
hand in `Unreal/CarlaUnreal/Config/DefaultWorldInterface.ini` as `Major` and `Minor`, and it is `1.0`
today. A distribution shows it in its `VERSION` file and in
`CarlaServer\CarlaUnreal\Config\DefaultWorldInterface.ini`. Your level records the version of the
checkout it was cooked in.

**The rule.** A world installs into a distribution when both are true:

- the distribution's Major **equals** the world's Major, and
- the distribution's Minor is **at least** the world's Minor.

So a world cooked at 1.2 installs into 1.2 or 1.7, and is refused by 1.1 or 2.0. The whole rule, and
when the version changes, are in [World interface version](../Schemas/World_Interface_Version.md).

`PackageWorld -Distribution` cooks only in a checkout at the distribution's own commit, so a level cooked
that way has the distribution's own version and installs into it.

`InstallWorld` does not check the configuration or the platform. Install a level only into a
distribution of the configuration and platform its `world.json` names (`config` and `platform`), which
are also the end of the distribution's folder name.

### When it refuses

A refusal means the level was not cooked for this distribution. Installing it anyway would most likely
make the level fail to load: a cooked level names the base content it uses, and a distribution that
lacks that content cannot load it.

| The message says | What it means | What to do |
|---|---|---|
| `this package is world interface <x>.x, the world needs <y>.x` | The Majors differ. The level was cooked for a distribution whose content differs in ways that break loading. | Cook the level again against this distribution: a checkout at its commits, and `PackageWorld -Distribution` pointing at it. |
| `this package is minor <x>, the world needs <y> or later` | The level was cooked against content this distribution does not have yet. | Cook it again against this distribution, or install it into a newer distribution. |
| `this package does not declare a world interface version` | The distribution states no version, so what it supports is unknown. | Use a distribution that declares one. |
| `declares formatVersion <n> in world.json, and this InstallWorld reads formatVersion 1 and earlier` | A newer `PackageWorld` made the zip. | Install it with the `InstallWorld` of that newer release. `-Force` does not override this. |
| `carries no world.json` | The zip was not made by `PackageWorld`. | Make the zip with `PackageWorld`. |

The refusal names the CARLA commit the level was cooked from and the distribution's `Carla git hash`,
so you can tell which build each came from.

`-Force` installs despite a version refusal. Use it only when you know the distribution has
everything the level needs. If the level then fails to load, the version mismatch is why.

## 9. Loading it

The world's map is `/<World>/Maps/<World>`. Load it by this full path. A packaged server looks up a
short map name in a list made when the server was cooked, so it cannot find a world installed later
by its short name.

Set `CESIUM_ION_TOKEN` to a Cesium ion access token before you start the server. Without it, and
without a token saved in the level, the level loads with no imagery.

### In a distribution

```powershell
$env:CESIUM_ION_TOKEN = '<your token>'
.\run-server.ps1 /Arapahoe_I25/Maps/Arapahoe_I25
```

`run-server.ps1` starts the cooked server headless. Its first argument, when it does not start with a
dash, is the map. Any other arguments are passed to the server.

### From a checkout

```powershell
.\Scripts\Windows\RunCarlaServer.ps1 -Level Arapahoe_I25
```

`-Level <name>` loads a world by its name. It expands to `-Map /<name>/Maps/<name>`. `RunCarlaServer`
starts the editor binary headless against the checkout's project, so it loads the exported plugin in
`Unreal\CarlaUnreal\Plugins\GeneratedWorlds\<World>\`, not a cooked level. Use it to check the
exported level before you cook it.

- Close the editor first. `RunCarlaServer` refuses to start while any `UnrealEditor` process is running.
- `-RpcPort <n>` sets the port (default 2000). `-Version` prints the CARLA version and the world
  interface version this checkout declares, and exits.
- It prints `SERVER READY` when the server accepts connections. `Ctrl+C` stops it.

### Checking it loaded

When a map cannot be found, the engine loads its default map instead, which looks like the wrong world
rather than a missing one. So check that the server loaded your world.

**In the server's log.** In a checkout, the log is `Unreal\CarlaUnreal\Saved\Logs\CarlaUnreal.log`. In a
Development distribution, it is `CarlaServer\CarlaUnreal\Saved\Logs\CarlaUnreal.log`. Look for:

| Line | What it shows |
|---|---|
| `Mounted exported world '<World>'.` | The server found the world's plugin. |
| `[GeneratedWorld] applied: origin <latitude>, <longitude> at <height> m ...` | The initializer applied the world settings. The origin should be the one in the `.cwp`'s `world.json`. |
| `Loaded the road network of '<World>' from the level's own asset` | The level has its road network. |

These lines mean something is wrong:

| Line | What to do |
|---|---|
| `Exported world '<World>' could not be mounted; it cannot be loaded by name.` | Install the world again. |
| `[GeneratedWorld] ... imagery layer(s) have no Cesium ion token` | Set `CESIUM_ION_TOKEN` and start the server again. |
| `[GeneratedWorld] no world settings assigned; leaving this world as authored.` | The level's GeneratedWorldInitializer did not reach a world settings asset, so the world has no georeference and no bare-earth data. In the delivered level, its `Settings` must name an asset the level package holds: `/<World>/<World>_WorldSettings`. |
| `[GeneratedWorld] world settings carry no origin` | The settings were changed or lost. Export and cook again. |

**From a client.** With the `carlanet` Python package (in a distribution, run `.\setup-venv.ps1` once,
then `. .\carla-env.ps1` in each new session):

```python
import carlanet as carla

client = carla.Client("localhost", 2000)
client.set_timeout(60.0)
print(client.get_world().get_map().name)      # ends with the world's name
print(client.list_delivered_worlds())         # lists '<World> (mounted)'
print(client.get_world_interface_version())   # such as 1.0
```

With a server already running, `client.load_delivered_world("<World>")` mounts the world if needed and
loads it.

**The full check.** A SUMO drive or a capture given the world's `.cwp` checks the loaded level against
it before it starts: the georeference origin, the road network and the bare-earth data. When one of
them differs, it refuses to start and says which. Passing it shows that the clean-up left the
georeference, the road network and the bare-earth data unchanged (section 4).
