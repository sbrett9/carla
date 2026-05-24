// Source: carla/rpc/MapLayer.h — using MapLayerType = uint16_t
namespace CarlaNet.Types.Rpc.Enums;

[Flags]
public enum MapLayer : ushort
{
    None = 0, Buildings = 0x1, Decals = 0x2, Foliage = 0x4, Ground = 0x8,
    ParkedVehicles = 0x10, Particles = 0x20, Props = 0x40, StreetLights = 0x80,
    Walls = 0x100, All = 0xFFFF
}
