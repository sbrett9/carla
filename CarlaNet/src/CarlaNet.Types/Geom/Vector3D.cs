// Source: carla/geom/Vector3D.h
// Note: MSGPACK_DEFINE_ARRAY uses a manual template expansion (not the macro)
// due to the 'z' variable shadowing issue in msgpack-c — field order is x,y,z.
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct Vector3D(
    [property: Key(0)] float X,
    [property: Key(1)] float Y,
    [property: Key(2)] float Z)
{
    public Vector3D() : this(0f, 0f, 0f) {}
    // Lowercase Python-style aliases (consumed by carlanet Python shim via pythonnet).
    // [IgnoreMember] keeps them out of msgpack serialization.
    [IgnoreMember] public float x => X;
    [IgnoreMember] public float y => Y;
    [IgnoreMember] public float z => Z;
}
