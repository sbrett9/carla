// Source: carla/rpc/WalkerControl.h
// MSGPACK_DEFINE_ARRAY(direction, speed, jump)
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct WalkerControl(
    [property: Key(0)] Vector3D Direction,
    [property: Key(1)] float Speed,
    [property: Key(2)] bool Jump)
{
    public WalkerControl() : this(default, 0f, false) {}
    [IgnoreMember] public Vector3D direction => Direction;
    [IgnoreMember] public float speed => Speed;
    [IgnoreMember] public bool jump => Jump;
}
