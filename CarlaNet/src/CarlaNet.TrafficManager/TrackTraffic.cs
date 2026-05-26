// Source: carla/trafficmanager/TrackTraffic.h / TrackTraffic.cpp
//
// Maintains two parallel inverted indexes:
//   - waypoint -> set of actors passing through it
//   - actor -> set of geodesic-grid ids occupied along its planned path
//
// CollisionStage uses <see cref="GetOverlappingVehicles"/> as its broad-phase
// filter — without this index the per-frame collision sweep would be O(N²)
// across all registered vehicles.
//
// Single-threaded by contract: the TM worker thread mutates this from
// LocalizationStage and reads from CollisionStage all in the same tick. No
// concurrency wrapper required.
#nullable enable

namespace CarlaNet.TrafficManager;

internal sealed class TrackTraffic
{
    // waypoint_id (uint64) -> set of actor ids passing through
    private readonly Dictionary<ulong, HashSet<ActorId>> _waypointOverlapTracker = new();
    // actor_id -> set of waypoint ids the actor currently occupies
    private readonly Dictionary<ActorId, HashSet<ulong>> _waypointOccupied = new();
    // actor_id -> set of geodesic-grid ids the actor's path crosses
    private readonly Dictionary<ActorId, HashSet<GeoGridId>> _actorToGrids = new();
    // geodesic-grid id -> set of actor ids whose paths cross it
    private readonly Dictionary<GeoGridId, HashSet<ActorId>> _gridToActors = new();

    private Location _heroLocation = new(0f, 0f, 0f);

    public TrackTraffic() { }

    /// <summary>
    /// Mark <paramref name="actorId"/> as passing through <paramref name="waypointId"/>.
    /// </summary>
    public void UpdatePassingVehicle(ulong waypointId, ActorId actorId)
    {
        if (_waypointOverlapTracker.TryGetValue(waypointId, out var actorIdSet))
            actorIdSet.Add(actorId);
        else
            _waypointOverlapTracker[waypointId] = new HashSet<ActorId> { actorId };

        if (_waypointOccupied.TryGetValue(actorId, out var waypointIdSet))
            waypointIdSet.Add(waypointId);
        else
            _waypointOccupied[actorId] = new HashSet<ulong> { waypointId };
    }

    /// <summary>Remove <paramref name="actorId"/> from <paramref name="waypointId"/>.</summary>
    public void RemovePassingVehicle(ulong waypointId, ActorId actorId)
    {
        if (_waypointOverlapTracker.TryGetValue(waypointId, out var actorIdSet))
        {
            actorIdSet.Remove(actorId);
            if (actorIdSet.Count == 0)
                _waypointOverlapTracker.Remove(waypointId);
        }

        if (_waypointOccupied.TryGetValue(actorId, out var waypointIdSet))
        {
            waypointIdSet.Remove(waypointId);
            if (waypointIdSet.Count == 0)
                _waypointOccupied.Remove(actorId);
        }
    }

    /// <summary>
    /// Snapshot of the actor set currently passing through
    /// <paramref name="waypointId"/>. Returns an empty set (not null) when
    /// nothing is known — matches the upstream contract.
    /// </summary>
    public IReadOnlySet<ActorId> GetPassingVehicles(ulong waypointId)
    {
        if (_waypointOverlapTracker.TryGetValue(waypointId, out var actorIdSet))
            return actorIdSet;
        // Static empty set — allocates once for the whole process.
        return s_emptyActorSet;
    }
    private static readonly HashSet<ActorId> s_emptyActorSet = new();

