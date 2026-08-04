using System.Globalization;
using CarlaNet.Transport;
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Rpc.Control;
using CarlaNet.Types.Geom;

namespace CarlaNet.Scenario;

/// <summary>
/// Runs a parsed storyboard against a running world, driven by the world tick.
///
/// The executor takes its pulse from the client's tick stream rather than from the Traffic Manager, so
/// scenario timing does not inherit the Traffic Manager's free-running clock, and it holds no
/// interpreter lock: nothing in the per-tick path crosses to the Python client, which only starts and
/// stops a scenario.
///
/// Construction begins the scenario; <see cref="Dispose"/> ends it and removes the entities it placed.
/// </summary>
public sealed class ScenarioExecutor : IDisposable
{
    /// At or below this speed a vehicle counts as stationary, for the purpose of a dwell trigger.
    private const double StationaryThresholdMps = 0.15;

    /// A commanded speed of zero produces neither throttle nor brake, so a vehicle told to stop that
    /// way coasts and cannot be placed. Speed commands are therefore never taken below this floor;
    /// a stop is completed by taking the vehicle out of Traffic Manager control and holding it on the
    /// brakes, which also keeps a long dwell from being culled as a stuck vehicle.
    private const double MinimumCommandedSpeedMps = 1.0;

    /// The Traffic Manager interprets a desired speed in kilometres per hour, despite the value being
    /// described elsewhere as metres per second.
    private const double MpsToTrafficManagerUnits = 3.6;

    /// Clearance used for the placing call itself, before the vehicle is set down precisely. It only
    /// has to exceed any vehicle's half-height so the placement does not intersect the road.
    private const double SpawnClearanceMetres = 3.0;

    private readonly CarlaClient _client;
    private readonly TrafficManager.TrafficManager _traffic;
    private readonly ScenarioDefinition _definition;
    private readonly Action<string>? _report;

    private readonly RoadNetwork _network;
    private readonly Dictionary<string, EntityRuntime> _entities = new();
    private readonly Dictionary<LanePosition, Location> _resolvedPositions = new();
    private readonly Dictionary<string, ActRuntime> _acts = new();

    private readonly Action<TickTimestamp> _tickHandler;
    private double _startElapsedSeconds = double.NaN;
    private int _stopped;

    public string Name => _definition.Name;
    public bool Running => Volatile.Read(ref _stopped) == 0;

    /// Scenario-relative time in seconds, or zero before the first tick arrives.
    public double ElapsedSeconds { get; private set; }

    public int ActsComplete { get; private set; }
    public int ActCount => _definition.Acts.Count;

    /// <param name="report">Receives one line per state change — an act starting, an entity stopping.
    /// Called on the tick thread, so it must not block.</param>
    public ScenarioExecutor(CarlaClient client, TrafficManager.TrafficManager traffic,
                            ScenarioDefinition definition, RoadNetwork network,
                            Action<string>? report = null)
    {
        _client = client;
        _traffic = traffic;
        _definition = definition;
        _network = network;
        _report = report;

        foreach (var act in definition.Acts)
            _acts[act.Name] = new ActRuntime();

        SpawnEntities(network);

        _tickHandler = OnTick;
        _client.OnTick += _tickHandler;
    }

    // ── placement ─────────────────────────────────────────────────────────────

