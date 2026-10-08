# Level package manifest (`world.json` in a level package)

A level package is one generated world delivered on its own, as add-on content for an existing CARLA package.\
It is a zip made by `PackageWorld`.

The zip holds the world's cooked plugin folder and, beside it, a `world.json`.\
That file says what the world is and what it needs from the package it is installed into.\
`InstallWorld` reads it before it installs anything.

This is a different file from the `world.json` inside a world package (`.cwp`).\
[World_Package_Manifest.md](World_Package_Manifest.md) describes that file.

- Schema: `CarlaControl/schemas/level_package_manifest.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:level-package-manifest:1`

## Who writes it and who reads it

An Unreal tech artist imports a `.cwp` into a level with the World Package Importer and exports it as a plugin under `Unreal/CarlaUnreal/Plugins/GeneratedWorlds/<World>/`.\
A `DeliverSeparately.txt` in that folder marks the world for separate delivery.\
`PackageWorld` then cooks that world on its own against the base release.\
Two scripts write the manifest:

- `Scripts/Windows/PackageWorld.ps1` writes it for Windows (platform `Win64`).
- `Scripts/Linux/PackageWorld.sh` writes it for Linux (platform `Linux`).

Both write the same keys in the same order.\
The file is indented JSON with camelCase keys.\
It is UTF-8 without a byte order mark, whether `PackageWorld.ps1` runs under PowerShell 7 or Windows PowerShell 5.1.\
A manifest written by `PackageWorld.ps1` under Windows PowerShell 5.1 before 2026-10-07 begins with a byte order mark.\
If you write a reader, make it accept one.

`Scripts/Windows/InstallWorld.ps1` and `Scripts/Linux/InstallWorld.sh` read it.\
They check its `formatVersion` first.\
Then they check the world interface version against the target package's (see [World_Interface_Version.md](World_Interface_Version.md)).\
Then they copy the world's folder into the package's `CarlaUnreal/Plugins/GeneratedWorlds/`.\
They print the `mapPackage` to load.

The scripts read these fields:

- `formatVersion`
- `world`
- `mapPackage`
- `worldInterfaceMajor`
- `worldInterfaceMinor`
- `carlaGitHash`

The other fields identify the build for a person.

## The zip

```
<World>.zip
  world.json
  <World>/           the world's cooked plugin folder, as the cook staged it:
    <World>.uplugin  its descriptor, and below it the cooked level (.umap) and assets (.uasset, .uexp)
```

If the cook staged no level or no asset, `PackageWorld` refuses to make the zip.\
That is what happens to a world that is already part of the base release.

## Fields

Both scripts write every field.\
Every field but `formatVersion` is required.

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `formatVersion` | integer, always 1 | | no | The format of this file. A manifest without it is format 1. |
| `world` | string | | yes | The world's name. It is also the name of the plugin folder beside this file in the zip. |
| `mapPackage` | string | | yes | The level's Unreal package path, `/<world>/Maps/<world>`. Load the world by this name. |
| `worldInterfaceMajor` | integer | | yes | The Major of the world interface version of the checkout that cooked the world. The package it is installed into must declare the same Major. |
| `worldInterfaceMinor` | integer | | yes | The Minor of that version. The package must declare this Minor or a later one. |
| `basedOnRelease` | string | | yes | The base release the world was cooked against. By default it is the short CARLA commit, the name the base cook gives its release. |
| `releaseVersion` | string | | yes | The CARLA release version of the checkout that cooked the world, such as `0.10.0`. It comes from `CARLA_VERSION_MAJOR`, `_MINOR` and `_PATCH` in `CMakeLists.txt`. |
| `config` | string | | yes | The build configuration the world was cooked in: `Development`, `Shipping` or `Debug`. It must be the package's. |
| `platform` | string | | yes | `Win64` or `Linux`. |
| `carlaGitHash` | string | | yes | The full CARLA commit of the checkout that cooked the world. If git gave no commit, it is empty. |
| `contentGitHash` | string | | yes | The full commit of `Unreal/CarlaUnreal/Content/Carla`. If git gave no commit, it is empty. |
| `unrealGitHash` | string | | yes | The full Unreal Engine commit. If the engine folder is not a git checkout, it is empty. |
| `packagedAtUtc` | string | | yes | When the zip was made, ISO 8601 UTC to the millisecond, as both scripts write it. A manifest written before 2026-10-07 has seven decimal places of a second (`PackageWorld.ps1`) or whole seconds (`PackageWorld.sh`). |

The three commit hashes identify the build.\
No tool compares them.\
The world interface version decides whether a world installs.

## Format version

`formatVersion` is 1.\
There is no other version.\
`InstallWorld.ps1` and `InstallWorld.sh` read it before any other field: a manifest without it is format 1.\
The scripts refuse a manifest that declares a newer format.\
The refusal names the format the manifest declares and the newest format the script reads.\
`-Force` and `--force` do not override that refusal, because a newer format can change what a field means.

## Example

```json
{
  "formatVersion": 1,
  "world": "Arapahoe_I25",
  "mapPackage": "/Arapahoe_I25/Maps/Arapahoe_I25",
  "worldInterfaceMajor": 1,
  "worldInterfaceMinor": 0,
  "basedOnRelease": "025443a83",
  "releaseVersion": "0.10.0",
  "config": "Development",
  "platform": "Win64",
  "carlaGitHash": "025443a834b1f3c9d1e2a4b5c6d7e8f901234567",
  "contentGitHash": "6bcd042a91a54d9a2f2f002869fbf1c75f3768f4",
  "unrealGitHash": "e5e266de195a2400a6a74180402fb1a3e8f75472",
  "packagedAtUtc": "2026-10-07T17:40:12.345Z"
}
```
