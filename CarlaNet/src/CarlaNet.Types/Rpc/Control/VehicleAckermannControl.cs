// Source: carla/rpc/VehicleAckermannControl.h
// MSGPACK_DEFINE_ARRAY(steer, steer_speed, speed, acceleration, jerk)
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct VehicleAckermannControl(
    [property: Key(0)] float Steer,
    [property: Key(1)] float SteerSpeed,
    [property: Key(2)] float Speed,
    [property: Key(3)] float Acceleration,
    [property: Key(4)] float Jerk);
