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

    private readonly CarlaClient _client;
    private readonly TrafficManager.TrafficManager _traffic;
    private readonly ScenarioDefinition _definition;
    private readonly Action<string>? _report;

    private readonly Dictionary<string, EntityRuntime> _entities = new();
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
            Transform pose = network.Resolve(entity.InitialPosition);
            ActorDescription description = BlueprintChooser.Describe(catalogue, entity);

            Actor actor;
            try
            {
                actor = _client.SpawnActorAsync(description, pose).GetAwaiter().GetResult();
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

            var runtime = new EntityRuntime { Definition = entity, Actor = actor };
            _entities[entity.Name] = runtime;

            if (entity.InitialSpeedMps is { } initial && initial > 0.0)
            {
                Register(runtime);
                Command(runtime, initial);
            }

            _report?.Invoke($"placed {entity.Name} as {description.Id} on road " +
                            $"{entity.InitialPosition.RoadId} lane {entity.InitialPosition.LaneId}");
        }
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
            AdvanceEntities(now, tick.DeltaSeconds);
            AdvanceActs(now);

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
                state.FinishesAt = Math.Max(state.FinishesAt, Apply(ev.Action, act.ActorNames, now));
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
        if (action is not SpeedAction speed) return now;

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
            _ => false,
        };

    private bool StationaryLongEnough(ScenarioTrigger trigger, IReadOnlyList<string>? actorNames)
    {
        string? who = trigger.EntityRef ?? (actorNames is { Count: > 0 } ? actorNames[0] : null);
        return who is not null
            && _entities.TryGetValue(who, out EntityRuntime? e)
            && e.StationarySeconds >= trigger.Value;
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

    private void DestroyPlacedEntities()
    {
        foreach (EntityRuntime e in _entities.Values)
        {
            if (e.Actor is null) continue;
            if (e.Registered)
            {
                try { _traffic.UnregisterVehicles([e.Actor.Value]); } catch { }
            }
            Detached(_client.DestroyActorAsync(e.Actor.Value.Id));
            e.Actor = null;
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
        public Actor? Actor;
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
