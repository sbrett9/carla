namespace CarlaNet.Types.Supervision;

// The accounting half of the closed core (06_Truth_And_Annotation.md §3.7, §4.4, §8.3): whether a body
// drew a vehicle on a frame, and why none did. None of it is supervision; all of it is what happened,
// written by this pipeline's own code and read by its world truth track, so a value outside these sets
// is a defect. The core carries no outcome that rests on a pass mark (the charter's §4b): what became of
// a vehicle in the imagery is measured per frame -- occlusion, apparent size, range -- and never summed
// into a word.

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
