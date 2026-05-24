# CarlaNet Supplementary — Protocol & Implementation Notes

This document records everything learned about mapping the CARLA server wire protocol
to CarlaNet's managed .NET implementation. Use it as the reference before looking at
libcarla source. Sections match the `§` numbers used in the CarlaNet source comments.

---

## 1. msgpack-RPC Wire Protocol (§5, §7)

**Source:** `rpclib` — `Build/_deps/rpclib-src/`

rpclib uses **raw msgpack streaming with NO length prefix**. There is no framing envelope
around individual messages. The receiver uses `MessagePackStreamReader` to detect message
boundaries from the msgpack structure itself.

### Request format
```
[0, msg_id, "method_name", [args...]]
```

### Response format
```
[1, msg_id, error_or_nil, result_or_nil]
```

### The Metadata wrapper — critical
Every bound server function receives `Metadata` as its **first implicit argument**:
```
// carla/rpc/Client.h line 33:
_client.call(fn, Metadata::MakeSync(), args...)
```
`Metadata::MakeSync()` serializes as `[false]` (one-element array, `_asynchronous_call = false`).
This means the params array on the wire is always:
```
[[false], arg0, arg1, ...]
```
Forgetting this causes all calls to fail with "wrong argument count."

### msg_id wrapping
`_nextMsgId` starts at `uint.MaxValue`. The first call does `Interlocked.Increment`, which
wraps to `0`. This matches rpclib's id counter behavior.

---

## 2. Response Unpacking — `UnpackResult<T>` (§7)

**Source:** `carla/rpc/Response.h`

There are two distinct response shapes depending on the C++ return type.

### Response\<void\>
Uses `std::optional<ResponseError> _data; MSGPACK_DEFINE_ARRAY(_data)`.

| Outcome | Wire bytes (inside result field) |
|---------|----------------------------------|
| Success | `[[false]]` — optional empty → `[false]` |
| Error   | `[[true, ["message"]]]` — optional has value |

### Response\<T\>
Uses `std::variant<ResponseError, T> _data; MSGPACK_DEFINE_ARRAY(_data)`.

| Outcome | Wire bytes (inside result field) |
|---------|----------------------------------|
| Success | `[[1, value]]` — variant index 1 |
| Error   | `[[0, ["message"]]]` — variant index 0 |

### Parsing order — the critical rule
```csharp
int outer = reader.ReadArrayHeader();   // always 1 (MSGPACK_DEFINE_ARRAY wraps in 1-elem array)
if (outer == 0) return default!;
int inner = reader.ReadArrayHeader();   // 1 for void-success, 2 for everything else

if (inner == 1)
{
    reader.Skip();   // bool false — Response<void> success
    return default!;
}
// inner == 2 from here
if (reader.NextMessagePackType == MessagePackType.Boolean)
{
    // Response<void> error: [true, ["msg"]]
    reader.ReadBoolean();
    reader.ReadArrayHeader();
    throw new CarlaRpcException(reader.ReadString());
}
// Response<T>: [idx, value]
int idx = reader.ReadInt32();
if (idx == 0) { /* ResponseError */ throw ...; }
return MessagePackSerializer.Deserialize<T>(ref reader);
```
**You must check `inner == 1` before calling `ReadInt32()`**, because for void-success the
next token is a bool, not an int, and reading it as int corrupts the stream.

---

## 3. Sensor Streaming Protocol (§9)

**Source:** `LibCarla/source/carla/streaming/detail/tcp/Client.cpp` lines 114–116

The streaming port is always **RPCPort + 1** (default: 2001). Verified from `CarlaSettings.cpp` line 72.

### Connection sequence
1. TCP connect to `token.Address : token.Port`
2. Send `stream_id` as uint32 little-endian (4 bytes) — this is the subscribe message
3. Receive frames: `[uint32 LE total_size][payload_bytes]` — repeated forever

`message_size_type = uint32_t` confirmed from `Types.h`. The size field is the byte count
of the payload that follows, not including the 4-byte size field itself.

### Socket option
Set `TcpClient.NoDelay = true` to match libcarla's `no_delay` socket option. Without this
Nagle's algorithm can introduce latency on small writes (the stream_id send).

---

## 4. StreamToken Layout (§9)

**Source:** `carla/streaming/detail/Token.h` — `#pragma pack(push, 1)`, 24 bytes total.

```
Offset  Size  Field
0       4     stream_id  (uint32 LE)
4       2     port       (uint16 LE)
6       1     protocol   (0=NotSet, 1=TCP, 2=UDP)
7       1     address_type (0=NotSet, 1=IPv4, 2=IPv6)
8       16    address bytes
```

For IPv4: only the first 4 of the 16 address bytes are used; the rest are padding.

`0.0.0.0` (IPAddress.Any) in the address field means "same host as the RPC server."
When seen, resolve the server hostname instead of connecting to 0.0.0.0.

`AddressType == NotSet (0)` also falls through to server-host resolution.

