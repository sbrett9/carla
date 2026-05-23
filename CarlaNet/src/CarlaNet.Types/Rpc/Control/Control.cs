// Sources: carla/rpc/VehicleControl.h, VehicleAckermannControl.h,
//          AckermannControllerSettings.h, WalkerControl.h,
//          BoneTransformDataIn.h, BoneTransformDataOut.h,
//          WalkerBoneControlIn.h, WalkerBoneControlOut.h
namespace CarlaNet.Types.Rpc.Control;

// Source: VehicleControl.h
// MSGPACK_DEFINE_ARRAY(throttle, steer, brake, hand_brake, reverse, manual_gear_shift, gear)
[MessagePackObject]
public record struct VehicleControl(
    [property: Key(0)] float Throttle,
    [property: Key(1)] float Steer,
    [property: Key(2)] float Brake,
    [property: Key(3)] bool HandBrake,
    [property: Key(4)] bool Reverse,
    [property: Key(5)] bool ManualGearShift,
    [property: Key(6)] int Gear);

// Source: VehicleAckermannControl.h
// MSGPACK_DEFINE_ARRAY(steer, steer_speed, speed, acceleration, jerk)
[MessagePackObject]
public record struct VehicleAckermannControl(
    [property: Key(0)] float Steer,
    [property: Key(1)] float SteerSpeed,
    [property: Key(2)] float Speed,
    [property: Key(3)] float Acceleration,
    [property: Key(4)] float Jerk);

// Source: AckermannControllerSettings.h
// MSGPACK_DEFINE_ARRAY(speed_kp, speed_ki, speed_kd, accel_kp, accel_ki, accel_kd)
[MessagePackObject]
public record struct AckermannControllerSettings(
    [property: Key(0)] float SpeedKp,
    [property: Key(1)] float SpeedKi,
    [property: Key(2)] float SpeedKd,
    [property: Key(3)] float AccelKp,
    [property: Key(4)] float AccelKi,
    [property: Key(5)] float AccelKd);

// Source: WalkerControl.h
// MSGPACK_DEFINE_ARRAY(direction, speed, jump)
[MessagePackObject]
public record struct WalkerControl(
    [property: Key(0)] Vector3D Direction,
    [property: Key(1)] float Speed,
    [property: Key(2)] bool Jump);

// Source: BoneTransformDataIn.h — std::pair<std::string, geom::Transform>
// Serialized as 2-element array [bone_name, transform]
[MessagePackObject]
public record struct BoneTransformDataIn(
    [property: Key(0)] string BoneName,
    [property: Key(1)] Transform Transform);

// Source: BoneTransformDataOut.h
// MSGPACK_DEFINE_ARRAY(bone_name, world, component, relative)
[MessagePackObject]
public record struct BoneTransformDataOut(
    [property: Key(0)] string BoneName,
    [property: Key(1)] Transform World,
    [property: Key(2)] Transform Component,
    [property: Key(3)] Transform Relative);

// Source: WalkerBoneControlIn.h — MSGPACK_DEFINE_ARRAY(bone_transforms)
[MessagePackObject]
public record struct WalkerBoneControlIn(
    [property: Key(0)] IReadOnlyList<BoneTransformDataIn> BoneTransforms);

// Source: WalkerBoneControlOut.h — MSGPACK_DEFINE_ARRAY(bone_transforms)
[MessagePackObject]
public record struct WalkerBoneControlOut(
    [property: Key(0)] IReadOnlyList<BoneTransformDataOut> BoneTransforms);
