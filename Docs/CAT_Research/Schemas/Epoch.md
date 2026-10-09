# Epoch

| | |
|---|---|
| File | the `epoch` object of a scenario specification, or a file of its own such as `<site>.epoch.json` |
| Schema | `CarlaControl/schemas/epoch.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:epoch:1` |
| Format version | 1, in `epoch_version` |

## What it is

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

## Who writes it and who reads it

- A scenario developer writes it, in the specification's `epoch` block.\
  The scenario compiler checks it (checks 33 and 34) and copies it into the scenario lock.
- `carla-drive --epoch FILE` reads an epoch from a file: either the epoch object on its own, or a whole `scenario.json`.\
  From a whole specification, it reads the `epoch`.\
  It also reads the specification's `illumination` as the sun's policy.\
  It checks the epoch against this schema before the session sees it.\
  It refuses a file that does not conform.\
  The refusal quotes the schema.
- The co-simulation session (`CarlaNet.CoSim.SolarEpoch`) makes the final decision on whether an epoch is valid.\
  It also checks what a schema cannot:
  - the civil and UTC instants are one instant
  - the offset in `civil_datetime` equals `utc_offset_hours`
  - every date and time exists

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

## Versions

This page describes version 1, the only version.\
An epoch with no `epoch_version`, or any value other than 1, is refused: a time declaration is never read in part.

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
carla-drive --scenario Arapahoe_I25_SupervisionCheck.sumocfg --world-package Arapahoe_I25.cwp --epoch arapahoe.epoch.json --illumination freeze_at_window_start
```
