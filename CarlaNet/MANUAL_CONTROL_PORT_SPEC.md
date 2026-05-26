# MANUAL_CONTROL_PORT_SPEC

Authoritative specification for porting `PythonAPI/examples/manual_control.py`
to run against CarlaNet via the `carlanet` Python shim.

This document is the contract between three agents:
- **Agent 1** (this doc author): cataloging the gaps.
- **Agent 2**: extending the C# CarlaNet libraries to fill RPC/SensorType gaps.
- **Agent 3**: extending the Python shim (`carlanet/__init__.py`) to expose the upstream surface.

All file/line references are absolute paths into this repo. All upstream
behaviour is sourced from the boost.python bindings in
`g:\Projects\CarlaUE_5_7_4\carla\PythonAPI\carla\src\` and from
`LibCarla/source/carla/sensor/data/*.h` + `s11n/*.h`. Do not invent semantics —
quote from those files.

---

## 1. Inventory of all `carla.X` references in manual_control.py

Compiled by grepping the script for `carla.` and `cc.` (the `ColorConverter` alias).
Numbers in parentheses are source line numbers in
`g:\Projects\CarlaUE_5_7_4\carla\PythonAPI\examples\manual_control.py`.

| Symbol | Lines | Status | Notes |
|--------|-------|--------|-------|
| `carla.Client(host, port)` | 1278 | **OK** | shim `Client` accepts `(host, port, worker_threads=2)` |
| `client.set_timeout(ms)` | 1279 | **GAP** | shim updates internal `_timeout` but **never propagates it** to the underlying `MsgPackRpcClient` (whose `_timeout` is `readonly`). See §9. Note: script passes `2000.0` — which upstream means **2000 seconds**. Upstream `Client.cpp:15` `SetTimeout(seconds)` uses `TimeDurationFromSeconds(seconds)`. The shim currently does `int(timeout_s * 1000)` ms. That's correct unit-wise but the C# side ignores it. |
| `client.get_world()` | 1281 | **OK** | returns shim `World` |
| `client.get_trafficmanager()` | 1282 | **OK** | TrafficManager wrapper exists |
| `client.start_recorder(name)` | 494 | **OK** | shim calls `StartRecorderAsync(name, false)` |
| `client.stop_recorder()` | 490, 499, 1329 | **OK** | |
| `client.replay_file(name, start, dur, follow)` | 509 | **OK** | shim has `ReplayFileAsync` |
| `carla.Transform()` (no-arg + with loc/rot) | 294, 901, 942, 971, 1001, 1046, 1071, 1111, 1112, 1113, 1114, 1115, 1118, 1119, 1120, 1121, 1122 | **OK** | exposed via `from CarlaNet.Types.Geom import Transform` — already imported at top of shim |
| `carla.Location(x=, y=, z=)` | 971, 1047, 1072, 1111, 1112, 1113, 1114, 1115, 1118, 1119, 1120, 1121, 1122 | **OK** | exposed |
| `carla.Rotation(pitch=, yaw=, roll=)` | 1048, 1073, 1111, 1114, 1118, 1120, 1121, 1122 | **OK** | exposed |
| `carla.Vector3D(x, y, z)` | 454, 1070 | **OK** | exposed |
| `carla.Color(r, g, b)` | 1090 | **GAP** | C# has `CarlaNet.Types.Rpc.Color` (record struct, R/G/B) but it is **not exported** by `carlanet/__init__.py`. Must be added to the shim's import block AND made callable as `Color(r,g,b)`. |
| `carla.VehicleControl()` (and `.throttle/.steer/.brake/.hand_brake/.reverse/.manual_gear_shift/.gear`) | 394, 532, 538, 539, 541, 542, 543, 544, 585, 587-594, 599, 603, 740, 742-748 | **OK** | C# `VehicleControl` has these fields per upstream `Control.cpp:287-306`. **Verify** all fields are settable from Python — they're a `record struct` so they should be settable when assigned via `.X = Y`. |
| `carla.VehicleAckermannControl()` (`.steer/.steer_speed/.speed/.acceleration/.jerk`) | 395, 536, 601, 605, 699, 753, 779 | **OK** | per upstream `Control.cpp:308-323` |
| `carla.WalkerControl()` (`.speed/.direction/.jump`) | 400, 654-665, 757, 758 | **OK** | per upstream `Control.cpp:344-355` |
| `isinstance(world.player, carla.Vehicle)` | 393 | **GAP** | shim has NO `Vehicle` class. See §3. |
| `isinstance(world.player, carla.Walker)` | 399 | **GAP** | shim has NO `Walker` class. See §3. |
| `isinstance(self._control, carla.VehicleControl)` | 409, 523, 583, 740 | **OK** | C# `VehicleControl` is a type — `isinstance` works through pythonnet. |
| `isinstance(self._control, carla.WalkerControl)` | 607, 755 | **OK** | |
| `isinstance(c, carla.VehicleControl/WalkerControl)` | 740, 755 | **OK** | |
| `carla.VehicleLightState.NONE / .Position / .LowBeam / .HighBeam / .Brake / .LeftBlinker / .RightBlinker / .Reverse / .Fog / .Interior / .Special1` | 396, 555, 557, 561, 563, 566, 567, 569, 570, 572, 573, 574, 576, 578, 580, 588, 590, 592, 594, 596 | **GAP** | The C# enum is `VehicleLightStateFlags` (different name, otherwise correct numeric values). manual_control.py uses `carla.VehicleLightState` and **constructs an instance via `carla.VehicleLightState(int)` at line 596**. See §4. |
| `carla.VehicleDoor.All` | 462, 466 | **OK** | exposed as `VehicleDoor` (C# enum, has `All` value per upstream `Actor.cpp:170-176`) |
| `carla.MapLayer.NONE/Buildings/Decals/...` | 238-248 | **OK** | exposed; C# enum has `None=0` — Python uses `NONE` (caps). **Verify**: per upstream `World.cpp:240-252`, the binding maps Python `NONE` → C++ `cr::MapLayer::None`. C# enum value is named `None` (a reserved word in Python for the singleton `None`). pythonnet will let you access it as `MapLayer.None` only via `getattr`. Need to alias `NONE`. |
| `carla.AttachmentType` (`.Rigid / .SpringArmGhost`) | 1107, 1111-1115, 1118-1122 | **OK** | exposed |
| `carla.WeatherParameters` and `.ClearNoon`, `.CloudyNoon`, etc. (via `dir()` on line 165) | 165-166, 320 | **GAP** | C# `WeatherParameters` is a record struct. Lacks the **22 named preset class attributes** that `find_weather_presets()` discovers by `dir()` filter `[A-Z].+`. Without these, `find_weather_presets()` returns `[]` and `next_weather()` crashes. See §6. |
| `carla.ColorConverter.Raw / .Depth / .LogarithmicDepth / .CityScapesPalette` | 62 (alias `cc`), 1128-1142 | **GAP** | Not exposed by shim. Must be added as a Python enum. See §5. |
| `image.convert(cc.Xxx)` | 1256 | **GAP** | depends on `ColorConverter` being known + `Image` wrapper exposing `.convert()`. See §2 & §5. |
| `image.get_color_coded_flow()` | 1249 | **GAP** | optical-flow image conversion to BGRA color image. Needs Python implementation in shim (the math is in `SensorData.cpp:197-299`). See §2. |
| `image.raw_data` | 1221, 1234, 1250, 1257 | **GAP** | needs to be a buffer-like (bytes/memoryview/`np.frombuffer`-compatible). C# `ImageSensorData.RawBgra` is `ReadOnlyMemory<byte>`; need to expose `.raw_data` as Python `bytes` or memoryview. |
| `image.frame / .timestamp / .transform` | 1263 + per HUD chain | **GAP** | All `SensorData` subclasses must expose `frame`, `timestamp`, `transform` per upstream `SensorData.cpp:357-362`. Currently the shim's stream callback receives `SensorFrame` (`Header.Frame` / `Header.Timestamp`) — but does NOT wrap that in a high-level sensor data object. See §2 and §8. |
| `image.width / .height / .fov` | 1251, 1258, 1259 | **GAP** | exposed on `ImageSensorData` but the shim currently passes through raw `SensorFrame`, not the parsed `ImageSensorData`. See §2. |
| `image.save_to_disk(path)` | 1263 | **GAP** | upstream `SensorData.cpp:395` — writes PNG. Needs a Python implementation: use PIL or numpy + raw PNG. See §11. |
| `len(image)` / `image[i].object_tag` (semantic lidar iteration) | 1243-1246 | **GAP** | per upstream `SensorData.cpp:441-456`, the SemanticLidarMeasurement supports `__len__` and `__getitem__(i) -> SemanticLidarDetection` whose `.object_tag` is `uint32`. The C# `SemanticLidarSensorData.Points` is `IReadOnlyList<SemanticLidarDetection>` (works for `len()` and `[i]`), but the field on C# struct is **PascalCase** `ObjectTag`, not snake_case. See §2. |
| `event.other_actor` (collision) | 918 | **GAP** | needs `CollisionSensorData.OtherActor` → Actor wrapper exposing `type_id`. See §2. |
| `event.normal_impulse.x/.y/.z` | 920-921 | **OK** | C# `Vector3D` exposes X/Y/Z (PascalCase). **GAP**: manual_control reads `.x/.y/.z` (lowercase). The geom `Vector3D.cs` should expose both `X` (existing) and `x` (lowercase property alias). Check existing impl. |
| `event.frame` (collision event) | 922 | **GAP** | see §2 — wrapper must expose `frame` from header. |
| `event.crossed_lane_markings` (lane invasion) | 953 | **GAP** | lane invasion is a **NoopSerializer** sensor on the server — payload is empty. The original libcarla LaneInvasionSensor is a **client-side** sensor that builds the `LaneMarking` list locally from waypoint data. CarlaNet does NOT implement this. See §11. |
| `event.latitude / event.longitude` (GNSS) | 982-983 | **GAP** | C# `GnssSensorData` exposes `Latitude/Longitude/Altitude` PascalCase. Need snake_case via wrapper. See §2. |
| `sensor_data.accelerometer.x/.y/.z` (IMU) | 1015-1017 | **GAP** | C# `ImuSensorData.Accelerometer` is `Vector3D` — depends on `x`/`y`/`z` lowercase aliases. |
| `sensor_data.gyroscope.x/.y/.z` (IMU) | 1019-1021 | **GAP** | same |
| `sensor_data.compass` (IMU) | 1022 | **GAP** | C# is `Compass` PascalCase. Wrap. |
| `radar_data.transform` | 1064, 1086 | **GAP** | per §2: parse from 48-byte header → expose as `Transform`. |
| `radar_data` iteration (`for detect in radar_data:`) | 1065 | **GAP** | C# `RadarSensorData.Detections` is `IReadOnlyList<RadarDetection>` — iterable through pythonnet, but `RadarSensorData` itself is not. Wrapper must implement `__iter__` over `.Detections`. |
| `detect.azimuth / .altitude / .depth / .velocity` (RadarDetection) | 1066-1067, 1070, 1081 | **GAP** | C# `RadarDetection` fields are PascalCase. Wrap. |
| `world.tick()` | 311, 1308, 1315 | **OK** | shim has `tick()` → `SendTickCueAsync` |
| `world.wait_for_tick()` | 313, 1310 | **PARTIAL OK** | shim does `time.sleep(0.05)`. This is wrong semantically (it should wait for a real tick event) but **acceptable for manual_control** since it's only called once during init in async mode. Documented in §11. |
| `world.get_settings() / apply_settings(s) / settings.synchronous_mode / settings.fixed_delta_seconds` | 1284-1289, 1326 | **OK** | C# `EpisodeSettings` has `SynchronousMode` PascalCase — needs snake_case alias on Python side OR rely on pythonnet auto-mapping (which it DOES NOT do for fields — only methods). **GAP**: settings field names. |
| `world.get_map()` | 210, 729, 1124 | **OK** | shim returns `Map(name, spawn_points)` |
| `world.get_map().name` | 729, 1124 | **OK** | shim `Map.name` |
| `world.get_map().get_spawn_points()` | 293 | **OK** | |
| `world.get_blueprint_library()` | 174, 900, 941, 970, 999, 1041, 1145 | **OK** | shim `BlueprintLibrary` |
| `bp_library.filter(pattern)` | 174, 723 | **OK** | uses `fnmatch` |
| `bp_library.find(id)` | 900, 941, 970, 999, 1041, 1147 | **OK** | |
| `bp.has_attribute / get_attribute / set_attribute` | 188, 263-276, 1151-1162 | **OK** | shim `ActorBlueprint` |
| `attr.recommended_values` | 266, 269, 275, 276 | **OK** | shim `_Attribute` |
| `world.on_tick(callback)` | 230 | **GAP** | shim returns `None` (no-op). The HUD `on_world_tick(timestamp)` callback feeds `self.server_fps`/`self.frame`/`self.simulation_time`. Without it, HUD will display `0 FPS` and frame counter never increments. See §11. This NEEDS the EpisodeState stream (world observer) to fire a per-tick Python callback with a `Timestamp(frame, elapsed_seconds, delta_seconds, platform_timestamp)`. |
| `world.get_actors()` | 723 | **OK** | shim has `get_actors()` |
| `world.get_actors().filter('vehicle.*')` | 723 | **GAP** | shim returns a Python `list`, not an `ActorList`. List does not have `.filter()`. Need a small `ActorList`-like wrapper around the result with `filter()` and iteration. Upstream `World.cpp:141-148`: `ActorList` exposes `find`, `filter`, `__getitem__`, `__len__`, `__iter__`. |
| `world.spawn_actor(bp, t, attach_to=p, attachment_type=at)` | 901, 942, 971, 1000, 1044, 1184 | **OK** | shim has the right signature |
| `world.try_spawn_actor(bp, t)` | 285, 295 | **OK** | |
| `world.load_map_layer(layer) / unload_map_layer(layer)` | 332, 335 | **OK** | |
| `world.set_weather(preset)` | 320 | **OK** | |
| `actor.id / .type_id / .bounding_box / .attributes` | 170, 768, 937, 1034-1036, 1104-1106, 1109 | **OK** | shim `Actor` has all of these (bounding_box is from `actor.BoundingBox`, returns C# `BoundingBox` which has `Location/Extent/Rotation` PascalCase — `.extent.x` lowercase access required, see §1 row on Vector3D). |
| `actor.get_transform() / get_location() / get_velocity() / get_control()` | 280, 402, 539, 603, 711-713, 768 | **OK** | shim methods exist; **caller depends on world observer being started**. |
| `actor.get_world()` | 295, 899, 940, 969, 998, 1039, 1123, 1184 | **OK** | shim returns `World(self._client)` |
| `actor.destroy()` | 341, 377, 379, 1182 | **OK** | shim does this with stream cleanup |
| `actor.set_autopilot(b)` | 397, 419, 421, 506, 551 | **OK** | (lives on `Actor` in shim — fine, dispatch is by Python duck typing) |
| `actor.apply_control(VehicleControl|WalkerControl)` | 599, 609 | **OK** | shim has `apply_control` on Actor that calls `ApplyControlToVehicleAsync`. **GAP**: when passed a `WalkerControl`, current shim still calls vehicle RPC. See §3. |
| `actor.apply_ackermann_control(c)` | 601 | **OK** | shim method exists |
| `actor.set_light_state(state)` | 398, 596 | **OK** | shim method exists |
| `actor.set_transform(t)` | (not used directly — used in restart() implicitly) | — | n/a |
| `actor.enable_constant_velocity(v) / disable_constant_velocity()` | 450, 454 | **OK** | shim methods exist |
| `actor.show_debug_telemetry(b)` | 471, 476 | **OK** | shim method exists |
| `actor.open_door(d) / close_door(d)` | 462, 466 | **OK** | shim methods exist |
| `actor.get_physics_control() / apply_physics_control(c)` | 347, 349 | **OK** | shim methods exist |
| `physics_control.use_sweep_wheel_collision = True` | 348 | **OK** | C# `VehiclePhysicsControl.UseSweepWheelCollision` — wrapper must expose snake_case (or pythonnet may allow direct PascalCase access — verify). Manual_control will assign `physics_control.use_sweep_wheel_collision = True`; if pythonnet doesn't auto-map this it will silently fail (Python lets you set any attr on most objects). **GAP** — needs explicit shim wrapping. |
| `sensor.listen(callback)` | 905, 946, 975, 1005, 1052, 1192 | **GAP** | listen currently requires `Action[SensorFrame]` ceremony already wrapped — but the callback **receives the raw `SensorFrame`**, not a parsed sensor data object. manual_control's callbacks expect typed objects (`image`, `event.other_actor`, `radar_data`, etc.). See §8. |
| `sensor.stop()` | 376, 1180 | **OK** | shim method exists |
| `sensor.destroy()` | 341, 377 | **OK** | |
| `traffic_manager.update_vehicle_lights(actor, True)` | 308 | **OK** | shim method exists |
| `traffic_manager.set_synchronous_mode(True)` | 1291 | **OK** | shim method exists |
| `client_obj.get_trafficmanager()` returns object with `.get_port()` | (implied) | **OK** | shim |
| `time.sleep` (stdlib) | — | n/a | not a carla symbol |

### "GAP" summary (must be fixed in shim, NOT in script):

1. `Color` (carla.Color class)
2. `Vehicle`, `Walker` classes (for `isinstance` checks)
3. `VehicleLightState` class with named attributes + callable constructor (replacing/wrapping `VehicleLightStateFlags`)
4. `ColorConverter` enum
5. `WeatherParameters` preset attributes (22 of them)
6. `world.on_tick(callback)` real implementation
7. `world.get_actors().filter()` (ActorList-like wrapper)
8. `MapLayer.NONE` Python alias for C# `MapLayer.None`
9. `EpisodeSettings.synchronous_mode` / `.fixed_delta_seconds` snake_case access (currently PascalCase)
10. `BoundingBox.extent.x` lowercase access path
11. `Vector3D.x/.y/.z` lowercase aliases
12. Sensor data wrappers: `Image`, `CollisionEvent`, `LaneInvasionEvent` (with fallback), `GnssMeasurement`, `IMUMeasurement`, `RadarMeasurement`+`RadarDetection`, `LidarMeasurement`, `SemanticLidarMeasurement`, `OpticalFlowImage` — all need `frame`, `timestamp`, `transform`, plus type-specific attributes (see §2)
13. `sensor.listen(callback)` automatic SensorType dispatch + wrap-as-Action ceremony
14. `set_timeout` propagation to `MsgPackRpcClient`
15. `physics_control.use_sweep_wheel_collision` snake_case (or just expose `apply_physics_control` accepting both names)
16. `Actor.apply_control` dispatch based on type_id (vehicle vs walker)

---

## 2. Sensor wrapper specifications

`manual_control.py`'s `CameraManager.sensors` list (lines 1127-1143) requests **12 sensor types**:
- `sensor.camera.rgb` (× 2 — one normal, one distorted)
- `sensor.camera.depth` (× 3 — with Raw, Depth, LogarithmicDepth converters)
- `sensor.camera.semantic_segmentation` (× 2 — Raw and CityScapesPalette)
- `sensor.camera.instance_segmentation`
- `sensor.lidar.ray_cast`
- `sensor.lidar.ray_cast_semantic`
- `sensor.camera.optical_flow`
- `sensor.camera.normals`

Plus `CollisionSensor`, `LaneInvasionSensor`, `GnssSensor`, `IMUSensor`, `RadarSensor`.

All `SensorData` subclasses expose three attributes from the 48-byte `SensorHeader`,
per `SensorData.cpp:357-362`:
- `frame` — `uint64` from header offset 8
- `timestamp` — `double` from header offset 16 (seconds since simulation start)
- `transform` — `Transform(Location(x,y,z), Rotation(pitch,yaw,roll))` from header offsets 24-47

C# already parses the header in `SensorFrame` (`CarlaNet.Transport.Streaming.SensorFrame.Header`,
file `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Transport\Streaming\SensorFrame.cs`).
Each wrapper below reads `frame.Header.Frame`, `frame.Header.Timestamp`, `frame.SensorTransform`.

### 2.1 `carla.Image` (camera RGB, depth, semantic_segmentation, instance_segmentation, normals)

Upstream binding: `SensorData.cpp:389-405`.

| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | `frame.Header.Frame` | uint64 |
| `timestamp` | float | `frame.Header.Timestamp` | seconds |
| `transform` | Transform | `frame.SensorTransform` | sensor pose |
| `width` | int | payload[0..4] u32 LE | |
| `height` | int | payload[4..8] u32 LE | |
| `fov` | float | payload[8..12] f32 LE | aka `fov_angle` per `ImageSerializer.h:27-31` |
| `raw_data` | bytes / memoryview | payload[12..] | BGRA bytes — length = width × height × 4 |

Methods:
- `convert(color_converter)` — see §5.
- `save_to_disk(path)` — write PNG. See §11.
- `__len__()` → `width * height`
- `__getitem__(i)` → BGRA tuple (per upstream `SensorData.cpp:398`)

**Parse algorithm (Python side)**: C# `ImageSensorData.Deserialize(payload)` already does the
12-byte slice into width/height/fov + the BGRA bytes. The shim can either call that
or do the same in Python with `struct.unpack_from('<IIf', payload, 0)` and slice.

**Cross-reference**: `CarlaNetSupplementary.md` §6 — pixel reorder for pygame is
`arr[:, :, [2, 1, 0]]` (BGR → RGB).

### 2.2 `carla.CollisionEvent`

Upstream binding: `SensorData.cpp:458-463`.

| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | header | |
| `timestamp` | float | header | |
| `transform` | Transform | header | |
| `actor` | Actor | msgpack `self_actor` | the sensor's parent vehicle |
| `other_actor` | Actor | msgpack `other_actor` | the colliding actor |
| `normal_impulse` | Vector3D | msgpack | manual_control reads `.x/.y/.z` |

**Parse algorithm**: payload is **msgpack**, not raw binary
(per `CollisionEventSerializer.h:33` `MSGPACK_DEFINE_ARRAY(self_actor, other_actor, normal_impulse)`).
C# `CarlaNet.Sensors.CollisionSensorData` already handles this with
`MessagePackSerializer.Deserialize<CollisionSensorData>(payload)`. Need to do that on the
client side after receiving the SensorFrame.

The wrapper must expose `other_actor.type_id` because manual_control calls
`get_actor_display_name(event.other_actor)` which reads `actor.type_id` (line 170).
The `Actor` msgpack-decoded inside the event contains a full `ActorDescription` with `Id`
(blueprint id like `"vehicle.tesla.model3"`).

### 2.3 `carla.LaneInvasionEvent`

Upstream binding: `SensorData.cpp:472-476`.

| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | header | |
| `timestamp` | float | header | |
| `transform` | Transform | header | |
| `actor` | Actor | (from parent) | |
| `crossed_lane_markings` | `list[LaneMarking]` | **client-side** | Each LaneMarking has `.type`, `.color`, `.lane_change`, `.width` per `LaneMarking.h:18-66`. manual_control only reads `.type` (line 953). |

**KEY ISSUE**: The lane invasion sensor is a `s11n::NoopSerializer` (per
`SensorRegistry.h:71` — `std::pair<ALaneInvasionSensor *, s11n::NoopSerializer>`). The
server sends an empty payload. The original libcarla client manually constructs the
`LaneMarking` list from cached map waypoint data — see `LibCarla/source/carla/client/LaneInvasionSensor.cpp`.

**Recommendation**: Implement as a no-op wrapper that produces an event with
`crossed_lane_markings=[]`. The HUD line "Crossed line %s" will display "Crossed line "
(empty join). Acceptable degradation for first pass. See §11.

### 2.4 `carla.GnssMeasurement`

Upstream binding: `SensorData.cpp:478-483`.

| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | header | |
| `timestamp` | float | header | |
| `transform` | Transform | header | |
| `latitude` | float | msgpack `Latitude` | |
| `longitude` | float | msgpack `Longitude` | |
| `altitude` | float | msgpack `Altitude` | |

**Parse algorithm**: msgpack. C# `GnssSensorData` (in `ImuGnssSensorData.cs`) already does it.

### 2.5 `carla.IMUMeasurement`

Upstream binding: `SensorData.cpp:485-490`.

| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | header | |
| `timestamp` | float | header | |
| `transform` | Transform | header | |
| `accelerometer` | Vector3D | msgpack | manual_control reads `.x/.y/.z` |
| `gyroscope` | Vector3D | msgpack | in radians/s — manual_control converts via `math.degrees()` |
| `compass` | float | msgpack | in radians, north-relative |

**Parse algorithm**: msgpack. C# `ImuSensorData` already does it.

### 2.6 `carla.RadarMeasurement` + `carla.RadarDetection`

Upstream binding: `SensorData.cpp:492-512`.

`RadarMeasurement`:
| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | header | |
| `timestamp` | float | header | |
| `transform` | Transform | header | |
| `raw_data` | bytes | payload | |
| iteration | yields RadarDetection | per upstream `SensorData.cpp:496-503` | |
| `__len__` | int | payload_len / 16 | |

`RadarDetection`:
| Attribute | Type | Notes |
|-----------|------|-------|
| `velocity` | float | m/s |
| `azimuth` | float | radians |
| `altitude` | float | radians |
| `depth` | float | meters |

**Parse algorithm**: raw binary, payload is a flat array of 16-byte structs
`{float velocity, float azimuth, float altitude, float depth}` — per `CarlaNetSupplementary.md` §7.
C# `RadarSensorData.Deserialize` already does this.

### 2.7 `carla.LidarMeasurement` (ray_cast)

Upstream binding: `SensorData.cpp:424-439`.

| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | header | |
| `timestamp` | float | header | |
| `transform` | Transform | header | |
| `horizontal_angle` | float | payload[0..4] reinterp f32 | per `LidarSerializer.h:30-32` |
| `channels` | int | payload[4..8] u32 | |
| `raw_data` | bytes | full payload | manual_control reads this as flat `f4[]` and reshapes to `(N, 4)` for `(x,y,z,intensity)` (lines 1221-1223) |
| `__len__` | int | total point count | |
| `__getitem__` | LidarDetection | with `.point.x/.y/.z`, `.intensity` |

**Parse algorithm**: per `CarlaNetSupplementary.md` §7 / `LidarSerializer.h` —
header is `[horizontal_angle:u32_as_f32][channel_count:u32][point_count[0..C]:u32]`,
then 16-byte points `{float x,y,z,intensity}`. **Critical**: `horizontal_angle` is `uint32`
bits reinterpreted as float — use `Int32BitsToSingle`, not a straight cast (§13.5).

manual_control's lidar callback uses ONLY `image.raw_data` (line 1221) — it doesn't iterate.
**However**, the `raw_data` it receives includes the variable header (point counts) at the
start, which is wrong for its assumption that `frombuffer(dtype=f4)` reshapes evenly to `(N,4)`.

**Resolution**: The shim should expose `raw_data` as **just the point data** (skipping
the header). This matches upstream's `GetRawDataAsBuffer<csd::LidarMeasurement>` which
exposes only the `data()` portion (the LidarMeasurement's internal `_points` buffer,
**not** the on-the-wire header). Confirmed by reading `LidarMeasurement.h` (not read in this
session — TODO if assumption is wrong, agent 2 should verify).

### 2.8 `carla.SemanticLidarMeasurement` (ray_cast_semantic)

Upstream binding: `SensorData.cpp:441-456`.

| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | header | |
| `timestamp` | float | header | |
| `transform` | Transform | header | |
| `horizontal_angle` | float | header | |
| `channels` | int | header | |
| `raw_data` | bytes | flat points | manual_control reshapes to `(N, 6)` of `f4` (line 1234-1235) |
| `__len__` | int | total points | |
| `__getitem__` | SemanticLidarDetection | with `.object_tag` (uint32, used at line 1245) |

**Parse algorithm**: header same as Lidar above, points are 24 bytes each:
`{float x, float y, float z, float cos_inc_angle, uint32 object_idx, uint32 object_tag}`.
C# `SemanticLidarSensorData` already does it.

manual_control line 1234: `points = np.frombuffer(image.raw_data, dtype=np.dtype('f4'))`
then `np.reshape(points, (int(points.shape[0] / 6), 6))` — treats all 24 bytes as 6 floats.
That works because uint32 and float32 are both 4 bytes. So `points[i, 4]` and `points[i, 5]`
are `object_idx` and `object_tag` reinterpreted as floats — but manual_control doesn't use
those array indices. Instead it uses `image[i].object_tag` (line 1245) which returns the
real uint32. So we need BOTH the raw bytes AND the iterable.

### 2.9 `carla.OpticalFlowImage`

Upstream binding: `SensorData.cpp:407-422`.

| Attribute | Type | Source | Notes |
|-----------|------|--------|-------|
| `frame` | int | header | |
| `timestamp` | float | header | |
| `transform` | Transform | header | |
| `width` | int | payload[0..4] | |
| `height` | int | payload[4..8] | |
| `fov` | float | payload[8..12] | |
| `raw_data` | bytes | payload[12..] | 8 bytes/pixel: `{float X, float Y}` |

Methods:
- `get_color_coded_flow()` → returns a "FakeImage" object with `.raw_data`, `.width`, `.height`. The shim must provide a Python implementation following the HSV→RGB algorithm in `SensorData.cpp:197-298`. manual_control uses the returned object's `.raw_data` (4 bytes/pixel BGRA, A=0) and `.height`/`.width` (line 1249-1254).

**Parse algorithm**: same 12-byte header as image, then 8 bytes per pixel. C# `OpticalFlowSensorData` already does it.

### 2.10 `SensorType` dispatch table (peek the first 8 bytes of SensorFrame)

The 48-byte SensorHeader's first 8 bytes are `sensor_type` (uint64). The value is
the **template position index** in `SensorRegistry.h:64-84` (CompositeSerializer
template — index = position in `std::pair<...>, std::pair<...>, ...`).

Index mapping (from `SensorRegistry.h`, zero-based):

| Index | Sensor class | Serializer | Type ID (Python) |
|------:|--------------|------------|------------------|
| 0 | ACollisionSensor | CollisionEventSerializer | `sensor.other.collision` |
| 1 | ADepthCamera | ImageSerializer | `sensor.camera.depth` |
| 2 | ANormalsCamera | NormalsImageSerializer | `sensor.camera.normals` |
| 3 | ADVSCamera | DVSEventArraySerializer | `sensor.camera.dvs` |
| 4 | AGnssSensor | GnssSerializer | `sensor.other.gnss` |
| 5 | AInertialMeasurementUnit | IMUSerializer | `sensor.other.imu` |
| 6 | ALaneInvasionSensor | NoopSerializer | `sensor.other.lane_invasion` |
| 7 | AObstacleDetectionSensor | ObstacleDetectionEventSerializer | `sensor.other.obstacle` |
| 8 | AOpticalFlowCamera | OpticalFlowImageSerializer | `sensor.camera.optical_flow` |
| 9 | ARadar | RadarSerializer | `sensor.other.radar` |
| 10 | ARayCastSemanticLidar | SemanticLidarSerializer | `sensor.lidar.ray_cast_semantic` |
| 11 | ARayCastLidar | LidarSerializer | `sensor.lidar.ray_cast` |
| 12 | ARssSensor | NoopSerializer | `sensor.other.rss` |
| 13 | ASceneCaptureCamera | ImageSerializer | `sensor.camera.rgb` |
| 14 | ASemanticSegmentationCamera | ImageSerializer | `sensor.camera.semantic_segmentation` |
| 15 | AInstanceSegmentationCamera | ImageSerializer | `sensor.camera.instance_segmentation` |
| 16 | FWorldObserver | EpisodeStateSerializer | (internal — world observer) |
| 17 | FCameraGBufferUint8 | GBufferUint8Serializer | (gbuffer) |
| 18 | FCameraGBufferFloat | GBufferFloatSerializer | (gbuffer) |

**Recommended dispatch**: ignore the `SensorType` ID and instead dispatch by the actor's
`type_id` string (which the Python caller already knows when they call `actor.listen`).
The reason: there's no need for run-time sniffing when the sensor's role is already
known. The shim's listen wrapper should:

```python
def listen(self, py_callback):
    type_id = self.type_id
    parse = _PARSER_BY_TYPE_ID[type_id]   # ImageSensorData.Deserialize, etc.
    def cs_callback(frame):
        try:
            data = parse(frame)            # produces high-level wrapper
            py_callback(data)
        except Exception as ex:
            print(f"sensor callback failed: {ex}")
    cs_action = Action[SensorFrame](cs_callback)
    self._sub = self._client.SubscribeToStream(self._actor.StreamToken, cs_action)
```

Where `_PARSER_BY_TYPE_ID` maps:

```
"sensor.camera.rgb"                    → ImageWrapper
"sensor.camera.depth"                  → ImageWrapper
"sensor.camera.semantic_segmentation"  → ImageWrapper
"sensor.camera.instance_segmentation"  → ImageWrapper
"sensor.camera.normals"                → ImageWrapper
"sensor.camera.optical_flow"           → OpticalFlowImageWrapper
"sensor.lidar.ray_cast"                → LidarMeasurementWrapper
"sensor.lidar.ray_cast_semantic"       → SemanticLidarMeasurementWrapper
"sensor.other.radar"                   → RadarMeasurementWrapper
"sensor.other.collision"               → CollisionEventWrapper
"sensor.other.gnss"                    → GnssMeasurementWrapper
"sensor.other.imu"                     → IMUMeasurementWrapper
"sensor.other.lane_invasion"           → LaneInvasionEventWrapper (no-op payload)
"sensor.other.obstacle"                → ObstacleDetectionEventWrapper
```

This is more reliable than parsing the `SensorType` index because the index values
require us to keep the registry order in sync with the server build.

---

## 3. Vehicle / Walker / Sensor / TrafficLight subclass plan

Upstream `Actor.cpp` declares a Python class hierarchy:

```
Actor (Actor.cpp:99)
├── Vehicle (Actor.cpp:185)            — apply_control, set_light_state, open/close_door, etc.
├── Walker (Actor.cpp:214)             — apply_control(WalkerControl), get_bones
├── WalkerAIController (Actor.cpp:226) — start/stop, go_to_location
├── TrafficSign (Actor.cpp:234)        — trigger_volume
│   └── TrafficLight (Actor.cpp:248)   — state, freeze, etc.
└── Sensor (Sensor.cpp:24)             — listen, stop, is_listening
    ├── ServerSideSensor                — listen_to_gbuffer
    │   └── (camera, lidar, radar, etc.)
    └── ClientSideSensor                — (no extra methods)
        └── LaneInvasionSensor          — (constructs LaneMarking list locally)
```

### Dispatch mechanism

The current shim has a single `Actor` class with ALL methods (apply_control, listen, etc.).
This works for duck-typed access but **fails for `isinstance` checks** in
manual_control.py lines 393, 399, 409, 523, 583, 607, 740, 755.

**Recommended approach**:
1. **Keep** all methods on the base `Actor` class for compatibility — `apply_control`,
   `listen`, `set_light_state`, etc. (Already the case.)
2. **Add** empty subclasses `Vehicle`, `Walker`, `Sensor`, `TrafficLight`, `TrafficSign`
   that inherit from `Actor` and contribute nothing — pure marker classes for
   `isinstance`.
3. **Dispatch** in `World.spawn_actor` / `World.try_spawn_actor` / `World.get_actors` —
   inspect the returned `cs_actor.Description.Id` and instantiate the right subclass:
   ```python
   def _wrap_actor(cs_actor, client):
       type_id = str(cs_actor.Description.Id)
       if type_id.startswith("vehicle."):              cls = Vehicle
       elif type_id.startswith("walker.pedestrian"):   cls = Walker
       elif type_id.startswith("walker.controller"):   cls = WalkerAIController
       elif type_id.startswith("sensor."):             cls = Sensor
       elif type_id == "traffic.traffic_light":        cls = TrafficLight
       elif type_id.startswith("traffic."):            cls = TrafficSign
       else:                                            cls = Actor
       return cls(cs_actor, client)
   ```

### Method ownership per upstream binding

(This is for documentation — the **shim implementation** puts everything on `Actor`.)

- **Vehicle only** (`Actor.cpp:185-212`): `apply_control(VehicleControl)`,
  `apply_ackermann_control`, `get_control`, `set_light_state`, `get_light_state`,
  `open_door`, `close_door`, `set_wheel_steer_direction`, `get_wheel_steer_angle`,
  `apply_physics_control`, `get_physics_control`, `apply_ackermann_controller_settings`,
  `get_ackermann_controller_settings`, `set_autopilot`, `show_debug_telemetry`,
  `get_speed_limit`, `get_traffic_light_state`, `is_at_traffic_light`,
  `get_traffic_light`, `enable_carsim`, `use_carsim_road`, `enable_chrono_physics`,
  `get_failure_state`, `get_vehicle_bone_world_transforms`.
- **Walker only** (`Actor.cpp:214-224`): `apply_control(WalkerControl)`, `get_control`,
  `get_bones`, `set_bones`, `blend_pose`, `show_pose`, `hide_pose`,
  `get_pose_from_animation`.
- **Sensor only** (`Sensor.cpp:24-29`): `listen(callback)`, `is_listening`, `stop`.
- **TrafficLight only** (`Actor.cpp:248-271`): `state`, `set_state`, `get_state`,
  `set_green_time`/`get_green_time`, `set_yellow_time`/`get_yellow_time`,
  `set_red_time`/`get_red_time`, `get_elapsed_time`, `freeze`, `is_frozen`,
  `get_pole_index`, `get_group_traffic_lights`, `reset_group`,
  `get_affected_lane_waypoints`, `get_light_boxes`, `get_opendrive_id`,
  `get_stop_waypoints`.

### `apply_control` polymorphism

manual_control calls `world.player.apply_control(c)` where `c` is `VehicleControl` OR
`WalkerControl`. The shim's `Actor.apply_control` currently does ONLY
`ApplyControlToVehicleAsync`. Must dispatch:

```python
def apply_control(self, control):
    if isinstance(control, WalkerControl):
        _sync(self._client.ApplyControlToWalkerAsync(self._actor.Id, control))
    else:
        _sync(self._client.ApplyControlToVehicleAsync(self._actor.Id, control))
```

(C# already exposes `ApplyControlToWalkerAsync` — see `CarlaClient.cs:330`.)

---

## 4. `VehicleLightState` class

Upstream `Actor.cpp:145-159` exposes `VehicleLightState` as a **boost.python enum**.
While it's an enum at the C++ level, the boost.python `enum_<...>` wrapper in Python
behaves like a class:
- `VehicleLightState.NONE` → instance with `.value == 0`
- `VehicleLightState.Position` → instance with `.value == 0x1`
- `VehicleLightState(intval)` → constructs an enum instance from an int

manual_control uses both forms:
- Line 396: `self._lights = carla.VehicleLightState.NONE` (attribute access)
- Line 596: `world.player.set_light_state(carla.VehicleLightState(current_lights))` —
  **constructor call** with an int.
- Bitwise operators throughout (`|`, `^`, `&`, `~`) — int-like semantics.

### Current C# state

`g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Rpc\Lighting\VehicleLightState.cs`:

```csharp
public enum VehicleLightStateFlags : uint
{
    None = 0, Position = 0x1, LowBeam = 0x2, HighBeam = 0x4, Brake = 0x8,
    RightBlinker = 0x10, LeftBlinker = 0x20, Reverse = 0x40, Fog = 0x80,
    Interior = 0x100, Special1 = 0x200, Special2 = 0x400, All = 0xFFFFFFFF
}
```

Values are **correct**. Bitwise OR/AND on C# `[Flags]` enums works in Python via pythonnet
(operator overloads).

### Required Python-side shim

Implement `VehicleLightState` as a Python class with the named class attributes (ints, not
enum values) and a `__new__` that accepts an int:

```python
class VehicleLightState(int):
    NONE = 0
    Position = 0x1
    LowBeam = 0x2
    HighBeam = 0x4
    Brake = 0x8
    RightBlinker = 0x10
    LeftBlinker = 0x20
    Reverse = 0x40
    Fog = 0x80
    Interior = 0x100
    Special1 = 0x200
    Special2 = 0x400
    All = 0xFFFFFFFF
    # Inherits __new__ from int; carla.VehicleLightState(5) → VehicleLightState(5)
```

Using `int` as base lets `|`, `&`, `^`, `~` work natively; the `Actor.set_light_state`
shim must coerce to `VehicleLightStateFlags`:

```python
def set_light_state(self, state):
    flags = VehicleLightStateFlags(int(state))
    _sync(self._client.SetVehicleLightStateAsync(self._actor.Id, flags))
```

**Do NOT** simply rename `VehicleLightStateFlags` to `VehicleLightState` — the
class-attribute-access pattern + constructor call requires a Python class wrapper.

The existing `VehicleLightStateFlags` C# enum can **stay** as the wire type. The shim
exports `VehicleLightState` as a Python class and converts at the boundary.

---

## 5. `ColorConverter` specification

Upstream `SensorData.cpp:148-153, 364-369`:

```cpp
enum class EColorConverter {
  Raw, Depth, LogarithmicDepth, CityScapesPalette
};
```

Bound as:
```cpp
enum_<EColorConverter>("ColorConverter")
  .value("Raw", EColorConverter::Raw)
  .value("Depth", EColorConverter::Depth)
  .value("LogarithmicDepth", EColorConverter::LogarithmicDepth)
  .value("CityScapesPalette", EColorConverter::CityScapesPalette);
```

manual_control imports `from carla import ColorConverter as cc` (line 62) and uses
`cc.Raw / .Depth / .LogarithmicDepth / .CityScapesPalette` in the sensor table (line 1128-1142),
and `image.convert(self.sensors[self.index][1])` (line 1256).

### Required Python shim

```python
class ColorConverter:
    Raw               = 0
    Depth             = 1
    LogarithmicDepth  = 2
    CityScapesPalette = 3
```

### Conversion algorithms (Python implementations)

Source: `LibCarla/source/carla/image/ColorConverter.h` and `CityScapesPalette.h`.

All conversions operate on a **BGRA byte buffer** of length `W*H*4`. They modify the
buffer **in place** (matching upstream `ImageConverter::ConvertInPlace`).

#### Raw
No-op. `image.convert(cc.Raw)` does nothing.

#### Depth
Per `ColorConverter.h:28-42`. The 24-bit depth is encoded across the R, G, B channels:
```
depth = R + G*256 + B*65536        # 24-bit integer
normalized = depth / (256^3 - 1)   # [0, 1] float
```
The result is written back as a **grayscale image with normalized * 255 in each of B,G,R**
(A unchanged). Wait — upstream uses `gray32fc_pixel_t` which boost.gil converts back to
BGRA by replicating the normalized value across B,G,R. Implementation:
```python
def _convert_depth(raw_bytes):
    arr = np.frombuffer(raw_bytes, dtype=np.uint8).reshape(-1, 4)  # BGRA
    depth = arr[:, 2].astype(np.float32) + arr[:, 1].astype(np.float32) * 256.0 + \
            arr[:, 0].astype(np.float32) * 65536.0
    normalized = depth / (256.0 ** 3 - 1.0)
    gray = (normalized * 255.0).astype(np.uint8)
    arr[:, 0] = gray  # B
    arr[:, 1] = gray  # G
    arr[:, 2] = gray  # R
    # arr[:, 3] unchanged (alpha)
```

#### LogarithmicDepth
Per `ColorConverter.h:18-26` (the `LogarithmicLinear` functor is applied after Depth's
`gray32fc_pixel_t` conversion):
```
depth = R + G*256 + B*65536
normalized = depth / (256^3 - 1)
value = 1.0 + log(normalized) / 5.70378
clamped = clamp(value, 0.005, 1.0)
```
Then replicate `clamped * 255` into B, G, R. (`5.70378` ≈ `log(300)`, the far plane.)

```python
def _convert_log_depth(raw_bytes):
    arr = np.frombuffer(raw_bytes, dtype=np.uint8).reshape(-1, 4)
    depth = arr[:, 2].astype(np.float32) + arr[:, 1].astype(np.float32) * 256.0 + \
            arr[:, 0].astype(np.float32) * 65536.0
    normalized = depth / (256.0 ** 3 - 1.0)
    # avoid log(0)
    normalized = np.maximum(normalized, 1e-10)
    value = 1.0 + np.log(normalized) / 5.70378
    clamped = np.clip(value, 0.005, 1.0)
    gray = (clamped * 255.0).astype(np.uint8)
    arr[:, 0] = gray; arr[:, 1] = gray; arr[:, 2] = gray
```

#### CityScapesPalette
Per `ColorConverter.h:46-56`. The R channel holds a class label 0..29; look up the
RGB color in the palette table `CITYSCAPES_PALETTE_MAP` (29-entry palette in
`CityScapesPalette.h:20-53`). Upstream replicates manual_control's `OBJECT_TO_COLOR`
table at the **top of manual_control.py lines 125-155** (29 entries; matches the
palette). Implementation:

```python
_CITYSCAPES_PALETTE = np.array([
    (0, 0, 0), (128, 64, 128), (244, 35, 232), (70, 70, 70), (102, 102, 156),
    (190, 153, 153), (153, 153, 153), (250, 170, 30), (220, 220, 0), (107, 142, 35),
    (152, 251, 152), (70, 130, 180), (220, 20, 60), (255, 0, 0), (0, 0, 142),
    (0, 0, 70), (0, 60, 100), (0, 80, 100), (0, 0, 230), (119, 11, 32),
    (110, 190, 160), (170, 120, 50), (55, 90, 80), (45, 60, 150), (157, 234, 50),
    (81, 0, 81), (150, 100, 100), (230, 150, 140), (180, 165, 180), (180, 130, 70),
], dtype=np.uint8)

def _convert_cityscapes(raw_bytes):
    arr = np.frombuffer(raw_bytes, dtype=np.uint8).reshape(-1, 4)
    tags = arr[:, 2] % len(_CITYSCAPES_PALETTE)   # R channel = tag
    rgb = _CITYSCAPES_PALETTE[tags]
    arr[:, 0] = rgb[:, 2]   # B
    arr[:, 1] = rgb[:, 1]   # G
    arr[:, 2] = rgb[:, 0]   # R
```

### Implementation pattern in Image wrapper

```python
class Image:
    def __init__(self, sf):
        self._frame = sf.Header.Frame
        ...
        self._raw_bytes = bytearray(sf.PayloadBytes[12:])  # MUTABLE
        ...
    @property
    def raw_data(self):
        return bytes(self._raw_bytes)   # or memoryview
    def convert(self, cc):
        if cc == ColorConverter.Depth:             _convert_depth(self._raw_bytes)
        elif cc == ColorConverter.LogarithmicDepth: _convert_log_depth(self._raw_bytes)
        elif cc == ColorConverter.CityScapesPalette:_convert_cityscapes(self._raw_bytes)
        # Raw: no-op
```

**Critical**: convert is **in-place**. After `image.convert(...)`, subsequent reads of
`image.raw_data` see the converted bytes. manual_control depends on this (it calls
`.convert()` and then `np.frombuffer(image.raw_data, ...)` on the next line).

---

## 6. WeatherParameters presets

Upstream `Weather.cpp:72-94` and `WeatherParameters.cpp:14-37`. The 22 named presets
plus `Default`. Constructor order (14 floats, per `WeatherParameters.h:54-68`):

```
cloudiness, precipitation, precipitation_deposits, wind_intensity,
sun_azimuth_angle, sun_altitude_angle, fog_density, fog_distance, fog_falloff,
wetness, scattering_intensity, mie_scattering_scale, rayleigh_scattering_scale, dust_storm
```

Exact values from `WeatherParameters.cpp`:

| Preset | Cloudiness | Precip | Prec.Dep | Wind | Azimuth | Altitude | FogDens | FogDist | FogFall | Wetness | ScatI | MieScat | Rayleigh | DustStorm |
|--------|-----------:|-------:|---------:|-----:|--------:|---------:|--------:|--------:|--------:|--------:|------:|--------:|---------:|----------:|
| Default | -1.0 | -1.0 | -1.0 | -1.0 | -1.0 | -1.0 | -1.0 | -1.0 | -1.0 | -1.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| ClearNoon | 5.0 | 0.0 | 0.0 | 10.0 | -1.0 | 45.0 | 2.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| CloudyNoon | 60.0 | 0.0 | 0.0 | 10.0 | -1.0 | 45.0 | 3.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| WetNoon | 5.0 | 0.0 | 50.0 | 10.0 | -1.0 | 45.0 | 3.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| WetCloudyNoon | 60.0 | 0.0 | 50.0 | 10.0 | -1.0 | 45.0 | 3.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| MidRainyNoon | 60.0 | 60.0 | 60.0 | 60.0 | -1.0 | 45.0 | 3.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| HardRainNoon | 100.0 | 100.0 | 90.0 | 100.0 | -1.0 | 45.0 | 7.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| SoftRainNoon | 20.0 | 30.0 | 50.0 | 30.0 | -1.0 | 45.0 | 3.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| ClearSunset | 5.0 | 0.0 | 0.0 | 10.0 | -1.0 | 15.0 | 2.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| CloudySunset | 60.0 | 0.0 | 0.0 | 10.0 | -1.0 | 15.0 | 3.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| WetSunset | 5.0 | 0.0 | 50.0 | 10.0 | -1.0 | 15.0 | 2.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| WetCloudySunset | 60.0 | 0.0 | 50.0 | 10.0 | -1.0 | 15.0 | 2.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| MidRainSunset | 60.0 | 60.0 | 60.0 | 60.0 | -1.0 | 15.0 | 3.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| HardRainSunset | 100.0 | 100.0 | 90.0 | 100.0 | -1.0 | 15.0 | 7.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| SoftRainSunset | 20.0 | 30.0 | 50.0 | 30.0 | -1.0 | 15.0 | 2.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| ClearNight | 5.0 | 0.0 | 0.0 | 10.0 | -1.0 | -90.0 | 60.0 | 75.0 | 1.0 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| CloudyNight | 60.0 | 0.0 | 0.0 | 10.0 | -1.0 | -90.0 | 60.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| WetNight | 5.0 | 0.0 | 50.0 | 10.0 | -1.0 | -90.0 | 60.0 | 75.0 | 1.0 | 60.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| WetCloudyNight | 60.0 | 0.0 | 50.0 | 10.0 | -1.0 | -90.0 | 60.0 | 0.75 | 0.1 | 60.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| SoftRainNight | 60.0 | 30.0 | 50.0 | 30.0 | -1.0 | -90.0 | 60.0 | 0.75 | 0.1 | 60.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| MidRainyNight | 80.0 | 60.0 | 60.0 | 60.0 | -1.0 | -90.0 | 60.0 | 0.75 | 0.1 | 80.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| HardRainNight | 100.0 | 100.0 | 90.0 | 100.0 | -1.0 | -90.0 | 100.0 | 0.75 | 0.1 | 100.0 | 1.0 | 0.03 | 0.0331 | 0.0 |
| DustStorm | 100.0 | 0.0 | 0.0 | 100.0 | -1.0 | 45.0 | 2.0 | 0.75 | 0.1 | 0.0 | 1.0 | 0.03 | 0.0331 | 100.0 |

### Required shim implementation

These need to be **class attributes on a Python wrapper for `WeatherParameters`**, so
that `dir(carla.WeatherParameters)` yields preset names that match `[A-Z].+` (per
manual_control line 165's regex).

```python
class WeatherParameters:
    # Wraps the C# record struct so that we can attach class attributes (presets).
    # An instance is just the C# WeatherParameters.

    def __new__(cls, *args, **kwargs):
        from CarlaNet.Types.Rpc.Environment import WeatherParameters as _CSWeather
        if not args and not kwargs:
            return _CSWeather()
        return _CSWeather(*args, **kwargs)

# Then attach presets:
def _wp(*v): return _CSWeather(*v)
WeatherParameters.Default        = _wp(-1.0,-1.0,-1.0,-1.0,-1.0,-1.0,-1.0,-1.0,-1.0,-1.0, 1.0,0.03,0.0331, 0.0)
WeatherParameters.ClearNoon      = _wp(  5.0, 0.0, 0.0,10.0,-1.0,45.0, 2.0,0.75, 0.1, 0.0, 1.0,0.03,0.0331, 0.0)
... (all 23)
```

**Verify C# constructor**: `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Rpc\Environment\WeatherParameters.cs`
must accept these 14 floats positionally. If it's a `record struct` with `[Key(0..13)]` it
should work via positional construction in pythonnet. **TODO for Agent 2/3**: verify.

---

## 7. `world.debug` (DebugHelper)

Upstream `World.cpp:364-400`. Methods used by manual_control:

```python
world.debug.draw_point(
    location, size=0.075, life_time=0.06,
    persistent_lines=False, color=carla.Color(r, g, b))
```

(`RadarSensor._Radar_callback`, line 1085-1090.)

Other debug methods (not used by manual_control but should be exposed for compatibility):

| Method | Signature |
|--------|-----------|
| `draw_point` | `(location, size=0.1, color=Color(255,0,0), life_time=-1.0, persistent_lines=True)` |
| `draw_line` | `(begin, end, thickness=0.1, color=Color(255,0,0), life_time=-1.0, persistent_lines=True)` |
| `draw_arrow` | `(begin, end, thickness=0.1, arrow_size=0.1, color=Color(255,0,0), life_time=-1.0, persistent_lines=True)` |
| `draw_box` | `(box, rotation, thickness=0.1, color=Color(255,0,0), life_time=-1.0, persistent_lines=True)` |
| `draw_string` | `(location, text, draw_shadow=False, color=Color(255,0,0), life_time=-1.0, persistent_lines=True)` |

### Wire-level mapping to existing C# Primitive types

The C# already has the right types in
`g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Rpc\Debug\Primitive.cs`:
- `PointPrimitive(Location, float Size)`
- `LinePrimitive(Location Begin, Location End, float Thickness)`
- `ArrowPrimitive(LinePrimitive, float ArrowSize)`
- `BoxPrimitive(BoundingBox, Rotation, float Thickness)`
- `StringPrimitive(Location, string Text, bool DrawShadow)`

And `DebugShape(Primitive, Color, float LifeTime, bool PersistentLines)`.

The RPC: `CarlaClient.cs:462` — `DrawDebugShapeAsync(DebugShape shape)`.

### Required Python `DebugHelper` wrapper

```python
class DebugHelper:
    def __init__(self, client):
        self._client = client
    def draw_point(self, location, size=0.1, color=None, life_time=-1.0, persistent_lines=True):
        from CarlaNet.Types.Rpc.Debug import PointPrimitive, DebugShape
        from CarlaNet.Types.Rpc import Color as CSColor
        c = color if color is not None else CSColor(255, 0, 0)
        # If color is Python carla.Color, unwrap
        if not isinstance(c, CSColor):
            c = CSColor(int(c.r), int(c.g), int(c.b))
        primitive = PointPrimitive(location, float(size))
        shape = DebugShape(primitive, c, float(life_time), bool(persistent_lines))
        _sync(self._client.DrawDebugShapeAsync(shape))
    # Similar for draw_line, draw_arrow, draw_box, draw_string.
```

Expose as `World.debug` property:
```python
class World:
    @property
    def debug(self):
        return DebugHelper(self._client)
```

**Note**: manual_control passes `carla.Color(r, g, b)` (integers 0-255). The C# `Color`
record is `(byte R, byte G, byte B)`. The shim must coerce Python `Color` to C# `Color`.

---

## 8. `actor.listen(callback)` ergonomics

### Current state

```python
def listen(self, callback):
    token = self._actor.StreamToken
    ...
    from System import Action
    from CarlaNet.Transport.Streaming import SensorFrame
    cs_cb = Action[SensorFrame](callback)
    self._sub = self._client.SubscribeToStream(token, cs_cb)
```

This requires the **user** to write a callback that accepts a `SensorFrame`. manual_control's
callbacks expect typed objects (`image`, `event`, `radar_data`, etc.).

### Required behavior

The shim's `Actor.listen(callback)` must:
1. Decide the sensor type from `self.type_id` (e.g., `sensor.camera.rgb`).
2. Build an internal `SensorFrame -> wrapper_object` parser.
3. Wrap the user's callback so it receives the parsed wrapper, not the raw frame.
4. Construct the `Action[SensorFrame]` internally.
5. Call `_client.SubscribeToStream(self._actor.StreamToken, internal_cb)`.

### Pseudocode

```python
def listen(self, py_callback):
    from System import Action
    from CarlaNet.Transport.Streaming import SensorFrame

    type_id = self.type_id
    parser = _SENSOR_PARSERS.get(type_id)
    if parser is None:
        # Fallback: hand back the raw SensorFrame (legacy/unknown sensors)
        cs_cb = Action[SensorFrame](py_callback)
    else:
        def cs_cb_impl(sensor_frame):
            try:
                data = parser(sensor_frame)
                py_callback(data)
            except Exception as ex:
                import traceback; traceback.print_exc()
        cs_cb = Action[SensorFrame](cs_cb_impl)

    token = self._actor.StreamToken
    if len(token) != 24:
        raise RuntimeError(f"Actor {self.id} ({type_id}) has no sensor stream")
    self._sub = self._client.SubscribeToStream(token, cs_cb)
```

Where `_SENSOR_PARSERS` is built from §2.10's table. Each parser receives a `SensorFrame`
and returns the appropriate wrapper class (`Image`, `CollisionEvent`, etc.). The parser
reads `sensor_frame.Header.Frame`, `.Timestamp`, the sensor `Transform` from header,
plus the `sensor_frame.PayloadBytes` to decode the payload.

### SensorType peek (alternative dispatch, not recommended primary)

The 48-byte header's first 8 bytes are `sensor_type` (uint64). C# exposes this as
`sensor_frame.Header.SensorType` (uint64). The values are the **CompositeSerializer
template-position indexes** from `SensorRegistry.h`. They are not currently exposed as
a C# enum.

**Agent 2 should add** a `SensorType` enum to `CarlaNet.Sensors` matching the registry
indices (per §2.10's table). This is for diagnostic purposes (e.g., logging "unexpected
sensor type X"); the **primary** dispatch should still use `type_id` because that's
deterministic from the Python side and doesn't depend on the registry index ordering.

---

## 9. C# changes needed

Listed in priority order. Each lists the file to edit and what to add.

### 9.1 `MsgPackRpcClient.SetTimeout(TimeSpan)`
File: `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Transport\MsgPackRpc\MsgPackRpcClient.cs`

Currently line 24: `private readonly TimeSpan _timeout;`

Make mutable + add public setter on `CarlaClient` (since `MsgPackRpcClient` is `internal sealed`):

```csharp
// In MsgPackRpcClient.cs:
private TimeSpan _timeout;           // remove 'readonly'
public void SetTimeout(TimeSpan t) => _timeout = t;

// In CarlaClient.cs (add public method):
public void SetTimeout(TimeSpan t) => _rpc.SetTimeout(t);
```

Then the Python shim's `Client.set_timeout(timeout_s)` becomes:
```python
self._inner.SetTimeout(TimeSpan.FromMilliseconds(int(timeout_s * 1000)))
```

### 9.2 `ApplyControlToWalkerAsync` — exists, but verify it accepts WalkerControl directly
File: `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Transport\CarlaClient.cs:330`

Already exists. No change needed unless serialization needs work. Verify the Python shim
calls it.

### 9.3 `world.on_tick` real implementation
Not directly an RPC. The world observer (`StartWorldObserverAsync`) already subscribes
to the EpisodeState stream. Need to:

(a) **In `CarlaClient.cs`**: emit a `Timestamp(frame, elapsed_seconds, delta_seconds, platform_ts)`
event from `ParseEpisodeState` (it already has `episode_id`, `platform_ts`, `delta_s` —
plus the sensor frame's `Header.Frame` and `Header.Timestamp`).

Add to `CarlaClient.cs`:
```csharp
public event Action<TickTimestamp>? OnTick;

public sealed record TickTimestamp(ulong Frame, double ElapsedSeconds,
                                    double DeltaSeconds, double PlatformTimestamp);

private void OnWorldObserverFrame(SensorFrame frame)
{
    ParseEpisodeState(frame.Payload.Span);
    // Also raise tick event.
    OnTick?.Invoke(new TickTimestamp(
        Frame: frame.Header.Frame,
        ElapsedSeconds: frame.Header.Timestamp,
        DeltaSeconds: /* parse from EpisodeState header bytes 16..20 */,
        PlatformTimestamp: /* from EpisodeState header bytes 8..16 */));
}
```

(b) **In Python shim**: subscribe Python callback to the C# event:
```python
class World:
    def on_tick(self, py_callback):
        from System import Action
        def handler(ts):
            try:
                # Wrap ts in a Python Timestamp-like object
                t = _Timestamp(int(ts.Frame), float(ts.ElapsedSeconds),
                              float(ts.DeltaSeconds), float(ts.PlatformTimestamp))
                py_callback(t)
            except Exception:
                import traceback; traceback.print_exc()
        cs_handler = Action[CarlaClient.TickTimestamp](handler)
        self._client.OnTick += cs_handler
        return id(cs_handler)  # callback id for remove_on_tick (not implemented)
