# Worked examples

Each example is a specification with the resolution report its compile produced, recorded beside it.
`CarlaControl/test/test_skill_examples.py` compiles every one in the ordinary test run and compares what
it resolves to with the recorded report, so an example here is one the compiler accepts today and
resolves exactly as shown.

**The world.** The examples run on the compiler's fixture world, a street layout built by
`CarlaControl/test/ScenarioWorldFixture.py`: West Street and East Street run east–west through the
origin, Cross Street crosses East Street at a signal 100 m east of it, and an area of interest, `kerb`,
sits on East Street's eastbound lane. An example names it `StreetLayout.cwp` and names the catalogue by
its path in the repository; the test builds the package, copies the example beside it and points the
catalogue at the repository's.

| Example | What it shows |
|---|---|
| `minimal/street_layout_minimal.scenario.json` | The least a scenario declares: the world and its fingerprint, the epoch, the illumination default, the seed, the run, the catalogue, vehicle classes, and places — two gateways and a surveyed point — used by one flow and by one actor that stops at the point. No label is asserted |
| `counterfactual/street_layout_probe.scenario.json`, `probe_standoff_pairs.sweep.json` | Supervision of every kind — an annotated instance, a nominal hard negative, an unlabelled cohort and a rota read as a recurring series, whose one skipped occasion writes no trip and no slot — and a sweep pairing the annotated actor three ways: `nominal` with its stop removed, `absent`, and `displaced` thirty minutes. `probe_standoff_pairs.sweep-index.json` is the recorded index |
| `epoch/street_layout_epoch_whole_hour.scenario.json` | Mountain Standard Time, −07:00, no daylight saving |
| `epoch/street_layout_epoch_daylight_saving.scenario.json` | Mountain Daylight Time, −06:00 with `dst_in_effect: true` |
| `epoch/street_layout_epoch_half_hour.scenario.json` | Iran's +03:30 declared on this Colorado world: it compiles as declared, check 40 warns that the world's georeference configures −06:59:36, and because 06:55 at +03:30 is 03:25 UTC, the window's declared sun is 25.75° below the horizon and check 42 warns too. The offset is what the sun is computed from |

**Generated from a program.** `CarlaControl/scripts/make_sumo_scenario.py` writes the Gardnerville orbit as
a specification — the orbit as three route phases, twenty laps held to 11 m/s — and compiles it into
`Import/`, where the specification and its resolution report sit beside the compiled package.
`CarlaControl/test/test_gardnerville_generator.py` holds both to what the script produces.

After a deliberate change to what an example resolves to, record its report again with
`CARLACONTROL_RECORD_SKILL_EXAMPLES=1` set for `test_skill_examples.py`, and read the diff.
