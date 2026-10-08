# Run manifest

**Schema:** `CarlaControl/schemas/run_manifest.schema.json` (JSON Schema 2020-12)
**Identifier:** `urn:carla-sumo-capture:schema:run-manifest:1`
**Format version described:** 1 (`manifest_version`)

## What the file is

The run manifest, `truth/manifest.jsonl` in a capture folder, is the record of one SUMO-driven run as it
happened. Each line is one JSON object, a **row**, and its `row` field says what kind it is. Rows are
appended and flushed as the run goes:

- what the run is, first, before anything is rendered;
- the supervision plan the scenario was compiled with: its pattern instances, recurring series and
  cohorts;
- each camera as it is placed;
- every vehicle taking a place in the set of vehicles the world draws (the **render set**) and giving
  it up;
- every event that changes the vehicles SUMO simulates: collisions, vehicles SUMO gave up inserting,
  emergency stops, teleports;
- the sun at the capture window's first and last capture tick;
- each of the plan's intervals as the run opens and closes it, and any defect found in carrying the
  plan out;
- why the run ended, last.

The schema describes one row. Every line of the file must be valid against it.

**Reading a manifest.** Keep every line that ends in a line break: a manifest cut off at any instant is
the rows already written, each still valid. The last row of a run that reached its end is
`manifest_closed`. A manifest without it is a run that was interrupted: a vehicle admitted and never
released was still being drawn then, and an interval opened and never closed was still open.

**Times.** `sim_time_s` and every other `_s` field is simulated time in seconds. An admission or an event
is stamped with TraCI's clock for the SUMO frame it describes. A release is stamped with the instant of
the first frame that no longer draws the vehicle, so an admission-to-release span holds exactly the
frames that drew it. A sun row is stamped with the frame whose sun it reads, to the microsecond.

## Who writes it, and when

The SUMO drive session in CarlaNet writes it when its caller names a path. `carla-capture` always does,
at `truth/manifest.jsonl` under the capture folder, and hands over a header (`run`) naming the run, its
window and its channels. `carla-drive --run-manifest <path>` writes one too. A run writes a manifest of
its own and refuses a path that already holds one.

## Rows, in the order a run first writes them

| `row` | When |
|---|---|
| `manifest_opened` | First, before anything is rendered. Exactly one. |
| `instance`, `series`, `cohort` | Next, one per pattern instance, recurring series and cohort of the supervision plan, where the scenario has one. |
| `sensor_placed` | As each camera is placed. |
| `render_admitted`, `render_released` | As each vehicle takes and gives up a place in the render set. |
| `collision_began`, `collision_ended` | As a collision SUMO reported begins, and when it is over. |
| `vehicle_not_inserted`, `emergency_stop`, `teleport` | As SUMO gives up inserting a vehicle, a vehicle brakes in an emergency, or a vehicle starts a teleport. |
| `solar_window_open`, `solar_window_end` | At the capture window's first capture tick, and at the close. |
| `interval_opened`, `interval_closed` | As the run opens and closes each of the plan's intervals. |
| `supervision_defect` | As a defect in carrying the plan out is found. |
| `manifest_closed` | Last, in a run that reached its end. At most one. |

Every row has `row`, a string, required. The tables below give each row's other fields. In them,
"null" in the Type column means the value may be JSON `null`; a field marked "No" under Required may be
absent altogether.

