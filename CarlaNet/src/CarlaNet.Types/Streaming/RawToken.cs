// Source: carla/streaming/detail/Token.h
// Wire wrapper matching carla::streaming::Token's MSGPACK_DEFINE_ARRAY(data).
// Empirically confirmed: EpisodeInfo.token arrives as fixarray(1) containing bin8(24).
namespace CarlaNet.Types.Streaming;

[MessagePackObject]
public record struct RawToken(
    [property: Key(0)] byte[] Data)
{
    public static readonly RawToken Empty = new(Array.Empty<byte>());
}
