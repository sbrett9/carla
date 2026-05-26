// Source: carla/rpc/AckermannControllerSettings.h
// MSGPACK_DEFINE_ARRAY(speed_kp, speed_ki, speed_kd, accel_kp, accel_ki, accel_kd)
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct AckermannControllerSettings(
    [property: Key(0)] float SpeedKp,
    [property: Key(1)] float SpeedKi,
    [property: Key(2)] float SpeedKd,
    [property: Key(3)] float AccelKp,
    [property: Key(4)] float AccelKi,
    [property: Key(5)] float AccelKd)
{
    public AckermannControllerSettings() : this(0f, 0f, 0f, 0f, 0f, 0f) {}
    [IgnoreMember] public float speed_kp => SpeedKp;
    [IgnoreMember] public float speed_ki => SpeedKi;
    [IgnoreMember] public float speed_kd => SpeedKd;
    [IgnoreMember] public float accel_kp => AccelKp;
    [IgnoreMember] public float accel_ki => AccelKi;
    [IgnoreMember] public float accel_kd => AccelKd;
}
