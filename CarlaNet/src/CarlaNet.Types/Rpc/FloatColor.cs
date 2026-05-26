// Source: carla/rpc/FloatColor.h — MSGPACK_DEFINE_ARRAY(r, g, b, a)
namespace CarlaNet.Types.Rpc;

[MessagePackObject]
public record struct FloatColor(
    [property: Key(0)] float R,
    [property: Key(1)] float G,
    [property: Key(2)] float B,
    [property: Key(3)] float A)
{
    [IgnoreMember] public float r => R;
    [IgnoreMember] public float g => G;
    [IgnoreMember] public float b => B;
    [IgnoreMember] public float a => A;
}