## `manifest_opened`

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `manifest_version` | integer, always 1 | | Yes | The manifest's format version. |
| `producer` | object | | No | What made the manifest, in the shape the truth sidecar's `<_producer>` gives it (see the `carla:capture` page); absent from a manifest written before manifests carried it. |
| `opened_wall_utc` | string | | Yes | When the manifest was opened, by the wall clock, UTC to the millisecond. |
| `run` | object or null | | Yes | The header the caller handed over, verbatim: `carla-capture` writes the run and session ids, the scenario, the caller, the window and the channels. Its fields are the caller's; null where none was given. |
| `scenario` | object | | Yes | The scenario's files and digests, from its compile lock (below). |
| `plan` | object or null | | Yes | The supervision plan the compile lock binds (below); null where there is none. |
| `vocabulary` | object or null | | Yes | The plan's annotation vocabulary (below); null where there is no plan. |
| `sumo` | object | | Yes | The SUMO settings the session checked and runs under (below). |
| `clock` | object | | Yes | How the run's clocks relate (below). |
| `window` | object | | Yes | When the run renders and when its capture window opens (below). |
| `render_set` | object | | Yes | Which vehicles the run draws (below). |
| `vehicle_lights` | object | | Yes | The rule the vehicle lights follow for the whole run (below). |
| `solar` | object | | Yes | The epoch and the illumination the run declared (below). |
| `world_truth_track` | string or null | | Yes | Where the world truth track is written; null where none is. |

`scenario`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `config_path` | string | | Yes | The SUMO configuration the run started. |
| `world_package` | string | | Yes | The world package the run drove. |
| `compiled` | boolean | | Yes | Whether a compile lock was found and checked. |
| `lock_path` | string | | Yes | Where the compile lock was looked for. |
| `scenario_id` | string or null | | Yes | The scenario's id; null with no lock. |
| `specification_sha256` | string or null | | Yes | The scenario specification's SHA-256, as the lock records it; null with no lock. |
| `config_sha256`, `routes_sha256`, `network_sha256` | string or null | | Yes | The SUMO configuration's, routes file's and network's SHA-256, as the lock records them; null with no lock. |
| `additional_sha256` | string or null | | Yes | The additional file's SHA-256; null where there is none. |
| `catalogue_digest` | string | | Yes | The vehicle catalogue's digest. |
| `epoch_digest` | string or null | | Yes | The scenario epoch's digest; null with no lock. |
| `world_opendrive_sha256` | string or null | | Yes | The world's OpenDRIVE SHA-256 the lock records; null where it records none. |
| `world_network_fingerprint` | string or null | | Yes | The world network's fingerprint the lock records; null where it records none. |
| `dry_run_ran` | boolean or null | | Yes | Whether the compiler ran the scenario in SUMO alone before writing it; null where the lock records no such run, or there is no lock. |
| `skipped_dry_run_accepted` | boolean | | Yes | Whether the run was accepted although its compile skipped that run. |

`plan` (or null):

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `path` | string | | Yes | The plan file. |
| `sha256` | string | | Yes | Its SHA-256, 64 lowercase hex digits. |
| `plan_id` | string | | Yes | The plan's id, which every sidecar of the run names in `plan_id`. |
| `spec_version` | integer | | Yes | The specification version it was compiled from. |
| `scenario_id` | string | | Yes | The scenario it belongs to. |
| `instances`, `series`, `cohorts`, `entities` | integer | | Yes | How many pattern instances, recurring series, cohorts and entities it declares. |

`vocabulary` (or null):

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `vocabulary_version` | integer | | Yes | The annotation vocabulary's core version. |
| `vocabulary_digest` | string | | Yes | The digest that pins what every label means; every sidecar of the run carries it. |
| `namespaces` | array | | Yes | The author namespaces the plan uses, each `{"namespace": string, "version": integer}`. |

