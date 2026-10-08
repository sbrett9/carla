# Run lock

| | |
|---|---|
| File | `run.lock.json`, beside the run result |
| Schema | `CarlaControl/schemas/run_lock.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:run-lock:1` |
| Format version | 1, in `lock_version` |

## What it is

The run lock records what one accepted capture run is bound to: the digests of its scenario, world,
vehicle catalogue and epoch; every field of its effective configuration with the layer that set it
and where that layer read it; how each warning was handled; and this machine's site profile. It is
the record to compare when two runs differ.

**This is not the scenario lock.** The scenario compiler writes `<scenario_id>.lock.json`, which
records a compiled scenario (see [Scenario lock](Scenario_Lock.md)). A run lock records one run of
that scenario, and is always named after the run result: `run.lock.json` by default.

## Who writes it and who reads it

- **`carla-capture` writes it** beside the run result, as `<stem>.lock.json`, only when the offline
  checks accept the launch. It is written before any server is contacted, so its `producer` names no
  server.
- `run.effective.json` is written with it. That file reproduces the run; the lock explains it.
- No tool reads it back. It is for people and for scripts that compare runs.

## Fields

Every field is always present, except `producer` in a file written before October 7, 2026.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `lock_version` | constant `1` | | The format version of this file. |
| `producer` | object | | What wrote the file. See [Run result](Run_Result.md#the-producer-record). |
| `session_id` | string | | The run's identity. |
| `effective_configuration_sha256` | string, 64 hex digits | | SHA-256 of the replayable configuration, `run.effective.json` without its producer. Two machines resolving the same inputs get the same digest. |
| `tool_version` | string | | The carlacontrol release that resolved the run. |
| `schema_version` | constant `1` | | The run configuration format version. |
| `caller` | `attended` or `unattended` | | Whether a person watched the launch. |
| `caller_label` | string or null | | The caller's own label for the run. |
| `inputs.scenario_lock_sha256` | string, 64 hex digits | | SHA-256 of the scenario's `.lock.json`. |
| `inputs.scenario_id` | string | | The scenario's id. |
| `inputs.world_network_fingerprint` | string, 64 hex digits | | The canonical fingerprint of the world's SUMO network. |
| `inputs.world_opendrive_sha256` | string | | SHA-256 of the world's OpenDRIVE. |
| `inputs.catalogue_digest` | string | | The digest of the vehicle catalogue the scenario was compiled against. |
| `inputs.epoch_block_sha256` | string, 64 hex digits | | SHA-256 of the scenario's epoch object. |
| `inputs.sumo_seed` | integer | | SUMO's seed. |
| `adjudications` | array | | How each warning was handled: `code`, `adjudication` (`proceed`) and `adjudicated_by` (the `on_warning` field's source, or "the operator at the terminal"). |
| `expectations` | object | | The configuration's `expect` block, as given. |
| `effective_configuration` | object | | Every field the run is subject to. See below. |
| `replay.configuration_path` | string | | Path of `run.effective.json`. |
| `replay.non_interactive` | constant `true` | | Replaying needs no terminal. |
| `site_profile` | object | | This machine's facts as resolved. See below. |

### The effective configuration

The `effective_configuration` object, which the [Run resolution report](Run_Resolution_Report.md)
carries too:

| Field | Type | Meaning |
|---|---|---|
| `effective_configuration_version` | constant `1` | The format version of this block. |
| `tool_version` | string | The carlacontrol release that resolved it. |
| `schema_version` | constant `1` | The run configuration format version. |
| `digest` | string, 64 hex digits | The same digest as `effective_configuration_sha256`. |
| `fields` | object | Every run configuration field, keyed by its dotted path, such as `capture.prewarm_s`. Every field is present. |
| `channels` | array | One object per channel, keyed by channel field name. Every channel field is present. |

Each value in `fields` and in a channel object records one field:

| Field | Type | Required | Meaning |
|---|---|---|---|
| `value` | the field's type, or null | yes | The value the run uses. Null where the field does not apply or nothing supplied it. In the lock every value has its field's type. |
| `layer` | string | yes | The layer that set it: `tool_default`, `site_profile`, `world_package`, `scenario_package`, `run_configuration` or `operator_override`; or `derived`, `pinned_by_policy`, `not_applicable`, `unsupplied`. |
| `provenance` | string | yes | The file and key, or the command-line text, it was read from. |
| `overridden` | array or null | yes | What lower layers gave and a higher one replaced, each with `layer`, `value` and `provenance`. Null where nothing was replaced. |
| `tool_default` | the field's type, or null | for a field with a default | The tool's default, carried even where another layer set the value, so a deliberate match can be told from an accident. |
| `dropped` | array | no | An illumination value of a lower layer that was dropped, because a higher layer chose a policy that does not take it. |
| `note` | string | no | A note, such as a bound field restated with the same value. |
| `environment_variable` | string | no | The environment variable the value was read from. |

The fields themselves are listed in [Run configuration](Run_Configuration.md).

### The site profile record

The `site_profile` object, which the run resolution report carries too:

| Field | Type | Meaning |
|---|---|---|
| `source` | string | The site profile file the values were read from, or the layout they were derived from. |
| `declared_environment` | array of strings | The environment variables the profile file names in `environment`. |
| `host_searched` | array, of `SUMO_HOME` and `PATH` | The variables the session searches for SUMO because no field names one. Empty where a field does. |
| `values` | object | Every machine fact, keyed by its run configuration field (`server.port`, `paths.capture_root`, ...). The five `paths` and `sumo.home` are always present. |
| `values.<field>.value` | string, number or null | The value. |
| `values.<field>.provenance` | string | Where it came from. |
| `values.<field>.environment_variable` | string or null | The environment variable it was read from, if any. |

## Versions

This page describes version 1, the only version. No tool reads a run lock back, so no tool refuses
one. A reader should read version 1 and refuse a newer version rather than read it in part. A file
written before October 7, 2026 has no `producer`; it is still version 1.

## Example

Shortened (`...` marks left-out entries):

```json
{
  "lock_version": 1,
  "producer": {"tool": "carlacontrol.CaptureSession", "tool_version": "0.10.0+g252f459d0",
               "carlanet": "0.10.0+g252f459d0", "server": null, "sumo": null,
               "written_utc": "2026-10-07T17:34:34.020Z"},
  "session_id": "cap-20261007-173433-41f49b",
  "effective_configuration_sha256": "cb420338d18939f4b6459e6894afc3293d6a47c6448bbce568dcb90a956d2888",
  "tool_version": "0.10.0+g252f459d0",
  "schema_version": 1,
  "caller": "unattended",
  "caller_label": "supervision check",
  "inputs": {"scenario_lock_sha256": "51ff04af5747...", "scenario_id": "Arapahoe_I25_SupervisionCheck",
             "world_network_fingerprint": "ffe490b1ee67...", "world_opendrive_sha256": "67157d5d2f52...",
             "catalogue_digest": "6037e3bb2bde...", "epoch_block_sha256": "b92057175df8...",
             "sumo_seed": 42},
  "adjudications": [],
  "expectations": {},
  "effective_configuration": {
    "effective_configuration_version": 1,
    "tool_version": "0.10.0+g252f459d0",
    "schema_version": 1,
    "digest": "cb420338d18939f4b6459e6894afc3293d6a47c6448bbce568dcb90a956d2888",
    "fields": {
      "capture.prewarm_s": {"value": 60, "layer": "operator_override",
                            "provenance": "--set capture.prewarm_s=60", "tool_default": 300.0,
                            "overridden": [{"layer": "run_configuration", "value": 30.0,
                                            "provenance": "run configuration check.run.json"}]},
      "...": "..."
    },
    "channels": [{"sensor_id": {"value": "Check_Overhead_1", "layer": "run_configuration",
                                "provenance": "run configuration check.run.json :: capture.channels[0]",
                                "tool_default": null, "overridden": null}, "...": "..."}]
  },
  "replay": {"configuration_path": "out/check.effective.json", "non_interactive": true},
  "site_profile": {"source": "the layout at /home/me/carla", "declared_environment": [],
                   "host_searched": [], "values": {"...": "..."}}
}
```
