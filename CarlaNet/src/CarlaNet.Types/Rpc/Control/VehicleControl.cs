// Source: carla/rpc/VehicleControl.h
// MSGPACK_DEFINE_ARRAY(throttle, steer, brake, hand_brake, reverse, manual_gear_shift, gear)
namespace CarlaNet.Types.Rpc.Control;

[MessagePackObject]
public record struct VehicleControl(
    [property: Key(0)] float Throttle,
    [property: Key(1)] float Steer,
    [property: Key(2)] float Brake,
    [property: Key(3)] bool HandBrake,
    [property: Key(4)] bool Reverse,
    [property: Key(5)] bool ManualGearShift,
    [property: Key(6)] int Gear);
