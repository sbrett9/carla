# Run result

| | |
|---|---|
| File | `run.result.json`, or the path given to `--result` |
| Schema | `CarlaControl/schemas/run_result.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:run-result:1` |
| Format version | 1, in `result_version` |

## What it is

The run result says how one `carla-capture` run ended and where everything it wrote is.\
It records what was observed: how many captures each camera wrote, how each view became ready, each closing gate's observation against its threshold.

It carries no overall pass or fail.\
A reader decides what matters to them from the parts they care about.

The process exit status is read from `outcome`, so the file and the exit status always agree.

## Who writes it and who reads it

- **`carla-capture` writes it** in every outcome where the tool is not stopped first, including a refusal before any server was contacted.\
  It goes to `--result` (the `result_path` field), or by default to `<paths.runs_root>/<session id>/run.result.json`.\
  It is always outside the capture folder.
- It is written under a temporary name ending in `.partial` and then renamed, so a reader never sees a half-written result.\
  If there is no result, the tool was stopped before it could write one.
- Three other records are written beside it, named after it.\
  The first is `<stem>.resolution.json` (see [Run resolution report](Run_Resolution_Report.md)).\
  When the offline checks accept, the other two are `<stem>.lock.json` (see [Run lock](Run_Lock.md)) and `<stem>.effective.json` (see [Run configuration](Run_Configuration.md)).\
  For the default name `run.result.json` the stem is `run`.
- **Readers**: a camera or mission developer's own scripts, which read the outcome, the capture folder and the gate records.\
  `carlacontrol.RunResult.read` reads it with the version rule below.

## Fields

### Top level

