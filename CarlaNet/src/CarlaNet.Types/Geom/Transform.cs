// Source: carla/geom/Transform.h — MSGPACK_DEFINE_ARRAY(location, rotation)
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct Transform(
    [property: Key(0)] Location Location,
    [property: Key(1)] Rotation Rotation);
