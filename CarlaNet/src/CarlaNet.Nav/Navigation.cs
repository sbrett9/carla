// Source: carla/nav/Navigation.{h,cpp}
//
// The Detour-backed pedestrian-navigation facade. Owns:
//   - the parsed dtNavMesh + dtNavMeshQuery (loaded from CARLA's cooked
//     navmesh blob via DotRecast's `DtMeshSetReader.Read32Bit`)
//   - the dtCrowd that drives all walker agents
//   - HashSet shims for the CARLA-patched `paused` / `dead` agent flags
//     (NAV_PORT_SPEC.md Risk R1 — DotRecast is not patched and we don't
//     vendor it; the shims live above the crowd)
//   - VehicleObbTracker for the OBB-based walker-vs-vehicle separation
//     workaround
//
// Coordinate conventions:
//   - Caller-facing: Unreal (x,y on the ground plane, z up; right-handed)
//   - Recast/Detour internal: y up. We swap (x,y,z) ↔ (x,z,y) at every
//     interop boundary. Helper: ToRecast / FromRecast.
//
// Threading: this class is NOT thread-safe. The sibling WalkerNavigation
// class will pin a single worker thread to it. The Python shim's
// `set_pedestrians_*` calls funnel through that same thread.
#nullable enable

using Microsoft.Extensions.Logging;

namespace CarlaNet.Nav;

/// <summary>
/// Pedestrian navmesh + crowd facade. Construct → <see cref="LoadMesh"/>
/// → use; dispose to release the Detour native-ish memory.
/// </summary>
public sealed class Navigation : IDisposable
{
    // ── Constants (mirror Navigation.cpp:33-44) ─────────────────────────
    public const int   MaxPolys             = 256;
    public const int   MaxAgents            = 500;
    public const int   MaxQuerySearchNodes  = 2048;
    public const float AgentHeight          = 1.8f;
    public const float AgentRadius          = 0.3f;
    public const float AgentUnblockDistance = 0.5f;
    public const float AgentUnblockTime     = 4.0f;
    public const float AreaGrassCost        = 1.0f;
    public const float AreaRoadCost         = 10.0f;

    private readonly ILogger? _logger;
    private readonly IRcRand _rand;

    // ── Detour state ─────────────────────────────────────────────────────
    private DtNavMesh? _navMesh;
    private DtNavMeshQuery? _navQuery;
    private DtCrowd? _crowd;
    private byte[]? _binaryMesh;
    private bool _ready;

    // ── Workarounds for CARLA-only DetourCrowd patches (Risk R1) ────────
    private readonly HashSet<int> _pausedAgents = new();
    private readonly HashSet<int> _deadAgents = new();
    private readonly VehicleObbTracker _vehicles = new();

    // ── Bookkeeping ──────────────────────────────────────────────────────
    /// <summary>Index → managed agent reference (so we can dispatch by int).</summary>
    private readonly Dictionary<int, DtCrowdAgent> _agentsByIndex = new();

    private float _probabilityCrossing;
    private double _lastDeltaSeconds;

    public Navigation(ILogger? logger = null, IRcRand? rand = null)
    {
        _logger = logger;
        _rand = rand ?? new RcRand();
    }

    // ────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse the navmesh blob produced by RecastBuilder (the bytes returned
    /// by <c>get_navigation_mesh</c> / <c>get_cache_file</c>). Throws on
    /// parse failure.
    /// </summary>
    public void LoadMesh(byte[] blob)
    {
        if (blob is null) throw new ArgumentNullException(nameof(blob));
        if (blob.Length < 40) // minimum header size
            throw new NavmeshLoadException("Nav blob is too small to contain a valid header");

        DtNavMesh mesh;
        try
        {
            // Risk R2: MUST use Read32Bit + maxVertPerPoly=6. CARLA's
            // recastnavigation is compiled without DT_POLYREF64 (so
            // dtTileRef = 4 bytes) and writes 6 verts per poly (recast's
            // DT_VERTS_PER_POLYGON default). Using the default Read() would
            // silently corrupt every tile past tile #0.
            var bb = new RcByteBuffer(blob);
            var reader = new DtMeshSetReader();
            mesh = reader.Read32Bit(bb, maxVertPerPoly: 6);
        }
        catch (IOException ex)
        {
            throw new NavmeshLoadException("Failed to parse CARLA navmesh blob", ex);
        }
        catch (Exception ex) when (ex is not NavmeshLoadException)
        {
            throw new NavmeshLoadException("Unexpected error parsing CARLA navmesh blob", ex);
        }

        Debug.Assert(mesh.GetMaxTiles() > 0, "navmesh loaded with zero tiles — blob is empty or corrupt");

        // Swap in
        DisposeDetourState();
        _navMesh = mesh;
        _navQuery = new DtNavMeshQuery(mesh);
        _binaryMesh = blob;
        _ready = true;

        CreateCrowd();
    }

