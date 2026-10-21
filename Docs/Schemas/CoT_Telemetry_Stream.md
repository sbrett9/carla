# Cursor-on-Target telemetry stream (UDP)

| | |
|---|---|
| File | one UDP datagram of the live feed: one CoT event for one vehicle at one instant |
| Schema | `CarlaControl/schemas/cot_telemetry.xsd` (XSD 1.0), with `cot_event_body.xsd` |
| Schema id | `urn:carla-sumo-capture:schema:cot-telemetry:1` |
| Format version | 1 |
| Version field | none: a datagram carries no format version of these tools; `version="2.0"` is the CoT event version |

## Description

The tools can send each vehicle's position as a live Cursor-on-Target (CoT) feed over UDP, for a TAK client or any other CoT receiver.\
Each UDP datagram holds exactly one CoT `<event>`: one vehicle at one instant.

The feed is for a moving-map display: it carries what a display needs.\
It leaves out what only the truth files carry.

A vehicle event has the same shape as a vehicle event in a capture's truth sidecar.\
The attributes they share mean the same thing.\
Their meanings are given in full in [Truth_Sidecar.md](Truth_Sidecar.md).

The schema does not repeat the shared parts.\
It includes `truth_sidecar.xsd` and takes `<point>`, `<track>`, `<contact>` and the simple types under them from it.\
The shared parts therefore always match.\
The event's own parts are in `cot_event_body.xsd`.\
The schema also includes that file.\
All three files must sit in one folder.

## Writers and readers

Three tools send it.\
`carla-sctmv` and `carla-cot-telemetry` format events with `carlacontrol.CotUdpEmitter.vehicle_telemetry_to_cot`.\
`carla-drive` sends the events its SUMO drive session writes with `CotWriter.StreamEvent`, the code that writes a truth sidecar's vehicle events.

