// Source: carla/rpc/Color.h — MSGPACK_DEFINE_ARRAY(r, g, b)
// Distinct from sensor pixel Color {B,G,R,A} which is a raw binary layout, not msgpack.
namespace CarlaNet.Types.Rpc;

[MessagePackObject]
public record struct Color(
    [property: Key(0)] byte R,
    [property: Key(1)] byte G,
    [property: Key(2)] byte B);
