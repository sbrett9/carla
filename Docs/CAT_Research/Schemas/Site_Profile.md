# Site profile

| | |
|---|---|
| File | any name, passed to `carla-capture --site-profile` |
| Schema | `CarlaControl/schemas/site_profile.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:site-profile:1` |
| Format version | 1, in `site_profile_version` |

## What it is

A site profile holds the facts about one machine that a capture run needs:

- the CARLA server's address
- the SUMO installation
- where scenario packages, world packages, the vehicle catalog, captures and run records are kept

Keeping these out of the run configuration lets the same run configuration move between machines unchanged.

Without a site profile, the values come from the layout the tool runs from:

- In a source checkout, they are `Build/scenarios`, `Build/world-packages`, `CarlaControl/catalogue/vehicles.catalogue.json`, `Build/captures` and `Build/runs`.
- In an installed copy, they are `scenarios`, `world-packages`, `captures` and `runs` under the current folder.\
  The catalog is the one installed with carlacontrol.

A profile file overrides only the fields it names.

If no profile names `sumo.home`, the session looks for SUMO in this order:

- the `CARLANET_SUMO_HOME` environment variable
- the checkout's staged SUMO
- a search of `SUMO_HOME` and `PATH`

## Who writes it and who reads it

- You write it, usually once per machine.\
  `carla-capture --write-site-profile PATH` writes this machine's current values as a file to edit.
- `carla-capture --site-profile` reads it before resolving the run.\
  It refuses a file that is not JSON or does not conform to the schema.\
  It also refuses a file with no `site_profile_version` of 1, or with a block or a field it does not know.\
  The refusal ends the launch with exit status 1, before a run begins.\
  No run result is written.
- A relative path in the file is relative to the file's own folder.
- The values it gives are recorded, each with where it came from, in the run's lock and resolution report (see [Run lock](Run_Lock.md#the-site-profile-record)).

## Fields

| Field | Type | Unit | Required | Default | Meaning |
|---|---|---|---|---|---|
| `site_profile_version` | constant `1` | | yes | | The format version of this file. |
| `note` | string | | no | | A note for a person. Not read. |
| `environment` | array of strings | | no | | The environment variables the profile allows a value to be read from. If a value comes from a variable not named here, run check 36 refuses an unattended run. If SUMO must be searched for in `SUMO_HOME` or `PATH` and they are not named here, the same check refuses it. Only `sumo.home` is read from a variable, `CARLANET_SUMO_HOME`. |
| `server.host` | string | | no | `"127.0.0.1"` | The CARLA server's address. |
| `server.port` | integer, at least 1 | | no | `2000` | The CARLA server's RPC port. |
| `server.timeout_s` | number, above 0 | s | no | `30.0` | Seconds to wait for the server to answer a request. |
| `sumo.home` | string or null | | no | `null` | The SUMO installation the session launches: the folder holding `bin/sumo`. When null, the session searches `SUMO_HOME` and then `PATH`. |
| `paths.scenario_root` | string or null | | no | from the layout | Where compiled scenario packages are found by id. |
| `paths.world_package_root` | string or null | | no | from the layout | Where world packages are found by the name a scenario lock records. |
| `paths.catalogue` | string or null | | no | from the layout | The measured vehicle catalog. Its digest must match the scenario lock's (run check 48). |
| `paths.capture_root` | string or null | | no | from the layout | Where captures are written: one folder per run. |
| `paths.runs_root` | string or null | | no | from the layout | Where a run's result, resolution report and lock are written by default. |

A path can be written empty or null.\
The launch then refuses it by name (run check 37).\
A refusal by name says more than a type error.\
`sumo.home` empty is refused the same way.

## Versions

This page describes version 1, the only version.\
A file with no `site_profile_version`, or any value other than 1, is refused.

## Example

```json
{
  "site_profile_version": 1,
  "note": "The capture machine in the lab. Relative paths are relative to this file.",
  "environment": ["CARLANET_SUMO_HOME"],
  "server": {"host": "10.0.0.5", "port": 2000, "timeout_s": 30.0},
  "sumo": {"home": "/opt/sumo-1.27.0"},
  "paths": {
    "scenario_root": "scenarios",
    "world_package_root": "world-packages",
    "catalogue": "catalogue/vehicles.catalogue.json",
    "capture_root": "/data/captures",
    "runs_root": "runs"
  }
}
```
