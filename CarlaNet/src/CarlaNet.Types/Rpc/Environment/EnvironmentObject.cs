// Source: carla/rpc/EnvironmentObject.h
// MSGPACK_DEFINE_ARRAY(transform, bounding_box, id, name, type)
using CarlaNet.Types.Rpc.Enums;

namespace CarlaNet.Types.Rpc.Environment;

[MessagePackObject]
public record struct EnvironmentObject(
    [property: Key(0)] Transform Transform,
    [property: Key(1)] BoundingBox BoundingBox,
    [property: Key(2)] ulong Id,
    [property: Key(3)] string Name,
    [property: Key(4)] CityObjectLabel Type);
