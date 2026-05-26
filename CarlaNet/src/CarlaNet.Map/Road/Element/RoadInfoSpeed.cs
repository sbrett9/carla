// Source: carla/road/element/RoadInfoSpeed.h
namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoSpeed : RoadInfo
{
    public double Speed { get; }
    public string Type { get; }

    public RoadInfoSpeed(double s, double speed)
        : base(s)
    {
        Speed = speed;
        Type = "Town";
    }

    public RoadInfoSpeed(double s, double speed, string type)
        : base(s)
    {
        Speed = speed;
        Type = type;
    }
}
