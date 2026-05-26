// Source: carla/trafficmanager/InMemoryMap.{h,cpp}
//
// THE central data structure that the TM stages query for road-graph lookups.
// Holds the densely-interpolated waypoint graph (one SimpleWaypoint per
// MAP_RESOLUTION metres along every driving lane), the lane-change / next /
// previous links, the geodesic-grid bucketing, and a uniform-grid spatial
// index (replaces upstream's boost::geometry rtree — see design note below).
//
// SetUp() is called ONCE by the TM facade after construction. After that the
// graph is immutable and every read is concurrent-safe with no locks; the
// stages query it on the hot path each tick per vehicle.
//
// Wave 3G design choices:
//
//   • Spatial index: simple uniform grid bucket keyed on (int(x/CELL),
//     int(y/CELL)) with CELL = 5 m. GetWaypoint(loc) spirals outward in
//     CELL_SIZE rings until a non-empty cell is found, then scans one extra
//     ring for diagonal cases. O(1) average, no kd-tree dependency. Sized
//     for CARLA towns (~1 km × 1 km, ~50k waypoints).
//
//   • Junction post-passes (CreateJunctionBoundingBoxes,
//     ComputeJunctionRoadConflicts, ComputeSignalTransform) live here too —
//     they all need ComputeTransform / dense topology which doesn't exist
//     until SetUp() runs.
//
//   • The C++ implementation has a binary-cache Load/Save path (cooked .bin
//     per-map). Skipped in v1 per TRAFFIC_MANAGER_PORT_SPEC.md §6 risk-7.
#nullable enable

using CarlaNet.Map.Geom;
using CarlaNet.Map.Road;
using CarlaNet.Map.Road.Element;
using CarlaNet.Types.Geom;

namespace CarlaNet.TrafficManager;

/// <summary>
/// Dense-graph road map cache. Build once via <see cref="SetUp"/>; afterward
/// the graph is read-only and safe to query concurrently.
/// </summary>
internal sealed class InMemoryMap
{
    private readonly CarlaNet.Map.Road.Map _worldMap;
    private readonly List<SimpleWaypoint> _denseTopology = new();
    private readonly Dictionary<GeoGridId, Dictionary<ulong, SimpleWaypoint>> _byGrid = new();

    // Uniform spatial grid. CELL_SIZE is in metres. Each cell holds the
    // SimpleWaypoints whose .Location falls in that cell. Sparse: only cells
    // with members are stored in the dictionary.
    private const float CELL_SIZE = 5.0f;
    private const float INV_CELL_SIZE = 1.0f / CELL_SIZE;
    private readonly Dictionary<(int CX, int CY), List<SimpleWaypoint>> _grid = new();

    private static readonly Dictionary<ulong, SimpleWaypoint> _emptyGridDict = new();

    private bool _isBuilt;

    public InMemoryMap(CarlaNet.Map.Road.Map worldMap)
    {
        _worldMap = worldMap ?? throw new ArgumentNullException(nameof(worldMap));
    }

    // ── Public API surface ─────────────────────────────────────────────────

    /// <summary>The underlying parsed map. Stages occasionally need it.</summary>
    public CarlaNet.Map.Road.Map WorldMap => _worldMap;

    /// <summary>Closest dense-topology waypoint to <paramref name="location"/>.</summary>
    public SimpleWaypoint GetWaypoint(Location location)
    {
        var result = FindClosestInGrid(location);
        if (result == null) throw new InvalidOperationException("dense topology is empty; did SetUp() run?");
        return result;
    }

    /// <summary>The full discretized waypoint graph.</summary>
    public IReadOnlyList<SimpleWaypoint> GetDenseTopology() => _denseTopology;

    /// <summary>
    /// The map name (e.g. "Carla/Maps/Town03"); used for one
    /// Town03-roundabout special-case in LocalizationStage.
    /// </summary>
    public string GetMapName()
    {
        // The OpenDriveParser doesn't currently surface the source filename on
        // the Map object; return empty until that wiring exists. The Town03
        // exception in LocalizationStage already tolerates a name mismatch.
        return string.Empty;
    }

    /// <summary>
    /// True iff <paramref name="waypoint"/> has a populated
    /// <see cref="SimpleWaypoint.LeftWaypoint"/> or
    /// <see cref="SimpleWaypoint.RightWaypoint"/>.
    /// </summary>
    public bool IsLaneChangePossible(SimpleWaypoint waypoint)
        => waypoint.LeftWaypoint != null || waypoint.RightWaypoint != null;

