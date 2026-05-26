// Source: carla/road/element/Waypoint.h + Waypoint.cpp
//
// The canonical "place on the road" POD used by the entire road graph and the TM.
// Note `s` is truncated to half-cm precision in the hash, matching upstream
// std::hash<Waypoint>::operator(). See Waypoint.cpp (carla) for the bit layout.
namespace CarlaNet.Map.Road.Element;

public readonly record struct Waypoint
{
    public RoadId RoadId { get; init; }
    public SectionId SectionId { get; init; }
    public LaneId LaneId { get; init; }
    public double S { get; init; }

    public Waypoint(RoadId roadId, SectionId sectionId, LaneId laneId, double s)
    {
        RoadId = roadId;
        SectionId = sectionId;
        LaneId = laneId;
        S = s;
    }

    // Hash matches upstream's std::hash<Waypoint>(Waypoint.cpp): combines road, section,
    // lane, and s-quantized-to-half-cm (200·s as int32). Without it, records that
    // disagree only in floating-point noise of s would be treated as distinct.
    public override int GetHashCode()
    {
        unchecked
        {
            int sQuant = (int)System.Math.Floor(S * 200.0);
            int h = (int)RoadId;
            h = h * 31 + (int)SectionId;
            h = h * 31 + LaneId;
            h = h * 31 + sQuant;
            return h;
        }
    }

    public bool Equals(Waypoint other)
    {
        return RoadId == other.RoadId
            && SectionId == other.SectionId
            && LaneId == other.LaneId
            && (int)System.Math.Floor(S * 200.0) == (int)System.Math.Floor(other.S * 200.0);
    }
}
