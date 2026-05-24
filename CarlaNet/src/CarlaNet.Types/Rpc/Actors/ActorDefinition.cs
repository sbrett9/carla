// Source: carla/rpc/ActorDefinition.h
// MSGPACK_DEFINE_ARRAY(uid, id, tags, attributes)
namespace CarlaNet.Types.Rpc.Actors;

[MessagePackObject]
public record struct ActorDefinition(
    [property: Key(0)] ActorId Uid,
    [property: Key(1)] string Id,
    [property: Key(2)] string Tags,
    [property: Key(3)] IReadOnlyList<ActorAttribute> Attributes);
