// Source: carla/geom/Vector3DInt.h
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct Vector3DInt(
    [property: Key(0)] int X,
    [property: Key(1)] int Y,
    [property: Key(2)] int Z);
