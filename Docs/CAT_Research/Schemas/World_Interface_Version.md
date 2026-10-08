# World interface version

A generated world can be delivered on its own, as a level package.\
It can then be installed into a CARLA package that was built separately.\
The world interface version says which of those pairings work.

It is a promise a CARLA build makes about what a delivered world can rely on: the content, the asset classes and the cooked format that a world's files refer to.\
It is not a build number or a commit hash.

The version has two parts, `Major.Minor`.\
It is 1.0 today.

## Where it is declared and where it appears

It is declared by hand in `Unreal/CarlaUnreal/Config/DefaultWorldInterface.ini`:

```ini
[WorldInterface]
Major=1
Minor=0
```

Changing it is meant to be a visible one-line commit.\
Nothing derives it from anything else.

It then appears in these places:

| Where | What it shows |
|---|---|
| A cooked CARLA package | `CarlaUnreal/Config/DefaultWorldInterface.ini`, staged with the package |
| The package's `VERSION` file | `World interface:        1.0` |
| A running server | the startup log, the `get_world_interface_version` call and `world_interface` in the `get_build_identity` call |
| `RunCarlaServer -Version` | `world interface version : 1.0` |
| A level package's `world.json` | `worldInterfaceMajor` and `worldInterfaceMinor`: the version of the checkout that cooked the world |
| The record of what made a file | `world_interface` in the server's build identity |

A server whose declaration is missing or unreadable reports `0.0`.\
No delivered world matches it.

## The rule

A world installs into a package that meets both of these conditions:

- the package's Major equals the world's Major
- the package's Minor is at least the world's Minor

So a world cooked against 1.2 installs into 1.2, 1.7 or 1.99.\
It is refused by 2.0 or 1.1.

| Package | World | Installs |
|---|---|---|
| 1.0 | 1.0 | yes |
| 1.7 | 1.2 | yes |
| 1.1 | 1.2 | no: the package lacks content added in 1.2 |
| 2.0 | 1.0 | no: Major differs |
| 1.0 | 2.0 | no: Major differs |

## When the version changes

**Major goes up** for a change that stops a world already delivered from loading:

- content a world refers to is renamed or deleted, the road materials especially
- a class a world's assets store changes shape: `UGeoreferencedWorldSettings`, `URoadNetworkAsset` or `UBareEarthOffsetField`
- the engine moves to a version that changes the cooked package format

When Major goes up, Minor goes back to 0.

**Minor goes up** for added content that a world can refer to.\
Worlds built before the addition keep working.\
Worlds built after it need a package that has it.

**Neither changes** for anything a delivered world cannot observe:

- gameplay code
- server calls
- tools
- how a material looks
- new maps in the base package

## For an Unreal tech artist making a level

- A level package records the world interface version of the checkout it was cooked in.\
  Cook in a checkout at the CARLA, content and engine commits of the distribution the world is for.\
  The distribution's `VERSION` file lists them.\
  `PackageWorld -Distribution` refuses a checkout at another CARLA commit.
- A world is cooked against one base release (`basedOnRelease`), in one configuration and for one platform.\
  Install it only into a package of that configuration and platform.\
  The installer does not compare those two.
- The commit hashes in `world.json` identify the build for a person.\
  No tool compares them.
- If a change you make to the base content breaks worlds already delivered, the Major must go up.\
  If you add content a new world will use, the Minor must go up.\
  Either change is a commit to `DefaultWorldInterface.ini`.

## For an installer

`InstallWorld.ps1` (Windows) and `InstallWorld.sh` (Linux) apply the rule:

1. They unpack the zip and read `world.json`.\
   They refuse a zip without one.\
   They also refuse a `world.json` that declares a `formatVersion` newer than they read.
2. They read `Major` and `Minor` from the `[WorldInterface]` section of the target package's `CarlaUnreal/Config/DefaultWorldInterface.ini`.\
   They read no other section.\
   The target is a cooked package's root, or a distribution's root, whose package is in `CarlaServer/`.
3. They refuse to install into a package that declares no version, has a different Major or has a lower Minor than the world.\
   The message names both versions, the world's CARLA commit and the package's `VERSION` line.
4. Otherwise they copy the world's folder to `CarlaUnreal/Plugins/GeneratedWorlds/<world>/`.\
   The new copy replaces a copy already installed.\
   Then they print the map to load.

`-Force` (Windows) or `--force` (Linux) installs despite a refusal in step 3.\
If the world then fails to load, that is why.\
Neither overrides a `world.json` of a newer format.

## Example

A world packaged in a checkout that declares 1.3 has `"worldInterfaceMajor": 1` and `"worldInterfaceMinor": 3` in its `world.json`.\
Installed into a package that declares 1.0, it is refused:

```
world   : Arapahoe_I25
needs   : world interface 1.x, minor 3 or later
package : world interface 1.0

This world was not built for this package:
  - this package is minor 0, the world needs 3 or later
```
