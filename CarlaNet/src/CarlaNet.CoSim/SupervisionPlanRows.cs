using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim;

// The rows of a supervision plan (06_Truth_And_Annotation.md §8.1), as the scenario compiler writes
// them. Each is built only by reading a plan: its constructors are private, the one that reads its own
// fields and the record's copy, and it has no setter, so nothing in a run can make a row, change one or add one to a plan (06 D6.8).

/// <summary>
/// A pattern instance: an authored assertion about one or more vehicles over intervals (06 §3.4). Every
/// instance has a participant: SUMO reports vehicles, not places, and a label follows the vehicle it is
/// about (06 §3.5).
/// </summary>
public sealed record PatternInstance
{
    private PatternInstance(JsonElement json, SupervisionPlanReading reading, string where)
    {
        InstanceId = reading.Text(json, "instance_id", where);
        Supervision = reading.Core<SupervisionState>(json, "supervision", where, CoreVocabulary.Name,
                                                     "supervision_state");
        Labels = reading.Texts(json, "labels", where);
        Parameters = reading.Values(json, "parameters", where);
        HardNegativeFor = reading.NullableTexts(json, "hard_negative_for", where);
        Counterfactual = reading.NullableObject(json, "counterfactual", where, out JsonElement counterfactual)
            ? Counterfactual.Read(counterfactual, reading, $"{where}.counterfactual")
            : null;
        AoiRefs = reading.Texts(json, "aoi_refs", where);
        Participants = reading.Rows(json, "participants", where, InstanceParticipant.Read);
        Intervals = reading.Rows(json, "intervals", where, PlannedInterval.Read);

        if (Supervision == SupervisionState.Unlabelled)
        {
            reading.Problem($"{where}.supervision", "is unlabelled. An instance asserts something; a subject "
                                                    + "nothing is asserted of is written as an entity's or a "
                                                    + "cohort's state, never as an instance (06 §3.1)");
        }

        if (Participants.Length == 0)
        {
            reading.Problem($"{where}.participants", "is empty. An instance is an assertion about one or more "
                                                     + "vehicles, and a label follows its vehicle (06 §3.5)");
        }

        HashSet<string> participants = [.. Participants.Select(participant => participant.EntityId)];
        foreach ((PlannedInterval interval, int index) in Intervals.Select((interval, index) => (interval, index)))
        {
            if (!participants.Contains(interval.EntityId))
            {
                reading.Problem($"{where}.intervals[{index}].entity_id",
                                $"is '{interval.EntityId}', which is no participant of the instance; an interval "
                                + "is a participant's phase");
            }
        }
    }

    /// <summary><c>&lt;scenario_id&gt;/&lt;name&gt;</c>, the same in every run of the scenario.</summary>
    public string InstanceId { get; }

    /// <summary><see cref="SupervisionState.Annotated"/> or <see cref="SupervisionState.Nominal"/>.</summary>
    public SupervisionState Supervision { get; }

    /// <summary>The terms it asserts, which a nominal instance may carry too (06 D6.31).</summary>
    public ImmutableArray<string> Labels { get; }

    /// <summary>Its parameters, keys its labels' terms declare, with the values the author gave.</summary>
    public ImmutableSortedDictionary<string, JsonElement> Parameters { get; }

    /// <summary>
    /// Its terms' <c>hard_negative_for</c>, copied onto a nominal instance; null where nothing narrows
    /// it, which means unspecified, not a negative for nothing (06 §3.9(d)).
    /// </summary>
    public ImmutableArray<string>? HardNegativeFor { get; }

    /// <summary>What it is to be read against; a pointer, never a supervision write.</summary>
    public Counterfactual? Counterfactual { get; }

    /// <summary>The areas of interest it is defined against.</summary>
    public ImmutableArray<string> AoiRefs { get; }

    /// <summary>Its participants, with their roles; at least one.</summary>
    public ImmutableArray<InstanceParticipant> Participants { get; }

    /// <summary>Its intervals: each participant's phases.</summary>
    public ImmutableArray<PlannedInterval> Intervals { get; }

    internal static PatternInstance Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>One participant of a pattern instance: the vehicle and the role it plays.</summary>
public sealed record InstanceParticipant
{
    private InstanceParticipant(JsonElement json, SupervisionPlanReading reading, string where)
    {
        EntityId = reading.Text(json, "entity_id", where);
        Role = reading.Text(json, "role", where);
        SumoId = reading.Text(json, "sumo_id", where);
    }

    /// <summary>The entity, which defaults to the SUMO vehicle id (06 D6.13).</summary>
    public string EntityId { get; }

