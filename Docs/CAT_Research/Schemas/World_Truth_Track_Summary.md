# World truth track summary

**Schema:** `CarlaControl/schemas/world_truth_track_summary.schema.json` (JSON Schema 2020-12)\
**Identifier:** `urn:carla-sumo-capture:schema:world-truth-track-summary:2`\
**Format version described:** 2 (`world_truth_track_version`)

## What the file is

The summary sits beside a world truth track, with the track's name and `.summary.json` for its extension: `truth/world_truth_track.summary.json` in a capture folder.\
It holds:

- the track's format version;
- what made it;
- its columns and sampling rate;
- what it holds so far;
- why the run ended.

## Who writes it, and when

The SUMO drive session writes it with the track: once when the track opens, and again when the run ends.\
Each time it writes the whole file under a temporary name and renames it into place, so a reader never sees half a summary.\
A summary whose `ended` is null belongs to a track still being written, or to a run that was killed.

## Fields

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `world_truth_track_version` | integer, always 2 | | Yes | The format version of the track and its summary. |
| `producer` | object | | No | What made the track, stamped each time the summary is written, in the same shape as a manifest's or a still's (see the `carla:capture` page); absent from a summary written before summaries carried it. |
| `track` | string | | Yes | The track's file name, beside the summary. |
| `columns` | array of strings | | Yes | The track's columns, in order: its header line. Version 2 has exactly the 41 columns on the World truth track page. |
| `instant` | string, always `traci_clock` | | Yes | Every row's `sim_time_s` is TraCI's clock for the SUMO frame the row describes. |
| `sumo_step_s` | number | seconds | Yes | SUMO's step length. |
| `interval_s` | number | seconds | Yes | Simulated time between samples. |
| `every_sumo_steps` | integer | | Yes | How many SUMO frames apart the samples are; 1 samples every frame. |
| `outside_window_interval_s` | null, always | | Yes | Nothing is written outside the capture window. |
| `samples` | integer | | Yes | Samples written so far. |
| `rows` | integer | | Yes | Rows written so far, one per vehicle per sample. |
| `first_sample_s` | number or null | seconds | Yes | The first sample's instant; null before one is written. |
| `last_sample_s` | number or null | seconds | Yes | The last sample's instant; null before one is written. |
| `ended` | object or null | | Yes | How the run ended (below); null while the track is being written, or where the run was killed. |

`ended`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `reason` | string | | Yes | `scenario_finished` (SUMO had nothing left), `caller_stopped`, or `run_stopped` (the session stopped the run). |
| `stage` | string | | Only where `reason` is `run_stopped` | The stage the run stopped at: `Validation`, `Launch`, `Authority`, `PreRoll` or `Window`. |
| `cause` | string | | Only where `reason` is `run_stopped` | Why it stopped: `sumo-connection-lost`, `world-connection-lost`, `world-tick-timeout`, `solar-state-disagreement`, `missing-blueprint` or `none`. |
| `last_sumo_frame_s` | number | seconds | Yes | The last SUMO frame read. |
| `last_rendered_s` | number or null | seconds | Yes | The last frame rendered; null where none was. |
| `last_rendered_frame` | integer or null | | Yes | That frame's number. |

Once `ended` is set, `rows` equals the number of rows in the track.

## Format version

This page describes `world_truth_track_version` 2, which is also the track's version.\
Summaries written before the producer record was added have no `producer` and are valid against this schema.

A summary without `world_truth_track_version` is version 1, an older shape this schema does not describe.\
Readers read a version they know and refuse a newer one by name rather than reading it in part.

## Example

The file lists each column on a line of its own; they are run together here.

```json
{
  "world_truth_track_version": 2,
  "producer": {
    "tool": "carlacontrol.CaptureSession",
    "tool_version": "0.10.0+g252f459d0",
    "carlanet": "0.10.0+g252f459d0",
    "server": {
      "available": true,
      "release": "0.10.0",
      "world_interface": "1.0",
      "build": "package",
      "configuration": "Shipping",
      "carla_commit": "025443a83eaf1bb82f18795d608fca50eb77a452",
      "content_commit": "6bcd042a91a54d9a2f2f002869fbf1c75f3768f4",
      "engine_commit": "e5e266de195a2400a6a74180402fb1a3e8f75472",
      "commits_from": "version_file"
    },
    "sumo": "1.27.0",
    "written_utc": "2026-10-07T17:35:56.880Z"
  },
  "track": "world_truth_track.csv",
  "columns": [
    "time_utc", "sim_time_s", "uid", "callsign", "cot_type", "how", "lat", "lon", "hae_m", "ce_m",
    "le_m", "course_deg", "speed_mps", "vx", "vy", "vz", "base_type", "type_id", "special_type",
    "length_m", "width_m", "height_m", "color", "role_name", "edge", "lane", "sumo_x", "sumo_y",
    "carla_x", "carla_y", "sumo_id", "entity_id", "frame", "render_state", "render_reason",
    "actor_id", "in_window", "sun_elevation_deg", "sun_corrected_elevation_deg",
    "illumination_band", "illumination_band_elevation"
  ],
  "instant": "traci_clock",
  "sumo_step_s": 0.05,
  "interval_s": 0.05,
  "every_sumo_steps": 1,
  "outside_window_interval_s": null,
  "samples": 3600,
  "rows": 180125,
  "first_sample_s": 60,
  "last_sample_s": 239.95,
  "ended": {
    "reason": "caller_stopped",
    "last_sumo_frame_s": 240.05,
    "last_rendered_s": 239.9500000000203,
    "last_rendered_frame": 27273
  }
}
```

## Checking a file

`carla-validate <capture folder>` checks the summary against this schema and, once the run has ended, that it counts the rows the track holds.
