// Source: carla/rpc/Color.h — MSGPACK_DEFINE_ARRAY(r, g, b)
// Distinct from sensor pixel Color {B,G,R,A} which is a raw binary layout, not msgpack.
using CarlaNet.Types.Rpc.Enums;

namespace CarlaNet.Types.Rpc;

[MessagePackObject]
public record struct Color(
    [property: Key(0)] byte R,
    [property: Key(1)] byte G,
    [property: Key(2)] byte B);

// Source: carla/rpc/FloatColor.h — MSGPACK_DEFINE_ARRAY(r, g, b, a)
[MessagePackObject]
public record struct FloatColor(
    [property: Key(0)] float R,
    [property: Key(1)] float G,
    [property: Key(2)] float B,
    [property: Key(3)] float A);

// Source: carla/rpc/LightState.h — MSGPACK_DEFINE_ARRAY(_id, _location, _intensity, _group, _color, _active)
[MessagePackObject]
public record struct LightState(
    [property: Key(0)] LightId Id,
    [property: Key(1)] Location Location,
    [property: Key(2)] float Intensity,
    [property: Key(3)] LightGroup Group,
    [property: Key(4)] Color Color,
    [property: Key(5)] bool Active);

// Source: carla/rpc/LabelledPoint.h — MSGPACK_DEFINE_ARRAY(_location, _label)
[MessagePackObject]
public record struct LabelledPoint(
    [property: Key(0)] Location Location,
    [property: Key(1)] CityObjectLabel Label);
