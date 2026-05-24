// Source: carla/rpc/ActorDescription.h
// MSGPACK_DEFINE_ARRAY(uid, id, attributes)
namespace CarlaNet.Types.Rpc.Actors;

[MessagePackObject]
public record struct ActorDescription(
    [property: Key(0)] ActorId Uid,
    [property: Key(1)] string Id,
    [property: Key(2)] IReadOnlyList<ActorAttributeValue> Attributes);
