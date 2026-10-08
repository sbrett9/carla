# PNG text chunk `carla:capture`

**Schema:** `CarlaControl/schemas/png_chunk_capture.schema.json` (JSON Schema 2020-12)
**Identifier:** `urn:carla-sumo-capture:schema:png-chunk-capture:1`
**Format version described:** 1

## What it is

Every still a recorder writes is a PNG with up to four text chunks between its header and its pixels.
`carla:capture` says which capture the still is: the simulation frame and time it was rendered at, the
run it belongs to, and what wrote it. It lets a still that has been separated from its truth sidecar
still be traced to its frame, its run and the release that made it.

The chunk is a PNG `tEXt` chunk with the keyword `carla:capture`. Its text is one line of compact JSON.
PNG text chunks are Latin-1; the writer replaces any character outside Latin-1 with `?`.

Every still carries this chunk. The other three (`carla:solar`, `carla:illumination`, `carla:sensor`)
are written only when there is something to say.

## Who writes it, and when

The CarlaNet recorder writes it into every still it saves, at the same moment it writes the still's
truth sidecar. `carla-capture`, `carla-drive` and `carla-sctmv` all record through it.

## Fields

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `format_version` | integer, always 1 | | No | The chunk's format version; absent from a chunk written before chunks carried one, which is version 1. |
| `tick` | integer | | Yes | The simulation frame the image was rendered on; the same as the sidecar's `tick`. |
| `sim_time_s` | number | seconds | Yes | Simulated time at that frame. |
| `run_id` | string | | No | The run the still belongs to; every recorder names one. |
| `scenario_id` | string | | No | The scenario driving the run, where there is one. |
| `seed` | integer | | No | The seed the run was started with, where one was given. |
| `producer` | object | | No | What made the still; absent from a still written before stills carried it. |

`producer`:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `tool` | string | | Yes | The component that wrote the still, such as `carlacontrol.CaptureSession`. |
| `tool_version` | string or null | | Yes | The release of the package the tool comes from; null where the tool did not say. |
| `carlanet` | string | | Yes | The carlanet release: `0.10.0` for a tagged release, `0.10.0+g<commit>` for any other build. |
| `server` | object or null | | Yes | The CARLA server's build identity (below); null where no server was used. |
| `sumo` | string or null | | Yes | The SUMO release where SUMO drove the vehicles; null where it did not. |
| `written_utc` | string | | No | When the still was written, UTC to the millisecond, such as `2026-10-07T12:00:00.000Z`. |

`server` has one of two shapes. A server that answered the build identity call:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `available` | boolean, `true` | | Yes | The server answered. |
| `release` | string | | Yes | The CARLA release the server was compiled with. |
| `world_interface` | string | | Yes | The world interface version it declares, major.minor. |
| `build` | string | | Yes | `package` for a cooked server, `editor` for one run from the editor. |
| `configuration` | string | | Yes | The build configuration, such as `Shipping`. |
| `carla_commit` | string | | Yes | The CARLA commit it was built from. |
| `content_commit` | string | | Yes | The content commit its package was cooked from. |
| `engine_commit` | string | | Yes | The Unreal Engine commit. |
| `commits_from` | string | | Yes | `version_file`, `compiled` or `none`: where the commits came from. |

A server built before the call:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `available` | boolean, `false` | | Yes | The server did not answer the call. |
| `release` | string | | Yes | The release its version call reported, or `unknown`. |
| `world_interface` | string | | Yes | The world interface version it reported, or `unknown`. |
| `reason` | string | | Yes | Why the identity is not available. |

A value a server cannot know is `unknown`. The same record, with the same fields, is the truth
sidecar's `<_producer>` element, the run manifest's opening `producer` and the world truth track
summary's `producer`.

## Format version

This page describes format version 1. A chunk without `format_version` was written before chunks
carried one and is version 1; it has no `producer` either. Readers read a version they know, refuse a
newer one by name rather than reading it in part, and read a chunk with no version as version 1.

## Example

As written since the producer record was added:

```json
{"format_version":1,"tick":23804,"sim_time_s":200.679475,"run_id":"cap-20261007-173433-41f49b","producer":{"tool":"carlacontrol.CaptureSession","tool_version":"0.10.0+g252f459d0","carlanet":"0.10.0+g252f459d0","server":{"available":false,"release":"0.10.0","world_interface":"1.0","reason":"the server answers no get_build_identity: it was built before the call"},"sumo":"1.27.0","written_utc":"2026-10-07T17:34:51.402Z"}}
```

As written before it, still valid as version 1:

```json
{"tick":23804,"sim_time_s":200.679475,"run_id":"cap-20261007-173433-41f49b"}
```

## Checking a file

`carla-validate <capture folder>` reads every still's text chunks without reading its pixels and checks
each against its schema. To read the chunks yourself, any PNG library that lists `tEXt` chunks works,
for example Pillow's `Image.open(path).text`.
