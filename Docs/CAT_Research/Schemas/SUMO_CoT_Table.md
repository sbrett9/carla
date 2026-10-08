# SUMO bridge table (`carla-cot-telemetry --csv`) and its summary

Given `--csv <file>`, `carla-cot-telemetry` writes one row per vehicle per update of a SUMO run: the
same vehicles and instants as its [event file](SUMO_CoT_Event_File.md), as a plain table. Beside it, as it
opens the CSV, it writes `<file stem>.summary.json`, which records the CSV's format version, its columns
and what made it. The CSV's own first line stays its header, so any CSV reader can read it.

- Table schema: `CarlaControl/schemas/sumo_cot_telemetry.tableschema.json` (Frictionless Table Schema)
- Table schema id: `urn:carla-sumo-capture:schema:sumo-cot-telemetry:1`
- Summary schema: `CarlaControl/schemas/sumo_cot_telemetry_summary.schema.json` (JSON Schema)
- Summary schema id: `urn:carla-sumo-capture:schema:sumo-cot-telemetry-summary:1`

## Who writes it and who reads it

`carlacontrol.SumoCotBridge` writes both. The table schema is generated from the bridge's column list,
`SumoCotBridge.CSV_COLUMNS`, so the two cannot drift apart.

`carla-check-label-leaks --csv` reads the CSV, to check that no column tells the planted vehicles
from the others. `carla-validate`, given a folder holding them, checks the CSV against the table schema
and the summary against its schema; a CSV with no summary beside it is noted as format version 1.

## The CSV

Comma separated, UTF-8, with a header line. A value with a comma in it, such as `color`, is quoted. An
empty cell is the empty string, a value, never a missing one: `special_type` is empty for most
vehicles. Every column is present in every row.

