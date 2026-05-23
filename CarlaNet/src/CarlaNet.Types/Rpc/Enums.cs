// Sources: carla/rpc/ActorAttributeType.h, ActorState.h, AttachmentType.h,
//          TrafficLightState.h, VehicleDoor.h, VehicleWheels.h,
//          VehicleFailureState.h, MapLayer.h, ObjectLabel.h, MaterialParameter.h,
//          LightState.h, VehicleLightState.h
// Underlying types verified against source headers (uint8_t vs int).
namespace CarlaNet.Types.Rpc.Enums;

// ActorAttributeType has no explicit underlying type in C++ (defaults to int)
public enum ActorAttributeType : uint
{ Bool, Int, Float, String, RGBColor, Vector, SIZE, INVALID }

// ActorState : uint8_t (carla/rpc/ActorState.h)
public enum ActorState : byte
{ Invalid, Active, Dormant, PendingKill }

// AttachmentType : uint8_t (carla/rpc/AttachmentType.h)
public enum AttachmentType : byte
{ Rigid, SpringArm, SpringArmGhost, SIZE, INVALID }

// TrafficLightState : uint8_t (carla/rpc/TrafficLightState.h)
public enum TrafficLightState : byte
{ Red, Yellow, Green, Off, Unknown, SIZE }

// VehicleDoor : uint8_t (carla/rpc/VehicleDoor.h)
public enum VehicleDoor : byte
{ FL = 0, FR = 1, RL = 2, RR = 3, Hood = 4, Trunk = 5, All = 6 }

// VehicleWheelLocation has no explicit underlying type in source
public enum VehicleWheelLocation : uint
{ FL = 0, FR = 1, BL = 2, BR = 3, FrontWheel = 0, BackWheel = 1 }

// VehicleFailureState : uint8_t (carla/rpc/VehicleFailureState.h)
public enum VehicleFailureState : byte
{ None, Rollover, Engine, TirePuncture }

// MapLayer : uint16_t (carla/rpc/MapLayer.h — using MapLayerType = uint16_t)
[Flags]
public enum MapLayer : ushort
{
    None = 0, Buildings = 0x1, Decals = 0x2, Foliage = 0x4, Ground = 0x8,
    ParkedVehicles = 0x10, Particles = 0x20, Props = 0x40, StreetLights = 0x80,
    Walls = 0x100, All = 0xFFFF
}

// CityObjectLabel / ObjectLabel — byte in sensor data
public enum CityObjectLabel : byte
{
    None = 0, Roads = 1, Sidewalks = 2, Buildings = 3, Walls = 4, Fences = 5,
    Poles = 6, TrafficLight = 7, TrafficSigns = 8, Vegetation = 9, Terrain = 10,
    Sky = 11, Pedestrians = 12, Rider = 13, Car = 14, Truck = 15, Bus = 16,
    Train = 17, Motorcycle = 18, Bicycle = 19, Static = 20, Dynamic = 21,
    Other = 22, Water = 23, RoadLines = 24, Ground = 25, Bridge = 26,
    RailTrack = 27, GuardRail = 28, Rock = 29, Any = 0xFF
}

// MaterialParameter has no explicit underlying type in C++ (defaults to int)
public enum MaterialParameter : uint
{ TexNormal, TexAoRoughnessMetallicEmissive, TexDiffuse, TexEmissive }

// LightGroup : uint8_t (carla/rpc/LightState.h)
public enum LightGroup : byte
{ None = 0, Vehicle = 1, Street = 2, Building = 3, Other = 4 }

// VehicleLightState.flag_type = uint32_t (carla/rpc/VehicleLightState.h)
[Flags]
public enum VehicleLightStateFlags : uint
{
    None = 0, Position = 0x1, LowBeam = 0x2, HighBeam = 0x4, Brake = 0x8,
    RightBlinker = 0x10, LeftBlinker = 0x20, Reverse = 0x40, Fog = 0x80,
    Interior = 0x100, Special1 = 0x200, Special2 = 0x400, All = 0xFFFFFFFF
}
