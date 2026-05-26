// Source: carla/road/element/RoadInfoLaneRule.h
//
// Free-form rule strings. Recommended upstream values:
// "No Stopping At Any Time", "Disabled Parking", "Car Pool".
namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoLaneRule : RoadInfo
{
    public string Value { get; }

    public RoadInfoLaneRule(double s, string value)
        : base(s)
    {
        Value = value;
    }
}