`sumo`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `release` | string or null | | Yes | The SUMO release, such as `1.27.0`; null where it could not be read. |
| `binary` | string or null | | Yes | The SUMO executable the run launched. |
| `seed` | integer | | Yes | SUMO's random seed. |
| `step_s` | number | seconds | Yes | SUMO's step length. |
| `step_override_s` | number or null | seconds | Yes | The step length the run imposed over the configuration's; null where it imposed none. |
| `collision_action` | string | | Yes | What SUMO does on a collision, as in force: SUMO's own word, such as `warn`, or `none` where collisions are ignored. |
| `collision_action_declared` | string or null | | Yes | The collision action the configuration declares; null where it declares none. |
| `teleport_triggers` | array | | Yes | SUMO's teleport options, each `{"name": string, "declared": string or null, "seconds": number, "enabled": boolean}`. |
| `teleporting_accepted` | boolean | | Yes | Whether the run accepted teleporting. |
| `random_depart_offset_declared` | string or null | | Yes | SUMO's random departure offset, as declared; null where not. |
| `random_declared` | string or null | | Yes | SUMO's random option, as declared; null where not. |
| `scale` | number | | Yes | SUMO's demand scale. |
| `max_num_vehicles` | integer | | Yes | SUMO's vehicle limit; -1 for none. |
| `max_depart_delay_s` | number | seconds | Yes | How long SUMO waits to insert a vehicle before giving it up; -1 for no limit. |
| `lanechange_duration_s` | number | seconds | Yes | How long a lane change takes; 0 for SUMO's instant lane change. |

`clock`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `sumo_step_s` | number | seconds | Yes | SUMO's step length. |
| `world_delta_s` | number | seconds | Yes | The CARLA world's fixed step. |
| `capture_hz` | number | per second | Yes | Captures per simulated second. |
| `world_ticks_per_sumo_step` | integer | | Yes | World ticks per SUMO step. |
| `world_ticks_per_capture` | integer | | Yes | World ticks per capture. |

`window`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `rendered_from_s` | number | seconds | Yes | The simulated second rendering starts at; SUMO runs alone before it. |
| `opens_at_s` | number or null | seconds | Yes | The simulated second the capture window opens at; null where it opens with rendering. |

`render_set`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `policy` | string | | Yes | The render-set policy, in words, such as `every vehicle SUMO has`. |
| `limits` | boolean | | Yes | Whether an optional limit can leave a vehicle without a body. |
| `capacity` | integer or null | | Yes | The limit's capacity; null for none. |
| `draw_distance_m` | number or null | meters | Yes | The draw distance bodies are drawn under; null for none. |

`vehicle_lights`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `driven` | boolean | | Yes | Whether the session drives the lights at all; where not, every body keeps the lights it was spawned with and the rest is null. |
| `headlights_follow_sun` | boolean | | Yes | Whether the headlights follow the sun; `false` under a policy that leaves the sun alone, when every headlight stays off. |
| `headlights_on_below_deg` | number or null | degrees | Yes | The sun elevation the headlights come on below. |
| `headlights_off_above_deg` | number or null | degrees | Yes | The sun elevation the headlights go off above. |
| `headlights_elevation` | string or null | | Yes | Which sun elevation the headlights read: `geometric`. |
| `brake_lights` | string or null | | Yes | Where brake lights come from: `sumo_signals`, SUMO's signals for each vehicle. |
| `turn_signals` | string or null | | Yes | Where turn signals come from: `sumo_signals`. |

`solar`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `epoch` | object or null | | Yes | The scenario's epoch as declared: the civil time simulated second zero stands for, with its zone. Null where none was declared. |
| `epoch_declared` | boolean | | Yes | Whether an epoch was declared. |
| `epoch_block_sha256` | string or null | | Yes | The epoch's digest; null where none was declared. |
| `illumination_declared` | object or null | | Yes | The illumination policy the run declared (below); null for none. |
| `illumination_in_force` | object or null | | Yes | The illumination policy in force (below); null for none. |
| `epoch_honoured` | boolean | | Yes | Whether each frame is lit by the sun of its own declared civil time. |
| `advance_mechanism` | string, always `per_tick_write` | | Yes | How an advancing sun is moved: the session sets it on every tick. |

