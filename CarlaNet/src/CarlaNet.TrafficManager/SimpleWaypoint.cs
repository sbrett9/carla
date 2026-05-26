// Source: carla/trafficmanager/SimpleWaypoint.h + SimpleWaypoint.cpp
//
// Dense-graph waypoint node used by TM stages. Wraps a Wave-1B
// `CarlaNet.Map.Road.Element.Waypoint` POD (RoadId/SectionId/LaneId/S) and
// adds:
//   - a stable hash <see cref="Id"/> (uint64) used as the key in
//     <see cref="TrackTraffic"/>'s inverted indexes
//   - a geodesic-grid id assigned by <c>InMemoryMap.SetUp</c> (Wave 3)
//   - cached world-space <see cref="Location"/> populated externally during
//     topology construction (option (c) in the Wave-2E spec)
//   - graph linkage (next / previous / left / right) populated by
//     <c>InMemoryMap.SetUp</c>
//
// Design decision: SimpleWaypoint owns NO reference back to the Map. The
// fields with `internal set` are the slots that Wave 3's InMemoryMap.SetUp
// populates. This lets the TM hot-path read every field directly, with no
// indirection or per-call road-graph walk.
//
// Constructor accepts a `Waypoint` POD plus a precomputed Location. Wave 3
// will compute that Location once during topology construction (by calling
// the Map's road-graph evaluator) and pass it in.
#nullable enable

using CarlaNet.Map.Road.Element;

namespace CarlaNet.TrafficManager;

/// <summary>
/// High-level decision attached to a waypoint sequence (intersection turn).
/// Mirrors <c>traffic_manager::RoadOption</c> from SimpleWaypoint.h:25.
/// Also exposed publicly via the user-facing TM facade for
/// <c>SetImportedRoute</c> / <c>GetNextAction</c>.
/// </summary>
public enum RoadOption : byte
{
    Void = 0,
    Left = 1,
    Right = 2,
    Straight = 3,
    LaneFollow = 4,
    ChangeLaneLeft = 5,
    ChangeLaneRight = 6,
    RoadEnd = 7,
}

/// <summary>
/// Dense-graph waypoint node. One instance per discrete sample on the road
/// network (every <see cref="Constants.Map.MAP_RESOLUTION"/> metres along
/// each lane centreline).
/// </summary>
/// <remarks>
/// Owned by <c>InMemoryMap</c> (Wave 3). Identity is the hash of
/// (road, section, lane, s-rounded-to-half-cm), matching upstream's
/// <c>std::hash&lt;Waypoint&gt;</c>. Equality on the wrapped
/// <see cref="Waypoint"/> POD already enforces that quantization.
/// </remarks>
internal sealed class SimpleWaypoint
{
    // ── Identity (immutable after construction) ────────────────────────────

    /// <summary>The wrapped road-graph waypoint POD.</summary>
    public Waypoint Waypoint { get; }

    /// <summary>
    /// Unique id (hashed from road/section/lane/s). Used as the key in
    /// <see cref="TrackTraffic"/>'s waypoint-occupancy maps. Matches the
    /// upstream <c>cc::Waypoint::GetId()</c> output bit-for-bit (uint64).
    /// </summary>
    public ulong Id { get; }

    // ── Externally-populated state (Wave 3 InMemoryMap.SetUp writes these) ─

    /// <summary>
    /// World-space location at this waypoint. Pre-computed at construction
    /// time (option (c) in the Wave-2E spec); the setter is exposed so
    /// InMemoryMap.SetUp can refresh it if needed.
    /// </summary>
    public Location Location { get; internal set; }

    /// <summary>
    /// Forward-direction unit vector at this waypoint (lane heading). Cached
    /// alongside Location to keep the hot-path math allocation-free.
    /// </summary>
    public Vector3D ForwardVector { get; internal set; }

    /// <summary>
    /// Grid id assigned by <c>InMemoryMap.SetUp</c>; in C++ this is a
    /// <c>JuncId</c> (int32). Default 0 = "no grid".
    /// </summary>
    public GeoGridId GeodesicGridId { get; internal set; }

