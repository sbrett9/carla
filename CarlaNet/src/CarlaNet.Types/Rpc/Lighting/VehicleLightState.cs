// Source: carla/rpc/VehicleLightState.h
// VehicleLightState.flag_type = uint32_t
namespace CarlaNet.Types.Rpc.Lighting;

[Flags]
public enum VehicleLightStateFlags : uint
{
    None = 0, Position = 0x1, LowBeam = 0x2, HighBeam = 0x4, Brake = 0x8,
    RightBlinker = 0x10, LeftBlinker = 0x20, Reverse = 0x40, Fog = 0x80,
    Interior = 0x100, Special1 = 0x200, Special2 = 0x400, All = 0xFFFFFFFF
}