    private void SpawnEntities(RoadNetwork network)
    {
        IReadOnlyList<ActorDefinition> catalogue =
            _client.GetActorDefinitionsAsync().GetAwaiter().GetResult();

        foreach (ScenarioEntity entity in _definition.Entities)
        {
            Transform surface = network.Resolve(entity.InitialPosition, heightAboveSurface: 0.0);
            var clearance = new Transform(
                new Location(surface.Location.X, surface.Location.Y,
                             (float)(surface.Location.Z + SpawnClearanceMetres)),
                surface.Rotation);

            ActorDescription description = BlueprintChooser.Describe(catalogue, entity);

            Actor actor;
            try
            {
                actor = _client.SpawnActorAsync(description, clearance).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Placing one entity and not another would run a different scenario from the one
                // authored, so nothing is left behind to run half of it.
                DestroyPlacedEntities();
                throw new ScenarioParseException(
                    $"entity '{entity.Name}' could not be placed on road {entity.InitialPosition.RoadId} " +
                    $"at s={entity.InitialPosition.S:0.##}: {ex.Message}");
            }

            WarnIfOccupied(entity.Name, surface, actor.Id);
            Transform resting = SetDown(actor, surface);
            _entities[entity.Name] = new EntityRuntime { Definition = entity, Actor = actor, Pose = resting };

            _report?.Invoke($"placed {entity.Name} as {description.Id} on road " +
                            $"{entity.InitialPosition.RoadId} lane {entity.InitialPosition.LaneId}");
        }
    }

    /// Distance within which another vehicle will obstruct one being placed. A stationary vehicle this
    /// close ahead is enough for the Traffic Manager to hold the new one at a standstill.
    private const double ObstructionRadiusMetres = 8.0;

