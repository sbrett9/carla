// Source: carla/rpc/EpisodeInfo.h — MSGPACK_DEFINE_ARRAY(id, token)
// token is carla::streaming::Token → serializes as [[bin24]] via RawToken
using CarlaNet.Types.Streaming;

namespace CarlaNet.Types.Rpc.Actors;

[MessagePackObject]
public record struct EpisodeInfo(
    [property: Key(0)] ulong Id,
    [property: Key(1)] RawToken Token);
