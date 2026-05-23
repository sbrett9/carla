// Sources: carla/rpc/Actor.h, ActorDefinition.h, ActorDescription.h, ActorAttribute.h,
//          MapInfo.h, EpisodeInfo.h
using CarlaNet.Types.Rpc.Enums;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Types.Rpc.Actors;

// Source: carla/rpc/ActorAttribute.h
// MSGPACK_DEFINE_ARRAY(id, type, value, recommended_values, is_modifiable, restrict_to_recommended)
[MessagePackObject]
public record struct ActorAttribute(
    [property: Key(0)] string Id,
    [property: Key(1)] ActorAttributeType Type,
    [property: Key(2)] string Value,
    [property: Key(3)] IReadOnlyList<string> RecommendedValues,
    [property: Key(4)] bool IsModifiable,
    [property: Key(5)] bool RestrictToRecommended);

// Source: carla/rpc/ActorAttribute.h — ActorAttributeValue
// MSGPACK_DEFINE_ARRAY(id, type, value)
[MessagePackObject]
public record struct ActorAttributeValue(
    [property: Key(0)] string Id,
    [property: Key(1)] ActorAttributeType Type,
    [property: Key(2)] string Value);

// Source: carla/rpc/ActorDefinition.h
// MSGPACK_DEFINE_ARRAY(uid, id, tags, attributes)
[MessagePackObject]
public record struct ActorDefinition(
    [property: Key(0)] ActorId Uid,
    [property: Key(1)] string Id,
    [property: Key(2)] string Tags,
    [property: Key(3)] IReadOnlyList<ActorAttribute> Attributes);

// Source: carla/rpc/ActorDescription.h
// MSGPACK_DEFINE_ARRAY(uid, id, attributes)
[MessagePackObject]
public record struct ActorDescription(
    [property: Key(0)] ActorId Uid,
    [property: Key(1)] string Id,
    [property: Key(2)] IReadOnlyList<ActorAttributeValue> Attributes);

// Source: carla/rpc/Actor.h
// MSGPACK_DEFINE_ARRAY(id, parent_id, description, bounding_box, semantic_tags, stream_token)
// stream_token is carla::streaming::Token → serializes as [[bin24]] via RawToken
[MessagePackObject]
public record struct Actor(
    [property: Key(0)] ActorId Id,
    [property: Key(1)] ActorId ParentId,
    [property: Key(2)] ActorDescription Description,
    [property: Key(3)] BoundingBox BoundingBox,
    [property: Key(4)] IReadOnlyList<byte> SemanticTags,
    [property: Key(5)] RawToken StreamToken);

// Source: carla/rpc/MapInfo.h — MSGPACK_DEFINE_ARRAY(name, recommended_spawn_points)
[MessagePackObject]
public record struct MapInfo(
    [property: Key(0)] string Name,
    [property: Key(1)] IReadOnlyList<Transform> RecommendedSpawnPoints);

// Source: carla/rpc/EpisodeInfo.h — MSGPACK_DEFINE_ARRAY(id, token)
// token is carla::streaming::Token → serializes as [[bin24]] via RawToken
[MessagePackObject]
public record struct EpisodeInfo(
    [property: Key(0)] ulong Id,
    [property: Key(1)] RawToken Token);