    /// <summary>
    /// Re-index <paramref name="actorId"/>'s grid membership from the
    /// supplied buffer of upcoming waypoints. LocalizationStage calls this
    /// every tick after updating the horizon buffer.
    /// </summary>
    /// <remarks>
    /// PERF NOTE: the upstream version iterates the whole buffer with
    /// <c>buffer.at(i)</c>. We accept any <c>IReadOnlyList&lt;SimpleWaypoint&gt;</c>
    /// so callers (including the future ring-buffer deque) can pass without
    /// copying. The earlier-clear-then-rebuild pattern matches upstream so
    /// the actor's grid membership is fully refreshed each tick.
    /// </remarks>
    public void UpdateGridPosition(ActorId actorId, IReadOnlyList<SimpleWaypoint> buffer)
    {
        if (buffer.Count == 0)
            return;

        // 1. Clear current actor from every grid it was in.
        if (_actorToGrids.TryGetValue(actorId, out var currentGridsExisting))
        {
            foreach (var gridId in currentGridsExisting)
            {
                if (_gridToActors.TryGetValue(gridId, out var actorIds))
                    actorIds.Remove(actorId);
            }
            _actorToGrids.Remove(actorId);
        }

        // 2. Re-add for every waypoint in the new buffer.
        var currentGrids = new HashSet<GeoGridId>();
        for (int i = 0; i < buffer.Count; ++i)
        {
            var wp = buffer[i];
            GeoGridId ggid = wp.GetGeodesicGridId();
            currentGrids.Add(ggid);

            if (!_gridToActors.TryGetValue(ggid, out var actorIds))
            {
                actorIds = new HashSet<ActorId>();
                _gridToActors[ggid] = actorIds;
            }
            actorIds.Add(actorId);
        }

        _actorToGrids[actorId] = currentGrids;
    }

    /// <summary>
    /// Variant for actors not in the registered-vehicle pool (e.g. walkers).
    /// Updates both the per-waypoint passing-vehicle tracker AND the
    /// geodesic-grid tracker.
    /// </summary>
    public void UpdateUnregisteredGridPosition(ActorId actorId, IReadOnlyList<SimpleWaypoint> waypoints)
    {
        DeleteActor(actorId);

        var currentGrids = new HashSet<GeoGridId>();
        foreach (var wp in waypoints)
        {
            UpdatePassingVehicle(wp.GetId(), actorId);
            GeoGridId ggid = wp.GetGeodesicGridId();
            currentGrids.Add(ggid);

            if (_gridToActors.TryGetValue(ggid, out var actorIds))
                actorIds.Add(actorId);
            else
                _gridToActors[ggid] = new HashSet<ActorId> { actorId };
        }
        _actorToGrids[actorId] = currentGrids;
    }

    /// <summary>
    /// Aggregate of every actor whose planned path crosses one of
    /// <paramref name="actorId"/>'s grids. CollisionStage's broad-phase.
    /// </summary>
    public HashSet<ActorId> GetOverlappingVehicles(ActorId actorId)
    {
        var result = new HashSet<ActorId>();
        if (_actorToGrids.TryGetValue(actorId, out var gridIds))
        {
            foreach (var gridId in gridIds)
            {
                if (_gridToActors.TryGetValue(gridId, out var actorIds))
                {
                    foreach (var id in actorIds)
                        result.Add(id);
                }
            }
        }
        return result;
    }

    /// <summary>True if no vehicles are currently claiming the grid.</summary>
    public bool IsGeoGridFree(GeoGridId geogridId)
    {
        if (_gridToActors.TryGetValue(geogridId, out var actorIds))
            return actorIds.Count == 0;
        return true;
    }

    /// <summary>
    /// Used by MotionPlanStage when teleporting a respawned vehicle into a
    /// fresh grid: reserve the slot before the next ALSM update finalizes
    /// the kinematic state.
    /// </summary>
    public void AddTakenGrid(GeoGridId geogridId, ActorId actorId)
    {
        if (!_gridToActors.ContainsKey(geogridId))
            _gridToActors[geogridId] = new HashSet<ActorId> { actorId };
    }

    public void SetHeroLocation(Location location) => _heroLocation = location;
    public Location GetHeroLocation() => _heroLocation;

    /// <summary>
    /// Drop every trace of <paramref name="actorId"/> from both inverted
    /// indexes. Called by ALSM when a vehicle is destroyed.
    /// </summary>
    public void DeleteActor(ActorId actorId)
    {
        if (_actorToGrids.TryGetValue(actorId, out var gridIds))
        {
            foreach (var gridId in gridIds)
            {
                if (_gridToActors.TryGetValue(gridId, out var actorIds))
                    actorIds.Remove(actorId);
            }
            _actorToGrids.Remove(actorId);
        }

        if (_waypointOccupied.TryGetValue(actorId, out var waypointIdSet))
        {
            // Copy first; RemovePassingVehicle mutates `waypoint_occupied`.
            var ids = new ulong[waypointIdSet.Count];
            waypointIdSet.CopyTo(ids);
            foreach (var waypointId in ids)
                RemovePassingVehicle(waypointId, actorId);
        }
    }

    public void Clear()
    {
        _waypointOverlapTracker.Clear();
        _waypointOccupied.Clear();
        _actorToGrids.Clear();
        _gridToActors.Clear();
    }
}