    /// <summary>
    /// Waypoints (keyed by id) that share <paramref name="gridId"/>. Empty
    /// dictionary if the grid id is unknown.
    /// </summary>
    public IReadOnlyDictionary<ulong, SimpleWaypoint> GetWaypointsByGridId(GeoGridId gridId)
        => _byGrid.TryGetValue(gridId, out var d) ? d : _emptyGridDict;

    /// <summary>
    /// Sample up to <paramref name="nPoints"/> non-junction waypoints in an
    /// annular box around <paramref name="location"/>: outside an inner half-
    /// side of <paramref name="randomSample"/> and inside the outer half-side
    /// of <c>randomSample + DELTA</c>. Mirrors upstream
    /// <c>InMemoryMap::GetWaypointsInDelta</c>.
    /// </summary>
    public IReadOnlyList<SimpleWaypoint> GetWaypointsInDelta(
        Location location, ushort nPoints, float randomSample)
    {
        var result = new List<SimpleWaypoint>(nPoints);
        float lowerHalfXY = randomSample;                              // inner box half-side (xy)
        float upperHalfXY = randomSample + Constants.Map.DELTA;        // outer box half-side (xy)
        float halfZ = Constants.Map.Z_DELTA;

        int cxMin = (int)MathF.Floor((location.X - upperHalfXY) * INV_CELL_SIZE);
        int cxMax = (int)MathF.Floor((location.X + upperHalfXY) * INV_CELL_SIZE);
        int cyMin = (int)MathF.Floor((location.Y - upperHalfXY) * INV_CELL_SIZE);
        int cyMax = (int)MathF.Floor((location.Y + upperHalfXY) * INV_CELL_SIZE);

        for (int cx = cxMin; cx <= cxMax; cx++)
        {
            for (int cy = cyMin; cy <= cyMax; cy++)
            {
                if (!_grid.TryGetValue((cx, cy), out var bucket)) continue;
                foreach (var w in bucket)
                {
                    if (w.IsJunction) continue;
                    var p = w.Location;
                    float dx = p.X - location.X;
                    float dy = p.Y - location.Y;
                    float dz = p.Z - location.Z;
                    if (MathF.Abs(dz) > halfZ) continue;
                    bool inOuter = MathF.Abs(dx) <= upperHalfXY && MathF.Abs(dy) <= upperHalfXY;
                    bool inInner = MathF.Abs(dx) <= lowerHalfXY && MathF.Abs(dy) <= lowerHalfXY;
                    if (inOuter && !inInner)
                    {
                        result.Add(w);
                        if (result.Count >= nPoints) return result;
                    }
                }
            }
        }
        return result;
    }

    // ── Build (called once at construction time) ───────────────────────────

    /// <summary>
    /// Build the dense-topology waypoint graph. Idempotent: subsequent calls
    /// are no-ops.
    /// </summary>
    public void SetUp()
    {
        if (_isBuilt) return;

        // 1. Generate dense waypoints (every MAP_RESOLUTION metres on every
        //    driving lane), grouped by segment (road, section, lane).
        var segmentMap = BuildSegmentMap();

        // 2. Mark "real" junctions: a junction-road counts as real if it has
        //    2+ incoming OR 2+ outgoing standard-road connections.
        var isRealJunction = DetermineRealJunctions();

        // 3. Stitch intra-segment connections + assign geodesic-grid ids +
        //    junction flagging.
        StitchSegments(segmentMap, isRealJunction);

        // 4. Build the spatial index AFTER all waypoint locations are stable.
        BuildSpatialIndex();

        // 5. Stitch inter-segment connections (next-segment-first,
        //    prev-segment-last).
        StitchInterSegment(segmentMap);

        // 6. Lane-change links (left/right neighbours at the same s).
        StitchLaneChange();

        // 7. Patch any waypoint with no next link by inheriting from neighbour.
        PatchIsolatedNexts();

        // 8. Assign RoadOptions (Straight/Left/Right at junction entries,
        //    LaneFollow elsewhere).
        SetUpRoadOption();

        // 9. Group dense waypoints by their resolved geodesic-grid id.
        IndexByGrid();

        // 10. Junction-level post-passes (boxes, conflicts, signal transforms).
        ComputeJunctionBoundingBoxes();
        ComputeJunctionRoadConflicts();
        ComputeSignalTransforms();

        _isBuilt = true;
    }