```

### 9.4 Optional: `SensorType` enum

Add `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Sensors\SensorType.cs`:
```csharp
namespace CarlaNet.Sensors;
public enum SensorType : ulong {
    Collision = 0, Depth = 1, Normals = 2, Dvs = 3, Gnss = 4, Imu = 5,
    LaneInvasion = 6, Obstacle = 7, OpticalFlow = 8, Radar = 9,
    SemanticLidar = 10, Lidar = 11, Rss = 12, Rgb = 13,
    SemanticSegmentation = 14, InstanceSegmentation = 15,
    WorldObserver = 16, GBufferUint8 = 17, GBufferFloat = 18
}
```

(Per `SensorRegistry.h:64-84`.)

### 9.5 No other RPC plumbing missing from `CarlaClient.cs`

All manual_control RPC needs are covered by existing `CarlaClient.cs` methods. Verified by
matching the script's GAP list against the file's method list.

---

## 10. Manual_control.py edits required

Goal: **absolute minimum edits** to the script. The user wants this to remain a near-copy.

### Required edits

| Line | Original | New | Justification |
|-----:|----------|-----|---------------|
| 60 | `import carla` | `import carlanet as carla` | Switch the import source to the shim. |
| 62 | `from carla import ColorConverter as cc` | `from carlanet import ColorConverter as cc` | The `from X import Y` form does not benefit from the alias at line 60; it needs an explicit second import. |
| 1279 | `client.set_timeout(2000.0)` | `client.set_timeout(20.0)` | The upstream `set_timeout` takes **seconds** (see `Client.cpp:15` `SetTimeout(seconds)`). `2000.0` seconds is unintentionally generous and reflects an upstream bug (or stress test). 20 seconds is sane for first-RPC plus all subsequent calls (server can be slow when JIT'ing shaders). Optional — leaving as 2000.0 won't actually break anything but it is misleading. |

That's it. **Three lines**. (The third is optional and may be skipped.)

### Edits explicitly NOT required (handled in shim)

- `carla.Color(r, g, b)` — exposed by shim.
- `carla.VehicleLightState.X` — exposed by shim.
- `carla.WeatherParameters.ClearNoon` etc. — exposed by shim.
- `carla.Vehicle`, `carla.Walker` — exposed by shim.
- `isinstance(self._control, carla.VehicleControl)` — works because the shim exports the
  C# `VehicleControl` record type directly.
- `world.on_tick(...)` — shim implements it.

---

## 11. Test-blocking issues

These cannot be hidden in the shim and require explicit acknowledgement.

### 11.1 Lane invasion detection — degraded (no markings reported)

The server-side LaneInvasionSensor uses `NoopSerializer` (see `SensorRegistry.h:71`).
Upstream libcarla's `LaneInvasionSensor` is a client-side construct that maintains its
own map waypoint cache and reports which lane markings were crossed at each event.

**CarlaNet does not implement** the client-side map traversal needed to compute
`crossed_lane_markings`. The result: `LaneInvasionEvent.crossed_lane_markings` will always
be an empty list.

**Effect on manual_control**: line 953 `lane_types = set(x.type for x in event.crossed_lane_markings)`
yields the empty set; the HUD notification "Crossed line " displays without any specific
lane type names. The sensor still fires events when lanes are crossed (server side).

**Acceptable** — degraded but non-fatal.

### 11.2 `image.save_to_disk` — needs Python-side implementation

Upstream uses `ImageIO::WriteView` which dispatches through boost::gil to write PNG/JPEG/etc.
The shim must implement this in Python:

```python
def save_to_disk(self, path):
    import os
    os.makedirs(os.path.dirname(path), exist_ok=True)
    # Use PIL if available, else write raw BMP
    try:
        from PIL import Image as PILImage
        arr = np.frombuffer(self.raw_data, dtype=np.uint8).reshape((self.height, self.width, 4))
        rgb = arr[:, :, [2, 1, 0]]   # BGRA → RGB
        PILImage.fromarray(rgb).save(path + '.png')
    except ImportError:
        # Fallback: write raw .bin
        with open(path + '.bin', 'wb') as f:
            f.write(self.raw_data)