An illumination policy object:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `policy` | string | | Yes | `freeze_at_window_start`, `advance`, `freeze_at` or `ignore`. |
| `rate_sun_s_per_sim_s` | number | seconds per second | No | Sun-clock seconds per simulated second; under `advance` only. |
| `freeze_at_civil_time` | string | | No | The time of day the sun is held at, `hh:mm:ss`; under `freeze_at` only. |
| `freeze_date_advances` | boolean | | Yes | Whether a frozen sun's date follows the calendar. |
| `require_sun` | boolean | | Yes | Whether the run refuses a world with no sun. |
| `note` | string or null | | Yes | Why this policy, in the author's words; null for none. |

## `instance`

One pattern instance of the supervision plan, as declared.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `instance_id` | string | | Yes | The instance: `<scenario_id>/<name>`. Sidecars name it in `<annotation instance>`. |
| `supervision` | string | | Yes | What the author asserts: `annotated`, `nominal` (an authored negative) or `unlabelled` (no assertion). |
| `labels` | array of strings | | Yes | Its labels, each `namespace:name`. |
| `parameters` | object | | Yes | Its parameters, as the author declared them. |
| `hard_negative_for` | array of strings or null | | Yes | The patterns it is a matched negative for; null for none. |
| `aoi_refs` | array of strings | | Yes | The areas of interest it names. |
| `participants` | array | | Yes | Its participants, each `{"participant", "sumo_id", "role"}`, all strings. |
| `intervals` | array | | Yes | The intervals it declares (below). |

An interval as declared:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `participant` | string | | Yes | The participant it is about. |
| `phase` | string | | Yes | Its phase. |
| `anchor_start` | string or null | | Yes | The vehicle event its start is anchored to: `depart`, or `stop`, `stop_end` or `phase` with an index counted from 0, such as `stop:0`. Null where it is declared by time. |
| `anchor_end` | string or null | | Yes | The event its end is anchored to; null where none is. |
| `declared_start_s` | number or null | seconds | Yes | Its declared start. |
| `declared_start_civil` | string or null | | Yes | Its declared start as a civil time. |
| `declared_end_s` | number or null | seconds | Yes | Its declared end. |
| `declared_end_civil` | string or null | | Yes | Its declared end as a civil time. |
| `declared_duration_s` | number or null | seconds | Yes | Its declared duration. |

## `series`

One recurring series of the supervision plan, as declared, with its slots.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `series_id` | string | | Yes | The series. |
| `rota_ref` | string | | Yes | The schedule in the scenario specification the series is expanded from. |
| `cadence` | string | | Yes | How it lists its occasions: `enumerated`, or `period_s + offsets_s[] + span` for a periodic schedule. |
| `member_role` | string | | Yes | The role each slot's vehicle plays. |
| `supervision` | string | | Yes | `annotated`, `nominal` or `unlabelled`. |
| `labels` | array of strings | | Yes | Its labels. |
| `parameters` | object | | Yes | Its parameters, as declared. |
| `hard_negative_for` | array of strings or null | | Yes | The patterns it is a matched negative for; null for none. |
| `slots` | array | | Yes | Its slots, each `{"slot_key": string, "aoi_ref": string, "declared_start_s": number, "declared_end_s": number or null, "entity_id": string}`. |

## `cohort`

Every vehicle a SUMO flow emits, as declared.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `flow_id` | string | | Yes | The SUMO flow. |
| `supervision` | string | | Yes | `annotated`, `nominal` or `unlabelled`. |
| `labels` | array of strings | | Yes | The labels its vehicles carry. |
| `parameters` | object | | Yes | Its parameters, as declared. |

## `sensor_placed`

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `sensor_id` | string | | Yes | The camera's name, which its stills and its platform track carry. |
| `camera_actor_id` | integer | | Yes | The camera's CARLA actor id. |
| `after_frame` | integer or null | | Yes | The last frame rendered before the camera was placed; null before any frame. |
| `exposure` | object | | No | The exposure the camera was given, with the fields of the sidecar's `<_carla_exposure>` (`post_process_profile`, `method`, `iso`, `shutter_s`, `fstop`, `compensation_ev`) and `ev100`, a number under `manual` and null under `histogram`. Absent for a camera that publishes none. |

