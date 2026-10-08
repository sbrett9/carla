# Scenario lock

| | |
|---|---|
| File | `<scenario_id>.lock.json`, in the compiled scenario's folder |
| Schema | `CarlaControl/schemas/scenario_lock.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:scenario-lock:1` |
| Format version | 1, in `lock_version` |

## What it is

The scenario lock records what one compiled scenario is bound to.\
It holds:

- the SHA-256 of its specification and of every file the compile wrote that decides the traffic or the labels;
- the world it was compiled against, the vehicle catalog and the vocabulary;
- SUMO's seed, step and options;
- what a SUMO-only run of the compiled files showed;
- the epoch, the illumination default and the capture windows.

The same specification, seed and world give the same traffic.\
The lock is how a run proves that the files it is about to drive are the ones that were compiled.

**This is not the run lock.**\
A capture run writes `run.lock.json` about one run of the scenario (see [Run lock](Run_Lock.md)).

## Who writes it and who reads it

- **`carla-compile-scenario` writes it** with the scenario's other files, and only when the compile succeeds.
- **`carla-capture` reads it** to bind the scenario: by id under `paths.scenario_root`, by folder, or by path.\
  It takes the epoch, the SUMO step, seed and end, the catalog digest and the illumination default from it.\
  It refuses a lock of another version (run check 6), a world that does not match (run check 5), a catalog that does not match (run check 48), and a skipped SUMO-only run unless the run accepts it (run check 54).
- **The co-simulation session reads it** when it starts, under `carla-capture` and `carla-drive` alike: the lock beside the `.sumocfg` it is given.\
  It refuses a scenario whose configuration, route file, network, lane closures or supervision plan is not the one the lock digests, or whose catalog or epoch is not the one the lock records.\
  A scenario with no lock beside it still runs, and the run records it as uncompiled.

## Fields

