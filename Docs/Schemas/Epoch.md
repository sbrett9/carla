# Epoch

| | |
|---|---|
| File | the `epoch` object of a scenario specification, or a file of its own such as `<site>.epoch.json` |
| Schema | `CarlaControl/schemas/epoch.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:epoch:1` |
| Format version | 1 |
| Version field | `epoch_version` |

## Description

SUMO counts simulated seconds from zero and knows nothing else about time.\
The epoch says what simulated second zero is in civil time at the site.\
Every civil time a tool reports is computed from it.\
The sun a capture is lit by is computed from it too.

The instant is written twice: once in civil time with its offset and once in UTC.\
The two must be the same instant, to the second.\
This check catches the most likely mistake, an offset applied in the wrong direction.\
It names the mistake.

The offset is the declared value.\
If daylight saving is in force, the offset includes it.\
`dst_in_effect` says whether daylight saving is in force.\
Half-hour and quarter-hour offsets are ordinary: Iran is +03:30.

The time zone name is carried for a reader and never looked up.\
Two machines with different time zone databases therefore always agree.

## Writers and readers

- A scenario developer writes it, in the specification's `epoch` block.\
  The scenario compiler checks it (checks 33 and 34) and copies it into the scenario lock.
- `carla-drive --epoch FILE` reads an epoch from a file: either the epoch object on its own, or a whole `scenario.json`.\
  Every drive that renders a world must declare an epoch, under every sun policy.\
  Every still is named and stamped from the scenario's civil time, so the session refuses a drive without an epoch.\
  From a whole specification, it reads the `epoch`.\
  It also reads the specification's `illumination` as the sun's policy.\
  Without one, the sun advances at a rate of 1.0 by default.\
  It checks the epoch against this schema before the session sees it.\
  It refuses a file that does not conform.\
  The refusal quotes the schema.