Every field is always present, except `producer` in a file written before October 7, 2026.

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `result_version` | constant `1` | | yes | The format version of this file. |
| `producer` | object | | no | What wrote the file. See [The producer record](#the-producer-record). |
| `outcome` | string, one of the eight below | | yes | How the run ended. |
| `exit_status` | integer 0 to 7 | | yes | The process exit status, read from `outcome`. |
| `closed_by` | string or null | | yes | What closed the run, where something did. See the list below. |
| `detail` | string or null | | yes | One line on how the run ended: the first refusal, or the fault. |
| `caller` | `attended` or `unattended` | | yes | Whether a person watched the launch at a terminal. |
| `caller_label` | string or null | | yes | The caller's own label for the run. Never interpreted. |
| `session_id` | string | | yes | The run's identity, `cap-<UTC date>-<UTC time>-<six hex digits>`. It names the capture folder. |
| `tool_version` | string | | yes | The carlacontrol release. The same as `producer.tool_version`, kept for older readers. |
| `schema_version` | integer or null | | yes | The run configuration format version the run was resolved against. Null when nothing was resolved. |
| `effective_configuration_digest` | string or null | | yes | SHA-256 of `run.effective.json` without its producer. Null when nothing was resolved. |
| `resolution_report` | string or null | | yes | Path of the resolution report. Null when none was written. |
| `lock` | string or null | | yes | Path of the run lock. Null when the offline checks did not accept. |
| `effective_configuration` | string or null | | yes | Path of `run.effective.json`. Null when the offline checks did not accept. |
| `launch_echo` | object or null | | yes | What the run said it would do before it started. See [Launch echo](Launch_Echo.md). Null when the offline checks refused first. |
| `authority_holder` | string or null | | yes | Who holds the world's population lease, when the outcome is `refused_authority`. |
| `refusals` | array of findings | | yes | Every refusal the launch made. |
| `warnings` | array of findings | | yes | Every warning raised, with how it was handled. |
| `expectations_declared` | integer | | yes | How many `expect` entries the configuration declared. |
| `produced` | object or null | | yes | What the run produced and observed. Null when the run ended before the record was opened. |
| `started_wall_utc` | string | | yes | When the launch began, ISO 8601 in UTC. |
| `ended_wall_utc` | string | | yes | When the result was written, ISO 8601 in UTC. |

The outcomes and their exit statuses:

| `exit_status` | `outcome` | Meaning |
|---|---|---|
| 0 | `run_finished` | The run reached its end: the window's end or the scenario's. |
| 1 | `usage_error` | The invocation could not be resolved: an unknown key, an unreadable package, an override of a bound field. |
| 2 | `refused_offline` | The offline checks refused. No server was contacted. |
| 3 | `refused_server` | The server checks refused, or the session refused while starting. |
| 4 | `refused_authority` | Another client holds the world's population lease. `authority_holder` names it. |
| 5 | `refused_preroll` | Refused after the lease was taken and before the window opened: the fast-forward, the sun, a prewarm tick, the cameras, the pace, or the traffic a stare aims at. |
| 6 | `run_stopped` | The run ended before its end. `closed_by` says why. |
| 7 | `internal_error` | An unexpected fault. |

`closed_by` is one of:

- `window_end`;
- `scenario_end`;
- `aborted_at_preroll`;
- `operator_stop`;
- `write_headroom`;
- `signal:<name>` (such as `signal:SIGINT`);
- `loud:<condition>` (an unattended run stopped by a loud condition such as `loud:recorder_dropped`);
- `fault:<exception type>`.

### A finding

Each entry of `refusals` and `warnings`:

| Field | Type | Required | Meaning |
|---|---|---|---|
| `check` | integer | yes | The run check's number in the carla-capture run checks. |
| `outcome` | `refuse` or `warn` | yes | A refusal stops the launch. A warning lets it go on once it is handled. |
| `subject` | string | yes | What the finding is about: a field path, a channel, a package. |
| `message` | string | yes | The finding in full. |
| `catalogue` | string | yes | The catalog the check number belongs to: `carla-capture run checks`. |
| `code` | string | warnings only | The warning's code, which `on_warning.<code>` handles. |
| `adjudication` | `proceed` or null | warnings only | How the warning was handled. Null when nothing handled it. |
| `adjudicated_by` | string or null | warnings only | Where that decision came from: the `on_warning` field's source, or "the operator at the terminal". |

### `produced`

Present once the co-simulation session has started, whatever happened after.\
A run that was refused before that, offline or by the server, has `produced` null.\
Every field below is written; only `termination` is added at the very end.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `capture_directory` | string | | The capture folder: `<paths.capture_root>/<session id>`, one folder per channel inside it. |
| `world_truth_track` | string | | Path of `truth/world_truth_track.csv` in the capture folder. |
| `run_manifest` | string | | Path of `truth/manifest.jsonl` in the capture folder. |
| `closed_by` | string or null | | What closed the run. |
| `window.name` | string or null | | The declared window's id, or null for an explicit `begin:end`. |
| `window.begin_s` | number | s | Where the window begins, in simulated seconds. |
| `window.end_declared_s` | number | s | Where it was to end. |
| `window.end_source` | string | | Where that end came from. |
| `window.end_reached_s` | number or null | s | The simulated second the run reached. |
| `window.civil` | array of 2 strings | | The begin and the end reached, in civil time (ISO 8601 with offset). |
| `channels` | array | | Each channel's recorder counts, below. |
| `cameras` | array of objects | | Where each camera looked: `sensor_id`, `pattern`; for a stare its `form`, `look_at` and `pose`; for an orbit its `centre`, `radius_m`, `altitude_m` and `period_s`; and the `exposure` it was given. |
| `readiness` | object or null | | How each view became ready before the window: the rule, the ceilings, each channel's state and when its tiles were in. |
| `gates` | array | | The closing gate records, below. |
| `admissions` | object or null | | The render set's admission passes: `at_window_open` and a summary over the `window`. |
| `session` | object | | What the co-simulation session established: its clock, SUMO, pace, sun, layers, drive lease, render set and draw distance in words; its compile lock and teleport checks. It also holds `last_snapshot`, the last figures it reported. |
| `preroll_achieved_factor` | number or null | sim s per wall s | The real-time factor the prewarm achieved. |
| `recorder_run_id` | string | | The run id the recorders stamped on every still: the session id. |
| `termination` | array | | The shutdown steps in order, each with `phase`, `step`, `ran`, `completed` and `failure`. |

Each entry of `produced.channels` (every count is an integer, or null where this channel's recorder does not count it):

| Field | Meaning |
|---|---|
| `sensor_id` | The camera's name. |
| `directory` | The channel's folder. |
| `captured` | Always null: the recorder does not count captures accepted into its queue. |
| `written` | Captures written. |
| `recorder_dropped` | Captures the recorder's queue had no room for. Any is a loud condition. |
| `frame_unpaired` | Stills not written because the truth of their own frame was not available when the image arrived. |
| `illumination_paired`, `illumination_unpaired` | Captures written with and without their frame's illumination declaration. |
| `solar_block_missing` | Captures written with no recorded sun. They have no illumination band. |
| `render_set_paired`, `render_set_unpaired` | Captures written with and without their frame's list of drawn vehicles. |
| `supervision_paired`, `supervision_unpaired` | Captures written with and without their frame's supervision. |
| `lights_unknown` | Captures where a vehicle in the picture went without its lights. |
| `pose_source_unknown` | Captures where a drawn SUMO vehicle went without its pose source. |
| `occlusion_measured` | Captures whose vehicles were measured against a depth capture of the same instant. |
| `occlusion_unmatched` | Captures left without occlusion because no usable depth capture matched them. |
| `sensor_pose_from_snapshot` | Captures whose camera pose came from the snapshot of their own frame. |
| `sensor_pose_header_disagreed` | Of those, captures whose image header carried a different pose. |
| `sensor_pose_from_header` | Captures written with their image header's pose, unchecked. |
| `depth_pose_header_disagreed`, `depth_pose_from_header` | The same two counts for the depth captures. |
| `draw_distance_captures` | Captures rendered under a draw distance. |
| `vehicles_beyond_draw_distance` | Vehicle records marked wholly beyond the draw distance, summed over the captures. |
| `vehicles_partly_beyond_draw_distance` | Vehicle records the draw distance fell across, summed over the captures. |

Each entry of `produced.gates`:

| Field | Type | Meaning |
|---|---|---|
| `id` | string | The gate, such as `capture.recorder_dropped[Check_Overhead_1]` or `bridge.position_divergence`. |
| `name` | string | What it observes. |
| `owner` | string | What publishes the observation. |
| `status` | `evaluated` or `skipped` | Whether it was measured. A skipped gate is never a pass. |
| `observed` | number, boolean or null | What was observed. |
| `threshold` | number, boolean or null | What it is compared against. |
| `comparison` | `equals`, `at_least`, `at_most` or null | How. |
| `met` | boolean or null | Whether the observation met the threshold. Null when skipped. |
| `skip_reason` | string | Why the gate was skipped. Present only on a skipped gate. |

## The producer record

Every file these tools write carries a `producer` object:

| Field | Type | Meaning |
|---|---|---|
| `tool` | string | The component that wrote the file, here `carlacontrol.CaptureSession`. |
| `tool_version` | string | The carlacontrol release, with the commit it was built at. |
| `carlanet` | string or null | The CarlaNet release the process loaded. |
| `server` | object or null | The CARLA server's build identity once the run reached a server. If it could not be read, `available` is false and a `reason` is given. |
| `sumo` | string or null | The SUMO release, once the session started SUMO. |
| `written_utc` | string | When the file was written, ISO 8601 in UTC. |

## Versions

This page describes version 1, the only version.\
`RunResult.read` reads a result with no `result_version` as version 1 and refuses a newer version, saying to read it with the release that wrote it.\
A file written before October 7, 2026 has no `producer`; it is still version 1.

## Example

A finished run, shortened (`...` marks left-out entries):

```json
{
  "result_version": 1,
  "producer": {"tool": "carlacontrol.CaptureSession", "tool_version": "0.10.0+g252f459d0",
               "carlanet": "0.10.0+g252f459d0", "server": {"...": "the server's build identity"},
               "sumo": "1.27.0", "written_utc": "2026-10-07T17:35:21.512Z"},
  "outcome": "run_finished",
  "exit_status": 0,
  "closed_by": "window_end",
  "detail": null,
  "caller": "unattended",
  "caller_label": "supervision check",
  "session_id": "cap-20261007-173433-41f49b",
  "tool_version": "0.10.0+g252f459d0",
  "schema_version": 1,
  "effective_configuration_digest": "cb420338d18939f4b6459e6894afc3293d6a47c6448bbce568dcb90a956d2888",
  "resolution_report": "out/check.resolution.json",
  "lock": "out/check.lock.json",
  "effective_configuration": "out/check.effective.json",
  "launch_echo": {"launch_echo_version": 1, "...": "..."},
  "authority_holder": null,
  "refusals": [],
  "warnings": [],
  "expectations_declared": 0,
  "produced": {
    "capture_directory": "Build/captures/cap-20261007-173433-41f49b",
    "window": {"name": "dwell_golden", "begin_s": 60.0, "end_declared_s": 240.0,
               "end_source": "declared by the scenario as window dwell_golden",
               "end_reached_s": 240.0,
               "civil": ["2026-09-29T07:27:00-06:00", "2026-09-29T07:30:00-06:00"]},
    "channels": [{"sensor_id": "Check_Overhead_1", "written": 360, "recorder_dropped": 0, "...": "..."}],
    "gates": [{"id": "capture.recorder_dropped[Check_Overhead_1]",
               "name": "captures the recorder's queue had no room for", "owner": "recorder",
               "status": "evaluated", "observed": 0, "threshold": 0, "comparison": "equals",
               "met": true}],
    "...": "..."
  },
  "started_wall_utc": "2026-10-07T17:34:33.104822Z",
  "ended_wall_utc": "2026-10-07T17:35:21.498117Z"
}
```