    /// <summary>
    /// Reports any vehicle already standing where an entity is being placed.
    ///
    /// The usual cause is a previous run whose vehicles have not gone yet, or wreckage left by one that
    /// went wrong. Either way the entity will sit motionless behind it, and saying so here turns a
    /// puzzling stall into a stated fact.
    /// </summary>
    private void WarnIfOccupied(string name, Transform where, ActorId self)
    {
        var ids = _client.GetCachedActorIds();
        if (ids.Count == 0) return;

        // Only vehicles are reported. The spectator and the roadside furniture sit near the carriageway
        // by their nature and obstruct nothing, so naming them would bury the one case that matters.
        var records = _client.GetActorsByIdAsync(ids).GetAwaiter().GetResult();
        foreach (Actor other in records)
        {
            if (other.Id == self) continue;
            if (other.Description.Id is not { } typeId
                || !typeId.StartsWith("vehicle.", StringComparison.Ordinal)) continue;

            var snapshot = _client.GetActorSnapshot(other.Id);
            if (snapshot is null) continue;
            Location p = snapshot.Transform.Location;
            double dx = p.X - where.Location.X, dy = p.Y - where.Location.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= ObstructionRadiusMetres)
                _report?.Invoke(
                    $"{name} is placed {distance:0.0} m from vehicle {other.Id} ({typeId}), " +
                    "which will hold it stationary");
        }
    }

    /// <summary>
    /// Sets a vehicle down so it rests on the road rather than falling onto it.
    ///
    /// Placing a vehicle above the surface and letting physics settle it makes the outcome depend on
    /// timing: it may settle, or be pinned, or be pushed aside, and several vehicles placed together
    /// resolve differently from one placed alone. Computing the resting height from the vehicle's own
    /// bounding box removes the fall, and with it the variability.
    /// </summary>
    private Transform SetDown(Actor actor, Transform surface)
    {
        BoundingBox box = actor.BoundingBox;

        // The box centre is offset from the actor's origin, so the lowest point is the origin plus that
        // offset less the half-height. Placing that on the surface is what "resting" means.
        double restingZ = surface.Location.Z - box.Location.Z + box.Extent.Z;
        var resting = new Transform(
            new Location(surface.Location.X, surface.Location.Y, (float)restingZ),
            surface.Rotation);

        _client.SetActorTransformAsync(actor.Id, resting).GetAwaiter().GetResult();
        Detached(_client.SetActorTargetVelocityAsync(actor.Id, new Vector3D(0f, 0f, 0f)));
        return resting;
    }

    // ── per-tick ──────────────────────────────────────────────────────────────

    private void OnTick(TickTimestamp tick)
    {
        if (!Running) return;
        if (double.IsNaN(_startElapsedSeconds)) _startElapsedSeconds = tick.ElapsedSeconds;

        double now = tick.ElapsedSeconds - _startElapsedSeconds;
        ElapsedSeconds = now;

        try
        {
            InitialisePending();
            AdvanceEntities(now, tick.DeltaSeconds);
            AdvanceActs(now);
            ReportProgress(now);

            if (_definition.StopTrigger is { } stop && Fired(stop, now, actorNames: null))
            {
                _report?.Invoke($"scenario complete at {now:0.0}s");
                Stop();
            }
        }
        catch (Exception ex)
        {
            _report?.Invoke($"scenario aborted: {ex.Message}");
            Stop();
        }
    }

    /// <summary>
    /// Registers each placed vehicle and gives it its route and speed, on the first tick after it was
    /// placed rather than during placement.
    ///
    /// A vehicle is not visible to the Traffic Manager until the world has reported it, so commanding
    /// one at the moment it is created leaves it registered but unattended until something else
    /// disturbs it.
    /// </summary>
    private void InitialisePending()
    {
        foreach (EntityRuntime e in _entities.Values)
        {
            if (e.Initialised || e.Actor is null) continue;
            if (_client.GetActorSnapshot(e.Actor.Value.Id) is null) continue;   // not reported yet

            Register(e);
            if (e.Definition.InitialRoute is { Count: > 0 } waypoints)
                _traffic.SetCustomPath(e.Actor.Value, ForwardPath(e.Pose, waypoints, e.Definition.Name), true);
            if (e.Definition.InitialSpeedMps is { } initial && initial > 0.0)
                Command(e, initial);

            e.Initialised = true;
        }
    }

    private void AdvanceEntities(double now, double delta)
    {
        foreach (EntityRuntime e in _entities.Values)
        {
            if (e.Actor is null) continue;

            // Standstill is measured from the world's own report of the vehicle rather than from what
            // was commanded, so a vehicle that fails to stop does not satisfy a dwell trigger.
            var snapshot = _client.GetActorSnapshot(e.Actor.Value.Id);
            if (snapshot is not null)
            {
                Vector3D v = snapshot.Velocity;
                double speed = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
                e.StationarySeconds = speed <= StationaryThresholdMps ? e.StationarySeconds + delta : 0.0;
            }

            if (e.Transition is not { } transition) continue;

            if (now < transition.EndTime)
            {
                double fraction = transition.Seconds <= 0.0
                    ? 1.0
                    : (now - transition.StartTime) / transition.Seconds;
                Command(e, transition.From + (transition.To - transition.From) * Math.Clamp(fraction, 0.0, 1.0));
                continue;
            }

            e.Transition = null;
            if (transition.To <= 0.0)
            {
                Hold(e);
                _report?.Invoke($"{e.Definition.Name} stopped at {now:0.0}s");
            }
            else
            {
                Command(e, transition.To);
            }
        }
    }

    private const double ProgressIntervalSeconds = 5.0;
    private double _nextProgressAt = ProgressIntervalSeconds;

    private void ReportProgress(double now)
    {
        if (_report is null || now < _nextProgressAt) return;
        _nextProgressAt = now + ProgressIntervalSeconds;

        var parts = new List<string>();
        foreach (var (name, e) in _entities)
        {
            if (e.Actor is null) continue;
            var snapshot = _client.GetActorSnapshot(e.Actor.Value.Id);
            if (snapshot is null) { parts.Add($"{name} unseen"); continue; }
            Vector3D v = snapshot.Velocity;
            parts.Add($"{name} {Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z):0.0}");
        }
        if (parts.Count > 0) _report($"{now:0.0}s speeds m/s: {string.Join("  ", parts)}");
    }

    private void AdvanceActs(double now)
    {
        foreach (ScenarioAct act in _definition.Acts)
        {
            ActRuntime state = _acts[act.Name];

            if (state.Phase == ActPhase.Pending)
            {
                if (!Fired(act.StartTrigger, now, act.ActorNames)) continue;
                state.Phase = ActPhase.Running;
                _report?.Invoke($"{act.Name} started at {now:0.0}s");
            }

            if (state.Phase != ActPhase.Running) continue;

            foreach (ScenarioEvent ev in act.Events)
            {
                if (state.Fired.Contains(ev.Name)) continue;
                if (!Fired(ev.StartTrigger, now, act.ActorNames)) continue;
                state.Fired.Add(ev.Name);
                foreach (ScenarioAction action in ev.Actions)
                    state.FinishesAt = Math.Max(state.FinishesAt, Apply(action, act.ActorNames, now));
            }

            // An act is finished once every event it holds has fired and the last of their transitions
            // has run its course. This is what a trigger waiting on the act's completion observes, and
            // it is how the authoring tool chains one authored phase to the next.
            if (state.Fired.Count == act.Events.Count && now >= state.FinishesAt)
            {
                state.Phase = ActPhase.Complete;
                ActsComplete++;
                _report?.Invoke($"{act.Name} complete at {now:0.0}s");
            }
        }
    }

    /// <summary>Applies an action, returning the scenario time at which it will have finished.</summary>
    private double Apply(ScenarioAction action, IReadOnlyList<string> actorNames, double now)
    {
        // A private action follows the actors of the act that owns it; a global action names its own.
        IReadOnlyList<string> targets = action.TargetEntity is { } named ? [named] : actorNames;

        return action switch
        {
            SpeedAction speed => ApplySpeed(speed, targets, now),
            AssignRouteAction route => ApplyRoute(route, targets, now),
            DeleteEntityAction => ApplyDelete(targets, now),
            _ => now,
        };
    }

    private double ApplySpeed(SpeedAction speed, IReadOnlyList<string> actorNames, double now)
    {
        foreach (string name in actorNames)
        {
            if (!_entities.TryGetValue(name, out EntityRuntime? e) || e.Actor is null) continue;

            double from = CurrentSpeed(e);
            if (speed.TargetSpeedMps > 0.0)
            {
                Release(e);
                Register(e);
            }

            e.Transition = new SpeedTransition
            {
                From = from,
                To = speed.TargetSpeedMps,
                StartTime = now,
                Seconds = Math.Max(0.0, speed.TransitionSeconds),
            };
        }

        return now + Math.Max(0.0, speed.TransitionSeconds);
    }

    /// Hands the route to the Traffic Manager as a path to follow. The waypoints say where to go rather
    /// than when to be there, so the vehicle drives the network between them at whatever speed is set.
    private double ApplyRoute(AssignRouteAction route, IReadOnlyList<string> actorNames, double now)
    {
        foreach (string name in actorNames)
        {
            if (!_entities.TryGetValue(name, out EntityRuntime? e) || e.Actor is null) continue;
            var snapshot = _client.GetActorSnapshot(e.Actor.Value.Id);
            if (snapshot is null) continue;
            Register(e);
            _traffic.SetCustomPath(e.Actor.Value, ForwardPath(snapshot.Transform, route.Waypoints, name), true);
        }
        return now;
    }

    private double ApplyDelete(IReadOnlyList<string> actorNames, double now)
    {
        foreach (string name in actorNames)
        {
            if (!_entities.TryGetValue(name, out EntityRuntime? e) || e.Actor is null) continue;
            if (e.Registered)
            {
                try { _traffic.UnregisterVehicles([e.Actor.Value]); } catch { }
                e.Registered = false;
            }
            Detached(_client.DestroyActorAsync(e.Actor.Value.Id));
            e.Actor = null;
            _report?.Invoke($"{name} removed at {now:0.0}s");
        }
        return now;
    }

    /// <summary>
    /// Route waypoints as world points, dropping any leading ones that lie behind the vehicle.
    ///
    /// A route names the roads to traverse, and its first waypoint is customarily the start of the road
    /// the vehicle is already standing on — which is behind it. Followed literally the vehicle turns
    /// around to reach that point, and a convoy does so at the first junction it meets. Only the leading
    /// run is dropped: a waypoint behind the vehicle later in the route is a loop, and legitimate.
    /// </summary>
    private List<Location> ForwardPath(Transform pose, IReadOnlyList<LanePosition> waypoints, string name)
    {
        double yaw = pose.Rotation.Yaw * Math.PI / 180.0;
        double fx = Math.Cos(yaw), fy = Math.Sin(yaw);

        var path = new List<Location>(waypoints.Count);
        int dropped = 0;
        foreach (LanePosition waypoint in waypoints)
        {
            Location point = WorldPoint(waypoint);
            if (path.Count == 0)
            {
                double dx = point.X - pose.Location.X, dy = point.Y - pose.Location.Y;
                if (dx * fx + dy * fy <= 0.0) { dropped++; continue; }
            }
            path.Add(point);
        }

        if (path.Count == 0)
        {
            // Every waypoint is behind: following the route as given would reverse the vehicle, so the
            // route is used unchanged and the reason is reported rather than silently corrected.
            _report?.Invoke($"{name}: every route waypoint lies behind it; using the route as authored");
            foreach (LanePosition waypoint in waypoints) path.Add(WorldPoint(waypoint));
            return path;
        }

        _report?.Invoke(dropped == 0
            ? $"{name} routed via {path.Count} waypoints"
            : $"{name} routed via {path.Count} waypoints ({dropped} behind it, dropped)");
        return path;
    }

    /// World point for a lane position, resolved once and remembered: a route waypoint or an arrival
    /// position is fixed for the life of the scenario, and arrival is tested every tick.
    private Location WorldPoint(LanePosition position)
    {
        if (_resolvedPositions.TryGetValue(position, out Location cached)) return cached;
        Location point = _network.Resolve(position, heightAboveSurface: 0.0).Location;
        _resolvedPositions[position] = point;
        return point;
    }

    private double CurrentSpeed(EntityRuntime e)
    {
        if (e.Actor is null) return 0.0;
        var snapshot = _client.GetActorSnapshot(e.Actor.Value.Id);
        if (snapshot is null) return e.CommandedMps;
        Vector3D v = snapshot.Velocity;
        return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }

    // ── trigger evaluation ────────────────────────────────────────────────────

    private bool Fired(ScenarioTrigger trigger, double now, IReadOnlyList<string>? actorNames)
        => trigger.Kind switch
        {
            TriggerKind.Immediately => true,
            TriggerKind.SimulationTime => now > trigger.Value,
            TriggerKind.StoryboardElementState =>
                trigger.ElementRef is { } reference
                && _acts.TryGetValue(reference, out ActRuntime? other)
                && other.Phase == ActPhase.Complete,
            TriggerKind.StandStill => StationaryLongEnough(trigger, actorNames),
            TriggerKind.ReachPosition => Arrived(trigger, actorNames),
            _ => false,
        };

    private bool StationaryLongEnough(ScenarioTrigger trigger, IReadOnlyList<string>? actorNames)
    {
        string? who = trigger.EntityRef ?? (actorNames is { Count: > 0 } ? actorNames[0] : null);
        return who is not null
            && _entities.TryGetValue(who, out EntityRuntime? e)
            && e.StationarySeconds >= trigger.Value;
    }

    private bool Arrived(ScenarioTrigger trigger, IReadOnlyList<string>? actorNames)
    {
        if (trigger.Position is not { } destination) return false;
        string? who = trigger.EntityRef ?? (actorNames is { Count: > 0 } ? actorNames[0] : null);
        if (who is null || !_entities.TryGetValue(who, out EntityRuntime? e) || e.Actor is null) return false;

        var snapshot = _client.GetActorSnapshot(e.Actor.Value.Id);
        if (snapshot is null) return false;

        Location here = snapshot.Transform.Location;
        Location target = WorldPoint(destination);
        double dx = here.X - target.X, dy = here.Y - target.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= trigger.Value;
    }

    // ── vehicle commands ──────────────────────────────────────────────────────

    private void Register(EntityRuntime e)
    {
        if (e.Registered || e.Actor is null) return;
        Detached(_client.SetActorAutopilotAsync(e.Actor.Value.Id, true));
        _traffic.RegisterVehicles([e.Actor.Value]);
        e.Registered = true;
    }

    private void Command(EntityRuntime e, double speedMps)
    {
        if (e.Actor is null) return;
        double commanded = Math.Max(MinimumCommandedSpeedMps, speedMps);
        e.CommandedMps = commanded;
        _traffic.SetDesiredSpeed(e.Actor.Value, (float)(commanded * MpsToTrafficManagerUnits));
    }

    /// Takes the vehicle out of Traffic Manager control and holds it on the brakes. A registered
    /// vehicle receives a fresh control command every tick which would otherwise overwrite the brake,
    /// and an unregistered one is outside the idle-removal population, so a dwell of any length keeps
    /// its vehicle.
    private void Hold(EntityRuntime e)
    {
        if (e.Actor is null || e.Held) return;
        if (e.Registered)
        {
            _traffic.UnregisterVehicles([e.Actor.Value]);
            Detached(_client.SetActorAutopilotAsync(e.Actor.Value.Id, false));
            e.Registered = false;
        }
        Detached(_client.ApplyControlToVehicleAsync(
            e.Actor.Value.Id, new VehicleControl(0f, 0f, 1f, false, true, false, 0)));
        e.Held = true;
        e.CommandedMps = 0.0;
    }

    private void Release(EntityRuntime e)
    {
        if (e.Actor is null || !e.Held) return;
        Detached(_client.ApplyControlToVehicleAsync(
            e.Actor.Value.Id, new VehicleControl(0f, 0f, 0f, false, false, false, 0)));
        e.Held = false;
    }

    /// Commands are issued without awaiting them: an action must take effect on the tick that
    /// triggered it, and waiting on a round trip would delay the tick for everything else.
    private static void Detached(Task task)
        => task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

    // ── lifetime ──────────────────────────────────────────────────────────────

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _client.OnTick -= _tickHandler;
        DestroyPlacedEntities();
    }

    /// <summary>
    /// Removes every vehicle this scenario placed, and waits for the removals to take effect.
    ///
    /// Waiting matters: a scenario started immediately after another would otherwise be placed into a
    /// world still holding the previous run's vehicles, and a vehicle left across the carriageway blocks
    /// the one placed behind it. That presents as a scenario which runs correctly sometimes and stalls
    /// other times, with the difference being what the last run left behind.
    /// </summary>
    private void DestroyPlacedEntities()
    {
        var removals = new List<Task>();
        foreach (EntityRuntime e in _entities.Values)
        {
            if (e.Actor is null) continue;
            if (e.Registered)
            {
                try { _traffic.UnregisterVehicles([e.Actor.Value]); } catch { }
                e.Registered = false;
            }
            removals.Add(_client.DestroyActorAsync(e.Actor.Value.Id));
            e.Actor = null;
        }

        if (removals.Count == 0) return;
        try
        {
            // Bounded: a scenario ending must not hang on an unresponsive server, and this runs on the
            // tick thread when a scenario reaches its own stop condition.
            Task.WaitAll([.. removals], TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _report?.Invoke($"not every vehicle could be removed: {ex.Message}");
        }
    }

    public void Dispose() => Stop();

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Name}: {ElapsedSeconds:0.0}s, {ActsComplete}/{ActCount} acts complete");

    // ── runtime state ─────────────────────────────────────────────────────────

    private enum ActPhase { Pending, Running, Complete }

    private sealed class ActRuntime
    {
        public ActPhase Phase = ActPhase.Pending;
        public readonly HashSet<string> Fired = new();
        public double FinishesAt;
    }

    private sealed class EntityRuntime
    {
        public required ScenarioEntity Definition { get; init; }
        public required Transform Pose { get; init; }
        public Actor? Actor;
        public bool Initialised;
        public bool Registered;
        public bool Held;
        public double CommandedMps;
        public double StationarySeconds;
        public SpeedTransition? Transition;
    }

    private sealed class SpeedTransition
    {
        public required double From { get; init; }
        public required double To { get; init; }
        public required double StartTime { get; init; }
        public required double Seconds { get; init; }
        public double EndTime => StartTime + Seconds;
    }
}
