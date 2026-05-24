// Source: carla/rpc/LightState.h
using CarlaNet.Types.Rpc;

namespace CarlaNet.Types.Rpc.Lighting;

// LightGroup : uint8_t (carla/rpc/LightState.h)
public enum LightGroup : byte
{ None = 0, Vehicle = 1, Street = 2, Building = 3, Other = 4 }

// MSGPACK_DEFINE_ARRAY(_id, _location, _intensity, _group, _color, _active)
[MessagePackObject]
public record struct LightState(
    [property: Key(0)] LightId Id,
    [property: Key(1)] Location Location,
    [property: Key(2)] float Intensity,
    [property: Key(3)] LightGroup Group,
    [property: Key(4)] Color Color,
    [property: Key(5)] bool Active);