## `render_admitted` and `render_released`

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `sim_time_s` | number | seconds | Yes | Admitted: when it was admitted. Released: the instant of the first frame that no longer draws it. |
| `sumo_id` | string | | Yes | The SUMO vehicle. |
| `vtype_id` | string | | Yes | Its SUMO vehicle type; empty where the frame did not carry it. |
| `reason` | string | | Yes | Why (below). |
| `frame` | integer or null | | Admitted only | The frame that first drew it, or the frame stamped with its admission where no body drew it; null where no frame rendered it. |
| `actor_id` | integer or null | | Yes | The body that drew it; null where none did. |
| `admitted_s` | number | seconds | Released only | When it was admitted. |

Admission reasons: `rendering_began`, the vehicle was already in SUMO on the first frame the run
rendered, after it fast-forwarded SUMO; `inserted`, SUMO inserted it at this frame; `entered_limit`, it
was already simulated and entered the render set later, as under an optional limit.

Release reasons: `left_the_simulation`, SUMO reported it arriving or removed it during the step;
`session_ended`, the session ended while it was drawn; `vanished`, it stopped reporting a state without
SUMO listing it among the arrivals; `left_the_region`, under an optional limit it left the region or
every camera's footprint; `capacity`, under an optional capacity it ranked out. A vehicle still drawn
when the run ends has an admission row and no release row.

## `collision_began` and `collision_ended`

A collision SUMO reported, written as it begins and again, as a span, once the two vehicles are apart.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `sim_time_s` | number | seconds | Yes | Began: the SUMO frame that first reported it. Ended: when it ended. |
| `collider` | string | | Yes | The vehicle SUMO holds responsible, the follower of a rear-end collision. |
| `victim` | string | | Yes | The other vehicle. |
| `collider_vtype_id`, `victim_vtype_id` | string | | Yes | Their SUMO vehicle types. |
| `kind` | string | | Yes | SUMO's own word for what happened, such as `collision`, `frontal` or `junction`. |
| `lane` | string | | Yes | The SUMO lane it was registered on. |
| `lane_pos_m` | number | meters | Yes | Where along that lane. |
| `began_s` | number | seconds | Ended only | When it began. |
| `ended_s` | number | seconds | Ended only | When it ended. |
| `collider_actor_id`, `victim_actor_id` | integer or null | | Ended only | The bodies that drew the two; null where none did. |

## `vehicle_not_inserted`, `emergency_stop` and `teleport`

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `sim_time_s` | number | seconds | Yes | The SUMO frame that reported it; for a vehicle not inserted, when SUMO gave it up. |
| `sumo_id` | string | | Yes | The SUMO vehicle. |
| `waiting_at_s` | number | seconds | `vehicle_not_inserted` only | When the vehicle was last seen waiting to be inserted. |

## `solar_window_open` and `solar_window_end`

