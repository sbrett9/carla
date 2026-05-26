// Source: carla/road/element/LaneMarking.h + LaneMarking.cpp
//
// Data-only — the runtime conversion `LaneMarking(const RoadInfoMarkRecord&)`
// from C++ lives in Wave 3 (its constructor walks RoadInfoMarkRecord fields).
namespace CarlaNet.Map.Road;

public sealed class LaneMarking
{
    public enum MarkingType
    {
        Other,
        Broken,
        Solid,
        SolidSolid,
        SolidBroken,
        BrokenSolid,
        BrokenBroken,
        BottsDots,
        Grass,
        Curb,
        None,
    }

    public enum MarkingColor : byte
    {
        Standard = 0, // == White
        Blue     = 1,
        Green    = 2,
        Red      = 3,
        White    = Standard,
        Yellow   = 4,
        Other    = 5,
    }

    [System.Flags]
    public enum LaneChangeKind : byte
    {
        None  = 0x00,
        Right = 0x01,
        Left  = 0x02,
        Both  = 0x03,
    }

    public MarkingType Type { get; set; } = MarkingType.None;
    public MarkingColor Color { get; set; } = MarkingColor.Standard;
    public LaneChangeKind LaneChange { get; set; } = LaneChangeKind.None;
    public double Width { get; set; }

    public string GetColorInfoAsString() => Color switch
    {
        MarkingColor.Yellow => "yellow",
        MarkingColor.Standard => "white",
        _ => "white",
    };
}
