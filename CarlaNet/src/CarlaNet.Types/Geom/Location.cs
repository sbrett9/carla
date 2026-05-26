// Source: carla/geom/Location.h — inherits Vector3D, no additional members.
// Serialized identically to Vector3D: [x, y, z].
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct Location(
    [property: Key(0)] float X,
    [property: Key(1)] float Y,
    [property: Key(2)] float Z)
{
    public Location() : this(0f, 0f, 0f) {}
    [IgnoreMember] public float x => X;
    [IgnoreMember] public float y => Y;
    [IgnoreMember] public float z => Z;
}
