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
    [property: Key(29)] bool UseSweepWheelCollision);
