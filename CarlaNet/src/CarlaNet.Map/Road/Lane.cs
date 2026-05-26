// Source: carla/road/Lane.h + Lane.cpp (data members only)
//
// Lane behaviour (GetWidth/IsStraight/ComputeTransform/GetCornerPositions) lives
// in Wave 3 — it requires walking the InformationSet and the Road's geometry,
// which is topology, not schema. Here we expose the raw fields.
using System;
using System.Linq;
using CarlaNet.Map.Road.Element;

namespace CarlaNet.Map.Road;

/// <summary>
/// Lane types from OpenDRIVE. Defined as flags so unions / "any drivable" masks
/// can be expressed via bitwise OR (upstream's `LaneType::Any = 0xFFFFFFFE`).
/// </summary>
[Flags]
public enum LaneType : int
{
    None          = 0x1,
    Driving       = 0x1 << 1,
    Stop          = 0x1 << 2,
    Shoulder      = 0x1 << 3,
    Biking        = 0x1 << 4,
    Sidewalk      = 0x1 << 5,
    Border        = 0x1 << 6,
    Restricted    = 0x1 << 7,
    Parking       = 0x1 << 8,
    Bidirectional = 0x1 << 9,
    Median        = 0x1 << 10,
    Special1      = 0x1 << 11,
    Special2      = 0x1 << 12,
    Special3      = 0x1 << 13,
    RoadWorks     = 0x1 << 14,
    Tram          = 0x1 << 15,
    Rail          = 0x1 << 16,
    Entry         = 0x1 << 17,
    Exit          = 0x1 << 18,
    OffRamp       = 0x1 << 19,
    OnRamp        = 0x1 << 20,
    /// <summary>Sentinel "match any lane type" mask (0xFFFFFFFE).</summary>
    Any           = unchecked((int)0xFFFFFFFE),
}

public sealed class Lane
{
    // -- topology back-references (filled by MapBuilder) ----------------------

    /// <summary>Owning section. Set by builder.</summary>
    public LaneSection? Section { get; internal set; }

    public LaneId Id { get; internal set; }

    public LaneType Type { get; internal set; } = LaneType.None;

    /// <summary>If true the lane is offset from the surrounding plane (e.g. sidewalk).</summary>
    public bool Level { get; internal set; }

    public LaneId Successor { get; internal set; }
    public LaneId Predecessor { get; internal set; }

    /// <summary>Resolved next-lane pointers (cross-section continuation through the road graph). Wave 3.</summary>
    public List<Lane> NextLanes { get; } = new();

    /// <summary>Resolved previous-lane pointers. Wave 3.</summary>
    public List<Lane> PreviousLanes { get; } = new();

    /// <summary>
    /// Polymorphic per-lane info records (LaneWidth, LaneBorder, LaneAccess, LaneHeight,
    /// LaneMaterial, LaneRule, LaneVisibility, MarkRecord, Signal references).
    /// Sorted by `s` for binary search.
    /// </summary>
    public RoadElementSet<RoadInfo> Info { get; private set; }

    public Lane()
    {
        Info = new RoadElementSet<RoadInfo>();
    }

    public Lane(LaneSection laneSection, LaneId id, IEnumerable<RoadInfo> info)
    {
        Section = laneSection;
        Id = id;
        Info = new RoadElementSet<RoadInfo>(info);
    }

    /// <summary>Used by Wave 2's MapBuilder to install the final per-lane info set after parsing.</summary>
    internal void SetInfo(RoadElementSet<RoadInfo> info) => Info = info;

    // -- convenience filters (cheap projections, not topology) ---------------

    /// <summary>First RoadInfo of type T active at `s`, or null.</summary>
    public T? GetInfoAt<T>(double s) where T : RoadInfo
        => Info.GetReverseSubset(s).OfType<T>().FirstOrDefault();

    public IEnumerable<T> GetInfos<T>() where T : RoadInfo
        => Info.All.OfType<T>();

    /// <summary>
    /// Lanes with id &gt; 0 are to the left of the reference line and have direction
    /// opposite to road s; lanes with id &lt; 0 are to the right and follow s.
    /// </summary>
    public bool IsPositiveDirection => Id <= 0;
}
