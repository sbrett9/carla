# Behavioral annotations

This page is for people who build pattern-of-life models from our captures. It explains the labels a
scenario's author writes into a scenario, how a run carries them onto the vehicles, and where you find
them in a capture. [What a capture folder holds](Capture_Folder.md) explains the files, and
[The vehicles behind the truth](Vehicle_Catalogue.md) explains the vehicles.

**We label; we never score.** A label states what the author declared, and the truth around it states
what the simulation did and what was measured. No label carries a confidence, a pass mark or a judgment
of how well a picture shows the behavior. Whether a labeled behavior can be seen in a still is for your
model to find out.

The labels are fixed when the scenario is compiled, in its
[supervision plan](../Schemas/Supervision_Plan.md), `<scenario_id>.supervision.json`. A run never adds
a label. It only binds the plan's labels to the vehicles as SUMO drives them, and records when each one
started and ended.

## Concepts

### The vocabulary and its version

A label is a **term**, spelled `namespace:name`, such as `check:kerbside_dwell`. The vocabulary has two
parts:

- **The core**: a closed set of words our own code acts on, such as the supervision states and the
  reasons an interval closes. Its version is the `vocabulary` number on every sidecar and in the
  manifest: 3 in current captures. The plan lists every core word by family, in `vocabulary.core`.
- **Author namespaces**: the terms a scenario's author declares, such as `check` (version 1) in the
  Arapahoe supervision check and `bahonar` (version 2) in the Shahid Bahonar Port scenario. Each
  namespace has its own version.

Each author term has a written definition and says what it may label: `entity`, one authored vehicle,
or `cohort`, every vehicle of a flow. A term may also declare parameters, each with a type, a unit and a
definition, and relations to other terms: `contrast_with`, `counterfactual`, `hard_negative_for`,
`broader`. Here is one term, as the Arapahoe supervision check declares it:

```json
{
  "term": "check:kerbside_dwell",
  "since": 1,
  "status": "active",
  "applies_to": ["entity"],
  "definition": "A car pulls to the curb and waits there for minutes, with nothing to deliver or collect, then drives on.",
  "parameters": {
    "dwell_s": {"type": "number", "unit": "s", "definition": "the authored length of the wait at the curb"}
  },
  "contrast_with": ["check:brief_kerb_stop"],
  "counterfactual": {"kind": "term", "ref": "check:brief_kerb_stop"},
  "exemplar_instances": ["kerbside_dwell"]
}
```

**`vocabulary_digest`** is the SHA-256 of the whole vocabulary, core and namespaces, as the plan
publishes it. Two captures with the same digest use the same words with the same meanings. Every
sidecar, the manifest's first row and the plan carry it. In the sample capture it is
`0fbcea06c4a28575486d05528b3bdf029d139a829c8ec7a3f0adc71527f9ece6`.

