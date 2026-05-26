// Source: carla/rpc/VehicleAckermannControl.h
// MSGPACK_DEFINE_ARRAY(steer, steer_speed, speed, acceleration, jerk)
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct VehicleAckermannControl(
    [property: Key(0)] float Steer,
    [property: Key(1)] float SteerSpeed,
    [property: Key(2)] float Speed,
    [property: Key(3)] float Acceleration,
    [property: Key(4)] float Jerk)
{
    public VehicleAckermannControl() : this(0f, 0f, 0f, 0f, 0f) {}
    [IgnoreMember] public float steer => Steer;
    [IgnoreMember] public float steer_speed => SteerSpeed;
    [IgnoreMember] public float speed => Speed;
    [IgnoreMember] public float acceleration => Acceleration;
    [IgnoreMember] public float jerk => Jerk;
}