### Hostname resolution
`IPAddress.Parse("localhost")` throws. Use `Dns.GetHostAddresses()` with IPv4 preference:
```csharp
var addrs = Dns.GetHostAddresses(host);
return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
```

### Token serialization
`carla::streaming::Token` serializes via `MSGPACK_DEFINE_ARRAY(data)` as a 1-element
msgpack array containing a 24-byte binary blob — i.e., `[[bin24]]`.
This is why `EpisodeInfo.Token` is typed as `RawToken` (a record with a single `byte[]`
field) rather than `StreamToken` directly.

Actor `stream_token` fields serialize as a flat binary blob (`std::vector<unsigned char>`),
**not** wrapped in an array — so they are read directly as `byte[]`.

---

## 5. SensorFrame Layout (§9)

Every frame received from the streaming port is:
```
[48 bytes: SensorHeaderSerializer::Header][variable: sensor-specific payload]
```

### Header layout (48 bytes)
```
Offset  Size  Field
0       8     SensorType  (uint64)
8       8     Frame       (uint64)
16      8     Timestamp   (double, seconds)
24      12    Location    (float x, y, z)
36      12    Rotation    (float pitch, yaw, roll)
```
`sizeof(RawSensorHeader) = 48` — verified from `SensorHeaderSerializer.h`.

`SensorFrame` strips the header and exposes `Payload` (everything after byte 48).

---

## 6. Image Sensor Payload Layout (§10.2)

**Source:** `carla/sensor/s11n/ImageSerializer.h`

After the 48-byte `SensorFrame` header, the payload for all camera types is:

```
Offset  Size  Field
0       4     width   (uint32 LE)
4       4     height  (uint32 LE)
8       4     fov_angle (float LE)
12      W*H*4 pixel data
```

**Pixel format: BGRA** — `{B, G, R, A}` in byte order. `A` is always 255.
This is `FColor` in Unreal Engine 5, which stores `{B, G, R, A}` not `{R, G, B, A}`.

To display in pygame (which expects RGB):
```python
arr = np.frombuffer(raw, dtype=np.uint8, offset=12).reshape((H, W, 4))
rgb = arr[:, :, [2, 1, 0]]   # channel reorder: BGR → RGB, drop A
surf = pygame.surfarray.make_surface(rgb.swapaxes(0, 1))
```
The `offset=12` skips the `ImageSerializer::ImageHeader`. Without it the reshape fails
because `W*H*4 + 12` bytes do not divide evenly into `(H, W, 4)`.

---

## 7. Other Sensor Payload Formats

### IMU and GNSS (§10.8, §10.9)
These use **msgpack encoding**, not raw binary. Deserialize with `MessagePackSerializer`.
```
IMU:  [accelerometer:Vector3D, gyroscope:Vector3D, compass:float]
GNSS: [latitude:double, longitude:double, altitude:double]
```

### Collision / Obstacle (§10.10, §10.11)
Also msgpack-encoded:
```
Collision:  [self_actor:Actor, other_actor:Actor, normal_impulse:Vector3D]
Obstacle:   [self_actor:Actor, other_actor:Actor, distance:float]
```

### Radar (§10.7)
Raw binary after 48-byte header. Flat array of 16-byte `RadarDetection` structs:
```
{float velocity, float azimuth, float altitude, float depth}  — all in SI units / radians
```

### LiDAR — Ray-Cast (§10.5)
Raw binary after 48-byte header:
```
[HorizontalAngle: uint32 bits reinterpreted as float]
[ChannelCount: uint32]
[PointCount[0]: uint32] ... [PointCount[C-1]: uint32]
[Points: {float x, y, z, intensity} * totalPoints]
```
`HorizontalAngle` is stored as `uint32` bits — use `BitConverter.Int32BitsToSingle`, not a
straight cast.

### Optical Flow (§10.3)
Same 12-byte header as image (width, height, fov), then `{float X, float Y}` per pixel (8 bytes/pixel).

---

## 8. Actor Struct Layout (§8)

**Source:** `carla/rpc/Actor.h`
```
MSGPACK_DEFINE_ARRAY(id, parent_id, description, bounding_box, semantic_tags, stream_token)
```

| Key | Field | Notes |
|-----|-------|-------|
| 0 | `Id` | `uint32` |
| 1 | `ParentId` | `uint32`, 0 if none |
| 2 | `Description` | `ActorDescription` (uid, id string, attributes) |
| 3 | `BoundingBox` | `{Location, Vector3D Extent, Rotation}` — **already in the spawn response** |
| 4 | `SemanticTags` | `byte[]` raw binary |
| 5 | `StreamToken` | `byte[]`, 0 bytes for non-sensors, 24 bytes for sensors |

**The bounding box does not require a separate RPC call.** It is returned as part of
`spawn_actor` / `spawn_actor_with_parent` response. Access it as `actor.BoundingBox.Extent`.