- `carla-drive --epoch-at INSTANT` moves simulated second zero for one run.\
  Without `--epoch`, it reads the epoch from the scenario lock beside `--scenario` and checks it against this schema.\
  See [Moving simulated second zero for one run](#moving-simulated-second-zero-for-one-run).
- `carla-capture` reads the epoch from the scenario lock as `scenario.epoch`, a field the run cannot change.\
  `scenario.epoch_at` moves simulated second zero for one run.
- The co-simulation session (`CarlaNet.CoSim.SolarEpoch`) makes the final decision on whether an epoch is valid.\
  It also checks what a schema cannot:
  - the civil and UTC instants are one instant
  - the offset in `civil_datetime` equals `utc_offset_hours`
  - every date and time exists

  `SolarEpoch.ReadInstant` reads the instant a run moves simulated second zero to.\
  `SolarEpoch.MovedTo` moves the epoch.

## Moving simulated second zero for one run

A run can put simulated second zero at another instant without recompiling the scenario.\
`carla-drive --epoch-at` and `carla-capture`'s `scenario.epoch_at`, short option `--epoch-at`, take the instant.\
SUMO runs the scenario's traffic second for second as compiled.\
Every civil time the run states follows the new second zero.

The instant is a UTC date and time, such as `2026-10-22T17:40:00Z`, with up to seven digits of fraction.\
The `Z` may be left off.\
A zero offset, `+00:00` or `-00:00`, is read as `Z`.\
Any other offset is refused.\
The refusal names the same instant in UTC.\
The run shows the instant at the scenario's own offset.

Only the instant moves.\
The run's epoch is the compiled epoch with `civil_datetime` and `utc_datetime` naming the new instant, at the compiled epoch's offset.\
`utc_offset_hours`, `dst_in_effect`, `calendar_advances`, `time_zone_id` and `epoch_version` are kept as written.\
A new `note` names the move, the instant moved from and the compiled epoch's digest.\
A move to the compiled epoch's own instant moves nothing: the run's epoch is the compiled epoch.

Bahonar's compiled epoch is 07:00 at +03:30 on 29 September 2026.\
With simulated second zero moved to `2026-10-22T17:40:00Z`, the run's epoch is:

```json
{
  "calendar_advances": true,
  "civil_datetime": "2026-10-22T21:10:00+03:30",
  "dst_in_effect": false,
  "epoch_version": 1,
  "note": "Simulated second zero moved at run time to 2026-10-22T21:10:00+03:30 from 2026-09-29T07:00:00+03:30, the epoch f1ba0ddab35f800a4b78ebc902fa9a1dd9d47a64fee72eb3cfab687969bbecdf.",
  "time_zone_id": "Asia/Tehran",
  "utc_datetime": "2026-10-22T17:40:00Z",
  "utc_offset_hours": 3.5
}
```

Its `epoch_block_sha256` is `59f0d5a8bb8bbe38a03880bc02f902cfefcad4bade22cb722da313c292d5fbd8`.\
Simulated second 271169, 3 d 03:19:29 after second zero, is then `2026-10-26T00:29:29+03:30`.\
Under the compiled epoch it is `2026-10-02T10:19:29+03:30`.

The session checks the scenario lock against the compiled epoch, before the move.\
So a move needs a compiled scenario.\
A scenario with no lock beside its `.sumocfg` is refused.\
So is a run whose epoch is not the lock's.\
The refusal names both digests.

These are on the run's epoch:

- the sun, which the session sets and audits on the moved clock
- every still's name, its sidecar's `captured` stamp and its illumination chunk
- the world truth track's `time_utc`
- the stamps of the live Cursor-on-Target feed
- the run manifest's `solar.epoch` and every civil time it writes: the window's, each supervision interval's declared start and end, and the `civil` of each label parameter of type `time`
- the session's report, `carla-capture`'s launch echo and the run result's `produced.window.civil`

These record the compiled epoch:

- the run manifest's `solar.epoch_moved_from` and `scenario.epoch_digest`
- the session's report, on its `moved from` line
- the launch echo's `civil.scenario_epoch_block_sha256` and the run lock's `inputs.epoch_block_sha256`

The compiled scenario's own files stay on the compiled epoch.\
The civil times in the scenario lock's `capture_windows`, in the supervision plan and in the compiler's resolution report are the compiled epoch's.\
See [Scenario lock](Scenario_Lock.md).

## Fields

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `epoch_version` | constant `1` | | yes | The format version of this object. |
| `civil_datetime` | string, ISO 8601 with offset | | yes | Simulated second zero in civil time, such as `2026-03-21T00:00:00+03:30`. Fractional seconds are allowed to seven digits. `Z` is allowed only where the offset is zero. |
| `utc_offset_hours` | number, a multiple of 0.25 from -12 to 14 | hours | yes | The site's offset from UTC, daylight saving included: 3.5 for +03:30. It must equal the offset written in `civil_datetime`. |
| `utc_datetime` | string, ISO 8601 ending `Z` or `+00:00` | | yes | The same instant in UTC, such as `2026-03-20T20:30:00Z`. |
| `calendar_advances` | boolean | | yes | Whether the civil date moves on as simulated time passes a civil midnight. False keeps the sun's date at the epoch's date while the clock keeps running. |
| `dst_in_effect` | boolean | | yes | Whether `utc_offset_hours` already includes daylight saving. |
| `time_zone_id` | string or null | | no | The IANA time zone of the site, such as `America/Denver`, for a reader. Never looked up. |
| `note` | string or null | | no | One sentence saying what simulated second zero is in the scenario's own terms. |

No other field is allowed.

The SHA-256 of the epoch, written with its keys sorted and a two-space indent, is its `epoch_block_sha256`.\
The scenario lock and every run record name the epoch by it.

## Example

Arapahoe County, Colorado, on a September morning under Mountain Daylight Time:

```json
{
  "epoch_version": 1,
  "civil_datetime": "2026-09-29T07:26:00-06:00",
  "utc_offset_hours": -6,
  "utc_datetime": "2026-09-29T13:26:00Z",
  "calendar_advances": true,
  "dst_in_effect": true,
  "time_zone_id": "America/Denver",
  "note": "Simulated second zero is 07:26:00 Mountain Daylight Time on September 29, 2026."
}
```

```
carla-drive --scenario Arapahoe_I25_SupervisionCheck.sumocfg --world-package Arapahoe_I25.cwp --epoch arapahoe.epoch.json --illumination advance --solar-rate 1.0
```

## Versions

This page describes version 1, the only version.\
An epoch with no `epoch_version`, or any value other than 1, is refused: a time declaration is never read in part.