    /// <summary>
    /// Junction id of the underlying road (-1 if not in a junction). Set by
    /// InMemoryMap.SetUp from <c>Map.GetJunctionId(road_id)</c>.
    /// </summary>
    public GeoGridId JunctionId { get; internal set; } = -1;

    /// <summary>
    /// True if the waypoint lies inside a junction. Mirrors upstream's
    /// <c>_is_junction</c> field — set by Wave 3 from
    /// <c>Map.IsJunction(road_id)</c>.
    /// </summary>
    public bool IsJunction { get; internal set; }

    /// <summary>High-level <see cref="RoadOption"/> attached at this waypoint.</summary>
    public RoadOption RoadOption { get; internal set; } = RoadOption.Void;

    // ── Graph back-references (Wave 3 wires these up) ──────────────────────

    /// <summary>Successor waypoints (next-along-the-lane-graph).</summary>
    public List<SimpleWaypoint> NextWaypoints { get; } = new();

    /// <summary>Predecessor waypoints.</summary>
    public List<SimpleWaypoint> PreviousWaypoints { get; } = new();

    /// <summary>Lane-change neighbour to the left (null if not changeable).</summary>
    public SimpleWaypoint? LeftWaypoint { get; internal set; }

    /// <summary>Lane-change neighbour to the right (null if not changeable).</summary>
    public SimpleWaypoint? RightWaypoint { get; internal set; }

    // ── Construction ───────────────────────────────────────────────────────

    /// <summary>
    /// Constructs a SimpleWaypoint from a road-graph <see cref="Waypoint"/>
    /// POD. <paramref name="location"/> and <paramref name="forwardVector"/>
    /// are pre-computed by Wave 3's InMemoryMap.SetUp. The <see cref="Id"/>
    /// is derived from the Waypoint's hash so it matches upstream.
    /// </summary>
    public SimpleWaypoint(Waypoint waypoint, Location location, Vector3D forwardVector)
    {
        Waypoint = waypoint;
        Location = location;
        ForwardVector = forwardVector;
        // Reinterpret the 32-bit Waypoint hash as uint64 so the id matches
        // upstream's `cc::Waypoint::GetId()` width. Equality uses the same
        // half-cm quantization, so collisions track upstream behaviour.
        Id = unchecked((ulong)(uint)waypoint.GetHashCode());
    }

    /// <summary>
    /// Convenience overload for tests / placeholder construction that lets
    /// callers create a SimpleWaypoint with just identity. Location and
    /// ForwardVector default to zero — InMemoryMap.SetUp is expected to
    /// fill them in before the stages run.
    /// </summary>
    public SimpleWaypoint(Waypoint waypoint)
        : this(waypoint, new Location(0f, 0f, 0f), new Vector3D(0f, 0f, 0f))
    {
    }

    // ── Accessors (mirror upstream C++ API) ────────────────────────────────

    public ulong GetId() => Id;
    public Location GetLocation() => Location;
    public Vector3D GetForwardVector() => ForwardVector;
    public Waypoint GetWaypoint() => Waypoint;

    public IReadOnlyList<SimpleWaypoint> GetNextWaypoint() => NextWaypoints;
    public IReadOnlyList<SimpleWaypoint> GetPreviousWaypoint() => PreviousWaypoints;
    public SimpleWaypoint? GetLeftWaypoint() => LeftWaypoint;
    public SimpleWaypoint? GetRightWaypoint() => RightWaypoint;

    /// <summary>
    /// Append the supplied waypoints to <see cref="NextWaypoints"/>. Returns
    /// the new count (matches the upstream <c>uint64_t SetNextWaypoint</c>
    /// signature).
    /// </summary>
    public ulong SetNextWaypoint(IReadOnlyList<SimpleWaypoint> waypoints)
    {
        NextWaypoints.AddRange(waypoints);
        return (ulong)waypoints.Count;
    }

