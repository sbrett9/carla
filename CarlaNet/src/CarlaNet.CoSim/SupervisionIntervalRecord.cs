using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim;

/// <summary>Where one of the plan's intervals stands in a run.</summary>
public enum SupervisionIntervalStatus
{
    /// <summary>Not opened: nothing that commits it has happened in the run, as far as it was watched.</summary>
    Planned,

    /// <summary>Open: committed, or in force on its declared seconds, and not yet closed.</summary>
    Open,

    /// <summary>Closed, for the reason <see cref="SupervisionIntervalRecord.ClosedBy"/> gives.</summary>
    Closed,
}

/// <summary>A span of simulated seconds over which a plan subject SUMO had was not drawn.</summary>
/// <param name="FromSeconds">The first SUMO frame of the span that drew no body for it.</param>
/// <param name="ToSeconds">The first SUMO frame after it that did, or the instant the span was cut off.</param>
public readonly record struct NotDrawnSpan(double FromSeconds, double ToSeconds);

/// <summary>
/// One of the plan's intervals as the run has bound it: the row it is, what its author declared, and the
/// onsets, the close and the drawing gaps the run filled in beside them.
/// </summary>
/// <remarks>
/// <para><b>A copy of a row and what the run did to it, never a row of its own.</b> Every field from
/// <see cref="InstanceId"/> to <see cref="DeclaredDurationSeconds"/> is the plan's, copied; the rest is
/// what the binder bound. There is exactly one record per interval the plan declares, so two runs of one
/// scenario hold the same <c>(instance, participant, phase)</c> triples, differing only in the times and
/// the outcomes (06 D6.8).</para>
///
/// <para><b>An onset that did not happen is null, never substituted</b> (06 D6.4): an unanchored interval
/// has no committed onset, an interval over a <c>duration</c> stop no declared start, a session with no
/// world no observed onset, and a phase entered before the window no committed one.</para>
///
/// <para><b>Every instant is the bridge's.</b> A committed onset is the clock TraCI reported with the
/// step whose event list announced it; where the binder read it back from SUMO for an interval that began
/// before the window, it is SUMO's own stamp plus one SUMO step, which is that same clock
/// (<see cref="CarlaNet.Sumo.SumoStepEvents"/>). An observed onset is the instant of a rendered frame.</para>
/// </remarks>
public sealed record SupervisionIntervalRecord
{
    /// <summary>The instance, as the plan names it.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The participant whose phase it is, its SUMO id.</summary>
    public required string EntityId { get; init; }

    /// <summary>The phase.</summary>
    public required string Phase { get; init; }

    /// <summary>The participant's role in the instance.</summary>
    public required string Role { get; init; }

    /// <summary>What the instance asserts: annotated or nominal.</summary>
    public required SupervisionState Supervision { get; init; }

    /// <summary>The instance's labels.</summary>
    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>The events that commit it, as the plan resolved them; null for an interval declared in civil time.</summary>
    public required IntervalAnchor? Anchor { get; init; }

    /// <summary>The declared start, simulated seconds; null where the declaration names no instant.</summary>
    public required double? DeclaredStartSeconds { get; init; }

    /// <summary>The declared end, simulated seconds; null where none is declared.</summary>
    public required double? DeclaredEndSeconds { get; init; }

    /// <summary>The declared length, where the declaration is one.</summary>
    public required double? DeclaredDurationSeconds { get; init; }

    /// <summary>Where the interval stands.</summary>
    public required SupervisionIntervalStatus Status { get; init; }

    /// <summary>
    /// When SUMO committed its start, by the bridge's clock; null for an unanchored interval, one not
    /// opened, and a phase entered before the window.
    /// </summary>
    public required double? CommittedStartSeconds { get; init; }

    /// <summary>When SUMO committed its end, by the bridge's clock; null where no event ended it.</summary>
    public required double? CommittedEndSeconds { get; init; }

    /// <summary>
    /// The instant of the first rendered frame in the window that showed the start: the frame that first
    /// drew the vehicle, for a departure, or whose applied speed first held at or below 0.15 m/s, for a
    /// stop. Null where none did, where nothing is observed of the anchor, where no world rendered, and
    /// for an interval begun before the window.
    /// </summary>
    public required double? ObservedStartSeconds { get; init; }

    /// <summary>That frame's number; null with <see cref="ObservedStartSeconds"/>.</summary>
    public required ulong? ObservedStartFrame { get; init; }

    /// <summary>
    /// Whether it began before the capture window opened: its start was committed, read back from SUMO, or
    /// declared at an earlier instant, or it is a phase entered before the window, whose instant cannot be
    /// recovered. Nothing is written for the frames before the window.
    /// </summary>
    public required bool BegunBeforeWindow { get; init; }

    /// <summary>Why it closed; null while it is planned or open.</summary>
    public required ClosedBy? ClosedBy { get; init; }

    /// <summary>The instant it closed; null while it is planned or open.</summary>
    public required double? ClosedAtSeconds { get; init; }

    /// <summary>
    /// The spans inside the window over which its participant was open, SUMO had it, and no body drew it:
    /// a gap in drawing under an optional render-set limit, which ends nothing (the owner's ruling of
    /// 2026-10-05).
    /// </summary>
    public required IReadOnlyList<NotDrawnSpan> NotDrawn { get; init; }
}

/// <summary>
/// Something told every interval the supervision binder opens and closes, as it does: a run manifest
/// writing a row for each.
/// </summary>
/// <remarks>
/// A session hands its binder every step observer it is given that also implements this, so such an
/// observer is told of the intervals in the same run it observes, from the first frame, with nothing to
/// subscribe to after the start. Each call comes from the thread that advances the session, inside the
/// binder's own call, and carries a record nothing changes afterwards.
/// </remarks>
public interface ISupervisionIntervalSink
{
    /// <summary>An interval opened.</summary>
    void OnIntervalOpened(SupervisionIntervalRecord interval);

    /// <summary>An interval closed.</summary>
    void OnIntervalClosed(SupervisionIntervalRecord interval);
}
