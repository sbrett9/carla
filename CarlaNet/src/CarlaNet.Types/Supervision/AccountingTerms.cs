namespace CarlaNet.Types.Supervision;

// The accounting half of the closed core (06_Truth_And_Annotation.md §3.7, §5.1, §8.3): what became of
// a vehicle or an interval in the imagery. None of it is supervision; all of it is written by this
// pipeline's own code and read by its accounting, so a value outside these sets is a defect.

/// <summary>
/// What became of an interval, or a site, in one sensor's frames (06 §5.1, D6.11, D6.39).
/// </summary>
public enum ObservabilityOutcome
{
    /// <summary>Drawn, in frame and seen.</summary>
    Observed,

    /// <summary>Drawn, and outside the frame.</summary>
    OutOfFrame,

    /// <summary>Drawn, in frame, and hidden.</summary>
    Occluded,

    /// <summary>No body drew it: an exclusion, never one of the corpus's contents.</summary>
    NotRendered,

    /// <summary>An absence whose site no sensor looked at.</summary>
    SiteUnobserved,

    /// <summary>
    /// Wholly beyond the camera's optional draw distance: the camera did not draw it. Occurs only under
    /// a draw distance, which is off by default.
    /// </summary>
    BeyondDrawDistance,
}

/// <summary>Whether a body drew a vehicle on a frame, as the world truth track writes it (06 §8.3).</summary>
public enum RenderState
{
    /// <summary>A body drew it.</summary>
    Rendered,

    /// <summary>SUMO simulated it and no body drew it.</summary>
    SimulatedOnly,
}

/// <summary>
/// Why no body drew a vehicle SUMO had: the first that holds, in this order (06 §4.4, D6.40).
/// </summary>
public enum RenderReason
{
    /// <summary>The session renders no world.</summary>
    NoWorld,

    /// <summary>SUMO no longer had it at its next frame, listed among the arrivals.</summary>
    LeftTheSimulation,

    /// <summary>SUMO no longer had it at its next frame, and did not list it among the arrivals.</summary>
    Vanished,

    /// <summary>An optional render-set limit left it no body.</summary>
    OutsideLimit,

    /// <summary>Its type names no blueprint.</summary>
    NoBlueprint,

    /// <summary>Its type's blueprint has no measured extent.</summary>
    UnknownExtent,

    /// <summary>It stands off the world's ground grid, where no body can be seated.</summary>
    NoGround,

    /// <summary>None of those: the frame drew no body for it, for a reason that cannot be named.</summary>
    NotDrawn,
}