    /// <summary>
    /// Its role: <see cref="CoreVocabulary.SubjectRole"/> for the one participant of a one-participant
    /// instance, otherwise an author's namespaced role.
    /// </summary>
    public string Role { get; }

    /// <summary>The vehicle's SUMO id, which the run binds the participant by.</summary>
    public string SumoId { get; }

    internal static InstanceParticipant Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>
/// One declared interval: a participant's phase, with what commits it and what its author declared of it
/// (06 §3.3).
/// </summary>
/// <remarks>
/// The declared fields are what the author's declaration, or the anchoring events, themselves declare:
/// a departure its instant, an <c>until</c> stop's end its instant, a <c>duration</c> stop only its
/// length, so <see cref="DeclaredStartSeconds"/> is null where the start declares no instant, and a
/// consumer must tell that from zero (06 D6.4). The committed and observed onsets are the run's, bound
/// beside these, never written into them.
/// </remarks>
public sealed record PlannedInterval
{
    private PlannedInterval(JsonElement json, SupervisionPlanReading reading, string where)
    {
        EntityId = reading.Text(json, "entity_id", where);
        Phase = reading.Text(json, "phase", where);
        Anchor = reading.NullableObject(json, "anchor", where, out JsonElement anchor)
            ? IntervalAnchor.Read(anchor, reading, $"{where}.anchor")
            : null;
        DeclaredStartSeconds = reading.NullableNumber(json, "declared_start_s", where);
        DeclaredStartCivil = reading.NullableText(json, "declared_start_civil", where);
        DeclaredEndSeconds = reading.NullableNumber(json, "declared_end_s", where);
        DeclaredEndCivil = reading.NullableText(json, "declared_end_civil", where);
        DeclaredDurationSeconds = reading.NullableNumber(json, "declared_duration_s", where);

        if (Anchor is null && DeclaredStartSeconds is null)
        {
            reading.Problem(where, "has neither an anchor nor a declared start; an interval is declared by "
                                   + "its civil begin or by the events that commit it (06 §3.3)");
        }
    }

    /// <summary>The participant whose phase it is.</summary>
    public string EntityId { get; }

    /// <summary>The phase, an author's term.</summary>
    public string Phase { get; }

    /// <summary>The events of its participant that commit it; null for an interval declared in civil time.</summary>
    public IntervalAnchor? Anchor { get; }

    /// <summary>The declared start in simulated seconds; null where the start declares no instant.</summary>
    public double? DeclaredStartSeconds { get; }

    /// <summary>The declared start as the epoch names it in civil time; null with <see cref="DeclaredStartSeconds"/>.</summary>
    public string? DeclaredStartCivil { get; }

    /// <summary>The declared end in simulated seconds; null where none is declared.</summary>
    public double? DeclaredEndSeconds { get; }

    /// <summary>The declared end in civil time; null with <see cref="DeclaredEndSeconds"/>.</summary>
    public string? DeclaredEndCivil { get; }

    /// <summary>The declared length, where the declaration is one; null otherwise.</summary>
    public double? DeclaredDurationSeconds { get; }

    internal static PlannedInterval Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>
/// The events of an interval's participant that commit its start and, where given, its end, resolved
/// by the compiler against the vehicle's own stops and compiled route (06 §3.3, 07 check 58).
/// </summary>
public sealed record IntervalAnchor
{
    private IntervalAnchor(JsonElement json, SupervisionPlanReading reading, string where)
    {
        // A plan missing the start is refused, so the null is never seen outside the reader.
        Start = reading.Object(json, "start", where, out JsonElement start)
            ? AnchorPoint.Read(start, reading, $"{where}.start")
            : null!;
        End = reading.NullableObject(json, "end", where, out JsonElement end)
            ? AnchorPoint.Read(end, reading, $"{where}.end")
            : null;
    }

    /// <summary>The event that commits the start.</summary>
    public AnchorPoint Start { get; }

    /// <summary>The event that commits the end; null where the interval closes otherwise.</summary>
    public AnchorPoint? End { get; }