| | `carla-sctmv` | `carla-cot-telemetry --udp HOST:PORT` | `carla-drive --cot-udp HOST:PORT` |
|---|---|---|---|
| Vehicles | CARLA's, from `get_vehicle_telemetry` | SUMO's, from TraCI, with no CARLA server | every vehicle with a CARLA body on a sampled frame, from the truth a capture of that frame records, cut to the bodies the frame drew |
| Turned on | with the Y key while it runs | by `--udp` | by `--cot-udp` |
| Destination | `--tak-host` (default `239.2.3.1`, TAK's multicast group) and `--tak-port` (default 6969) | the address given. If no port is given, port 6969. | the address given. If no port is given, port 6969. |
| Rate | `--rate`, default 5 Hz | `--rate`, default 1 Hz of simulation time | `--cot-hz`, default 1 Hz of simulation time, in whole world ticks: the newest frame of each interval |
| `time` of each event | the wall clock at the moment the event was formatted | the run's epoch plus the simulation time | the frame's civil time: the scenario's epoch plus the frame's simulated second |
| uid prefix | `CARLA-TRUTH` | `SUMO-TRUTH`, or `--uid-prefix` | `CARLA-TRUTH`, or `--cot-uid-prefix` |
| Affiliation | `--affiliation`, default `n` | the run's display convention, by population. `--marked-affiliation` sets it for planted vehicles, in the live feed only. | the run's display convention, by population, and `--cot-affiliation`, default `n`, for a population it does not name |
| Extra detail | `_capture` (the frame) and `_solar` (the sun) | none | none |

Multicast datagrams are sent with time-to-live `--ttl` (`carla-sctmv`), `--udp-ttl` (`carla-cot-telemetry`) or `--cot-udp-ttl` (`carla-drive`), default 1.

A vehicle's uid from `carla-drive` is `CARLA-TRUTH-SUMO-<SUMO id>`, the uid the drive's truth sidecars give it.\
From `carla-cot-telemetry` the same vehicle is `SUMO-TRUTH-<SUMO id>`.\
A receiver given both feeds draws it as two tracks.\
`carla-drive` sends the frames before its capture window opens too.\
It writes no file of its own: the capture sidecars and the world truth track record the same truth.

There is no handshake and no acknowledgment.\
A receiver listens on the port and parses each datagram as one XML document.\
Datagrams are UTF-8 and carry no XML declaration.

## How the stream differs from the truth sidecar

- It never carries what depends on a picture: `pitch_deg`, `roll_deg`, `in_frame`, the apparent size, the box fields, occlusion, `lights`, `pose_source`, `beyond_draw_distance`, `camera_range_m`, `<_box3d>` or `<_supervision>`.
- A `carla-drive` datagram is the event a capture of the same frame writes for the vehicle, written by the same code, less those fields.\
  It also leaves out `vtype_id`, which holds the scenario author's class name, and `sumo_angle_deg`.\
  Its `type` carries the display convention's affiliation, where a sidecar's carries the recorder's single affiliation, `n` by default.\
  Its `stale` is `--cot-stale` seconds after its `time`, where a sidecar's is 3 seconds after.
- A SUMO bridge datagram leaves out `type_id`, `special_type`, `role_name` and `marked`.\
  Those fields hold the scenario author's names for its vehicle types and flows.\
  They also say which vehicles it planted.\
  The bridge's XML and CSV files keep them.
- `--marked-affiliation` can give the planted vehicles a different affiliation in the live feed.\
  This lets an operator see them.\
  The written files never do.
- It carries `_capture` and `_solar` inside each event's `<detail>`.\
  A sidecar holds the solar state once, on its container.\
  The sidecar's `_solar` also has the latitude, longitude and the illumination band.
- There is no container.\
  The stream therefore has no format version and no record of what made it.
- The SUMO bridge's `hae` is the bare-earth ground height under the vehicle's front bumper.\
  The `hae` of `carla-sctmv` and `carla-drive` is the height of the body's origin with the height-align offset removed.\
  It therefore includes the origin's height above the ground.\
  A body on a bridge or an overpass is reported at the height CARLA seated it at, on the structure.

## Fields

### The event

```
<event version uid type how time start stale>
  <point lat lon hae ce le/>
  <detail>
    <track course speed/>
    <contact callsign/>
    <_carla .../>
    <_capture tick/>          carla-sctmv only
    <_solar .../>             carla-sctmv only
  </detail>
</event>
```

### `<event>`

| Attribute | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `version` | string | | yes | `2.0`, the CoT event version. |
| `uid` | string | | yes | The track id. A CARLA vehicle is `CARLA-TRUTH-<actor id>`. A vehicle a SUMO drive lent a CARLA body is `CARLA-TRUTH-SUMO-<SUMO id>`, from `carla-sctmv` and from `carla-drive`, whose `--cot-uid-prefix` can replace `CARLA-TRUTH`. This id keeps its track the same through a change of body. A SUMO bridge vehicle is `SUMO-TRUTH-<SUMO id>`. |
| `type` | string | | yes | `a-<affiliation>-G-E-V`: a ground vehicle. The affiliation is one of `p u a f n s h j k o x`. |
| `how` | string | | yes | `m-g`: machine-generated truth. |
| `time` | string | | yes | When the vehicle was at the point. ISO 8601 UTC to the millisecond. |
| `start` | string | | yes | The same instant as `time`. |
| `stale` | string | | yes | `time` plus `--stale` seconds (`--cot-stale` for `carla-drive`), 3 by default. |

### `<point>`

| Attribute | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `lat` | decimal | degrees | yes | WGS84 latitude, seven decimals. |
| `lon` | decimal | degrees | yes | WGS84 longitude, seven decimals. |
| `hae` | decimal | meters | yes | WGS84 ellipsoidal height, two decimals, in the bare-earth convention. From CARLA, the body origin's height with the height-align offset removed. From the SUMO bridge, the bare-earth ground under the front bumper. |
| `ce` | decimal | meters | yes | Horizontal error: `0.0` for truth. |
| `le` | decimal | meters | yes | Vertical error: `0.0` for truth. |

### `<track>` and `<contact>`

| Element | Attribute | Type | Unit | Required | Meaning |
|---|---|---|---|---|---|
| `track` | `course` | decimal | degrees | yes | Direction of travel, clockwise from north, one decimal. |
| `track` | `speed` | decimal | m/s | yes | Speed, two decimals. |
| `contact` | `callsign` | string | | yes | `<base_type>-<track>`, where the track is the actor id or the SUMO id the uid ends with. |

### `<_carla>`

| Attribute | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `source` | string | | yes | `truth`. |
| `actor_id` | string | | yes | The CARLA actor id of the body. The SUMO bridge has no CARLA actor and writes the SUMO id. |
| `type_id` | string | | no | The CARLA blueprint id. Left out of a SUMO bridge datagram. |
| `base_type` | string | | yes | `car`, `van`, `truck`, `bus`, `motorcycle` or `bicycle`, from the vehicle catalog. |
| `special_type` | string | | no | `emergency`, `taxi`, `electric`, or empty. Left out of a SUMO bridge datagram. |
| `length_m`, `width_m`, `height_m` | decimal | meters | yes | The vehicle's size, two decimals. CARLA's is the drawn body's bounding box. The bridge's is SUMO's. |
| `color` | string | | yes | `R,G,B`. From CARLA, the body color, empty for a blueprint with none. From the bridge, the SUMO type's sumo-gui color. |
| `role_name` | string | | no | The CARLA role name: `sumo` for every body a SUMO drive lends. Left out of a SUMO bridge datagram. |
| `vx`, `vy`, `vz` | decimal | m/s | yes | Velocity in CARLA's frame: east, south, up. |
| `heading_deg` | decimal | degrees | no | The direction the body points, clockwise from north. CARLA vehicles only. |
| `sumo_id` | string | | no | The SUMO vehicle a SUMO drive lent this body to. |
| `vtype_id` | string | | no | That vehicle's SUMO type. Left out of a `carla-drive` datagram, because it holds the scenario author's class name. |
| `admitted_tick` | integer | frames | no | The frame on which the body began drawing that vehicle. |

A `carla-drive` datagram carries every attribute above but `vtype_id`.\
`heading_deg`, `sumo_id` and `admitted_tick` are always present in it.

### `<_capture>` and `<_solar>` (`carla-sctmv` only)

| Element | Attribute | Type | Unit | Required | Meaning |
|---|---|---|---|---|---|
| `_capture` | `tick` | integer | frames | yes | The simulation frame the event was taken on. |
| `_solar` | `solar_time` | decimal | hours | yes | Local solar time. |
| `_solar` | `date` | date | | yes | `YYYY-MM-DD`. |
| `_solar` | `time_zone` | decimal | hours | yes | The engine's time zone, east of UTC. |
| `_solar` | `sun_elevation_deg` | decimal | degrees | yes | The sun's elevation. |
| `_solar` | `sun_azimuth_deg` | decimal | degrees | yes | The sun's azimuth. |
| `_solar` | `advancing` | `true` or `false` | | yes | Whether the engine itself moved the sun's clock with the world's tick. `false` in a SUMO drive: the drive sets the sun itself on every tick. |
| `_solar` | `rate` | number | | yes | Solar seconds per simulation second. |

If reading the server's solar state fails, `_solar` is left out.

## Example

A SUMO bridge datagram:

```xml
<event version="2.0" uid="SUMO-TRUTH-traffic.1" type="a-f-G-E-V" how="m-g" time="2026-03-21T05:00:01.000Z" start="2026-03-21T05:00:01.000Z" stale="2026-03-21T05:00:04.000Z"><point lat="39.5942830" lon="-104.8843156" hae="1747.40" ce="0.0" le="0.0" /><detail><track course="90.0" speed="6.00" /><contact callsign="van-traffic.1" /><_carla source="truth" actor_id="traffic.1" base_type="van" length_m="4.89" width_m="1.83" height_m="1.52" color="204,204,209" vx="6.00" vy="-0.00" vz="0.00" /></detail></event>
```

A `carla-sctmv` datagram for a body lent to a SUMO vehicle, laid out for reading:

```xml
<event version="2.0" uid="CARLA-TRUTH-SUMO-traffic.17" type="a-n-G-E-V" how="m-g"
       time="2026-03-21T05:00:00.000Z" start="2026-03-21T05:00:00.000Z" stale="2026-03-21T05:00:03.000Z">
  <point lat="39.5943123" lon="-104.8844901" hae="1716.25" ce="0.0" le="0.0"/>
  <detail>
    <track course="271.3" speed="12.50"/>
    <contact callsign="car-traffic.17"/>
    <_carla source="truth" actor_id="214" type_id="vehicle.lincoln.mkz" base_type="car" special_type=""
            length_m="4.89" width_m="2.05" height_m="1.52" color="17,213,91" role_name="autopilot"
            vx="-12.49" vy="-0.28" vz="0.00" heading_deg="270.9" sumo_id="traffic.17"
            vtype_id="civ_car" admitted_tick="4410"/>
    <_capture tick="4520"/>
    <_solar solar_time="7.2500" date="2026-03-21" time_zone="-6.9923" sun_elevation_deg="9.871"
            sun_azimuth_deg="97.412" advancing="true" rate="1"/>
  </detail>
</event>
```

## Versions

A datagram carries no format version defined by these tools.\
`version="2.0"` is the CoT event version.\
If you write a receiver, make it ignore attributes and elements it does not know, as CoT receivers do.
