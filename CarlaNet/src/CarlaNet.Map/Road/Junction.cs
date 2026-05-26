// Source: carla/road/Junction.h
//
// An intersection. Holds connections (incoming road → connecting road, plus
// lane-id mappings) and the set of TL controllers that govern it. The
// road-conflict map (per-incoming → set of conflicting incomings) is computed
// by `Map::ComputeJunctionConflicts` in Wave 3; we expose the field for fill.
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.Road;

public sealed class Junction
{
    public readonly record struct LaneLink(LaneId From, LaneId To);

    public sealed class Connection
    {
        public ConId Id { get; }
        public RoadId IncomingRoad { get; }
        public RoadId ConnectingRoad { get; }
        public List<LaneLink> LaneLinks { get; } = new();

        public Connection(ConId id, RoadId incomingRoad, RoadId connectingRoad)
        {
            Id = id;
            IncomingRoad = incomingRoad;
            ConnectingRoad = connectingRoad;
        }

        public void AddLaneLink(LaneId from, LaneId to) => LaneLinks.Add(new LaneLink(from, to));
    }

    public JuncId Id { get; }
    public string Name { get; }

    public Dictionary<ConId, Connection> Connections { get; } = new();

    /// <summary>Controllers (TL groups) associated with this junction. Filled by MapBuilder.</summary>
    public SortedSet<ContId> Controllers { get; } = new();

    /// <summary>
    /// Per-road conflict map (incoming road → set of roads that conflict on this junction).
    /// Computed by Wave 3 (<c>Map.ComputeJunctionConflicts</c>); empty until then.
    /// </summary>
    public Dictionary<RoadId, HashSet<RoadId>> RoadConflicts { get; } = new();

    // Wave 3G: BoundingBox is set by InMemoryMap.SetUp() (in the
    // CarlaNet.TrafficManager assembly), so the setter has to cross the
    // assembly boundary. `set` (public) is the simplest fix — there are no
    // mutating callers other than the TM build-time setup.
    public BoundingBox BoundingBox { get; set; }

    public Junction(JuncId id, string name)
    {
        Id = id;
        Name = name;
    }

    public Connection? GetConnection(ConId id) => Connections.TryGetValue(id, out var c) ? c : null;

    public bool RoadHasConflicts(RoadId roadId) => RoadConflicts.ContainsKey(roadId);

    public IReadOnlySet<RoadId> GetConflictsOfRoad(RoadId roadId) => RoadConflicts[roadId];
}