```

Manual_control invokes this when recording (R key). Functional only if PIL is installed
(which it usually is via pygame's dependencies).

### 11.3 `world.wait_for_tick()` — sleep-based fallback

Per current shim line 645-647. In async mode, calling `wait_for_tick()` blocks for 50ms
instead of waiting for an actual server tick event. Manual_control calls it only once
during init (line 1310) when not in sync mode. **Acceptable** — server will produce frames
within 50ms easily.

If async mode causes early-tick desync (no actor transforms cached yet), the world observer's
`StartWorldObserverAsync` should be called early (before any actor query). The shim already
exposes `Client.start_observer()` — **Agent 3 must call it inside `Client.get_world()`** so it
happens automatically. (Or document in §11 that the test driver must call it.)

### 11.4 `BlueprintLibrary.filter` semantics

manual_control line 188:
```python
bps = [x for x in bps if int(x.get_attribute('generation')) == int_generation]
```

The shim's `_Attribute.__str__` returns the string value; `int(str_attr)` works if the
value is numeric. **No issue** but worth confirming on first run.

### 11.5 `ActorList.filter` for `world.get_actors()`

Used at line 723: `vehicles = world.world.get_actors().filter('vehicle.*')`.

Current shim `get_actors()` returns a **Python list**. Python list has no `.filter()`.
**Must** wrap the result in a small `_ActorList` class that has `filter`, `__iter__`,
`__len__`, `__getitem__`, `find`. This is a shim-side fix, not a script-side fix.

### 11.6 `World.player.id` filtering vs Python list

Line 768: `vehicles = [(distance(x.get_location()), x) for x in vehicles if x.id != world.player.id]`.

This iterates the previous `vehicles` (the filtered ActorList). Works fine once `_ActorList`
has `__iter__`.

### 11.7 `Settings` field name casing

Lines 1284-1289:
```python
settings = sim_world.get_settings()
if not settings.synchronous_mode:
    settings.synchronous_mode = True
    settings.fixed_delta_seconds = 0.05
