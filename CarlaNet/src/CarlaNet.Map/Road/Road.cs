// Source: carla/road/Road.h + Road.cpp (data members only)
//
// Behaviour (GetLaneByDistance, GetDirectedPointIn, GetNearestPoint, etc.) is
// Wave 3 — those methods need to walk the InformationSet and the Geometry,
// which involves topology traversal.
//
// The C++ upstream uses a `LaneSectionMap` (multimap<double, LaneSection>); in
// C# we just use a SortedDictionary<double, LaneSection> for the s→section
// lookup PLUS a Dictionary<SectionId, LaneSection> for id-based lookup. Wave 3
// can layer convenience query methods on top.
using CarlaNet.Map.Road.Element;

namespace CarlaNet.Map.Road;

public sealed class Road
{
    // -- identity / metadata (filled by MapBuilder from <road>) --------------

    public RoadId Id { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    public double Length { get; internal set; }

    /// <summary>True if this road is a junction connecting-road (junction != -1).</summary>
    public bool IsJunction { get; internal set; }

    /// <summary>Junction id this road belongs to, or -1 if a normal road.</summary>
    public JuncId JunctionId { get; internal set; } = -1;

    /// <summary>True if right-hand traffic (default per OpenDRIVE).</summary>
    public bool IsRightHandTraffic { get; internal set; } = true;

    public RoadId SuccessorRoadId { get; internal set; }
    public RoadId PredecessorRoadId { get; internal set; }

    // -- back-pointer to owning map data (filled by MapBuilder) --------------

    public MapData? MapData { get; internal set; }

    // -- lane sections -------------------------------------------------------

    /// <summary>Sections by start-distance "s". Duplicate `s` values are possible (multimap upstream).</summary>
    public List<LaneSection> LaneSections { get; } = new();

    /// <summary>Index for upstream's <c>LaneSectionMap::GetById</c>.</summary>
    public Dictionary<SectionId, LaneSection> LaneSectionsById { get; } = new();

    // -- info records (RoadInfoGeometry, RoadInfoElevation, RoadInfoLaneOffset,
    //                  RoadInfoSpeed, RoadInfoSignal, RoadInfoCrosswalk, ...) -

    public RoadElementSet<RoadInfo> Info { get; internal set; } = new();

    // -- resolved adjacency (filled by MapBuilder after parsing) -------------

    public List<Road> Nexts { get; } = new();
    public List<Road> Prevs { get; } = new();

    public Road() { }
}