There is no `get_actor_bounding_box` RPC on the server. The original Python API reads
`actor.bounding_box` from the locally cached actor description.

---

## 9. Camera Placement — Matching manual_control.py

The original `manual_control.py` camera position 0 (default behind-vehicle view):
```python
bound_x = 0.5 + actor.bounding_box.extent.x
bound_z = 0.5 + actor.bounding_box.extent.z
Location(x=-2.0*bound_x, y=0, z=2.0*bound_z), Rotation(pitch=8.0)
AttachmentType.SpringArmGhost
```

**pitch must be positive (+8.0).** Negative pitch points the camera downward into the vehicle
body. When the camera is inside the vehicle mesh, pixel values are nearly all zero and
auto-exposure causes them to decrease over time as the engine compensates for tiny bright
leaks through mesh gaps.

The `y=+0.0*bound_y` in the original is always 0 regardless of `bound_y`. y=0 is correct.

In `spawn_actor_with_parent`, pass `AttachmentType.SpringArmGhost` (enum value 2).

---

## 10. Vehicle Physics Modification

The original `manual_control.py` calls `modify_vehicle_physics` after every spawn:
```python
physics_control = actor.get_physics_control()
physics_control.use_sweep_wheel_collision = True
actor.apply_physics_control(physics_control)
```
This corresponds to `GetVehiclePhysicsControlAsync` + setting `UseSweepWheelCollision = true`
+ `ApplyPhysicsControlToVehicleAsync`. Improves wheel collision accuracy, especially on
uneven terrain.

---

## 11. Spawn Collision Handling

Spawn points can be blocked by actors left over from previous sessions (e.g., after a
process kill). The server returns a `CarlaRpcException` with the text
`"Spawn failed because of collision at spawn position"`. Retry with the next spawn point
index. Trying 10 consecutive points is generally sufficient.

---

## 12. Synchronous Mode

To enable sync mode, fetch current settings, rebuild with `SynchronousMode=true` and
`FixedDeltaSeconds=0.05`, then call `SetEpisodeSettingsAsync`. After each frame, call
`SendTickCueAsync()` before rendering.

In sync mode, the server does not advance until `tick_cue` is received, so the camera
stream will not produce new frames without calling it. Always restore original settings on
exit.

---

## 13. pythonnet / C# Interop Notes

- Load `coreclr` runtime with `pythonnet.load("coreclr")` before `import clr`.
- Remove the script directory from `sys.path` before importing `clr` to prevent Python
  namespace shadowing of C# namespaces.
- Async C# methods return `Task<T>`. Call `.GetAwaiter().GetResult()` to block synchronously:
  ```python
  def rpc(task): return task.GetAwaiter().GetResult()
  ```
- `DisposeAsync()` returns `ValueTask`, not `Task`. Chain `.AsTask()` before calling `rpc()`:
  ```python
  rpc(client.DisposeAsync().AsTask())
  ```
- C# `Action<T>` delegates must be constructed explicitly for pythonnet to resolve the
  generic type:
  ```python
  from System import Action
  from CarlaNet.Transport.Streaming import SensorFrame
  cs_action = Action[SensorFrame](on_frame)
  ```
- `sf.PayloadBytes` returns a C# `byte[]`. Wrap with `bytes()` before passing to numpy:
  ```python
  raw = bytes(sf.PayloadBytes)
  ```

---

## 14. DDC (Derived Data Cache) and Shader Recompilation

UE5.5+ uses a Zen DDC store separate from compiled C++ binaries. Shader cache is **not**
invalidated by rebuilding the C++ project. If a shader parameter struct changes (e.g.,
`FPostProcessMaterialInputs`), the stale DDC causes a runtime assertion crash:
```
Assertion failed: [FPostProcessMaterialVS] parameter structure mismatch
```
Fix: wipe the DDC directories and kill the ZenServer process before relaunching the editor.
The shaders recompile on the next PIE session. Cooking shaders in the editor (Play In Editor)
populates the cook cache and avoids recompilation on subsequent launches.

---

## 15. Control Application — Frame Rate Impact

Calling `ApplyControlToVehicleAsync` (an RPC round-trip) synchronously every frame on the
main thread blocks at the game framerate — at 60 FPS each call adds ~16 ms latency budget.
Only send control when the tuple `(throttle, brake, steer, hand_brake, reverse)` changes:
```python
ctrl_key = (round(throttle, 2), round(brake, 2), steer_r, hand_brake, reverse)
if ctrl_key != last_ctrl:
    rpc(client.ApplyControlToVehicleAsync(vehicle_id, ctrl))
    last_ctrl = ctrl_key
```

---

## 16. BuildAndPackageCarla.ps1 — `-NoArchive` Switch

Pass `-NoArchive` to skip the tar/zip step when you intend to run the binaries locally:
```powershell
.\BuildAndPackageCarla.ps1 -NoArchive
```
Internally sets CMake option `CARLA_UNREAL_PACKAGE_NO_COMPRESSION=ON`.
