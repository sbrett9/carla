namespace CarlaNet.Types.Supervision;

// The supervision half of the annotation vocabulary's closed core (06_Truth_And_Annotation.md §3.7):
// the values the pipeline's own code branches on, so that a misspelling makes it behave differently.
// Each is published by CoreVocabulary under the name every record writes it by.

/// <summary>What the author asserts of a subject (06 §3.1).</summary>
public enum SupervisionState
{
    /// <summary>The subject executes the named pattern over the interval.</summary>
    Annotated,

    /// <summary>The subject executes no target pattern: an authored negative.</summary>
    Nominal,

    /// <summary>No assertion. Never a negative.</summary>
    Unlabelled,
}

/// <summary>What a supervision row is about (06 §3.2).</summary>
public enum SubjectKind
{
    /// <summary>One authored vehicle.</summary>
    Entity,

    /// <summary>Every vehicle a flow emits.</summary>
    Cohort,

    /// <summary>An occasion of a recurring series, the one subject that can go unrealised.</summary>
    Slot,
}

/// <summary>Whether a pattern instance happened or is a declared absence (06 §3.5).</summary>
public enum Realisation
{
    /// <summary>A vehicle realised it.</summary>
    Present,

    /// <summary>An occasion passed with no vehicle; world-scoped, never a vehicle's event.</summary>
    Absent,
}

/// <summary>The three authorities an interval's start is read from (06 §3.3).</summary>
public enum IntervalOnset
{
    /// <summary>The author's, from the plan.</summary>
    Declared,

    /// <summary>SUMO's own model, from TraCI at the step it happened.</summary>
    Committed,

    /// <summary>The rendered body's, from CARLA.</summary>
    Observed,
}

/// <summary>Why an interval closed (06 §3.4). The values are not interchangeable.</summary>
public enum ClosedBy
{
    /// <summary>The authored condition ended it.</summary>
    Trigger,

    /// <summary>The vehicle reached its destination and SUMO removed it.</summary>
    EntityArrived,

    /// <summary>SUMO removed it for another reason.</summary>
    SumoRemoved,

    /// <summary>Declared, and discarded before it ever existed.</summary>
    NeverInserted,

    /// <summary>The occasion passed with no vehicle: the absence.</summary>
    SlotUnrealised,

    /// <summary>SUMO committed and the body never did: a defect signal.</summary>
    PhysicalPredicateNeverHeld,

    /// <summary>CARLA lost the body while SUMO still had the vehicle. The behaviour did not end.</summary>
    RenderReleased,

    /// <summary>The capture window closed while it was open.</summary>
    CaptureWindowEnd,

    /// <summary>The simulation ended while it was open.</summary>
    ScenarioEnd,
}

/// <summary>How a recurring series lists its occasions (06 §3.4).</summary>
public enum CadenceForm
{
    /// <summary>One slot per occasion, listed.</summary>
    Enumerated,

    /// <summary>A period, offsets within it, and a span.</summary>
    Periodic,
}

/// <summary>The one role the pipeline reserves (06 D6.35).</summary>
public enum ReservedRole
{
    /// <summary>The single participant of a one-participant instance.</summary>
    Subject,
}

/// <summary>The one phase the pipeline reserves (06 D6.35).</summary>
public enum ReservedPhase
{
    /// <summary>The interval of an absence, which the absence writer emits and no author writes.</summary>
    Vacancy,
}

/// <summary>
/// The events of a vehicle an interval may be anchored to, each committing its start or its end
/// (06 §3.3). A stop or a phase is named with its index, counted from 0: <c>stop:0</c>, <c>phase:2</c>.
/// </summary>
public enum AnchorEvent
{
    /// <summary>Its insertion.</summary>
    Depart,

    /// <summary>Arriving at one of its stops.</summary>
    Stop,

    /// <summary>Leaving one of its stops.</summary>
    StopEnd,

    /// <summary>Entering one of its declared phases, at the first edge's first pass.</summary>
    Phase,
}
