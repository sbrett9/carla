# SUMO bridge event file (`carla-cot-telemetry --xml`)

`carla-cot-telemetry` runs a SUMO scenario through TraCI, with no CARLA server, and turns each vehicle's
state into a Cursor-on-Target event at a chosen rate. Given `--xml <file>`, it writes every event of
the run to one XML file. The file is truth: each event carries the whole record, including the
scenario author's names for its vehicle types and flows, and which vehicles it planted.

- Schema: `CarlaControl/schemas/sumo_cot_events.xsd` (XSD 1.0), root element `<events>`
- Schema id: `urn:carla-sumo-capture:schema:sumo-cot-events:1`

The events have the shape of the live feed's events, described in
[CoT_Telemetry_Stream.md](CoT_Telemetry_Stream.md); each one, taken alone, is also a valid datagram. The
same run can also write a CSV, described in [SUMO_CoT_Table.md](SUMO_CoT_Table.md), and send the live
feed.

The schema includes `cot_event_body.xsd`, the event's own parts, which the datagram schema includes
too. It cannot include `truth_sidecar.xsd` as the datagram schema does: this file's root and a truth
sidecar's root are both `<events>`, with no namespace, and one schema can describe only one of them.
So it holds copies of the sidecar's `<point>`, `<track>`, `<contact>` and `<_server>` types, and a
test checks that each copy matches the sidecar's.

## Who writes it and who reads it

`carlacontrol.SumoCotBridge` writes it as the run goes: the opening of `<events>`, the record of what
made it, the display convention, then one line per event, and the closing tag when the run ends. A run
that stops early leaves a file without its closing tag.

`carla-check-label-leaks --xml` reads it, to check that no field tells the planted vehicles from the
others. `carla-validate`, given a folder holding it, checks it against this schema, and notes a file
without its closing tag as a run that stopped early rather than failing it.

## Structure

```
<?xml version="1.0" encoding="UTF-8"?>
<events source="sumo" format_version="1" scenario="..." epoch="...">
  <_producer .../>
  <_display_convention ...> <population .../> ... </_display_convention>
  <event ...> ... </event>                     one per vehicle per update
  ...
</events>
```

### `<events>`

| Attribute | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `source` | string | | yes | `sumo`. |
| `format_version` | integer | | no | The file's format, 1. A file without it is version 1. |
| `scenario` | string | | yes | The `.sumocfg` file's name without its extension. |
| `epoch` | string | | yes | The UTC instant simulation time 0 was stamped with: `--epoch`, or the clock when the run started. ISO 8601 to the millisecond. |

### `<_producer>`

What made the file, as every XML file our tools write records it: the truth sidecar's `Producer`, of
which this schema holds a copy. Absent from files written before it was recorded.

| Attribute | Type | Required | Meaning |
|---|---|---|---|
| `tool` | string | yes | `carlacontrol.SumoCotBridge`. |
| `tool_version` | string | no | The carlacontrol release. |
| `carlanet` | string | no | The CarlaNet release the process loaded, when it loaded one. The bridge may run without CarlaNet and then leaves it out. A truth sidecar always has it, because CarlaNet writes the sidecar. |
| `sumo` | string | no | The SUMO release that ran, such as `1.27.0`. |
| `written_utc` | string | no | When the file was opened, ISO 8601 UTC to the millisecond. |

A `<_server>` child records a CARLA server's identity in other files; the bridge uses no server and
writes none.

### `<_display_convention>`

Which affiliation each vehicle population was drawn with. A population is a compiled scenario's
vehicle class, from its types' `carla:class_id`, or a hand-written route file's vehicle type id.

| Attribute | Type | Required | Meaning |
|---|---|---|---|
| `source` | string | yes | The convention file's name. Empty when the run had none. |
| `default_affiliation` | string | yes | The affiliation letter of every population the convention does not name: `--affiliation`, `n` by default. |
| `population/@type` | string | yes | A population. |
| `population/@affiliation` | string | yes | Its affiliation letter. |

### `<event>`

Each event is a vehicle event as on [CoT_Telemetry_Stream.md](CoT_Telemetry_Stream.md), with these
differences:

- `uid` is `<uid prefix>-<SUMO id>`; the prefix is `SUMO-TRUTH` unless `--uid-prefix` sets it.
- `type` carries the population's affiliation. A planted vehicle gets the same affiliation as its
  population; `--marked-affiliation` changes only the live feed.
- `time` is the epoch plus the simulation time of the update.
- `hae` is the ground height under the vehicle from the world package's `bareearth.bin` (given by
  `--bare-earth`), or `--hae`, 0 by default, when there is no grid or the vehicle is off it.
- `<_carla>` carries `type_id` (the SUMO vehicle type), `special_type` (from the vehicle catalog),
  `role_name` (the flow: the SUMO id before its last dot) and `marked` (`1` for a planted vehicle,
  `0` otherwise), which the live feed leaves out. `actor_id` is the SUMO id. There is no `heading_deg`,
  `sumo_id`, `vtype_id`, `admitted_tick`, `_capture` or `_solar`.
- `course` is SUMO's heading, clockwise from north. It is formatted to one decimal, so a heading just
  under 360 is written `360.0`.

## Format version

`format_version` is 1. `carla-check-label-leaks` reads a file without it as version 1, and refuses a
newer one, naming the file, the version and the newest it reads.

## Example

```xml
<?xml version="1.0" encoding="UTF-8"?>
<events source="sumo" format_version="1" scenario="fixture" epoch="2026-03-21T05:00:00.000Z">
  <_producer tool="carlacontrol.SumoCotBridge" tool_version="0.10.0+g252f459d0" carlanet="0.10.0+g252f459d0" sumo="1.27.0" written_utc="2026-10-08T03:49:04.360Z" />
  <_display_convention source="fixture.display.json" default_affiliation="n"><population type="ambulance" affiliation="f" /><population type="civ_car" affiliation="n" /></_display_convention>
  <event version="2.0" uid="SUMO-TRUTH-traffic.1" type="a-f-G-E-V" how="m-g" time="2026-03-21T05:00:01.000Z" start="2026-03-21T05:00:01.000Z" stale="2026-03-21T05:00:04.000Z"><point lat="39.5942830" lon="-104.8843156" hae="1747.40" ce="0.0" le="0.0" /><detail><track course="90.0" speed="6.00" /><contact callsign="van-traffic.1" /><_carla source="truth" actor_id="traffic.1" type_id="vehicle.ambulance.ford" base_type="van" special_type="emergency" length_m="4.89" width_m="1.83" height_m="1.52" color="204,204,209" role_name="traffic" marked="0" vx="6.00" vy="-0.00" vz="0.00" /></detail></event>
</events>
```
