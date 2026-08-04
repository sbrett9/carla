namespace CarlaNet.Scenario;

/// <summary>
/// A parsed OpenSCENARIO storyboard, reduced to what an executor needs.
///
/// This is deliberately not a faithful object model of the standard. It carries the constructs an
/// authored pattern actually uses, and rejects the rest at parse time rather than silently ignoring it,
/// so a scenario never executes as something other than what it says.
/// </summary>
public sealed class ScenarioDefinition
{
    public required string Name { get; init; }
    public required string Version { get; init; }

    /// The road network the scenario was authored against, as named in the file. Informational: the
    /// executor runs against whatever world is loaded, and the caller is responsible for their agreeing.
    public string? RoadNetworkFile { get; init; }

    /// Values declared by the storyboard, after any caller overrides were applied. A storyboard that
    /// declares its speeds and distances this way can be run repeatedly with different values, which is
    /// how one authored pattern becomes many.
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    public required IReadOnlyList<ScenarioEntity> Entities { get; init; }
    public required IReadOnlyList<ScenarioAct> Acts { get; init; }

    /// Ends the scenario. Null means it runs until stopped.
    public ScenarioTrigger? StopTrigger { get; init; }
}

/// <summary>An actor the scenario places and drives.</summary>
public sealed class ScenarioEntity
{
    public required string Name { get; init; }

    /// OpenSCENARIO vehicle category ("car", "truck", "bus", …), used to narrow blueprint selection.
    public required string Category { get; init; }

    /// Authoring-tool hint at the intended vehicle ("sedan", "truck"), where the file carries one.
    /// Advisory: it names a template in the authoring tool rather than a blueprint in this simulator,
    /// so it is honoured only when it happens to match one.
    public string? TemplateHint { get; init; }

    public string? Colour { get; init; }

    public required LanePosition InitialPosition { get; init; }

    /// Speed the entity is placed with. Null leaves it stationary until an action sets one.
    public double? InitialSpeedMps { get; init; }

    /// Route the entity is placed on, where the storyboard assigns one at initialisation. Without it a
    /// vehicle drives wherever the road network takes it, choosing at junctions, which is a different
    /// scenario from the one authored.
    public IReadOnlyList<LanePosition>? InitialRoute { get; init; }
}

/// <summary>A position on the road network, as OpenSCENARIO LanePosition.</summary>
/// <param name="RoadId">OpenDRIVE road identifier.</param>
/// <param name="LaneId">Lane within that road; negative to the right of the reference line.</param>
/// <param name="S">Distance along the road's reference line, in metres.</param>
/// <param name="Offset">Lateral offset from the lane centre, in metres.</param>
public readonly record struct LanePosition(int RoadId, int LaneId, double S, double Offset);

/// <summary>
/// A unit of the storyboard: a set of entities, a trigger that starts it, and the events it then runs.
///
/// The authoring tool emits one act per authored phase and carries the sequencing on the act's start
/// trigger, leaving the events inside to fire immediately. The executor does not depend on that
/// arrangement — it evaluates both levels — but it is why act-level triggers carry the timing in
/// practice.
/// </summary>
public sealed class ScenarioAct
{
    public required string Name { get; init; }
    public required ScenarioTrigger StartTrigger { get; init; }
    public required IReadOnlyList<string> ActorNames { get; init; }
    public required IReadOnlyList<ScenarioEvent> Events { get; init; }
}

public sealed class ScenarioEvent
{
    public required string Name { get; init; }
    public required ScenarioTrigger StartTrigger { get; init; }

    /// An event may carry several actions — assigning a route and setting a speed together, for
    /// instance — and they all apply when it fires.
    public required IReadOnlyList<ScenarioAction> Actions { get; init; }
}

public enum TriggerKind
{
    /// Fires on the first evaluation.
    Immediately,

    /// Fires when scenario-relative time passes a threshold.
    SimulationTime,

    /// Fires when a named act reaches a state, normally completion. Gives relative sequencing: a phase
    /// begins when the previous one finishes, without the author computing absolute times.
    StoryboardElementState,

    /// Fires once an entity has been stationary for a duration. This is what expresses a dwell.
    StandStill,

    /// Fires once an entity comes within a tolerance of a position.
    ReachPosition,
}

/// <summary>
/// A condition gating an act or an event.
///
/// Only the kinds an authored pattern needs are represented. Anything else is rejected at parse time:
/// a trigger quietly treated as "fires immediately" would run a scenario that does not match what was
/// authored, which is worse than refusing to run it.
/// </summary>
public sealed class ScenarioTrigger
{
    public required TriggerKind Kind { get; init; }

    /// Seconds for <see cref="TriggerKind.SimulationTime"/>, seconds stationary for
    /// <see cref="TriggerKind.StandStill"/>, metres of tolerance for
    /// <see cref="TriggerKind.ReachPosition"/>.
    public double Value { get; init; }

    /// Act named by <see cref="TriggerKind.StoryboardElementState"/>.
    public string? ElementRef { get; init; }

    /// Entity observed by <see cref="TriggerKind.StandStill"/> and
    /// <see cref="TriggerKind.ReachPosition"/>. Null means the act's own actors.
    public string? EntityRef { get; init; }

    /// Destination for <see cref="TriggerKind.ReachPosition"/>. Resolved against the loaded network when
    /// the scenario runs rather than at parse time, so parsing needs no world.
    public LanePosition? Position { get; init; }

    public static ScenarioTrigger Immediate() => new() { Kind = TriggerKind.Immediately };
}

/// <summary>Base of the actions an event can apply.</summary>
public abstract class ScenarioAction
{
    /// Entity this action applies to. Null means the actors of the act that owns it — the usual case
    /// for a private action. A global action names its own target and does not follow the act's actors.
    public string? TargetEntity { get; init; }
}

/// <summary>
/// Brings an entity to a target speed over a period.
///
/// A target of zero is a stop, and a stop is not merely the absence of throttle: the Traffic Manager
/// emits neither throttle nor brake at a zero target, so a vehicle commanded that way coasts and cannot
/// be placed. The executor therefore ramps the target down and then holds the vehicle on its brakes
/// outside Traffic Manager control, which is also what keeps a long dwell from being culled as stuck.
/// </summary>
public sealed class SpeedAction : ScenarioAction
{
    public required double TargetSpeedMps { get; init; }

    /// Seconds over which to reach the target, from the OpenSCENARIO dynamics. Zero applies it at once.
    public double TransitionSeconds { get; init; }
}

/// <summary>
/// Sends an entity along a sequence of waypoints.
///
/// The waypoints are a route rather than a trajectory: they say where to go, not when to be there, so
/// the vehicle follows the road network between them under Traffic Manager control at whatever speed a
/// speed action has set.
/// </summary>
public sealed class AssignRouteAction : ScenarioAction
{
    public required IReadOnlyList<LanePosition> Waypoints { get; init; }
}

/// <summary>Removes an entity from the world, ending its part in the scenario.</summary>
public sealed class DeleteEntityAction : ScenarioAction
{
}
