# The epoch, and writing civil time

A scenario is authored in civil time and SUMO counts seconds from zero. The `epoch` says which civil
instant zero is, and without it nothing — not the report, not the session, not the sun — can say what
time a vehicle departs. The compiler reads the epoch with the co-simulation session's own reader,
`CarlaNet.CoSim.SolarEpoch` (`CarlaControl/src/carlacontrol/ScenarioEpoch.py::ScenarioEpoch`), so a
scenario the compiler accepts is one the session accepts, with the same digest.

Design record: `Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/07_Scenario_Authoring.md` §3.5.1,
§4.5 and §8.2.1; the object is `04_Contracts.md` C9.

## The object

```json
{"epoch_version": 1, "civil_datetime": "2026-03-21T00:00:00+03:30", "utc_offset_hours": 3.5,
 "utc_datetime": "2026-03-20T20:30:00Z", "calendar_advances": true, "dst_in_effect": false,
 "time_zone_id": "Asia/Tehran", "note": "optional prose for a reader"}
```

- `utc_offset_hours` is **normative**: a whole number of quarter hours in [−12, +14] (check 34).
- `utc_datetime` must be `civil_datetime` less the offset. The commonest error, the offset applied in
  the wrong direction, is refused and named (check 33).
- `time_zone_id` is carried for a reader and **never resolved**: the engine has no zone database, and
  two hosts with different databases would disagree about a corpus.
- `calendar_advances` says whether day N falls on the epoch's date plus N days for the sun, or the sun
  keeps the epoch's date.

## Six conventions, in the order they go wrong

1. **Ask for the epoch; never assume it.** If the author has not said what civil date and time `t = 0`
   is, ask, and offer a candidate from the site — the world package's `solar.json` gives the origin
   and `longitude / 15`. A guessed epoch compiles and asserts the wrong thing. Every scenario declares
   one (check 33).
2. **Write civil times; never multiply.** `"d0 07:00"`, not `25200`. The sizing scenario had sixteen
   multiplication sites and 533 of 610 departures on exact hours; under the epoch it has none, and the
   civil meaning of all 610 is in the report.
3. **`t = 0` is conventionally midnight, and need not be.** Day N is the epoch's date plus N days and a
   clock is read at the epoch's offset, so a 06:00-to-06:00 day is an epoch at 06:00.
4. **Never write a SUMO `H:M:S` literal**, even on a hand-typed command line: `--begin 7:00:00` is step
   25 200, an offset wearing a clock's clothes (`references/gotchas.md` 12).
5. **A half-hour offset is ordinary.** Bahonar is Iran, **+03:30**; Nepal is +05:45. Nothing special
   is needed for either.
6. **Declare the offset in force on the scenario's dates**, with `dst_in_effect` saying whether it
   includes daylight saving. One offset holds for the whole run: Colorado in March is −06:00 with
   `dst_in_effect: true`, and a scenario spanning a transition is an hour off local clocks on one side
   of it unless it is split. Iran observes no daylight saving.

## Against the world

The compiler compares the declared offset with the zone the world's georeference configures,
`longitude / 15` from `solar.json`, and **warns** past an hour (check 40) — a declared offset that is
not this place's. It never refuses: the session writes the declared offset as the sun's zone, so a
difference of minutes moves no sun. At Bahonar `longitude / 15` is +03:44:43 against Iran's +03:30.

`examples/epoch/` holds one whole-hour, one daylight-saving and one +03:30 epoch, each compiled in the
test suite with its report recorded beside it.
