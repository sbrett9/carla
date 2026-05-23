// Source: carla/geom/Vector2D.h
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct Vector2D(
    [property: Key(0)] float X,
    [property: Key(1)] float Y);
