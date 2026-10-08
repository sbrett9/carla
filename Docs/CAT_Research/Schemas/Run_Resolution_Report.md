# Run resolution report

| | |
|---|---|
| File | `run.resolution.json`, beside the run result |
| Schema | `CarlaControl/schemas/run_resolution.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:run-resolution:1` |
| Format version | 1, in `resolution_version` |

## What it is

The run resolution report records what one `carla-capture` launch resolved and what its checks found:

- every field with the layer that set it (the ranked source its value came from)
- every refusal and warning
- the launch echo
- this machine's site profile

It is written whether the launch was accepted or refused.\
A refused launch can therefore always be read.

This is not the compiler's resolution report.\
The scenario compiler writes `<scenario_id>.resolution.json` about a compile (see [Scenario resolution report](Scenario_Resolution_Report.md)).\
This one is about a run.\
It is always named after the run result: `run.resolution.json` by default.

## Who writes it and who reads it

- **`carla-capture` writes it** beside the run result, as `<stem>.resolution.json`.\
  It is written on every path that gets as far as reading the configuration: after a usage error, after the offline checks refuse and after they accept.\
  It is written before any server is contacted.
- The run result names it in `resolution_report`.
- No tool reads it back.\
  It is for the person or script that launched the run.

## Fields

Every field is always present, except `producer` in a file written before October 7, 2026.

| Field | Type | Meaning |
|---|---|---|
| `resolution_version` | constant `1` | The format version of this file. |
| `producer` | object | What wrote the file. See [Run result](Run_Result.md#the-producer-record). |
| `outcome` | `accepted`, `usage_error` or `refused_offline` | `accepted`: the offline checks passed and the lock was written. Otherwise the launch stopped here, with that outcome. |
| `session_id` | string | The run's identity. |
| `check_catalogue` | string | The catalog the findings' check numbers belong to: `carla-capture run checks`. |
| `findings` | array | Every refusal and warning, in the order made. Each has `check`, `outcome` (`refuse` or `warn`), `subject`, `message` and `catalogue`. A warning also has its `code`. See [Run result](Run_Result.md#a-finding). |
| `launch_echo` | object or null | What the run was about to do. See [Launch echo](Launch_Echo.md). If the offline checks refused before it was computed, it is null. |
| `effective_configuration` | object or null | Every field, its value and where it came from. See [Run lock](Run_Lock.md#the-effective-configuration). If nothing was resolved, as after an unknown key, it is null. |
| `site_profile` | object | This machine's facts as resolved. See [Run lock](Run_Lock.md#the-site-profile-record). |

The effective configuration here is the same block the run lock carries, with one difference.\
The report also records a resolved value that a check then refused.\
So a value here does not always have its field's type.\
An empty path in the site profile, for example, appears here as `""` and is the reason the launch was refused (run check 37).

## Versions

This page describes version 1, the only version.\
Because no tool reads the report back, no tool refuses one.\
Read version 1 and refuse a newer version rather than read it in part.

A file written before October 7, 2026 has no `producer`.\
It is possible that it names the check catalog differently.\
It is still version 1.

## Example

A launch refused by the offline checks, shortened:

```json
{
  "resolution_version": 1,
  "producer": {"tool": "carlacontrol.CaptureSession", "tool_version": "0.10.0+g252f459d0",
               "carlanet": "0.10.0+g252f459d0", "server": null, "sumo": null,
               "written_utc": "2026-10-07T17:34:33.811Z"},
  "outcome": "refused_offline",
  "session_id": "cap-20261007-173433-41f49b",
  "check_catalogue": "carla-capture run checks",
  "findings": [
    {"check": 9, "outcome": "refuse", "subject": "capture.world_delta_s",
     "message": "...", "catalogue": "carla-capture run checks"}
  ],
  "launch_echo": null,
  "effective_configuration": {"effective_configuration_version": 1, "...": "..."},
  "site_profile": {"source": "site profile /home/me/site.profile.json", "...": "..."}
}
```
