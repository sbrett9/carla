// Source: carla/rpc/ObjectLabel.h
// CityObjectLabel / ObjectLabel — byte in sensor data
namespace CarlaNet.Types.Rpc.Enums;

public enum CityObjectLabel : byte
{
    None = 0, Roads = 1, Sidewalks = 2, Buildings = 3, Walls = 4, Fences = 5,
    Poles = 6, TrafficLight = 7, TrafficSigns = 8, Vegetation = 9, Terrain = 10,
    Sky = 11, Pedestrians = 12, Rider = 13, Car = 14, Truck = 15, Bus = 16,
    Train = 17, Motorcycle = 18, Bicycle = 19, Static = 20, Dynamic = 21,
    Other = 22, Water = 23, RoadLines = 24, Ground = 25, Bridge = 26,
    RailTrack = 27, GuardRail = 28, Rock = 29, Any = 0xFF
}