    // ── Step 1 — segment generation ────────────────────────────────────────

    private readonly record struct SegmentId(RoadId RoadId, LaneId LaneId, SectionId SectionId);

    private Dictionary<SegmentId, List<SimpleWaypoint>> BuildSegmentMap()
    {
        var map = new Dictionary<SegmentId, List<SimpleWaypoint>>();
        const double EPS = 1e-4;
        double step = Constants.Map.MAP_RESOLUTION;

        foreach (var road in _worldMap.Roads.Values)
        {
            for (double s = EPS; s < road.Length - EPS; s += step)
            {
                foreach (var section in LaneSectionsAt(road, s))
                {
                    foreach (var kv in section.Lanes)
                    {
                        var lane = kv.Value;
                        if (lane.Id == 0) continue;
                        if ((lane.Type & LaneType.Driving) == 0) continue;

                        // Min-width gate: upstream skips lanes narrower than
                        // MIN_LANE_WIDTH (so vehicles don't try to drive through
                        // shoulders that happen to be tagged driving).
                        var widthInfo = lane.GetInfoAt<RoadInfoLaneWidth>(s);
                        double width = widthInfo != null
                            ? widthInfo.Polynomial.Evaluate(s)
                            : 0.0;
                        if (width <= Constants.Map.MIN_LANE_WIDTH) continue;

                        var wp = new Waypoint(road.Id, section.Id, lane.Id, s);
                        Transform transform;
                        try
                        {
                            transform = _worldMap.ComputeTransform(wp);
                        }
                        catch
                        {
                            // Skip degenerate geometry — better than hard-fail.
                            continue;
                        }
                        var forward = ForwardFromRotation(transform.Rotation);
                        var sw = new SimpleWaypoint(wp, transform.Location, forward);

                        var sid = new SegmentId(road.Id, lane.Id, section.Id);
                        if (!map.TryGetValue(sid, out var list))
                        {
                            list = new List<SimpleWaypoint>();
                            map[sid] = list;
                        }
                        list.Add(sw);
                    }
                }
            }
        }
        return map;
    }

    private static IEnumerable<LaneSection> LaneSectionsAt(Road road, double s)
    {
        // Upstream's `road->GetLaneSectionsAt(s)` returns the section(s) whose
        // s_start ≤ s < next_s_start. Iterate sorted sections and yield the
        // active one (sections are non-overlapping by construction).
        LaneSection? active = null;
        foreach (var sec in road.LaneSections)
        {
            if (sec.S <= s) active = sec;
            else break;
        }
        if (active != null) yield return active;
    }

    private static Vector3D ForwardFromRotation(Rotation rot)
    {
        // Matches carla::geom::Math::GetForwardVector(Rotation).
        float cy = MathF.Cos(rot.Yaw * MathF.PI / 180f);
        float sy = MathF.Sin(rot.Yaw * MathF.PI / 180f);
        float cp = MathF.Cos(rot.Pitch * MathF.PI / 180f);
        float sp = MathF.Sin(rot.Pitch * MathF.PI / 180f);
        return new Vector3D(cy * cp, sy * cp, sp);
    }

    // ── Step 2 — real-junction detection ──────────────────────────────────

    private Dictionary<RoadId, bool> DetermineRealJunctions()
    {
        // Upstream walks Map::GetTopology() pairs (waypoint, successor). We
        // approximate by walking Road.Nexts: for each junction-path road
        // that connects to 2+ distinct standard-road incomings or outgoings,
        // mark every path on that side as "real".
        var inCount = new Dictionary<long, HashSet<RoadId>>();
        var outCount = new Dictionary<long, HashSet<RoadId>>();
        var real = new Dictionary<RoadId, bool>();

        foreach (var road in _worldMap.Roads.Values)
        {
            foreach (var next in road.Nexts)
            {
                if (road.IsJunction && !next.IsJunction)
                {
                    long stdId = HasNegativeLane(next) ? -(long)next.Id : (long)next.Id;
                    if (!inCount.TryGetValue(stdId, out var paths))
                    {
                        paths = new HashSet<RoadId>();
                        inCount[stdId] = paths;
                    }
                    paths.Add(road.Id);
                    if (paths.Count >= 2)
                        foreach (var p in paths) real[p] = true;
                }
                if (!road.IsJunction && next.IsJunction)
                {
                    long stdId = HasNegativeLane(road) ? -(long)road.Id : (long)road.Id;
                    if (!outCount.TryGetValue(stdId, out var paths))
                    {
                        paths = new HashSet<RoadId>();
                        outCount[stdId] = paths;
                    }
                    paths.Add(next.Id);
                    if (paths.Count >= 2)
                        foreach (var p in paths) real[p] = true;
                }
            }
        }
        return real;
    }

