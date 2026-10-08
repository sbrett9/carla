# PNG text chunk `carla:sensor`

**Schema:** `CarlaControl/schemas/png_chunk_sensor.schema.json` (JSON Schema 2020-12)\
**Identifier:** `urn:carla-sumo-capture:schema:png-chunk-sensor:1`\
**Format version described:** 1

## What it is

`carla:sensor` is where the camera was and where it pointed at the capture, with its pinhole intrinsics, so a still can be projected or placed on a map from the image file alone.\
It is the same pose the truth sidecar's camera platform event carries.\
The camera's exposure is in the sidecar's `<_carla_exposure>` only.

The chunk is a PNG `tEXt` chunk with the keyword `carla:sensor`, holding one line of compact JSON.

## Who writes it and when

The CarlaNet recorder writes it into a still when it records the camera as a platform and the world's georeference is known.\
A recorder started through carlanet (`start_recording`), which every `carla-*` tool uses, always records the camera as a platform.

## Fields

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `format_version` | integer, always 1 | | No | The chunk's format version; absent from a chunk written before chunks carried one, which is version 1. |
| `uid` | string | | Yes | The camera's track identifier, such as `CARLA-SENSOR-107`. |
| `type` | string | | Yes | The camera platform's Cursor-on-Target air-track type, such as `a-f-A-M-F-Q`. |
| `callsign` | string | | Yes | The camera's name, which every still of the camera is named after. |
| `lat` | number | degrees | Yes | The camera's latitude, WGS84. |
| `lon` | number | degrees | Yes | The camera's longitude, WGS84. |
| `hae` | number | meters | Yes | The camera's height above the WGS84 ellipsoid in the bare-earth convention every vehicle's height uses. |
| `align_offset_m` | number | meters | Yes | The height-align offset under the camera: its physical height is `hae` plus this. |
| `az_deg` | number | degrees | Yes | Boresight azimuth, clockwise from true north. |
| `el_deg` | number | degrees | Yes | Boresight elevation: 0 level, -90 straight down. |
| `roll_deg` | number | degrees | Yes | Roll about the boresight. |
| `course_deg` | number | degrees | Yes | The camera's direction of movement since the previous capture; its boresight azimuth when it moved slower than 0.5 m/s. |
| `speed_mps` | number | meters per second | Yes | The camera's ground speed since the previous capture. |
| `intrinsics` | object | | Yes | The pinhole intrinsics, below. |

`intrinsics`: square pixels, the principal point at the picture's center.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `width` | integer | pixels | Yes | The picture's width. |
| `height` | integer | pixels | Yes | The picture's height. |
| `fx` | number | pixels | Yes | Focal length across: width / (2 tan(hfov_deg / 2)). |
| `fy` | number | pixels | Yes | Focal length down, equal to `fx`. |
| `cx` | number | pixels | Yes | Principal point across: width / 2. |
| `cy` | number | pixels | Yes | Principal point down: height / 2. |
| `hfov_deg` | number | degrees | Yes | Horizontal field of view. |
| `vfov_deg` | number | degrees | Yes | Vertical field of view, from the height and the focal length. |
| `model` | string | | Yes | The projection model: `pinhole`. |
| `distortion` | string | | Yes | `none` at CARLA's defaults, otherwise CARLA's own lens parameters. |
| `sensor_model` | string | | Yes | The camera blueprint, such as `sensor.camera.rgb`. |

Some names differ from the sidecar's for the same value: `az_deg` and `el_deg` here are `azimuth` and `elevation` on the sidecar's `<sensor>`, and `intrinsics.sensor_model` is the `<sensor>`'s `model`.

## Format version

This page describes format version 1.\
A chunk without `format_version` was written before chunks carried one and is version 1.\
Readers read a version they know, refuse a newer one by name rather than reading it in part, and read a chunk with no version as version 1.

## Example

```json
{"format_version":1,"uid":"CARLA-SENSOR-107","type":"a-f-A-M-F-Q","callsign":"Check_Overhead_1","lat":39.5971345,"lon":-104.8891363,"hae":1818.06,"align_offset_m":-0.63,"az_deg":90,"el_deg":-70.346,"roll_deg":0,"course_deg":90,"speed_mps":0.00,"intrinsics":{"width":1920,"height":1080,"fx":2058.73,"fy":2058.73,"cx":960,"cy":540,"hfov_deg":50,"vfov_deg":29.395,"model":"pinhole","distortion":"none","sensor_model":"sensor.camera.rgb"}}
```

## Checking a file

`carla-validate <capture folder>` checks this chunk in every still of a capture.
