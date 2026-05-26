// Source: carla/rpc/VehiclePhysicsControl.h
// MSGPACK_DEFINE_ARRAY order verified from source — 30 fields
namespace CarlaNet.Types.Rpc.Physics;

[MessagePackObject]
public record struct VehiclePhysicsControl(
    [property: Key(0)]  IReadOnlyList<Vector2D> TorqueCurve,
    [property: Key(1)]  float MaxTorque,
    [property: Key(2)]  float MaxRpm,
    [property: Key(3)]  float IdleRpm,
    [property: Key(4)]  float BrakeEffect,
    [property: Key(5)]  float RevUpMoi,
    [property: Key(6)]  float RevDownRate,
    [property: Key(7)]  byte DifferentialType,
    [property: Key(8)]  float FrontRearSplit,
    [property: Key(9)]  bool UseAutomaticGears,
    [property: Key(10)] float GearChangeTime,
    [property: Key(11)] float FinalRatio,
    [property: Key(12)] IReadOnlyList<float> ForwardGearRatios,
    [property: Key(13)] IReadOnlyList<float> ReverseGearRatios,
    [property: Key(14)] float ChangeUpRpm,
    [property: Key(15)] float ChangeDownRpm,
    [property: Key(16)] float TransmissionEfficiency,
    [property: Key(17)] float Mass,
    [property: Key(18)] float DragCoefficient,
    [property: Key(19)] Location CenterOfMass,
    [property: Key(20)] float ChassisWidth,
    [property: Key(21)] float ChassisHeight,
    [property: Key(22)] float DownforceCoefficient,
    [property: Key(23)] float DragArea,
    [property: Key(24)] Vector3D InertiaTensorScale,
    [property: Key(25)] float SleepThreshold,
    [property: Key(26)] float SleepSlopeLimit,
    [property: Key(27)] IReadOnlyList<Vector2D> SteeringCurve,
    [property: Key(28)] IReadOnlyList<WheelPhysicsControl> Wheels,
    [property: Key(29)] bool UseSweepWheelCollision)
{
    [IgnoreMember] public IReadOnlyList<Vector2D> torque_curve => TorqueCurve;
    [IgnoreMember] public float max_torque => MaxTorque;
    [IgnoreMember] public float max_rpm => MaxRpm;
    [IgnoreMember] public float idle_rpm => IdleRpm;
    [IgnoreMember] public float brake_effect => BrakeEffect;
    [IgnoreMember] public float rev_up_moi => RevUpMoi;
    [IgnoreMember] public float rev_down_rate => RevDownRate;
    [IgnoreMember] public byte differential_type => DifferentialType;
    [IgnoreMember] public float front_rear_split => FrontRearSplit;
    [IgnoreMember] public bool use_automatic_gears => UseAutomaticGears;
    [IgnoreMember] public float gear_change_time => GearChangeTime;
    [IgnoreMember] public float final_ratio => FinalRatio;
    [IgnoreMember] public IReadOnlyList<float> forward_gear_ratios => ForwardGearRatios;
    [IgnoreMember] public IReadOnlyList<float> reverse_gear_ratios => ReverseGearRatios;
    [IgnoreMember] public float change_up_rpm => ChangeUpRpm;
    [IgnoreMember] public float change_down_rpm => ChangeDownRpm;
    [IgnoreMember] public float transmission_efficiency => TransmissionEfficiency;
    [IgnoreMember] public float mass => Mass;
    [IgnoreMember] public float drag_coefficient => DragCoefficient;
    [IgnoreMember] public Location center_of_mass => CenterOfMass;
    [IgnoreMember] public float chassis_width => ChassisWidth;
    [IgnoreMember] public float chassis_height => ChassisHeight;
    [IgnoreMember] public float downforce_coefficient => DownforceCoefficient;
    [IgnoreMember] public float drag_area => DragArea;
    [IgnoreMember] public Vector3D inertia_tensor_scale => InertiaTensorScale;
    [IgnoreMember] public float sleep_threshold => SleepThreshold;
    [IgnoreMember] public float sleep_slope_limit => SleepSlopeLimit;
    [IgnoreMember] public IReadOnlyList<Vector2D> steering_curve => SteeringCurve;
    [IgnoreMember] public IReadOnlyList<WheelPhysicsControl> wheels => Wheels;
    [IgnoreMember] public bool use_sweep_wheel_collision => UseSweepWheelCollision;
}