    private static bool HasNegativeLane(Road road)
    {
        if (road.LaneSections.Count == 0) return false;
        foreach (var lane in road.LaneSections[0].Lanes.Values)
            if (lane.Id < 0) return true;
        return false;
    }

    // ── Step 3 — intra-segment stitching + grid ids + junction flag ────────

    private void StitchSegments(
        Dictionary<SegmentId, List<SimpleWaypoint>> segmentMap,
        Dictionary<RoadId, bool> isRealJunction)
    {
        GeoGridId gridCounter = -1;

        foreach (var segment in segmentMap.Values)
        {
            gridCounter++;
            if (segment.Count == 0) continue;

            // Sort by `s`. Positive lane direction (negative lane id) drives
            // in increasing s; reverse the list for negative-direction lanes.
            segment.Sort((a, b) => a.Waypoint.S.CompareTo(b.Waypoint.S));
            bool isPositiveDirection = segment[0].Waypoint.LaneId <= 0;
            if (!isPositiveDirection) segment.Reverse();

            // Intra-segment chain + grid ids.
            var gridEdge = segment[0].Location;
            for (int i = 0; i < segment.Count - 1; i++)
            {
                var cur = segment[i];
                var nxt = segment[i + 1];

                float dx = gridEdge.X - cur.Location.X;
                float dy = gridEdge.Y - cur.Location.Y;
                float dz = gridEdge.Z - cur.Location.Z;
                float dsq = dx * dx + dy * dy + dz * dz;
                float thresh = Constants.Map.MAX_GEODESIC_GRID_LENGTH
                             * Constants.Map.MAX_GEODESIC_GRID_LENGTH;
                if (dsq > thresh)
                {
                    gridCounter++;
                    gridEdge = cur.Location;
                }
                cur.SetGeodesicGridId(gridCounter);
                cur.NextWaypoints.Add(nxt);
                nxt.PreviousWaypoints.Add(cur);
            }
            segment[^1].SetGeodesicGridId(gridCounter);

            // Mark junction-ness + push to dense topology.
            foreach (var sw in segment)
            {
                var roadId = sw.Waypoint.RoadId;
                bool roadIsJunction = _worldMap.IsJunction(roadId);
                if (roadIsJunction && !isRealJunction.ContainsKey(roadId))
                    sw.SetIsJunction(false);
                else
                    sw.SetIsJunction(roadIsJunction);
                if (sw.IsJunction)
                    sw.JunctionId = _worldMap.GetJunctionId(roadId);
                _denseTopology.Add(sw);
            }
        }
    }

    // ── Step 4 — spatial index ────────────────────────────────────────────

    private void BuildSpatialIndex()
    {
        foreach (var sw in _denseTopology)
        {
            var key = ((int)MathF.Floor(sw.Location.X * INV_CELL_SIZE),
                       (int)MathF.Floor(sw.Location.Y * INV_CELL_SIZE));
            if (!_grid.TryGetValue(key, out var bucket))
            {
                bucket = new List<SimpleWaypoint>();
                _grid[key] = bucket;
            }
            bucket.Add(sw);
        }
    }

