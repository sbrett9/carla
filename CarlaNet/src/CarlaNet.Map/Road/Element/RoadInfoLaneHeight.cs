// Source: carla/road/element/RoadInfoLaneHeight.h
//
// Inner/outer vertical offset of a lane from the road reference plane
// (e.g. sidewalks sit a few cm above road level).
namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoLaneHeight : RoadInfo
{
    public double Inner { get; }
    public double Outer { get; }

    public RoadInfoLaneHeight(double s, double inner, double outer)
        : base(s)
    {
        Inner = inner;
        Outer = outer;
    }
}