| Column | Type | Unit | Meaning |
|---|---|---|---|
| `time_utc` | date-time | | When the update happened: the run's epoch plus `sim_time_s`, ISO 8601 UTC to the millisecond. |
| `sim_time_s` | number | seconds | SUMO simulation time, two decimals. |
| `uid` | string | | The track id: `<uid prefix>-<SUMO id>`. The prefix is `SUMO-TRUTH` unless `--uid-prefix` sets it. |
| `callsign` | string | | `<base_type>-<SUMO id>`. |
| `cot_type` | string | | `a-<affiliation>-G-E-V`, with the affiliation the run's display convention gives the vehicle's population. |
| `how` | string | | `m-g`: machine-generated truth. |
| `lat` | number | degrees | WGS84 latitude of the front bumper's center, seven decimals, by SUMO's own projection. |
| `lon` | number | degrees | WGS84 longitude, likewise. |
| `hae_m` | number | meters | WGS84 ellipsoidal bare-earth ground height under the front bumper, from `bareearth.bin`; or `--hae` when there is no grid or the vehicle is off it. |
| `ce_m` | number | meters | Horizontal error: always `0.0`. |
| `le_m` | number | meters | Vertical error: always `0.0`. |
| `course_deg` | number | degrees | SUMO's heading, clockwise from north, one decimal. From 0 to 360: a heading just under 360 is written `360.0`. |
| `speed_mps` | number | m/s | Speed, two decimals. |
| `vx` | number | m/s | Velocity east (CARLA's x). |
| `vy` | number | m/s | Velocity south (CARLA's y). |
| `vz` | number | m/s | Always `0.00`: a SUMO network is flat. |
| `base_type` | string | | The vehicle catalogue's `cot_base_type` for the blueprint the vehicle's type names, or its SUMO vehicle class's when the type names none. |
| `type_id` | string | | The SUMO vehicle type id. |
| `special_type` | string | | The catalogue's `cot_special_type` for the blueprint: `emergency`, `taxi`, `electric`, or empty. |
| `length_m` | number | meters | Length SUMO gives the vehicle. |
| `width_m` | number | meters | Width SUMO gives the vehicle: the body without mirrors, for a catalogue type. |
| `height_m` | number | meters | Height SUMO gives the vehicle. |
| `color` | string | | The type's sumo-gui color, `R,G,B`. Not the color CARLA renders. |
| `role_name` | string | | The flow the vehicle came from: its SUMO id before the last dot. |
| `marked` | boolean | | `1` when the scenario planted the vehicle, `0` otherwise. |
| `edge` | string | | The SUMO edge the vehicle is on. |
| `lane` | string | | The SUMO lane the vehicle is on. |
| `sumo_x` | number | meters | SUMO x: east, in the network's projection. |
| `sumo_y` | number | meters | SUMO y: north, in the network's projection. |
| `carla_x` | number | meters | CARLA x: `sumo_x`. |
| `carla_y` | number | meters | CARLA y: `−sumo_y`, since CARLA's y points south. |

A vehicle is planted when its id is `--marked-vehicle` (`orbiter` by default) or is among a legacy
labels file's `marked_ids`.

## The summary

| Field | Type | Required | Meaning |
|---|---|---|---|
| `format_version` | integer, always 1 | yes | The CSV's format. |
| `producer` | object | yes | What made the CSV: `tool` is `carlacontrol.SumoCotBridge`, `server` is null, `sumo` is the SUMO release. See the producer record in [World_Package_Manifest.md](World_Package_Manifest.md#the-producer-record). |
| `csv` | string | yes | The CSV's file name, without a folder. |
| `columns` | array of strings | yes | The CSV's columns, in order: exactly its header. |

The summary is named for the CSV's stem: `orbit_cot.csv` gets `orbit_cot.summary.json`. It is written
when the CSV is opened, so it exists even when the run stops early.

## Format version

The CSV's format version is in its summary: `format_version` 1. A CSV with no summary beside it was
written before summaries existed and is version 1. Nothing checks the version today; a reader that
meets a version it does not know should refuse the CSV.

## Example

```csv
time_utc,sim_time_s,uid,callsign,cot_type,how,lat,lon,hae_m,ce_m,le_m,course_deg,speed_mps,vx,vy,vz,base_type,type_id,special_type,length_m,width_m,height_m,color,role_name,marked,edge,lane,sumo_x,sumo_y,carla_x,carla_y
2026-03-21T05:00:01.000Z,1.00,SUMO-TRUTH-traffic.1,van-traffic.1,a-f-G-E-V,m-g,39.5942830,-104.8843156,1747.40,0.0,0.0,90.0,6.00,6.00,-0.00,0.00,van,vehicle.ambulance.ford,emergency,4.89,1.83,1.52,"204,204,209",traffic,0,edge1,edge1_0,15.00,-3.00,15.00,3.00
2026-03-21T05:00:01.000Z,1.00,SUMO-TRUTH-orbiter,car-orbiter,a-n-G-E-V,m-g,39.5942559,-104.8841993,1747.40,0.0,0.0,180.0,7.00,0.00,7.00,0.00,car,vehicle.lincoln.mkz,,4.89,1.83,1.52,"204,204,209",orbiter,1,edge2,edge2_0,25.00,-6.00,25.00,6.00
```

```json
{
  "format_version": 1,
  "producer": {"tool": "carlacontrol.SumoCotBridge", "tool_version": "0.10.0+g252f459d0",
               "carlanet": "0.10.0+g252f459d0", "server": null, "sumo": "1.27.0",
               "written_utc": "2026-10-08T03:49:04.360Z"},
  "csv": "orbit_cot.csv",
  "columns": ["time_utc", "sim_time_s", "uid", "callsign", "cot_type", "how", "lat", "lon", "hae_m",
              "ce_m", "le_m", "course_deg", "speed_mps", "vx", "vy", "vz", "base_type", "type_id",
              "special_type", "length_m", "width_m", "height_m", "color", "role_name", "marked",
              "edge", "lane", "sumo_x", "sumo_y", "carla_x", "carla_y"]
}
```