The sun the world reported at the capture window's first capture tick, and at its last. The fields
named `..._begin...` on the opening row are `..._end...` on the end row.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `window_index` | integer, always 0 | | Yes | The capture window; a run has one. |
| `sim_time_s` | number (null on the end row) | seconds | Yes | The window's first, or last, capture tick, to the microsecond; null on the end row where the window had none. |
| `frame` | integer (null on the end row) | | Yes | That tick's frame. |
| `begin_s` / `end_s` | number (null on the end row) | seconds | Yes | The same instant. |
| `civil_begin` / `civil_end` | string or null | | Yes | The same instant as a civil time; null where no epoch was declared. |
| `solar_date_begin` / `_end` | string or null | | Yes | The sun's date, `YYYY-MM-DD`; null where the frame carried no sun. |
| `solar_time_begin` / `_end` | number or null | hours | Yes | The sun's clock in its own time zone. |
| `sun_elevation_begin_deg` / `_end_deg` | number or null | degrees | Yes | The sun's geometric elevation. |
| `sun_corrected_elevation_begin_deg` / `_end_deg` | number or null | degrees | Yes | Its refraction-corrected elevation; null where the world did not report it. |
| `sun_azimuth_begin_deg` / `_end_deg` | number or null | degrees | Yes | Its azimuth, clockwise from true north. |
| `illumination_band_begin` / `_end` | string or null | | Yes | Its illumination band, cut as a still's is. |
| `illumination_band_begin_elevation` / `_end_elevation` | string or null | | Yes | Which elevation the band was cut from. |
| `advancing` | boolean or null | | Yes | Whether the engine itself advanced the sun's clock. |
| `rate` | number or null | seconds per second | Yes | Sun-clock seconds per simulated second while it does. |
| `sun_time_zone_hours` | number or null | hours | Opening only | The time zone the world's sun was configured with before the run set it. |
| `declared_civil_vs_solar_delta_h` | number or null | hours | Opening only | The declared sun's clock minus the civil clock at the window's opening. |
| `capture_ticks` | integer | | End only | How many capture ticks the window held. |
| `solar_residual` | object | | End only | The audit of the world's sun against the declaration over the window (below). |
| `no_sun` | boolean | | Yes | Whether the world held no sun. |
| `sun_matched_declaration` | boolean | | End only | A fact, not a verdict: an epoch was declared, the policy set the sun, the world held one, and the audit stayed within its tolerance over the window. |

`solar_residual`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `max_delta_solar_s` | number or null | seconds | Yes | The worst clock difference. |
| `max_delta_elev_deg` | number or null | degrees | Yes | The worst angle between the two suns. |
| `max_delta_corrected_deg` | number or null | degrees | Yes | The worst refraction-corrected elevation difference. |
| `max_at_tick` | integer or null | | Yes | The tick of the worst angle. |
| `max_at_sim_time_s` | number or null | seconds | Yes | The instant of the worst angle. |
| `tolerance_s` | number or null | seconds | Yes | The audit's clock tolerance. |
| `tolerance_elev_deg` | number or null | degrees | Yes | The audit's angle tolerance. |
| `audited_ticks` | integer | | Yes | How many ticks were audited. |
| `capture_ticks` | integer | | Yes | How many capture ticks the window held. |
| `within_tolerance` | boolean | | Yes | Whether the audit stayed within its tolerance. |
| `audit_skipped` | boolean | | Yes | Whether no audit was made. |

## `interval_opened` and `interval_closed`

One of the plan's intervals as the run opens and closes it. An interval is named by the triple
`(instance_id, participant, phase)`, which two runs of one scenario share. An interval whose
participant SUMO never inserted closes without having opened.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `sim_time_s` | number or null | seconds | Yes | Opened: when it opened, committed by an event or else declared; null where neither is known, as for a phase entered before the window. Closed: when it closed. |
| `instance_id` | string | | Yes | The pattern instance. |
| `participant` | string | | Yes | The participant the interval is about. |
| `phase` | string | | Yes | The interval's phase. |
| `role` | string | | Opened only | The participant's role in the instance. |
| `closed_by` | string or null | | Closed only | Why it closed (below). |
| `declared_start_s`, `declared_end_s`, `declared_duration_s` | number or null | seconds | Yes | What the author declared; null where nothing was. |
| `committed_start_s` | number or null | seconds | Yes | When SUMO committed its start, by TraCI's clock; null where it did not. |
| `observed_start_s` | number or null | seconds | Yes | The rendered frame that showed its start; null where none did. |
| `observed_start_frame` | integer or null | | Yes | That frame. |
| `begun_before_window` | boolean | | Yes | Whether it began before the capture window opened. |
| `committed_end_s` | number or null | seconds | Closed only | When SUMO committed its end; null where it did not. |
| `not_drawn` | array | | Closed only | The spans its participant was not drawn while it was open, each `{"from_s": number, "to_s": number}`. |