Every field is always present, except `producer` in a file written before October 7, 2026.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `lock_version` | constant `1` | | The format version of this file. |
| `scenario_id` | string | | The scenario's id. |
| `scenario_name` | string | | Its name for a person. |
| `spec_version` | constant `1` | | The specification format version compiled. |
| `specification` | string | | The specification's file name. |
| `specification_sha256` | string, 64 hex digits | | SHA-256 of the specification's bytes. |
| `compiler.name` | constant `"carlacontrol.ScenarioCompiler"` | | The compiler. |
| `compiler.version` | string | | The compiler's release, without the commit, so recompiling at another commit of one release names the same compiler. |
| `producer` | object | | What wrote the file, with the commit and the SUMO release that routed it. See [Run result](Run_Result.md#the-producer-record). |
| `files.routes` | `{path, sha256}` | | The route file, `<scenario_id>.rou.xml`. |
| `files.config` | `{path, sha256}` | | The SUMO configuration, `<scenario_id>.sumocfg`. |
| `files.network` | `{path, sha256}` | | The world's network, copied as `<MapName>.net.xml`. |
| `files.additional` | `{path, sha256}` | | The lane closures, `<scenario_id>.add.xml`. Present only when the specification declares lane closures. |
| `files.supervision` | `{path, sha256}` | | The supervision plan, `<scenario_id>.supervision.json`. |
| `world.package` | string | | The world package's file name. |
| `world.map_name` | string | | The map name. |
| `world.network_fingerprint` | string, 64 hex digits | | The canonical fingerprint of the network: of the road graph, not the file's bytes. |
| `world.netconvert_argv` | array of strings | | The netconvert command that built the world, with machine paths replaced by placeholders such as `<output-file>`. |
| `world.netconvert_version` | string | | The netconvert release that built the world. |
| `world.opendrive_sha256` | string | | SHA-256 of the world's OpenDRIVE. |
| `world.source_osm_sha256` | string | | SHA-256 of the OpenStreetMap extract the world was built from. |
| `world.origin_latitude`, `world.origin_longitude` | number | degrees | The map's origin. |
| `world.georeference` | string | | The map's projection, as a PROJ string. |
| `catalogue.catalogue_id` | string | | The vehicle catalog's id. |
| `catalogue.catalogue_digest` | string | | The catalog's digest. A run's catalog must match it. |
| `catalogue.blueprint_set_digest` | string | | The digest of the set of blueprints the catalog measured. |
| `catalogue.content_build_id` | string | | The content build the catalog was measured on. |
| `vocabulary.core_version` | integer | | The core vocabulary's version. |
| `vocabulary.namespaces` | array of `{namespace, version}` | | The author's namespaces. |
| `vocabulary.vocabulary_digest` | string, 64 hex digits | | SHA-256 of the whole vocabulary, as the supervision plan carries it. |
| `traffic.sumo_seed` | integer | | SUMO's seed. |
| `traffic.step_length_s` | number | s | SUMO's step. |
| `traffic.end_s` | number | s | The simulated second the scenario ends. |
| `traffic.processing` | object | | The SUMO options the compiler fixes: `time-to-teleport` `"-1"` (no teleports), `max-depart-delay` `"900"`, `collision.action` `"warn"`, `lanechange.duration` `"3"`. |
| `traffic.routed_by.tool` | constant `"duarouter"` | | The router. |
| `traffic.routed_by.version` | string | | Its release. |
| `traffic.routed_by.world_converter` | string | | The netconvert release that built the world. |
| `traffic.routed_by.release_agreement` | `SameRelease`, `NotRecorded`, `MismatchAccepted` or `Mismatch` | | Whether the router's release is the world converter's (check 6). |
| `traffic.routed_by.mismatch_accepted` | boolean | | Whether a different release was accepted with `--allow-sumo-version-mismatch`. |
| `dry_run` | object | | What a SUMO-only run of the compiled files showed (check 59). See below. |
| `epoch` | object | | The epoch, keys sorted. See [Epoch](Epoch.md). |
| `epoch_block_sha256` | string, 64 hex digits | | SHA-256 of the epoch. |
| `illumination` | object | | The specification's illumination default, as written. |
| `capture_windows` | array | | Each declared window: `id`, `begin_s`, `end_s` (simulated seconds), `civil_begin`, `civil_end` and `civil_date`. |
| `ephemeris` | string | | What computes the sun. |
| `illumination_label_association` | object | | The headline of check 41: `normalized_mutual_information` (or null), `bands` (route entries by illumination band and supervision state, or null) and `entries`. |

`dry_run` takes one of two forms:

| Field | Type | Meaning |
|---|---|---|
| `ran` | `true` | The compiled files were run in SUMO alone over the whole span. |
| `sumo_release` | string | The SUMO release that ran them. |
| `begin_s`, `end_s` | number, s | The span run. |
| `vehicles` | object | SUMO's counts: `loaded`, `inserted`, `discarded` (after waiting `max-depart-delay`), `waiting_at_end`. |
| `planned_vehicles` | object | The vehicles the supervision plan names: `total` and how many were `inserted`. |
| `collisions` | integer | How many collisions SUMO reported. |

or `{"ran": false, "reason": "..."}` when the compile was run with `--skip-dry-run`.

## Versions

This page describes version 1, the only version.\
`carla-capture` and the co-simulation session read only version 1 and refuse any other, including a lock with no version.\
A file written before October 7, 2026 has no `producer`; it is still version 1.

## Example

`Import/Arapahoe_I25_SupervisionCheck.lock.json`, shortened:

```json
{
  "lock_version": 1,
  "scenario_id": "Arapahoe_I25_SupervisionCheck",
  "scenario_name": "Arapahoe supervision check",
  "spec_version": 1,
  "specification": "Arapahoe_I25_SupervisionCheck.scenario.json",
  "specification_sha256": "8a31c197058ad87c473f7cf502df52883429f42bcbd91feab18693925c4b0a77",
  "compiler": {"name": "carlacontrol.ScenarioCompiler", "version": "0.10.0"},
  "producer": {"tool": "carlacontrol.ScenarioCompiler", "tool_version": "0.10.0+g8ef0978cb",
               "carlanet": "0.10.0+g7d218c48c", "server": null, "sumo": "1.27.0",
               "written_utc": "2026-10-08T02:15:44.761Z"},
  "files": {
    "routes": {"path": "Arapahoe_I25_SupervisionCheck.rou.xml", "sha256": "016b810a..."},
    "config": {"path": "Arapahoe_I25_SupervisionCheck.sumocfg", "sha256": "79a64f25..."},
    "network": {"path": "Arapahoe_I25.net.xml", "sha256": "78185aa8..."},
    "supervision": {"path": "Arapahoe_I25_SupervisionCheck.supervision.json", "sha256": "6d71f0ee..."}
  },
  "traffic": {"sumo_seed": 42, "step_length_s": 0.05, "end_s": 360.0,
              "processing": {"time-to-teleport": "-1", "max-depart-delay": "900",
                             "collision.action": "warn", "lanechange.duration": "3"},
              "routed_by": {"tool": "duarouter", "version": "1.27.0",
                            "world_converter": "Eclipse SUMO netconvert 1.27.0",
                            "release_agreement": "SameRelease", "mismatch_accepted": false}},
  "dry_run": {"ran": true, "sumo_release": "1.27.0", "begin_s": 0.0, "end_s": 360.0,
              "vehicles": {"loaded": 133, "inserted": 133, "discarded": 0, "waiting_at_end": 0},
              "planned_vehicles": {"total": 3, "inserted": 3}, "collisions": 0},
  "epoch_block_sha256": "b92057175df8fcf7d3f5070d76aac03c301b672e891bd7b044b888efa8a24831",
  "illumination": {"illumination_version": 1, "policy": "freeze_at_window_start"},
  "capture_windows": [{"id": "dwell_golden", "begin_s": 60.0, "end_s": 240.0,
                       "civil_begin": "2026-09-29T07:27:00-06:00",
                       "civil_end": "2026-09-29T07:30:00-06:00", "civil_date": "2026-09-29"}],
  "...": "..."
}
```