    /// <inheritdoc cref="SetNextWaypoint" />
    public ulong SetPreviousWaypoint(IReadOnlyList<SimpleWaypoint> waypoints)
    {
        PreviousWaypoints.AddRange(waypoints);
        return (ulong)waypoints.Count;
    }

    /// <summary>
    /// Set the lane-change-left neighbour iff <paramref name="candidate"/>
    /// lies to the geometric left of this waypoint (computed by the
    /// 2D cross product of forward × (this - candidate)). Matches
    /// <c>SimpleWaypoint::SetLeftWaypoint</c> in SimpleWaypoint.cpp:69–76.
    /// </summary>
    public void SetLeftWaypoint(SimpleWaypoint candidate)
    {
        Vector3D heading = ForwardVector;
        Vector3D relative = new(
            Location.X - candidate.Location.X,
            Location.Y - candidate.Location.Y,
            Location.Z - candidate.Location.Z);
        // 2D cross (z-component) positive ⇒ candidate is to the left.
        float crossZ = heading.X * relative.Y - heading.Y * relative.X;
        if (crossZ > 0f)
            LeftWaypoint = candidate;
    }

    /// <summary>
    /// Set the lane-change-right neighbour. Mirror of
    /// <see cref="SetLeftWaypoint"/>; the sign of the cross product flips.
    /// </summary>
    public void SetRightWaypoint(SimpleWaypoint candidate)
    {
        Vector3D heading = ForwardVector;
        Vector3D relative = new(
            Location.X - candidate.Location.X,
            Location.Y - candidate.Location.Y,
            Location.Z - candidate.Location.Z);
        float crossZ = heading.X * relative.Y - heading.Y * relative.X;
        if (crossZ < 0f)
            RightWaypoint = candidate;
    }

    public void SetGeodesicGridId(GeoGridId id) => GeodesicGridId = id;

    /// <summary>
    /// Returns the geodesic-grid id. Inside a junction this is the junction
    /// id (so every waypoint in the same junction shares a grid); otherwise
    /// it is the assigned <see cref="GeodesicGridId"/>. Mirrors
    /// <c>SimpleWaypoint::GetGeodesicGridId()</c> in SimpleWaypoint.cpp:119.
    /// </summary>
    public GeoGridId GetGeodesicGridId()
    {
        return IsJunction ? JunctionId : GeodesicGridId;
    }

    /// <summary>Returns the junction id of the underlying road (-1 if none).</summary>
    public GeoGridId GetJunctionId() => JunctionId;

    public bool CheckJunction() => IsJunction;
    public void SetIsJunction(bool value) => IsJunction = value;

    /// <summary>
    /// Returns true if this waypoint has multiple successors — used as a
    /// non-OpenDRIVE-dependent "is this an intersection" check. Matches
    /// upstream <c>CheckIntersection</c> exactly (SimpleWaypoint.cpp:111).
    /// </summary>
    public bool CheckIntersection() => NextWaypoints.Count > 1;

    public void SetRoadOption(RoadOption value) => RoadOption = value;
    public RoadOption GetRoadOption() => RoadOption;

    // ── Distance helpers ───────────────────────────────────────────────────

    /// <summary>Euclidean distance from this waypoint's location to <paramref name="location"/>.</summary>
    public float Distance(Location location)
    {
        float dx = Location.X - location.X;
        float dy = Location.Y - location.Y;
        float dz = Location.Z - location.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Euclidean distance between two SimpleWaypoints.</summary>
    public float Distance(SimpleWaypoint other) => Distance(other.Location);

    /// <summary>Squared Euclidean distance — preferred in the collision broad-phase.</summary>
    public float DistanceSquared(Location location)
    {
        float dx = Location.X - location.X;
        float dy = Location.Y - location.Y;
        float dz = Location.Z - location.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <inheritdoc cref="DistanceSquared(Location)" />
    public float DistanceSquared(SimpleWaypoint other) => DistanceSquared(other.Location);
}