`closed_by` words: `trigger` (the authored condition ended it), `entity_arrived` (the vehicle reached its
destination and SUMO removed it), `sumo_removed` (SUMO removed it for another reason), `never_inserted`
(declared, and discarded before it existed), `physical_predicate_never_held` (SUMO committed it and the
drawn body never did), `render_released` (CARLA lost the body while SUMO still had the vehicle),
`capture_window_end` (the window closed while it was open), `scenario_end` (the simulation ended while it
was open).

## `supervision_defect`

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `found_by_s` | number or null | seconds | Yes | The SUMO frame it was found at or before; null before any SUMO frame. |
| `defect` | string | | Yes | What was wrong with how the run carried the plan out, rather than with the scenario, in words that name its own instants. |

## `manifest_closed`

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `ended` | string | | Yes | Why the run ended: `scenario_finished` (SUMO had nothing left), `caller_stopped`, or `run_stopped` (the session stopped it). |
| `stage` | string or null | | Yes | The stage a stopped run stopped at: `Validation`, `Launch`, `Authority`, `PreRoll` or `Window`; null where it was not stopped. |
| `cause` | string or null | | Yes | Why a stopped run stopped: `sumo-connection-lost`, `world-connection-lost`, `world-tick-timeout`, `solar-state-disagreement`, `missing-blueprint` or `none`; null where it was not stopped. |
| `caller_reason` | string or null | | Yes | The caller's own word for why it stopped, such as `window_end`; null where it gave none. |
| `last_sumo_frame_s` | number or null | seconds | Yes | The last SUMO frame read. |
| `last_rendered_s` | number or null | seconds | Yes | The last frame rendered. |
| `last_rendered_frame` | integer or null | | Yes | That frame's number. |
| `render_admitted`, `render_released` | integer | | Yes | Admission and release rows written. |
| `still_in_render_set` | integer | | Yes | Vehicles still drawn as the run ended. |
| `events` | integer | | Yes | Collision, not-inserted, emergency-stop and teleport rows written. |
| `intervals_opened`, `intervals_closed` | integer | | Yes | Interval rows written. |
| `supervision_defects` | integer | | Yes | Defect rows written. |
| `open_intervals` | array | | Yes | The intervals still open as the run ended, each `{"instance_id", "participant", "phase", "opened_s": number or null, "begun_before_window": boolean}`. |
| `open_intervals_close_as` | string | | Yes | The `closed_by` word those are closed with after this row: `scenario_end` or `capture_window_end`. |
| `never_opened` | array | | Yes | The plan's intervals nothing in the run opened or closed, each `{"instance_id", "participant", "phase"}`. |
| `bridge_divergence` | object | | Yes | How far the world departed from the poses the run commanded, over the whole run (below). |
| `rows_before` | integer | | Yes | How many rows come before this one. |
| `closed_wall_utc` | string | | Yes | When the manifest was closed, by the wall clock, UTC to the millisecond. |

A closed manifest names every `(instance_id, participant, phase)` the plan declares: in an
`interval_opened` or `interval_closed` row, in `open_intervals`, or in `never_opened`.

`bridge_divergence`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `samples` | integer | | Yes | Comparisons taken; 0 where nothing was compared, as with no world. |
| `vehicle_ticks_with_no_read_back` | integer | | Yes | Vehicle-ticks nothing was read back for. |
| `worst_position_m`, `mean_position_m` | number | meters | Yes | The worst and mean position difference. |
| `worst_yaw_deg`, `worst_pitch_deg`, `worst_roll_deg` | number | degrees | Yes | The worst rotation differences. |
| `worst_velocity_m_per_s`, `mean_velocity_m_per_s` | number | meters per second | Yes | The worst and mean velocity difference. |
| `mean_commanded_speed_m_per_s` | number | meters per second | Yes | The mean commanded speed the differences are read against. |
| `worst_position_on`, `worst_velocity_on` | object or null | | Yes | Where each worst figure was measured, `{"sumo_id": string, "sim_time_s": number, "tick": integer, "actor_id": integer}`; null where nothing was compared. |

