// Source: carla/road/LaneSection.h
//
// A cross-section of a road at a given `s`. Holds lanes keyed by signed id
// (negative=right of center, positive=left, 0=reference line) and a cubic
// polynomial describing how the lane reference line shifts laterally.
using CarlaNet.Map.Geom;

namespace CarlaNet.Map.Road;

public sealed class LaneSection
{
    /// <summary>Stable section id assigned by MapBuilder.</summary>
    public SectionId Id { get; }

    /// <summary>Distance from the start of the road where this section begins.</summary>
    public double S { get; }

    /// <summary>Owning road; back-pointer filled by MapBuilder.</summary>
    public Road? Road { get; internal set; }

    /// <summary>
    /// Lanes keyed by lane id. SortedDictionary so iteration from largest negative id
    /// (rightmost) up to largest positive id (leftmost) matches upstream's
    /// <c>std::map&lt;LaneId, Lane&gt;</c>.
    /// </summary>
    public SortedDictionary<LaneId, Lane> Lanes { get; } = new();

    /// <summary>Lateral offset polynomial applied to the lane reference line.</summary>
    public CubicPolynomial LaneOffset { get; internal set; }

    public LaneSection(SectionId id, double s)
    {
        Id = id;
        S = s;
        LaneOffset = new CubicPolynomial();
    }

    /// <summary>Convenience getter; returns null if no lane with this id exists.</summary>
    public Lane? GetLane(LaneId id) => Lanes.TryGetValue(id, out var l) ? l : null;

    public bool ContainsLane(LaneId id) => Lanes.ContainsKey(id);
}
