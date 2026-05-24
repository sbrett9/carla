// Source: carla/rpc/ActorAttribute.h — ActorAttributeValue
// MSGPACK_DEFINE_ARRAY(id, type, value)
namespace CarlaNet.Types.Rpc.Actors;

[MessagePackObject]
public record struct ActorAttributeValue(
    [property: Key(0)] string Id,
    [property: Key(1)] ActorAttributeType Type,
    [property: Key(2)] string Value);
