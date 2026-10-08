# Solar frame (`solar.json` in a `.cwp`)

`solar.json` holds the facts about a world's sun that a scenario's start time is checked against without a running server:

- the origin the engine computes the sun's position from;
- the time zone the engine sets from the origin's longitude.

The engine's time zone is local mean solar time, `longitude / 15` hours.\
It is not the site's civil time zone.\
At a site near 56.2° E it is +03:44:43, while the local civil time is +03:30.

The scenario compiler reports the difference between the two.\
The file never claims a civil time zone.

- Schema: `CarlaControl/schemas/solar_frame.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:solar-frame:1`

## Who writes it and who reads it

carlacontrol's `SolarFrame` writes it from `world.json`'s origin when the authoring reference set is published.\
The scenario compiler reads it through `WorldPackageReader.solar_frame()`.

The file cannot say two things:

- the time zone during a run, since a SUMO drive sets the scenario's own civil offset on the server's sun when it starts;
- whether the world has a sun at all.

Both are read from the server.

The file is JSON, UTF-8, two-space indent, keys sorted.

## Fields

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `solar_frame_version` | integer, always 1 | | no | The format of this file. A frame without it is version 1. |
| `origin_latitude` | number | degrees | yes | WGS84 latitude of the world's origin, from `world.json`. From −90 to 90. |
| `origin_longitude` | number | degrees | yes | WGS84 longitude of the world's origin. From −180 to 180. |
| `engine_time_zone_hours` | number | hours east of UTC | yes | The zone the engine sets: `origin_longitude / 15`. |
| `engine_time_zone` | string | | yes | The same zone as a signed offset `±HH:MM:SS`, rounded to the second. |
| `engine_time_zone_rule` | string | | yes | The rule in words. |
| `engine_daylight_saving` | boolean | | yes | Whether the engine applies daylight saving when it configures the world. Always `false`. |
| `engine_solar_time_at_configure_hours` | number | hours | yes | The solar time the engine sets when it configures the world's georeference. Always 12.0. |

## Format version

`solar_frame_version` is 1, and there is no other version.\
A file without it is version 1.

`WorldPackageReader` and CarlaNet's `WorldPackage` refuse a file that declares a newer version, naming the version and the newest they read, rather than reading part of it.\
`WorldPackageReader` then checks the file against the schema.\
If the file does not match, it refuses the file and names each problem.

## Example

```json
{
  "engine_daylight_saving": false,
  "engine_solar_time_at_configure_hours": 12.0,
  "engine_time_zone": "-06:59:32",
  "engine_time_zone_hours": -6.992299333333333,
  "engine_time_zone_rule": "clamp(origin_longitude, -180, 180) / 15, set when the world's georeference is configured",
  "origin_latitude": 39.59431,
  "origin_longitude": -104.88449,
  "solar_frame_version": 1
}
```