    private SimpleWaypoint? FindClosestInGrid(Location loc)
    {
        if (_denseTopology.Count == 0) return null;

        int cxBase = (int)MathF.Floor(loc.X * INV_CELL_SIZE);
        int cyBase = (int)MathF.Floor(loc.Y * INV_CELL_SIZE);

        SimpleWaypoint? best = null;
        float bestD2 = float.MaxValue;
        bool foundAny = false;

        // Spiral outward in CELL_SIZE rings until we find a non-empty cell,
        // then sweep one extra ring to handle borderline diagonal cases.
        // Hard cap of 32 rings ≈ 160 m which exceeds any plausible spawn-to-
        // road distance in CARLA towns.
        const int maxRings = 32;
        int extraAfterFirstHit = 1;
        int extraScanned = 0;
        for (int r = 0; r <= maxRings; r++)
        {
            for (int cx = cxBase - r; cx <= cxBase + r; cx++)
            {
                for (int cy = cyBase - r; cy <= cyBase + r; cy++)
                {
                    if (r > 0
                        && System.Math.Abs(cx - cxBase) < r
                        && System.Math.Abs(cy - cyBase) < r) continue;
                    if (!_grid.TryGetValue((cx, cy), out var bucket)) continue;
                    foundAny = true;
                    foreach (var w in bucket)
                    {
                        float dx = w.Location.X - loc.X;
                        float dy = w.Location.Y - loc.Y;
                        float dz = w.Location.Z - loc.Z;
                        float d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            best = w;
                        }
                    }
                }
            }
            if (foundAny)
            {
                extraScanned++;
                if (extraScanned > extraAfterFirstHit) break;
            }
        }

        if (best != null) return best;

