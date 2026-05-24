// Source: carla/rpc/ActorAttribute.h
// MSGPACK_DEFINE_ARRAY(id, type, value, recommended_values, is_modifiable, restrict_to_recommended)
namespace CarlaNet.Types.Rpc.Actors;

[MessagePackObject]
public record struct ActorAttribute(
    [property: Key(0)] string Id,
    [property: Key(1)] ActorAttributeType Type,
    [property: Key(2)] string Value,
    [property: Key(3)] IReadOnlyList<string> RecommendedValues,
    [property: Key(4)] bool IsModifiable,
    [property: Key(5)] bool RestrictToRecommended);
