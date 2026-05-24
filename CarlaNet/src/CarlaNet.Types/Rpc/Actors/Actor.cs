// Source: carla/rpc/Actor.h
// MSGPACK_DEFINE_ARRAY(id, parent_id, description, bounding_box, semantic_tags, stream_token)
// semantic_tags : std::vector<uint8_t>  → raw binary blob (bin format)
// stream_token  : std::vector<unsigned char> → raw binary blob, 0 bytes for non-sensors,
//                 24 bytes for sensors.  Use StreamToken.Parse(actor.StreamToken, host).
namespace CarlaNet.Types.Rpc.Actors;

[MessagePackObject]
public record struct Actor(
    [property: Key(0)] ActorId Id,
    [property: Key(1)] ActorId ParentId,
    [property: Key(2)] ActorDescription Description,
    [property: Key(3)] BoundingBox BoundingBox,
    [property: Key(4)] byte[] SemanticTags,
    [property: Key(5)] byte[] StreamToken);