        // Fallback: linear scan over the whole topology. Extremely rare.
        foreach (var w in _denseTopology)
        {
            float dx = w.Location.X - loc.X;
            float dy = w.Location.Y - loc.Y;
            float dz = w.Location.Z - loc.Z;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = w;
            }
        }
        return best;
    }

    // ── Step 5 — inter-segment stitching ──────────────────────────────────

    private void StitchInterSegment(Dictionary<SegmentId, List<SimpleWaypoint>> segmentMap)
    {
        var segTopologyNext = new Dictionary<SegmentId, List<SegmentId>>();
        var segTopologyPrev = new Dictionary<SegmentId, List<SegmentId>>();

        foreach (var (sid, _) in segmentMap)
        {
            if (!_worldMap.Roads.TryGetValue(sid.RoadId, out var road)) continue;
            if (!road.LaneSectionsById.TryGetValue(sid.SectionId, out var section)) continue;
            var lane = section.GetLane(sid.LaneId);
            if (lane == null) continue;

            foreach (var next in lane.NextLanes)
            {
                if (next == null || next.Id == 0 || next.Section == null || next.Section.Road == null) continue;
                var succSid = new SegmentId(next.Section.Road.Id, next.Id, next.Section.Id);
                AddEdge(segTopologyNext, sid, succSid);
                AddEdge(segTopologyPrev, succSid, sid);
            }
            foreach (var prev in lane.PreviousLanes)
            {
                if (prev == null || prev.Id == 0 || prev.Section == null || prev.Section.Road == null) continue;
                var preSid = new SegmentId(prev.Section.Road.Id, prev.Id, prev.Section.Id);
                AddEdge(segTopologyPrev, sid, preSid);
                AddEdge(segTopologyNext, preSid, sid);
            }
        }

        foreach (var (sid, segment) in segmentMap)
        {
            if (segment.Count == 0) continue;
            var successors = ResolveSuccessors(sid, segTopologyNext, segmentMap, new HashSet<SegmentId>());
            var predecessors = ResolvePredecessors(sid, segTopologyPrev, segmentMap, new HashSet<SegmentId>());
            if (successors.Count > 0)
            {
                var back = segment[^1];
                foreach (var succ in successors)
                {
                    if (!back.NextWaypoints.Contains(succ))
                        back.NextWaypoints.Add(succ);
                }
            }
            if (predecessors.Count > 0)
            {
                var front = segment[0];
                foreach (var pre in predecessors)
                {
                    if (!front.PreviousWaypoints.Contains(pre))
                        front.PreviousWaypoints.Add(pre);
                }
            }
        }
    }

    private static void AddEdge(Dictionary<SegmentId, List<SegmentId>> dict, SegmentId a, SegmentId b)
    {
        if (!dict.TryGetValue(a, out var list))
        {
            list = new List<SegmentId>();
            dict[a] = list;
        }
        if (!list.Contains(b)) list.Add(b);
    }

    private static List<SimpleWaypoint> ResolveSuccessors(
        SegmentId sid,
        Dictionary<SegmentId, List<SegmentId>> topology,
        Dictionary<SegmentId, List<SimpleWaypoint>> segmentMap,
        HashSet<SegmentId> visited)
    {
        var result = new List<SimpleWaypoint>();
        if (!topology.TryGetValue(sid, out var nexts)) return result;
        foreach (var nsid in nexts)
        {
            if (!visited.Add(nsid)) continue;
            if (segmentMap.TryGetValue(nsid, out var nsegment) && nsegment.Count > 0)
                result.Add(nsegment[0]);
            else
                result.AddRange(ResolveSuccessors(nsid, topology, segmentMap, visited));
        }
        return result;
    }

    private static List<SimpleWaypoint> ResolvePredecessors(
        SegmentId sid,
        Dictionary<SegmentId, List<SegmentId>> topology,
        Dictionary<SegmentId, List<SimpleWaypoint>> segmentMap,
        HashSet<SegmentId> visited)
    {
        var result = new List<SimpleWaypoint>();
        if (!topology.TryGetValue(sid, out var prevs)) return result;
        foreach (var psid in prevs)
        {
            if (!visited.Add(psid)) continue;
            if (segmentMap.TryGetValue(psid, out var psegment) && psegment.Count > 0)
                result.Add(psegment[^1]);
            else
                result.AddRange(ResolvePredecessors(psid, topology, segmentMap, visited));
        }
        return result;
    }

    // ── Step 6 — lane-change linkage ──────────────────────────────────────

    private void StitchLaneChange()
    {
        // For each non-junction waypoint, find its same-direction lane
        // neighbours at the same s. Upstream queries lane.GetLeft() /
        // GetRight() (which respects RHT/LHT). We approximate by looking for
        // sibling lanes in the same section with adjacent ids and the same
        // sign — TrafficManager only uses these for opportunistic lane-change
        // anyway.
        foreach (var sw in _denseTopology)
        {
            if (sw.IsJunction) continue;
            var w = sw.Waypoint;
            if (!_worldMap.Roads.TryGetValue(w.RoadId, out var road)) continue;
            if (!road.LaneSectionsById.TryGetValue(w.SectionId, out var section)) continue;

            LaneId inner = w.LaneId > 0 ? w.LaneId - 1 : w.LaneId + 1;
            LaneId outer = w.LaneId > 0 ? w.LaneId + 1 : w.LaneId - 1;

            TryLinkLaneChange(sw, section, inner, isLeft: true);
            TryLinkLaneChange(sw, section, outer, isLeft: false);
        }
    }

    private void TryLinkLaneChange(SimpleWaypoint reference, LaneSection section, LaneId neighbourId, bool isLeft)
    {
        if (neighbourId == 0) return;
        if (System.Math.Sign(neighbourId) != System.Math.Sign(reference.Waypoint.LaneId)) return;
        var neighbourLane = section.GetLane(neighbourId);
        if (neighbourLane == null) return;
        if ((neighbourLane.Type & LaneType.Driving) == 0) return;

        // Build a waypoint POD at the same s on the neighbour lane and snap
        // to the closest dense node by Location.
        var neighbourWp = new Waypoint(reference.Waypoint.RoadId, section.Id, neighbourId, reference.Waypoint.S);
        Transform neighbourTransform;
        try
        {
            neighbourTransform = _worldMap.ComputeTransform(neighbourWp);
        }
        catch
        {
            return;
        }

        var neighbour = FindClosestInGrid(neighbourTransform.Location);
        if (neighbour == null) return;

        // SimpleWaypoint.SetLeftWaypoint/SetRightWaypoint already filter by
        // 2D cross-product sign and only commit if the candidate actually
        // lies on the expected side.
        if (isLeft) reference.SetLeftWaypoint(neighbour);
        else reference.SetRightWaypoint(neighbour);
    }

    // ── Step 7 — patch isolated next-less waypoints ──────────────────────

    private void PatchIsolatedNexts()
    {
        foreach (var sw in _denseTopology)
        {
            if (sw.NextWaypoints.Count > 0) continue;
            var neighbour = sw.RightWaypoint ?? sw.LeftWaypoint;
            if (neighbour == null) continue;
            foreach (var n in neighbour.NextWaypoints)
            {
                sw.NextWaypoints.Add(n);
                if (!n.PreviousWaypoints.Contains(sw))
                    n.PreviousWaypoints.Add(sw);
            }
        }
    }

    // ── Step 8 — RoadOption assignment ────────────────────────────────────

    private void SetUpRoadOption()
    {
        foreach (var sw in _denseTopology)
        {
            int nextCount = sw.NextWaypoints.Count;
            if (nextCount == 0)
            {
                sw.SetRoadOption(RoadOption.RoadEnd);
                continue;
            }
            if (nextCount > 1 || (!sw.IsJunction && sw.NextWaypoints[0].IsJunction))
            {
                sw.SetRoadOption(RoadOption.LaneFollow);
                foreach (var entry in sw.NextWaypoints)
                {
                    var traversed = new List<SimpleWaypoint>();
                    var current = entry;
                    int safety = 0;
                    while (current.IsJunction && safety < 1024)
                    {
                        traversed.Add(current);
                        if (current.NextWaypoints.Count == 0) break;
                        current = current.NextWaypoints[0];
                        safety++;
                    }
                    if (traversed.Count == 0) continue;

                    var firstFwd = traversed[0].ForwardVector;
                    var lastFwd = traversed[^1].ForwardVector;
                    float firstYaw = MathF.Atan2(firstFwd.Y, firstFwd.X) * 180f / MathF.PI;
                    float lastYaw = MathF.Atan2(lastFwd.Y, lastFwd.X) * 180f / MathF.PI;
                    int diff = ((int)(lastYaw - firstYaw)) % 360;
                    bool straight = (diff < Constants.Map.STRAIGHT_DEG && diff > -Constants.Map.STRAIGHT_DEG)
                                 || (diff > 360 - Constants.Map.STRAIGHT_DEG && diff <= 360)
                                 || (diff < -360 + Constants.Map.STRAIGHT_DEG && diff >= -360);
                    bool right = (diff >= Constants.Map.STRAIGHT_DEG && diff <= 180)
                              || (diff <= -180 && diff >= -360 + Constants.Map.STRAIGHT_DEG);
                    var opt = straight ? RoadOption.Straight
                             : (right ? RoadOption.Right : RoadOption.Left);
                    foreach (var twp in traversed) twp.SetRoadOption(opt);
                }
            }
            else if (nextCount == 1 && sw.GetRoadOption() == RoadOption.Void)
            {
                sw.SetRoadOption(RoadOption.LaneFollow);
            }
        }
    }

    // ── Step 9 — geodesic-grid bucket index ──────────────────────────────

    private void IndexByGrid()
    {
        foreach (var sw in _denseTopology)
        {
            int gid = sw.GetGeodesicGridId();
            if (!_byGrid.TryGetValue(gid, out var dict))
            {
                dict = new Dictionary<ulong, SimpleWaypoint>();
                _byGrid[gid] = dict;
            }
            dict[sw.GetId()] = sw;
        }
    }

    // ── Step 10a — junction bounding boxes ───────────────────────────────

    private void ComputeJunctionBoundingBoxes()
    {
        var byJunction = new Dictionary<JuncId, List<SimpleWaypoint>>();
        foreach (var sw in _denseTopology)
        {
            var rid = sw.Waypoint.RoadId;
            if (!_worldMap.Roads.TryGetValue(rid, out var road)) continue;
            if (!road.IsJunction) continue;
            var jid = road.JunctionId;
            if (!byJunction.TryGetValue(jid, out var list))
            {
                list = new List<SimpleWaypoint>();
                byJunction[jid] = list;
            }
            list.Add(sw);
        }

        foreach (var junction in _worldMap.Junctions.Values)
        {
            if (!byJunction.TryGetValue(junction.Id, out var members) || members.Count == 0)
                continue;
            float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue;
            float maxx = -float.MaxValue, maxy = -float.MaxValue, maxz = -float.MaxValue;
            foreach (var w in members)
            {
                var p = w.Location;
                if (p.X < minx) minx = p.X;
                if (p.Y < miny) miny = p.Y;
                if (p.Z < minz) minz = p.Z;
                if (p.X > maxx) maxx = p.X;
                if (p.Y > maxy) maxy = p.Y;
                if (p.Z > maxz) maxz = p.Z;
            }
            var loc = new Location(0.5f * (maxx + minx), 0.5f * (maxy + miny), 0.5f * (maxz + minz));
            var ext = new Vector3D(0.5f * (maxx - minx), 0.5f * (maxy - miny), 0.5f * (maxz - minz));
            junction.BoundingBox = new BoundingBox(loc, ext, default);
        }
    }

    // ── Step 10b — junction road conflicts ───────────────────────────────

    private void ComputeJunctionRoadConflicts()
    {
        // Upstream walks an rtree of road segments inside each junction's
        // bbox and pair-wise checks 2D segment distance ≤ 2 m. Without that
        // rtree we approximate by pair-wise dense-waypoint distance on a
        // per-(road1, road2) basis. The result feeds TrafficLightStage's
        // "do these incoming roads conflict?" — coarseness is acceptable.
        const float CONFLICT_DISTANCE_SQ = 2.0f * 2.0f;

        var byJunction = new Dictionary<JuncId, List<SimpleWaypoint>>();
        foreach (var sw in _denseTopology)
        {
            var rid = sw.Waypoint.RoadId;
            if (!_worldMap.Roads.TryGetValue(rid, out var road)) continue;
            if (!road.IsJunction) continue;
            var jid = road.JunctionId;
            if (!byJunction.TryGetValue(jid, out var list))
            {
                list = new List<SimpleWaypoint>();
                byJunction[jid] = list;
            }
            list.Add(sw);
        }

        foreach (var junction in _worldMap.Junctions.Values)
        {
            junction.RoadConflicts.Clear();
            if (!byJunction.TryGetValue(junction.Id, out var members)) continue;
            for (int i = 0; i < members.Count; i++)
            {
                var w1 = members[i];
                var r1 = w1.Waypoint.RoadId;
                for (int j = i + 1; j < members.Count; j++)
                {
                    var w2 = members[j];
                    var r2 = w2.Waypoint.RoadId;
                    if (r1 == r2) continue;
                    float dx = w1.Location.X - w2.Location.X;
                    float dy = w1.Location.Y - w2.Location.Y;
                    if (dx * dx + dy * dy > CONFLICT_DISTANCE_SQ) continue;
                    AddConflict(junction, r1, r2);
                    AddConflict(junction, r2, r1);
                }
            }
        }
    }

    private static void AddConflict(Junction junction, RoadId a, RoadId b)
    {
        if (!junction.RoadConflicts.TryGetValue(a, out var set))
        {
            set = new HashSet<RoadId>();
            junction.RoadConflicts[a] = set;
        }
        set.Add(b);
    }

    // ── Step 10c — signal transforms for road-relative signals ───────────

    private void ComputeSignalTransforms()
    {
        // Walk every signal whose Transform is still default (i.e. wasn't
        // populated by an inertial-position record). Compute its world-space
        // transform per MapBuilder.cpp:ComputeSignalTransform.
        var defaultTransform = default(Transform);
        foreach (var signal in _worldMap.Signals.Values)
        {
            if (!signal.Transform.Equals(defaultTransform)) continue;
            if (signal.UsingInertialPosition) continue;
            if (!_worldMap.Roads.TryGetValue(signal.RoadId, out var road)) continue;

            DirectedPoint point;
            try
            {
                point = CarlaNet.Map.Road.Map.GetDirectedPointInNoLaneOffset(road, signal.S);
            }
            catch
            {
                continue;
            }

            point.ApplyLateralOffset((float)-signal.T);
            var p = point.Location;
            p = new Location(p.X, -p.Y, p.Z + (float)signal.ZOffset);
            float pitchDeg = (float)(signal.Pitch * 180.0 / Math.PI);
            float yawDeg = (float)(-(point.Tangent + signal.HOffset) * 180.0 / Math.PI);
            float rollDeg = (float)(signal.Roll * 180.0 / Math.PI);
            var rotation = new Rotation(pitchDeg, yawDeg, rollDeg);

            var transform = new Transform(p, rotation);

            // Traffic-light type signals get nudged forward 0.25 units.
            if (IsTrafficLightType(signal.Type))
            {
                var fwd = ForwardFromRotation(rotation);
                transform = new Transform(
                    new Location(p.X + fwd.X * 0.25f, p.Y + fwd.Y * 0.25f, p.Z + fwd.Z * 0.25f),
                    rotation);
            }

            signal.Transform = transform;
        }
    }

    private static bool IsTrafficLightType(string type)
    {
        // OpenDRIVE signal type codes for traffic lights (per
        // carla::road::SignalType::IsTrafficLight upstream). Codes 1000001..
        // 1000014 cover all the standard traffic-light variants.
        return type == "1000001" || type == "1000002" || type == "1000003"
            || type == "1000004" || type == "1000005" || type == "1000006"
            || type == "1000007" || type == "1000008" || type == "1000009"
            || type == "1000010" || type == "1000011" || type == "1000012"
            || type == "1000013" || type == "1000014";
    }
}
