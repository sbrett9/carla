// Source: carla/road/element/RoadInfoMarkTypeLine.h
//
// A single dashed-line component of a RoadInfoMarkRecord (e.g. one of the two
// lines making up a "solid solid" centerline marking).
namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoMarkTypeLine : RoadInfo
{
    public int RoadMarkId { get; }
    public double Length { get; }
    public double Space { get; }
    public double TOffset { get; }
    public string Rule { get; }
    public double Width { get; }

    public RoadInfoMarkTypeLine(
        double s,
        int roadMarkId,
        double length,
        double space,
        double tOffset,
        string rule,
        double width)
        : base(s)
    {
        RoadMarkId = roadMarkId;
        Length = length;
        Space = space;
        TOffset = tOffset;
        Rule = rule;
        Width = width;
    }
}
