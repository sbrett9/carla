// Source: carla/road/element/RoadInfoLaneVisibility.h
namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoLaneVisibility : RoadInfo
{
    public double Forward { get; }
    public double Back { get; }
    public double Left { get; }
    public double Right { get; }

    public RoadInfoLaneVisibility(double s, double forward, double back, double left, double right)
        : base(s)
    {
        Forward = forward;
        Back = back;
        Left = left;
        Right = right;
    }
}
