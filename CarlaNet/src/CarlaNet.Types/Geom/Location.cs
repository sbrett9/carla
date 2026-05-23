// Source: carla/geom/Location.h — inherits Vector3D, no additional members.
// Serialized identically to Vector3D: [x, y, z].
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct Location(
    [property: Key(0)] float X,
    [property: Key(1)] float Y,
    [property: Key(2)] float Z);
