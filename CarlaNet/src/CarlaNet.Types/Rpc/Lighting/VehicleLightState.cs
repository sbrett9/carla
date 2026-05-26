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

// MSGPACK_DEFINE_ARRAY(light_state) — server expects light state wrapped in a
// 1-element msgpack array. Use this struct (not the raw enum) when sending the
// value to set_vehicle_light_state and reading get_vehicle_light_state.
[MessagePackObject]
public record struct VehicleLightState([property: Key(0)] uint LightState)
{
    public VehicleLightState() : this(0u) {}
    public VehicleLightState(VehicleLightStateFlags flags) : this((uint)flags) {}
    [IgnoreMember] public VehicleLightStateFlags Flags => (VehicleLightStateFlags)LightState;
    [IgnoreMember] public uint light_state => LightState;
}
