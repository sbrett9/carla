// Source: carla/road/element/RoadInfoMarkRecord.h
//
// Lane-edge road marking record. For left lanes this is the left border,
// for right lanes the right border. The center lane's mark defines the
// line separating left/right traffic.
namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoMarkRecord : RoadInfo
{
    /// <summary>Lane-change permission bitfield (can be ORed).</summary>
    [System.Flags]
    public enum LaneChangeKind : byte
    {
        None     = 0x00,
        Increase = 0x01, // toward larger lane-id
        Decrease = 0x02, // toward smaller lane-id
        Both     = 0x03,
    }

    public int RoadMarkId { get; }
    public string Type { get; }
    public string Weight { get; }
    public string Color { get; }
    public string Material { get; }
    public double Width { get; }
    public LaneChangeKind LaneChange { get; }
    public double Height { get; }
    public string TypeName { get; }
    public double TypeWidth { get; }
    public bool IsRht { get; }

    /// <summary>Sub-lines composing this mark (e.g. the two strokes of a double-solid).</summary>
    public List<RoadInfoMarkTypeLine> Lines { get; } = new();

    public RoadInfoMarkRecord(double s, int roadMarkId)
        : base(s)
    {
        RoadMarkId = roadMarkId;
        Type = string.Empty;
        Weight = string.Empty;
        Color = "white";
        Material = "standard";
        Width = 0.15;
        LaneChange = LaneChangeKind.None;
        Height = 0.0;
        TypeName = string.Empty;
        TypeWidth = 0.0;
        IsRht = true;
    }

    public RoadInfoMarkRecord(
        double s,
        int roadMarkId,
        string type,
        string weight,
        string color,
        string material,
        double width,
        LaneChangeKind laneChange,
        double height,
        string typeName,
        double typeWidth,
        bool isRht)
        : base(s)
    {
        RoadMarkId = roadMarkId;
        Type = type;
        Weight = weight;
        Color = color;
        Material = material;
        Width = width;
        LaneChange = laneChange;
        Height = height;
        TypeName = typeName;
        TypeWidth = typeWidth;
        IsRht = isRht;
    }
}