sim_world.apply_settings(settings)
```

`settings` is the C# `EpisodeSettings` record struct. Pythonnet does NOT auto-rename
PascalCase fields to snake_case. **Fix needed**: shim must wrap `get_settings()` in a
mutable Python class that has `synchronous_mode` etc. as properties backed by the C#
record.

Or, alternative: rename the C# fields to snake_case. **Do NOT do this** — it breaks the
msgpack wire compatibility (record fields are serialized in their declared order, so renames
don't change the wire — but if someone uses `MessagePackObject` named-key serialization, it
could). Safer to wrap in Python.

```python
class _WorldSettings:
    def __init__(self, cs_settings):
        self._cs = cs_settings
    @property
    def synchronous_mode(self): return bool(self._cs.SynchronousMode)
    @synchronous_mode.setter
    def synchronous_mode(self, v):
        # EpisodeSettings is a record struct — must construct new one
        self._cs = EpisodeSettings(v, self._cs.NoRenderingMode, ...)
    # similarly for fixed_delta_seconds, no_rendering_mode, ...
    def _to_cs(self):
        return self._cs
```

`World.get_settings` returns `_WorldSettings`. `World.apply_settings` accepts either
`_WorldSettings` or `EpisodeSettings` and unwraps.

### 11.8 `actor.bounding_box.extent.x` — lowercase fields on C# types

`BoundingBox.Extent` is a `Vector3D` (PascalCase X/Y/Z). Manual_control uses `.x`. Either:
- Add Python aliases on the shim's Vector3D returned from `actor.bounding_box.extent`:
  wrap the C# `BoundingBox` access path so `.extent` returns a `_Vector3D` with `.x`.
- OR add `x`/`y`/`z` properties to the C# Vector3D struct (less invasive — they're just
  aliases that don't affect serialization).

**Recommended**: add `[IgnoreMember] public float x => X;` (and similar) to `Vector3D.cs`.
Easier and applies everywhere. **Action**: Agent 2 in §9.

Same for `Location` (which derives from / contains `Vector3D`), `Rotation` (`pitch`, `yaw`,
`roll`), `BoundingBox` (`location`, `extent`, `rotation`), `Transform` (`location`,
`rotation`), `Timestamp` (`frame`, `elapsed_seconds`, etc.).

This is the **single biggest source of GAPs** — most fields in `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Geom\*.cs` need lowercase
property aliases. Verify each geom type and add them.

### 11.9 `BlueprintLibrary` filter returning a generator vs list

The shim's `BlueprintLibrary.filter` returns a `BlueprintLibrary` (good). Manual_control
does `len(bps)` (line 181) and `random.choice(bps)` (line 261). `BlueprintLibrary` has
`__len__` and `__getitem__` so `random.choice` works. **OK**.

### 11.10 `get_actor_blueprints` `generation` filter expects a string attribute

Line 188: `int(x.get_attribute('generation'))`. Requires that vehicle blueprints have a
`generation` attribute. If they don't, the `int()` call throws and the function returns
`[]`. **No fix needed** — manual_control already handles this case with a `try/except`.

### 11.11 ColorConverter `Raw` is the default for the first camera image — race with restart

When the camera transform changes (TAB key), `set_sensor` destroys the old sensor and spawns
a new one. The shim's `Actor.destroy` calls `self.stop()` before destroying. This matches
upstream's behavior. Per `CarlaNetSupplementary.md` §13, the script uses a 0.4s sleep
between `stop()` and `destroy()` to drain in-flight callbacks (manual_control lines 1180-1182).

This is fragile but matches upstream practice. Don't try to fix it in the shim — it's a
race condition with the streaming TCP socket that's inherent to the protocol.

---

## Appendix A: Wire-level summary

For Agent 2's reference when implementing C# parsers (most already exist):

| Sensor | Wire format | C# class | Status |
|--------|-------------|----------|--------|
| `sensor.camera.rgb/depth/seg/inst/normals` | header + 12B + BGRA pixels | `ImageSensorData` | done |
| `sensor.camera.optical_flow` | header + 12B + (float X, float Y) per px | `OpticalFlowSensorData` | done |
| `sensor.camera.dvs` | header + DVSEventArray | `DvsSensorData` (file exists) | needs check |
| `sensor.lidar.ray_cast` | header + var header + 16B points | `LidarSensorData` | done |
| `sensor.lidar.ray_cast_semantic` | header + var header + 24B points | `SemanticLidarSensorData` | done |
| `sensor.other.radar` | header + 16B detections | `RadarSensorData` | done |
| `sensor.other.collision` | header + msgpack [actor, actor, vec3] | `CollisionSensorData` | done |
| `sensor.other.obstacle` | header + msgpack [actor, actor, float] | `ObstacleSensorData` | done |
| `sensor.other.gnss` | header + msgpack [lat, lon, alt] | `GnssSensorData` | done |
| `sensor.other.imu` | header + msgpack [accel, gyro, compass] | `ImuSensorData` | done |
| `sensor.other.lane_invasion` | header only (Noop payload) | (n/a) | client-side computation NOT implemented (§11.1) |

## Appendix B: Files Agent 2 likely needs to touch

- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Transport\MsgPackRpc\MsgPackRpcClient.cs` — `SetTimeout`
- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Transport\CarlaClient.cs` — `SetTimeout` passthrough, `OnTick` event, possibly emit a `TickTimestamp` from `OnWorldObserverFrame`
- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Geom\Vector3D.cs` — lowercase property aliases (`x`, `y`, `z`)
- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Geom\Location.cs` — lowercase (`x`, `y`, `z`)
- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Geom\Rotation.cs` — lowercase (`pitch`, `yaw`, `roll`)
- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Geom\Transform.cs` — lowercase (`location`, `rotation`)
- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Geom\BoundingBox.cs` — lowercase (`location`, `extent`, `rotation`)
- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Types\Rpc\Environment\EpisodeSettings.cs` — lowercase or Python wrapper (snake_case)
- Optional: `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\src\CarlaNet.Sensors\SensorType.cs` — enum from §9.4

## Appendix C: Files Agent 3 likely needs to touch

- `g:\Projects\CarlaUE_5_7_4\carla\CarlaNet\python\carlanet\__init__.py` — almost everything (per §§1-8, 11)
- Possibly add `carlanet/_sensors.py` for the wrapper classes (Image, CollisionEvent, etc.)
- Possibly add `carlanet/_color.py` for ColorConverter conversions
- Possibly add `carlanet/_weather.py` for the preset table

(Pure organization — the shim can also remain one big `__init__.py`.)

## Appendix D: Files Agent 3 likely needs to touch in manual_control

- `g:\Projects\CarlaUE_5_7_4\carla\PythonAPI\examples\manual_control.py` lines 60, 62, and possibly 1279 (see §10). **No other changes.**
