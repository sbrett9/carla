namespace CarlaNet.Scenario;

/// <summary>
/// A parsed OpenSCENARIO storyboard, reduced to what an executor needs.
///
/// This is deliberately not a faithful object model of the standard. It carries the constructs an
/// authored pattern actually uses — entities placed on the road network, acts gated by a trigger, and
/// speed changes — and rejects the rest at parse time rather than silently ignoring it, so a scenario
/// never executes as something other than what it says.
/// </summary>
public sealed class ScenarioDefinition
{
    public required string Name { get; init; }
    public required string Version { get; init; }

    /// The road network the scenario was authored against, as named in the file. Informational: the
    /// executor runs against whatever world is loaded, and the caller is responsible for their agreeing.
    public string? RoadNetworkFile { get; init; }

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
    /// Advisory: blueprint selection falls back to the category when it names nothing available.
    public string? TemplateHint { get; init; }

    public string? Colour { get; init; }

    public required LanePosition InitialPosition { get; init; }

    /// Speed the entity is placed with. Null leaves it stationary until an action sets one.
    public double? InitialSpeedMps { get; init; }
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
    public required ScenarioAction Action { get; init; }
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
    /// <see cref="TriggerKind.StandStill"/>.
    public double Value { get; init; }

    /// Act named by <see cref="TriggerKind.StoryboardElementState"/>.
    public string? ElementRef { get; init; }

    /// Entity observed by <see cref="TriggerKind.StandStill"/>. Null means the act's own actors.
    public string? EntityRef { get; init; }

    public static ScenarioTrigger Immediate() => new() { Kind = TriggerKind.Immediately };
}

/// <summary>Base of the actions an event can apply.</summary>
public abstract class ScenarioAction
{
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