    internal static IntervalAnchor Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>
/// One anchoring event, with what the run needs to recognise it: a stop's lane and end position, or a
/// phase's place in the compiled route.
/// </summary>
/// <remarks>
/// Spelled as the core's interval anchor names it (<see cref="AnchorEvent"/>), a stop
/// or a phase with its index counted from 0: <c>depart</c>, <c>stop:0</c>, <c>stop_end:0</c>,
/// <c>phase:2</c>.
/// </remarks>
public sealed record AnchorPoint
{
    private AnchorPoint(JsonElement json, SupervisionPlanReading reading, string where)
    {
        Spelled = reading.Text(json, "event", where);
        (Event, Index) = Parse(Spelled, reading, $"{where}.event");
        switch (Event)
        {
            case AnchorEvent.Stop or AnchorEvent.StopEnd:
                Lane = reading.Text(json, "lane", where);
                EndPositionMetres = reading.Number(json, "end_pos_m", where);
                break;
            case AnchorEvent.Phase:
                RouteIndex = reading.Integer(json, "route_index", where);
                Edge = reading.Text(json, "edge", where);
                break;
        }
    }

    /// <summary>The event as the plan spells it: <c>stop:0</c>.</summary>
    public string Spelled { get; }

    /// <summary>The kind of event: insertion, arriving at a stop, leaving one, or entering a phase.</summary>
    public AnchorEvent Event { get; }

    /// <summary>The stop's or the phase's index, counted from 0; null for <see cref="AnchorEvent.Depart"/>.</summary>
    public int? Index { get; }

    /// <summary>The stop's lane; null for a departure or a phase.</summary>
    public string? Lane { get; }

    /// <summary>The stop's end position on its lane, in metres; null for a departure or a phase.</summary>
    public double? EndPositionMetres { get; }

    /// <summary>The index in the compiled route at which the phase is entered; null except for a phase.</summary>
    public int? RouteIndex { get; }

    /// <summary>The edge at that index; null except for a phase.</summary>
    public string? Edge { get; }

    internal static AnchorPoint Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);

    /// <summary>The event and its index: a core anchor's name, followed by <c>:&lt;i&gt;</c> except for a departure.</summary>
    private static (AnchorEvent Event, int? Index) Parse(string spelled, SupervisionPlanReading reading,
                                                        string where)
    {
        int colon = spelled.IndexOf(':', StringComparison.Ordinal);
        string kind = colon < 0 ? spelled : spelled[..colon];
        string? number = colon < 0 ? null : spelled[(colon + 1)..];
        if (reading.TryCore<AnchorEvent>(kind, CoreVocabulary.Name, out AnchorEvent anchor))
        {
            bool indexed = anchor != AnchorEvent.Depart;
            if (!indexed && number is null)
            {
                return (anchor, null);
            }

            if (indexed && number is { Length: > 0 } && number.All(char.IsAsciiDigit)
                && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                return (anchor, index);
            }
        }

        if (spelled.Length > 0)
        {
            reading.Problem(where, $"is '{spelled}', which is none of "
                                   + string.Join(", ", Enum.GetValues<AnchorEvent>().Select(value =>
                                       CoreVocabulary.Name(value) + (value == AnchorEvent.Depart ? string.Empty : ":<i>")))
                                   + ", the core's interval anchors, a stop's or a phase's index counted from 0");
        }

        return (AnchorEvent.Depart, null);
    }
}

/// <summary>
/// A recurring series: a rota read as a cadence, one slot per occasion a vehicle realises, its members
/// in one declared state (06 §3.4, D6.5). An occasion the rota skips writes no trip and no slot: there is
/// no vehicle for a label to follow (06 §3.5).
/// </summary>
public sealed record RecurringSeries
{
    private RecurringSeries(JsonElement json, SupervisionPlanReading reading, string where)
    {
        SeriesId = reading.Text(json, "series_id", where);
        RotaRef = reading.Text(json, "rota_ref", where);
        Cadence = reading.Core<CadenceForm>(json, "cadence", where, CoreVocabulary.Name, "cadence");
        MemberRole = reading.Text(json, "member_role", where);
        Supervision = reading.Core<SupervisionState>(json, "supervision", where, CoreVocabulary.Name,
                                                     "supervision_state");
        Labels = reading.Texts(json, "labels", where);
        Parameters = reading.Values(json, "parameters", where);
        HardNegativeFor = reading.NullableTexts(json, "hard_negative_for", where);
        Slots = reading.Rows(json, "slots", where, SeriesSlot.Read);
    }

    /// <summary>The series' id.</summary>
    public string SeriesId { get; }

    /// <summary>The rota it reads.</summary>
    public string RotaRef { get; }

    /// <summary>How it lists its occasions.</summary>
    public CadenceForm Cadence { get; }

    /// <summary>The role every member plays.</summary>
    public string MemberRole { get; }

    /// <summary>The members' state.</summary>
    public SupervisionState Supervision { get; }

    /// <summary>The members' labels.</summary>
    public ImmutableArray<string> Labels { get; }