**Where to read a definition.** The definitions are in the plan, under `vocabulary.namespaces`, and in
the scenario specification ([vocabulary fields](../Schemas/Scenario_Specification.md#the-vocabulary-and-the-supervision)).
The capture folder does not hold them. The manifest's first row names the plan's path and SHA-256, and
the namespaces and versions it uses ([`manifest_opened`](../Schemas/Run_Manifest.md#manifest_opened)).

### Supervision states

Every label row asserts one of three states, which are core words:

- **`annotated`**: the vehicle carries out the named behavior.
- **`nominal`**: an authored negative. The vehicle carries out none of the target behaviors. An author
  can choose it because it looks like one of them (see [Hard negatives](#hard-negatives)).
- **`unlabelled`**: no assertion. It is not a negative: the author has said nothing about the vehicle.
  The word is an identifier and keeps this spelling.

Every SUMO vehicle a frame drew has a state in its sidecar record, `unlabelled` included. The plan lists
every authored vehicle no row names as `unlabelled`, and every flow no cohort names as an `unlabelled`
cohort, so a missing row never stands for a negative.

### Instances, participants and roles

A plan holds three kinds of label rows ([Supervision plan](../Schemas/Supervision_Plan.md)):

- **An instance** is one assertion about one or more authored vehicles. Its id is
  `<scenario_id>/<name>`, such as `Arapahoe_I25_SupervisionCheck/kerbside_dwell`. It has a state
  (`annotated` or `nominal`), its labels, its parameters, and its **participants**: each a vehicle, by
  its `sumo_id`, with a **role**. A one-vehicle instance names its vehicle `subject`. An instance with
  several vehicles uses roles its namespace declares: the Bahonar escort instance
  `pi_escort_drydock_d3` names one `bahonar:lead` and four `bahonar:follower`s.
- **A series** reads a schedule as a recurring behavior. It has one slot for each vehicle the schedule
  sends, and every slot's vehicle plays the series' `member_role`. In a sidecar, its annotation's
  `instance` is `series:<series_id>`.
- **A cohort** labels every vehicle a flow sends, for each vehicle's whole life. It is `annotated` or
  `unlabelled`, never `nominal`, and has no intervals. In a sidecar, its annotation's `instance` is
  `cohort:<flow_id>`. Its vehicles' ids are `<flow_id>.<n>`.

### Phases and intervals

An instance can declare **intervals**: the stretches of time its labels hold, one for each participant
and **phase**. The author names the phases, such as `dwell`, `transit` and `past_the_kerb`. An interval
is named by three things together: its instance, its participant and its phase. Two runs of one scenario
share these names.

An interval starts and ends in one of two ways:

- **On the vehicle's own events**, which the scenario calls anchors: `depart`, its insertion; `stop:<i>`,
  arriving at its stop number `i`; `stop_end:<i>`, leaving that stop; `phase:<i>`, entering the part of
  its route numbered `i`. Numbers count from 0. An interval with no end anchor stays open until the
  vehicle leaves the simulation or the run ends.
- **At declared times**: a civil start and end, or a duration.

When a vehicle carries an instance's labels:

- **An instance with intervals** labels each participant only while one of that participant's
  intervals is open. The annotation's `phase` names the interval. If two are open, the vehicle carries
  two annotations. Outside its intervals the vehicle is `unlabelled`, unless another row labels it.
- **A participant with no intervals of its own** carries the instance's labels for as long as SUMO has
  it. So does every vehicle of an instance that declares no intervals.
- **A series** labels each slot's vehicle for as long as SUMO has it. The slot's declared start and end
  are in the plan and in the manifest's `series` row.
- **An annotated cohort** labels each vehicle of its flow for as long as SUMO has it.

### The three onsets

An interval's start is recorded three ways, each from a different source:

- **Declared**: what the author wrote, from the plan. `declared_start_s` and `declared_start_civil`
  are a declared start, and `declared_duration_s` a declared length. An interval anchored to a stop of
  set length declares that length but no start time.
- **Committed**: when SUMO's model did it, on SUMO's clock at the step that reported it
  (`committed_start_s`, `committed_end_s`). An interval declared by time alone has no committed onset.
- **Observed**: when the drawn body showed it (`observed_start_s`, `observed_start_frame`). For a
  departure it is the first frame that drew the vehicle. For a stop it is the first frame on which the
  drawn body's speed was at or below 0.15 m/s. Frames between SUMO steps fill in the body's position, so
  the body can come to a standstill up to one SUMO step before the committed onset. A start anchored to
  anything else, or declared by time, has no observed onset. Neither has an interval that began before
  the capture window opened.

An interval opens at its committed onset where it has one, and at its declared start otherwise. A
missing onset is written as `null`, never filled in from another.

The observed onset is known only after a frame has shown it, so the `interval_opened` row, written as the
interval opens, always has it `null`. Read it from the `interval_closed` row.

### Why an interval closed

The `interval_closed` row's `closed_by` is a core word: `trigger` (the authored end happened),
`entity_arrived`, `sumo_removed`, `never_inserted`, `physical_predicate_never_held` (SUMO committed a
stop and the drawn body never stood still: a defect, not a behavior), `render_released`,
`capture_window_end` or `scenario_end`. Each is defined on the
[Run manifest](../Schemas/Run_Manifest.md#interval_opened-and-interval_closed) page. The row's
`not_drawn` lists the spans in which no body drew the vehicle while the interval was open.

## Hard negatives

A `nominal` row asserts that its vehicles carry out none of the target behaviors. A **hard negative**
is a nominal vehicle the author chose because it looks like a target behavior and is not one.

`hard_negative_for` lists the behaviors a nominal row is a matched negative for. The list comes from
the definition of the row's own labels: the term declares it, and the row carries a copy. `null` means
not specified. It does not mean "a negative for nothing".

`hard_negative_for` is on the manifest's [`instance`](../Schemas/Run_Manifest.md#instance) and
[`series`](../Schemas/Run_Manifest.md#series) rows and in the plan. It is not in the sidecar. To find it
for a vehicle in a still, take the `instance` of the vehicle's `<annotation>` and look up that row.

Two examples:

- **Arapahoe supervision check.** A van, `brief_stopper`, stops at the same curb as the `dweller` for
  twenty seconds. Its instance is `nominal`, labeled `check:brief_kerb_stop`, and its manifest row says
  `"hard_negative_for":["check:kerbside_dwell"]`. The term's definition says why: "A van pulls to the
  same curb for twenty seconds, as a delivery or a pick-up does, and drives on: an ordinary stop where
  the dwell happens."
- **Shahid Bahonar Port.** Guards relieve sixteen towers every eight hours. The series `tower_relief`
  is `nominal`, labeled `bahonar:tower_posting`, a "long parked dwell, in a legitimate place, for a
  legitimate reason". Its `hard_negative_for` is `bahonar:standoff_dwell_at_access_point`,
  `bahonar:arrival_without_departure` and `bahonar:posting_not_taken_up`. The 21 routine freight hauls
  are `nominal` `bahonar:routine_freight_haul`, a hard negative for `bahonar:coordinated_group_transit`
  and `bahonar:destination_off_pattern`.

So, for a behavior X, the record says three different things: a vehicle `annotated` with X is asserted
to carry out X during its interval; a `nominal` vehicle whose `hard_negative_for` holds X is asserted not
to, and was chosen because it resembles X; an `unlabelled` vehicle is asserted nothing.

## A label follows its vehicle

Every label row is about a vehicle, or about the vehicles of a flow. Nothing is labeled about a place,
an area or the scene as a whole. SUMO reports vehicles, not places, so a label always has a vehicle to
follow, and that vehicle carries it in every still that draws it.

This holds for an **omission** too: something expected that does not happen. Nothing is written for the
place where it fails to happen. The author labels the vehicle that deviates instead.

The Bahonar scenario shows this. Its schedule sends a guard to each of 16 towers at 07:00, 15:00 and
23:00 on each of 7 days: 336 postings. On day 4 at 07:00 the guard due at tower 3 does not take up the
post. The schedule skips that posting, so the series has 335 slots and no vehicle is sent to tower 3.
Nothing is labeled about the empty tower. The guard vehicle that was due, `offpost_d4_h7_t3`, departs
on time and parks on the west apron spur for the eight-hour shift. It carries the label, with
parameters that name the tower and the shift it missed:

```json
{
  "instance_id": "Shahid_Bahonar_Port_PatternOfLife/pi_posting_not_taken_up_d4",
  "supervision": "annotated",
  "labels": ["bahonar:posting_not_taken_up"],
  "parameters": {"expected_tower": "tower_03", "expected_shift_start": "2026-10-03T07:00:00+03:30"},
  "hard_negative_for": null,
  "counterfactual": null,
  "aoi_refs": [],
  "participants": [{"entity_id": "offpost_d4_h7_t3", "role": "subject", "sumo_id": "offpost_d4_h7_t3"}],
  "intervals": [
    {"entity_id": "offpost_d4_h7_t3", "phase": "dwell",
     "anchor": {"start": {"event": "stop:0", "lane": "-441624290#0_0", "end_pos_m": 70.0},
                "end": {"event": "stop_end:0", "lane": "-441624290#0_0", "end_pos_m": 70.0}},
     "declared_start_s": null, "declared_start_civil": null,
     "declared_end_s": null, "declared_end_civil": null,
     "declared_duration_s": 28800.0}
  ]
}
```

The term's definition reads: "A guard due to relieve a tower departs on schedule but parks elsewhere
for the shift; the tower it was due at goes unmanned." The other 15 guards of that shift carry the
series' `bahonar:tower_posting`, which is a hard negative for it.

## Where labels appear

### In each still: `<_supervision>`

Every SUMO vehicle a frame drew has a `<_supervision>` element in its sidecar record, whether it is in
the picture or not ([Truth sidecar](../Schemas/Truth_Sidecar.md#_supervision-and-annotation-what-the-scenarios-author-asserts)).
It holds what was in force for that vehicle on that frame, and nothing from any other frame.

- `state`: `annotated`, `nominal` or `unlabelled`.
- `vocabulary` and `vocabulary_digest`: the vocabulary the words come from.
- One `<annotation>` for each labeled row in force for the vehicle:
  - `instance`: the row. `<scenario_id>/<name>` for an instance, `series:<series_id>` for a series,
    `cohort:<flow_id>` for a cohort.
  - `labels`: its terms, separated by spaces.
  - `phase`: the open interval's phase; absent for a row with no intervals.
  - `role`: the vehicle's role; absent where the row declares none, as a cohort does not.

A vehicle that one row labels `annotated` and another `nominal` is `annotated`, and only its annotated
rows are listed. An `unlabelled` vehicle has no `<annotation>`. The `<events>` container names the
plan in force, `plan_id`, with `vocabulary` and `vocabulary_digest`. A container that says `supervision="unknown"`
means a plan was in force but the frame's labels could not be read, so no vehicle in that still
carries any. Do not read those vehicles as `unlabelled`.

In the sample capture, the car `transit` carries two annotations while it passes the curb, one for each
open interval of its instance:

```xml
<_supervision state="annotated" vocabulary="3" vocabulary_digest="0fbcea06c4a28575486d05528b3bdf029d139a829c8ec7a3f0adc71527f9ece6">
  <annotation instance="Arapahoe_I25_SupervisionCheck/through_transit" labels="check:through_transit" phase="transit" role="subject" />
  <annotation instance="Arapahoe_I25_SupervisionCheck/through_transit" labels="check:through_transit" phase="past_the_kerb" role="subject" />
</_supervision>
```

### In the run: the manifest

The [run manifest](../Schemas/Run_Manifest.md) records the plan as declared and the run as it bound it:

- **`instance`, `series` and `cohort` rows**, right after the first row: every row of the plan, as
  declared, with its state, labels, parameters, `hard_negative_for`, participants and roles, and an
  instance's intervals with their anchors and declared times.
- **`interval_opened` and `interval_closed` rows**, as each interval opens and closes: the three onsets,
  whether it began before the capture window opened, and on closing, `closed_by`, the committed end and
  `not_drawn`.
- **`supervision_defect` rows**: a fault in how the run carried out the plan, in words, such as a stop
  the drawn body never stood still for.
- **The last row, `manifest_closed`**: `open_intervals`, the intervals still open as the run ended, with
  `open_intervals_close_as`, the reason they close with; and `never_opened`, the intervals nothing in
  the run opened or closed. With the interval rows, a closed manifest names every interval the plan
  declares.

Times in the manifest are on SUMO's clock. To find the stills an interval covers, put each still on
that clock by its frame number, as [What a capture folder holds](Capture_Folder.md#how-the-stills-line-up-with-the-manifests-intervals)
explains. A still's own `sim_time_s` is on another clock.

The world truth track carries no labels. Join them to its rows by `sumo_id`.

## Worked example: the Arapahoe supervision check

The scenario `Arapahoe_I25_SupervisionCheck` runs six minutes on South Yosemite Street, just north of
East Arapahoe Road. Its files are in `Import/`: the specification
`Arapahoe_I25_SupervisionCheck.scenario.json` and the plan `Arapahoe_I25_SupervisionCheck.supervision.json`.
Among nine flows of background traffic, all `unlabelled` cohorts, it adds three vehicles:

| Vehicle | What it does | Instance | State and label | Interval |
|---|---|---|---|---|
| `dweller` | departs at 20 s, waits 120 s at the curb, drives on | `kerbside_dwell` | `annotated`, `check:kerbside_dwell`, `dwell_s` 120 | `dwell`, from `stop:0` to `stop_end:0` |
| `transit` | departs at 90 s and drives through, past the same curb | `through_transit` | `annotated`, `check:through_transit` | `transit`, from `depart`; `past_the_kerb`, from `phase:1` to `phase:2` |
| `brief_stopper` | departs at 200 s, stops 20 s at the same curb | `brief_stop` | `nominal`, `check:brief_kerb_stop`, hard negative for `check:kerbside_dwell` | `stop`, from `stop:0` to `stop_end:0` |

The sample capture, `cap-20261008-041347-270d6d`, covers the window `dwell_golden`: 60 s to 240 s, two
stills a second from one camera looking down at the curb. Here is what the record shows, on SUMO's
clock:

| SUMO time | What happened | Where it is recorded |
|---|---|---|
| 20.05 s | SUMO inserts `dweller`, and a body draws it from frame 149499. | `render_admitted` |
| 60 s | The capture window opens at frame 150298. The `dweller` is drawn and `unlabelled`. | `solar_window_open`; the first still |
| 90.05 s | SUMO inserts `transit`. Its `transit` interval opens: declared 90, committed 90.05. | `interval_opened` |
| 90.5 s | First still with `transit` `annotated`, phase `transit`. It is not in the picture yet. | sidecar, frame 150908 |
| 104.0 s | `dweller` comes into the picture with its right turn signal on. | sidecar, frame 151178 |
| 109.4 s | `dweller` stops at the curb. Its `dwell` interval opens: committed 109.4, observed 109.4 at frame 151286. | `interval_opened`; the observed onset on `interval_closed` |
| 109.5 s | First still with `dweller` `annotated`, phase `dwell`. | sidecar, frame 151288 |
| 144.15 s | `transit` enters part 1 of its route, northbound on Yosemite from Arapahoe. Its `past_the_kerb` interval opens. | `interval_opened` |
| 155.0 s to 159.5 s | `transit` is in the picture, with two annotations, beside the parked `dweller`. | sidecars, frames 152198 to 152288 |
| 160.4 s | `transit` enters part 2 of its route. `past_the_kerb` closes by `trigger`. | `interval_closed` |
| 200.05 s | SUMO inserts `brief_stopper`. It is drawn, never in the picture, and `unlabelled`: its stop has not begun. | `render_admitted`; sidecars |
| 229.4 s | `dweller` leaves the curb. `dwell` closes by `trigger`. | `interval_closed` |
| 229.5 s | First still with `dweller` `unlabelled` again. | sidecar, frame 153688 |
| 240 s | The window closes. The `transit` interval is still open and closes as `capture_window_end`. The `brief_stop` interval never opened. | `manifest_closed`: `open_intervals`, `never_opened` |

In numbers: 539 vehicle records in the 360 stills are `annotated`: 240 for the `dweller`'s dwell and
299 for `transit`. The other 17,453 are `unlabelled`. No record is `nominal`, because the van's stop
had not begun when the window closed. Its `never_opened` entry is the record of that.

Three things this shows:

- **A participant is `unlabelled` outside its intervals.** The `dweller` is `unlabelled` while it drives
  to the curb and after it leaves. The `brief_stopper` is `unlabelled` in all 79 stills that list it,
  although its instance is `nominal`.
- **Labels hold whether or not the vehicle is in the picture.** `transit` is `annotated` from 90.5 s
  but is in the picture only from 155.0 s to 159.5 s. Use `in_frame` to tell the two apart.
- **Some onsets are left empty.** The `transit` row was written as the interval opened, before a frame
  had drawn the car, and the interval never closed in the window. So the manifest holds no observed
  onset for it. Its `render_admitted` row shows the first frame that drew the car: frame 150899, at
  90.05 s.