    /// <summary>True iff <see cref="LoadMesh"/> has succeeded and the crowd is wired.</summary>
    public bool IsReady => _ready && _navMesh is not null && _navQuery is not null && _crowd is not null;

    public double LastDeltaSeconds => _lastDeltaSeconds;

    public void Dispose()
    {
        DisposeDetourState();
        _vehicles.Clear();
        _pausedAgents.Clear();
        _deadAgents.Clear();
        _agentsByIndex.Clear();
    }

    private void DisposeDetourState()
    {
        // DotRecast objects are managed; null them out so the GC can reclaim.
        _crowd = null;
        _navQuery = null;
        _navMesh = null;
        _binaryMesh = null;
        _ready = false;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Crowd setup (Navigation.cpp:201-264)
    // ────────────────────────────────────────────────────────────────────

    private void CreateCrowd()
    {
        if (_navMesh is null) return;

        // max agent radius = AGENT_RADIUS * 20 = 6.0 m (matches upstream;
        // chosen to cover the bounding sphere of CARLA's largest vehicle).
        var config = new DtCrowdConfig(maxAgentRadius: AgentRadius * 20.0f);

        // Construct two per-filter index ⇒ DtQueryDefaultFilter, mirroring
        // CARLA's filters 0 (cannot cross roads) and 1 (can).
        var crowd = new DtCrowd(config, _navMesh, queryFilterFactory: i =>
        {
            var areaCosts = new float[DtDetour.DT_MAX_AREAS];
            for (int a = 0; a < areaCosts.Length; ++a) areaCosts[a] = 1.0f;
            areaCosts[NavAreas.Road]  = AreaRoadCost;
            areaCosts[NavAreas.Grass] = AreaGrassCost;
            return i switch
            {
                0 => new DtQueryDefaultFilter(SamplePolyFlags.Walkable, SamplePolyFlags.Road, areaCosts),
                1 => new DtQueryDefaultFilter(SamplePolyFlags.Walkable, SamplePolyFlags.None, areaCosts),
                _ => new DtQueryDefaultFilter(SamplePolyFlags.Walkable, SamplePolyFlags.None, areaCosts),
            };
        });

        _crowd = crowd;

        // Mirror the 4 obstacle-avoidance quality tiers from Navigation.cpp:231-263.
        ConfigureAvoidance(0, adaptiveDivs: 5, adaptiveRings: 2, adaptiveDepth: 1);
        ConfigureAvoidance(1, adaptiveDivs: 5, adaptiveRings: 2, adaptiveDepth: 2);
        ConfigureAvoidance(2, adaptiveDivs: 7, adaptiveRings: 2, adaptiveDepth: 3);
        ConfigureAvoidance(3, adaptiveDivs: 7, adaptiveRings: 3, adaptiveDepth: 3);
    }

    private void ConfigureAvoidance(int tier, int adaptiveDivs, int adaptiveRings, int adaptiveDepth)
    {
        if (_crowd is null) return;
        var p = _crowd.GetObstacleAvoidanceParams(tier);
        p.velBias = 0.5f;
        p.adaptiveDivs  = adaptiveDivs;
        p.adaptiveRings = adaptiveRings;
        p.adaptiveDepth = adaptiveDepth;
        _crowd.SetObstacleAvoidanceParams(tier, p);
    }

    // ────────────────────────────────────────────────────────────────────
    //  CARLA-patch shims (Risk R1): paused / dead state.
    //  In upstream these are bool fields on the crowd agent itself; here
    //  we keep two HashSets keyed by the crowd agent's int idx.
    // ────────────────────────────────────────────────────────────────────

    public bool PauseWalker(int agentIndex, bool paused)
    {
        if (!IsReady || !_agentsByIndex.ContainsKey(agentIndex)) return false;
        if (paused) _pausedAgents.Add(agentIndex);
        else        _pausedAgents.Remove(agentIndex);
        return true;
    }

    public bool IsWalkerPaused(int agentIndex) => _pausedAgents.Contains(agentIndex);

    public bool IsAgentDead(int agentIndex) => _deadAgents.Contains(agentIndex);

    public void MarkWalkerDead(int agentIndex)
    {
        if (_agentsByIndex.ContainsKey(agentIndex))
            _deadAgents.Add(agentIndex);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Crowd / agent management
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert a walker agent at <paramref name="startLocation"/>. Returns
    /// the agent's crowd index, or -1 if it couldn't be placed (e.g.
    /// off-mesh or crowd full).
    /// </summary>
    public int AddWalker(Location startLocation, float radius, float height, float maxSpeed)
    {
        if (!IsReady || _crowd is null) return -1;

        var p = new DtCrowdAgentParams
        {
            radius                = radius,
            height                = height,
            maxAcceleration       = 160.0f,
            maxSpeed              = maxSpeed,
            collisionQueryRange   = 10.0f,
            pathOptimizationRange = radius * 30.0f,
            obstacleAvoidanceType = 3,
            separationWeight      = 0.5f,
            queryFilterType       = SampleFilterIndex(),
            updateFlags           =
                DtCrowdAgentUpdateFlags.DT_CROWD_ANTICIPATE_TURNS  |
                DtCrowdAgentUpdateFlags.DT_CROWD_OBSTACLE_AVOIDANCE |
                DtCrowdAgentUpdateFlags.DT_CROWD_SEPARATION,
            userData = null,
        };

        // Recast pivot is the agent's feet; CARLA's Location is the actor
        // pivot which sits at half-height. Subtract that so we don't drop
        // the agent inside the navmesh.
        var pos = ToRecast(startLocation.X, startLocation.Y, startLocation.Z - (height / 2.0f));

        DtCrowdAgent agent;
        try
        {
            agent = _crowd.AddAgent(pos, p);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Nav: AddWalker AddAgent threw");
            return -1;
        }

        if (agent is null)
            return -1;

        var idx = agent.idx;
        _agentsByIndex[idx] = agent;
        return idx;
    }

    public bool RemoveWalker(int agentIndex)
    {
        if (!IsReady || _crowd is null) return false;
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return false;
        try { _crowd.RemoveAgent(agent); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Nav: RemoveAgent threw"); }
        _agentsByIndex.Remove(agentIndex);
        _pausedAgents.Remove(agentIndex);
        _deadAgents.Remove(agentIndex);
        return true;
    }

    /// <summary>
    /// Directly point the crowd agent at <paramref name="destination"/>,
    /// bypassing the WalkerManager event machinery (no traffic-light /
    /// crosswalk stop). Equivalent to upstream
    /// <c>Navigation::SetWalkerDirectTarget</c>.
    /// </summary>
    public bool RequestMoveTarget(int agentIndex, Location destination)
    {
        if (!IsReady || _crowd is null || _navQuery is null) return false;
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return false;

        var pt = ToRecast(destination);
        var halfExt = _crowd.GetQueryExtents();
        var filter = _crowd.GetFilter(0);

        var status = _navQuery.FindNearestPoly(pt, halfExt, filter,
            out var targetRef, out var _, out var _);
        if (status.Failed() || targetRef == 0)
            return false;

        try
        {
            return _crowd.RequestMoveTarget(agent, targetRef, pt);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Nav: RequestMoveTarget threw");
            return false;
        }
    }

    /// <summary>
    /// Cancel any pending move target — walker will stop where it is.
    /// </summary>
    public bool RequestStop(int agentIndex)
    {
        if (!IsReady || _crowd is null) return false;
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return false;
        try
        {
            return _crowd.ResetMoveTarget(agent);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Nav: RequestStop ResetMoveTarget threw");
            return false;
        }
    }

    /// <summary>
    /// Reassign the agent's query-filter index (0 = no-roads, 1 = roads-ok).
    /// </summary>
    public bool SetAgentFilter(int agentIndex, int filterIndex)
    {
        if (!IsReady) return false;
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return false;
        agent.option.queryFilterType = filterIndex;
        return true;
    }

    public bool SetWalkerMaxSpeed(int agentIndex, float maxSpeed)
    {
        if (!IsReady) return false;
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return false;
        agent.option.maxSpeed = maxSpeed;
        return true;
    }

    public void SetPedestriansCrossFactor(float percentage)
    {
        _probabilityCrossing = Math.Clamp(percentage, 0.0f, 1.0f);
    }

    public float PedestriansCrossFactor => _probabilityCrossing;

    private int SampleFilterIndex()
        => _rand.Next() <= _probabilityCrossing ? 1 : 0;

    // ────────────────────────────────────────────────────────────────────
    //  Per-tick driver
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-frame update: applies vehicle-OBB repulsion to nearby walker
    /// agents (workaround for the missing CARLA dtCrowd patches), then
    /// advances the crowd by <paramref name="dt"/> seconds.
    /// </summary>
    public void Tick(float dt)
    {
        if (!IsReady || _crowd is null) return;
        _lastDeltaSeconds = dt;

        if (_vehicles.Count > 0)
        {
            foreach (var (idx, agent) in _agentsByIndex)
            {
                if (_pausedAgents.Contains(idx) || _deadAgents.Contains(idx))
                    continue;

                // Recast frame: (x, y_up, z). Convert walker pos back to
                // Unreal (x, z_in_recast=y_world, y_in_recast=z_world).
                var walkerPos = FromRecastVec(agent.npos);
                var push = _vehicles.ComputeRepulsion(walkerPos);
                if (push.LengthSquared() < 1e-6f) continue;

                // Convert push (Unreal x,y,z with z up) back into Recast
                // (x, z_up, y_horizontal) and bias `nvel` in that direction.
                var pushRecast = ToRecast(push.X, push.Y, push.Z);
                agent.nvel = new RcVec3f(
                    agent.nvel.X + pushRecast.X,
                    agent.nvel.Y + pushRecast.Y,
                    agent.nvel.Z + pushRecast.Z);
            }
        }

        try
        {
            _crowd.Update(dt, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Nav: DtCrowd.Update threw (continuing)");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Agent queries
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Current position + velocity for the walker, in Unreal coords.
    /// Returns (default, default) if the agent doesn't exist.
    /// </summary>
    public (Location Pos, Vector3D Vel) GetAgentState(int agentIndex)
    {
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent))
            return (default, default);

        var pos = FromRecast(agent.npos);
        var v = agent.vel;
        // Velocity needs the same (x, z, y) swap as positions.
        var vel = new Vector3D(v.X, v.Z, v.Y);
        return (pos, vel);
    }

    /// <summary>
    /// Convenience: just the location (matches upstream
    /// <c>Navigation::GetWalkerPosition</c>).
    /// </summary>
    public Location? GetWalkerPosition(int agentIndex)
    {
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return null;
        return FromRecast(agent.npos);
    }

    /// <summary>
    /// Magnitude of the walker's current velocity, m/s.
    /// </summary>
    public float GetWalkerSpeed(int agentIndex)
    {
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return 0.0f;
        var v = agent.vel;
        return MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }

    /// <summary>
    /// Yaw of the walker's velocity in degrees (Unreal convention).
    /// Returns null if the agent doesn't exist or its velocity is below
    /// a noise floor on both <c>vel</c> and <c>dvel</c>.
    /// </summary>
    public float? GetWalkerYawDeg(int agentIndex)
    {
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return null;
        var v = agent.vel;
        const float min = 0.1f;
        // Recast frame: vel.X = Unreal X, vel.Z = Unreal Y.
        if (v.X < -min || v.X > min || v.Z < -min || v.Z > min)
            return MathF.Atan2(v.Z, v.X) * (180.0f / MathF.PI);

        var dv = agent.dvel;
        return MathF.Atan2(dv.Z, dv.X) * (180.0f / MathF.PI);
    }

    /// <summary>
    /// Make the agent face <paramref name="target"/> by injecting a tiny
    /// directional velocity bias (matches upstream
    /// <c>Navigation::SetWalkerLookAt</c>). The crowd won't actually move
    /// the agent — the bias just feeds the yaw-from-velocity formula.
    /// </summary>
    public bool SetWalkerLookAt(int agentIndex, Location target)
    {
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return false;

        // Same scaling as upstream Navigation.cpp:1196-1209.
        const float scale = 0.0001f;
        var x = (target.X - agent.npos.X) * scale;
        var yWorld = (target.Y - agent.npos.Z) * scale;  // unreal Y ↔ recast Z
        var zWorld = (target.Z - agent.npos.Y) * scale;  // unreal Z ↔ recast Y

        var v = new RcVec3f(x, zWorld, yWorld);
        agent.vel  = v;
        agent.nvel = v;
        agent.dvel = v;
        return true;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Path queries
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Straight-line path of waypoints from <paramref name="start"/> to
    /// <paramref name="end"/> on the navmesh. Empty list = no path or no
    /// mesh. Uses the "filter 0" (no-roads) query filter by default;
    /// callers that need road-crossing should pre-route via the crowd.
    /// </summary>
    public IReadOnlyList<Location> FindStraightPath(Location start, Location end)
    {
        return FindStraightPathInternal(start, end, filterIndex: 0, out _);
    }

    /// <summary>
    /// Variant of <see cref="FindStraightPath(Location, Location)"/> that
    /// also returns each waypoint's area-type byte (NAV_AREA_*). Used by
    /// <c>WalkerManager.SetWalkerRoute</c> to decide where to insert
    /// <c>WalkerEventStopAndCheck</c> events. Pass the crowd agent's
    /// filter index (0 = no-roads, 1 = can-cross-roads).
    /// </summary>
    public IReadOnlyList<Location> FindAgentRoute(int agentIndex, Location start, Location end, out byte[] areas)
    {
        var filterIndex = 0;
        if (_agentsByIndex.TryGetValue(agentIndex, out var agent))
            filterIndex = agent.option.queryFilterType;
        return FindStraightPathInternal(start, end, filterIndex, out areas);
    }

    private IReadOnlyList<Location> FindStraightPathInternal(
        Location start, Location end, int filterIndex, out byte[] areas)
    {
        areas = Array.Empty<byte>();
        if (!IsReady || _navQuery is null || _navMesh is null || _crowd is null)
            return Array.Empty<Location>();

        var filter = _crowd.GetFilter(filterIndex);
        // CARLA uses extents (2, 4, 2) in Recast frame.
        var halfExt = new RcVec3f(2.0f, 4.0f, 2.0f);
        var startPos = ToRecast(start);
        var endPos   = ToRecast(end);

        var statusS = _navQuery.FindNearestPoly(startPos, halfExt, filter,
            out var startRef, out _, out _);
        var statusE = _navQuery.FindNearestPoly(endPos, halfExt, filter,
            out var endRef, out _, out _);
        if (statusS.Failed() || statusE.Failed() || startRef == 0 || endRef == 0)
            return Array.Empty<Location>();

        Span<long> polys = stackalloc long[MaxPolys];
        var pathStatus = _navQuery.FindPath(startRef, endRef, startPos, endPos, filter,
            polys, out var numPolys, MaxPolys);
        if (pathStatus.Failed() || numPolys == 0)
            return Array.Empty<Location>();

        // Clamp end position to the last polygon if FindPath returned partial.
        var clampedEnd = endPos;
        if (polys[numPolys - 1] != endRef)
        {
            var st = _navQuery.ClosestPointOnPoly(polys[numPolys - 1], endPos, out var closest, out _);
            if (st.Succeeded())
                clampedEnd = closest;
        }

        Span<DtStraightPath> straight = stackalloc DtStraightPath[MaxPolys];
        var spStatus = _navQuery.FindStraightPath(startPos, clampedEnd, polys.Slice(0, numPolys), numPolys,
            straight, out var numStraight, MaxPolys,
            DtStraightPathOptions.DT_STRAIGHTPATH_AREA_CROSSINGS);
        if (spStatus.Failed() || numStraight == 0)
            return Array.Empty<Location>();

        var path = new List<Location>(numStraight);
        var areaBuf = new byte[numStraight];
        for (int j = 0; j < numStraight; ++j)
        {
            path.Add(FromRecast(straight[j].pos));
            _navMesh.GetPolyArea(straight[j].refs, out var area);
            areaBuf[j] = (byte)area;
        }
        areas = areaBuf;
        return path;
    }

    /// <summary>
    /// Random sidewalk location on the navmesh, or null if 10 attempts
    /// failed (in practice means the mesh has no sidewalks, which would be
    /// a content bug). Matches upstream <c>Navigation::GetRandomLocation</c>
    /// behaviour (sidewalk-only filter, retry up to 10x).
    /// </summary>
    public Location? GetRandomReachableLocation()
    {
        if (!IsReady || _navQuery is null) return null;

        // Sidewalk-only filter so walkers don't spawn in the road.
        var areaCosts = new float[DtDetour.DT_MAX_AREAS];
        for (int a = 0; a < areaCosts.Length; ++a) areaCosts[a] = 1.0f;
        var filter = new DtQueryDefaultFilter(SamplePolyFlags.Sidewalk, SamplePolyFlags.None, areaCosts);

        for (int rounds = 0; rounds < 10; ++rounds)
        {
            var status = _navQuery.FindRandomPoint(filter, _rand, out _, out var point);
            if (status.Succeeded())
                return FromRecast(point);
        }
        return null;
    }

    /// <summary>
    /// Snap <paramref name="point"/> to the nearest polygon on the navmesh.
    /// Returns null if the point is too far from the mesh to snap.
    /// </summary>
    public Location? GetClosestPointOnMesh(Location point)
    {
        if (!IsReady || _navQuery is null || _crowd is null) return null;

        var halfExt = new RcVec3f(2.0f, 4.0f, 2.0f);
        var filter = _crowd.GetFilter(1);
        var pt = ToRecast(point);
        var status = _navQuery.FindNearestPoly(pt, halfExt, filter,
            out var polyRef, out var nearest, out _);
        if (status.Failed() || polyRef == 0)
            return null;
        return FromRecast(nearest);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Vehicle OBBs
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replace the current set of vehicle OBBs (yaw in degrees). Walker
    /// agents within range will be repelled away from these OBBs during
    /// the next <see cref="Tick"/>.
    /// </summary>
    public void UpdateVehicleObbs(IReadOnlyList<(ActorId Id, Location Center, Vector3D Extent, float YawDeg)> obbs)
    {
        _vehicles.Update(obbs);
    }

    /// <summary>
    /// Replacement for upstream <c>Navigation::HasVehicleNear</c>. Returns
    /// true if a vehicle OBB sits within <paramref name="distance"/> of
    /// the agent and (if <paramref name="direction"/> is non-zero) roughly
    /// in front of it.
    /// </summary>
    public bool HasVehicleNear(int agentIndex, float distance, Vector3D direction)
    {
        if (!_agentsByIndex.TryGetValue(agentIndex, out var agent)) return false;
        var walkerPos = FromRecastVec(agent.npos);
        var dirV = new Vector3(direction.X, direction.Y, direction.Z);
        return _vehicles.HasVehicleNear(walkerPos, distance, dirV);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Coordinate-system helpers (Unreal ↔ Recast).
    //
    //  Unreal: right-handed, Z up, Y forward.
    //  Recast: left-handed, Y up, Z forward (we swap Y↔Z).
    //  See Navigation.cpp:309 etc. for the upstream swap pattern.
    // ────────────────────────────────────────────────────────────────────

    private static RcVec3f ToRecast(Location loc) => new(loc.X, loc.Z, loc.Y);
    private static RcVec3f ToRecast(float x, float y, float z) => new(x, z, y);
    private static Location FromRecast(RcVec3f v) => new(v.X, v.Z, v.Y);
    private static Vector3 FromRecastVec(RcVec3f v) => new(v.X, v.Z, v.Y);
}
