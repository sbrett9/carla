// Source: carla/geom/BoundingBox.h — MSGPACK_DEFINE_ARRAY(location, extent, rotation)
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct BoundingBox(
    [property: Key(0)] Location Location,
    [property: Key(1)] Vector3D Extent,
    [property: Key(2)] Rotation Rotation);
