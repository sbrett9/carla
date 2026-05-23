// Source: carla/geom/Rotation.h — MSGPACK_DEFINE_ARRAY(pitch, yaw, roll)
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct Rotation(
    [property: Key(0)] float Pitch,
    [property: Key(1)] float Yaw,
    [property: Key(2)] float Roll);
