// Source: carla/road/element/RoadInfo.h
//
// Abstract base for the polymorphic "info record" type. Each subclass is a tiny
// data carrier tagged by C# type — Wave 3 will switch over `RoadInfo` instances
// instead of using the C++ visitor pattern.
namespace CarlaNet.Map.Road.Element;

public abstract class RoadInfo
{
    /// <summary>Distance from the road's start location along the reference line.</summary>
    public double Distance { get; }

    protected RoadInfo(double distance = 0.0)
    {
        Distance = distance;
    }
}
