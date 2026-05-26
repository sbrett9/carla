// Source: carla/road/element/RoadInfoLaneAccess.h
//
// Access restriction per lane (e.g. "Simulator", "Autonomous Traffic",
// "Pedestrian", "None"). Stored as a free-form string per upstream.
namespace CarlaNet.Map.Road.Element;

public sealed class RoadInfoLaneAccess : RoadInfo
{
    public string Restriction { get; }

    public RoadInfoLaneAccess(double s, string restriction)
        : base(s)
    {
        Restriction = restriction;
    }
}
