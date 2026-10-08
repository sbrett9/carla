# PNG text chunk `carla:solar`

**Schema:** `CarlaControl/schemas/png_chunk_solar.schema.json` (JSON Schema 2020-12)
**Identifier:** `urn:carla-sumo-capture:schema:png-chunk-solar:1`
**Format version described:** 1

## What it is

`carla:solar` is the sun the world reported on the tick nearest the still's pixels, and the
illumination band of that sun. The band is worked out from the sun the world actually had, never from
the time the run asked for. The truth sidecar's `<_solar>` element carries the same values.

The chunk is a PNG `tEXt` chunk with the keyword `carla:solar`, holding one line of compact JSON.

## Who writes it, and when

The CarlaNet recorder writes it into a still whenever the world reported a sun for that frame. A still
whose world reported no sun has no `carla:solar` chunk and no `<_solar>` in its sidecar; a capture run
counts such stills and its closing checks require the count to be zero.

## Fields

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `format_version` | integer, always 1 | | No | The chunk's format version; absent from a chunk written before chunks carried one, which is version 1. |
| `solar_time` | number | hours | Yes | The sun's clock in its own time zone, from 0 up to 24. |
| `date` | string | | Yes | The sun's calendar date, `YYYY-MM-DD`. |
| `time_zone` | number | hours | Yes | The time zone the sun's clock is read in, hours from UTC. |
| `lat` | number | degrees | Yes | The latitude the sun is computed for: the world's georeference origin, WGS84. |
| `lon` | number | degrees | Yes | The longitude the sun is computed for. |
| `sun_elevation_deg` | number | degrees | Yes | The sun's geometric elevation above the horizon, with no atmosphere. |
| `sun_azimuth_deg` | number | degrees | Yes | The sun's azimuth, clockwise from true north. |
| `advancing` | boolean | | Yes | Whether the engine itself moved the sun's clock with the world's tick; `false` in a SUMO drive, which sets the sun on every tick itself. |
| `rate` | number | seconds per second | Yes | Sun-clock seconds per simulated second while the engine advances the sun. |
| `sun_corrected_elevation_deg` | number | degrees | No | The elevation with atmospheric refraction applied, the elevation the frame was lit at; absent where the server does not report it. |
| `illumination_band` | string | | No | The sun's illumination band (below); absent where the elevation is not a real sun's, such as the value the engine reports for a sun it could not compute. |
| `illumination_band_elevation` | string | | No | Which elevation the band was cut from: `refraction_corrected` wherever the chunk carries it, `geometric` otherwise. Present exactly when `illumination_band` is. |

**Illumination bands**, defined by the sun's refraction-corrected elevation in degrees, each including its
upper edge: `day` above 6, `golden` above 0, `civil_twilight` above -6, `nautical_twilight` above -12,
`astronomical_twilight` above -18, `night` at -18 and below. The two elevations differ by up to a few
tenths of a degree near the horizon, which is a large share of a low sun.

## Format version

This page describes format version 1. A chunk without `format_version` was written before chunks
carried one and is version 1. Readers read a version they know, refuse a newer one by name rather than
reading it in part, and read a chunk with no version as version 1.

## Example

```json
{"format_version":1,"solar_time":7.45,"date":"2026-09-29","time_zone":-6,"lat":39.59431,"lon":-104.88449,"sun_elevation_deg":5.549088,"sun_azimuth_deg":97.826561,"advancing":false,"rate":0,"sun_corrected_elevation_deg":5.694,"illumination_band":"golden","illumination_band_elevation":"refraction_corrected"}
```

## Checking a file

`carla-validate <capture folder>` checks this chunk in every still of a capture.
