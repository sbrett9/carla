// Source: carla/geom/Vector2D.h
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct Vector2D(
    [property: Key(0)] float X,
    [property: Key(1)] float Y)
{
    public Vector2D() : this(0f, 0f) {}
    [IgnoreMember] public float x => X;
    [IgnoreMember] public float y => Y;
}
