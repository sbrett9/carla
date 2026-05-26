// Source: carla/road/MapData.h
//
// Bag of roads, junctions, signals, controllers, plus the georeference. Held by
// `Map` and treated as a writable surface for MapBuilder during construction;
// after Build() the public usage is read-only (we don't enforce this — the
// builder is friend-equivalent in C# via `internal`).
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.Road;

public sealed class MapData
{
    // -- georeference --------------------------------------------------------

    public GeoLocation GeoReference { get; internal set; }

    // -- collections (mutable until builder finishes) ------------------------

    public Dictionary<RoadId, Road> Roads { get; } = new();
    public Dictionary<JuncId, Junction> Junctions { get; } = new();

    /// <summary>Keyed by signal id (a string, per upstream).</summary>
    public Dictionary<SignId, Signal> Signals { get; } = new();

    /// <summary>Keyed by controller id (a string, per upstream).</summary>
    public Dictionary<ContId, Controller> Controllers { get; } = new();

    // -- accessors -----------------------------------------------------------

    public int RoadCount => Roads.Count;

    public bool ContainsRoad(RoadId id) => Roads.ContainsKey(id);

    public Road GetRoad(RoadId id) => Roads[id];

    public Junction? GetJunction(JuncId id) => Junctions.TryGetValue(id, out var j) ? j : null;
}