    /// <summary>The members' parameters.</summary>
    public ImmutableSortedDictionary<string, JsonElement> Parameters { get; }

    /// <summary>Its terms' <c>hard_negative_for</c> when nominal; null where nothing narrows it.</summary>
    public ImmutableArray<string>? HardNegativeFor { get; }

    /// <summary>One slot per realised occasion, by declared start.</summary>
    public ImmutableArray<SeriesSlot> Slots { get; }

    internal static RecurringSeries Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>One occasion of a recurring series, and the vehicle that realises it.</summary>
public sealed record SeriesSlot
{
    private SeriesSlot(JsonElement json, SupervisionPlanReading reading, string where)
    {
        SlotKey = reading.Text(json, "slot_key", where);
        AoiRef = reading.Text(json, "aoi_ref", where);
        DeclaredStartSeconds = reading.Number(json, "declared_start_s", where);
        DeclaredStartCivil = reading.Text(json, "declared_start_civil", where);
        DeclaredEndSeconds = reading.NullableNumber(json, "declared_end_s", where);
        DeclaredEndCivil = reading.NullableText(json, "declared_end_civil", where);
        EntityId = reading.Text(json, "entity_id", where);
    }

    /// <summary>The occasion's key.</summary>
    public string SlotKey { get; }

    /// <summary>The area it is sited at.</summary>
    public string AoiRef { get; }

    /// <summary>Its declared start, in simulated seconds.</summary>
    public double DeclaredStartSeconds { get; }

    /// <summary>Its declared start in civil time.</summary>
    public string DeclaredStartCivil { get; }

    /// <summary>Its declared end, in simulated seconds; null where it has none.</summary>
    public double? DeclaredEndSeconds { get; }

    /// <summary>Its declared end in civil time; null with <see cref="DeclaredEndSeconds"/>.</summary>
    public string? DeclaredEndCivil { get; }

    /// <summary>The entity that realises it: the rota entry's vehicle, its SUMO id.</summary>
    public string EntityId { get; }

    internal static SeriesSlot Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>
/// A flow's supervision: every vehicle it emits, annotated for its whole life or unlabelled, and never
/// nominal (06 D6.2).
/// </summary>
public sealed record CohortSupervision
{
    private CohortSupervision(JsonElement json, SupervisionPlanReading reading, string where)
    {
        FlowId = reading.Text(json, "flow_id", where);
        Supervision = reading.Core<SupervisionState>(json, "supervision", where, CoreVocabulary.Name,
                                                     "supervision_state");
        Labels = reading.Texts(json, "labels", where);
        Parameters = reading.Values(json, "parameters", where);

        if (Supervision == SupervisionState.Nominal)
        {
            reading.Problem($"{where}.supervision", "is nominal on a cohort. A flow's members are generated, "
                                                    + "not authored one by one, so nothing can assert that "
                                                    + "every member executes no target pattern (06 D6.2)");
        }
    }

    /// <summary>The flow.</summary>
    public string FlowId { get; }

    /// <summary><see cref="SupervisionState.Annotated"/> or <see cref="SupervisionState.Unlabelled"/>.</summary>
    public SupervisionState Supervision { get; }

    /// <summary>Its whole-life labels; empty when unlabelled.</summary>
    public ImmutableArray<string> Labels { get; }

    /// <summary>Its parameters.</summary>
    public ImmutableSortedDictionary<string, JsonElement> Parameters { get; }

    internal static CohortSupervision Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>
/// One authored vehicle's states: every state the instances and series it belongs to assert of it, or
/// <see cref="SupervisionState.Unlabelled"/>, written explicitly, where it belongs to none (06 §3.1).
/// </summary>
public sealed record EntitySupervision
{
    private EntitySupervision(JsonElement json, SupervisionPlanReading reading, string where)
    {
        EntityId = reading.Text(json, "entity_id", where);
        Supervision = reading.CoreList<SupervisionState>(json, "supervision", where, CoreVocabulary.Name,
                                                         "supervision_state");
        Refs = reading.Texts(json, "refs", where);
    }

    /// <summary>The entity, its SUMO vehicle id.</summary>
    public string EntityId { get; }

    /// <summary>Its states, sorted; <c>[unlabelled]</c> where nothing is asserted of it.</summary>
    public ImmutableArray<SupervisionState> Supervision { get; }

    /// <summary>The instances it participates in, and <c>series:&lt;id&gt;</c> for each series it realises a slot of.</summary>
    public ImmutableArray<string> Refs { get; }

    internal static EntitySupervision Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}