A run that compared nothing writes zero samples and null for both worst cases. Do not read it as a run
that measured zero.

## Format version

This page describes `manifest_version` 1, on the `manifest_opened` row. Every manifest has carried it.
Manifests written before the producer record was added have no `producer` and are valid against this
schema. Readers read a version they know and refuse a newer one by name rather than reading it in part.

## Example

Rows from a capture of the Arapahoe world. The opening row is long and is left out here; it holds every
field in its tables.

```json
{"row":"sensor_placed","sensor_id":"Check_Overhead_1","camera_actor_id":107,"after_frame":null,"exposure":{"post_process_profile":"Default","method":"manual","iso":100,"shutter_s":0.003125,"fstop":4,"compensation_ev":0,"ev100":12.321928094887362}}
{"row":"render_admitted","sim_time_s":0.05,"sumo_id":"arapahoe_east_to_west.0","vtype_id":"van.vehicle.sprinter.mercedes","reason":"inserted","frame":22475,"actor_id":109}
{"row":"interval_opened","sim_time_s":90.05,"instance_id":"Arapahoe_I25_SupervisionCheck/through_transit","participant":"transit","phase":"transit","role":"subject","declared_start_s":90,"declared_end_s":null,"declared_duration_s":null,"committed_start_s":90.05,"observed_start_s":null,"observed_start_frame":null,"begun_before_window":false}
{"row":"render_released","sim_time_s":78.1999999999977,"sumo_id":"arapahoe_west_to_east.0","vtype_id":"car.vehicle.ue4.bmw.grantourer","actor_id":112,"admitted_s":0.05,"reason":"left_the_simulation"}
{"row":"manifest_closed","ended":"caller_stopped","stage":null,"cause":null,"caller_reason":"window_end","last_sumo_frame_s":240.05,"last_rendered_s":239.95,"last_rendered_frame":27273,"render_admitted":95,"render_released":33,"still_in_render_set":62,"events":0,"intervals_opened":3,"intervals_closed":2,"supervision_defects":0,"open_intervals":[{"instance_id":"Arapahoe_I25_SupervisionCheck/through_transit","participant":"transit","phase":"transit","opened_s":90.05,"begun_before_window":false}],"open_intervals_close_as":"capture_window_end","never_opened":[{"instance_id":"Arapahoe_I25_SupervisionCheck/brief_stop","participant":"brief_stopper","phase":"stop"}],"bridge_divergence":{"samples":199827,"vehicle_ticks_with_no_read_back":0,"worst_position_m":9.984218289601819e-05,"mean_position_m":1.569918750533907e-05,"worst_yaw_deg":0.00010295372595692243,"worst_pitch_deg":9.996040028828479e-05,"worst_roll_deg":9.980968214806651e-05,"worst_velocity_m_per_s":3.100400437821817e-06,"mean_velocity_m_per_s":2.72097751669939e-07,"mean_commanded_speed_m_per_s":7.678172465720582,"worst_position_on":{"sumo_id":"arapahoe_east_to_yosemite_north.0","sim_time_s":100.95,"tick":2019,"actor_id":110},"worst_velocity_on":{"sumo_id":"clinton_to_arapahoe_west.3","sim_time_s":145.1,"tick":2902,"actor_id":164}},"rows_before":149,"closed_wall_utc":"2026-10-07T17:35:56.881Z"}
```

## Checking a file

`carla-validate <capture folder>` checks every row against this schema and the file as a whole: that it
opens with `manifest_opened`, that nothing follows `manifest_closed`, and it notes a manifest with no
closing row as an interrupted run. `carla-diff-manifests` compares the supervision rows of two runs of
one scenario.
