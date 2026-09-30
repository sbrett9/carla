namespace CarlaNet.CoSim;

/// <summary>Why a vehicle stopped holding a rendered actor.</summary>
/// <remarks>
/// Recorded rather than inferred. A track that stops mid-scene because the render set was full
/// means something different to a consumer than one that stops because the vehicle reached its
/// destination, and nothing downstream can tell the two apart from the imagery.
/// </remarks>
public enum RenderSetReleaseReason
{
    /// <summary>
    /// The render-set predicate stopped holding for it: it left the circle, or every camera's footprint
    /// and its margin, and stayed out for the policy's release lag.
    /// </summary>
    LeftTheRegion,

    /// <summary>More vehicles passed the predicate than the capacity allows, and this one ranked out.</summary>
    Capacity,

    /// <summary>
    /// SUMO reported the vehicle among the step's arrivals: it reached the end of its route, or
    /// something SUMO did during the step took it out.
    /// </summary>
    LeftTheSimulation,

    /// <summary>The session ended while the vehicle was still rendered.</summary>
    SessionEnded,

    /// <summary>
    /// The vehicle stopped reporting a state without SUMO having listed it among the step's arrivals:
    /// taken out between two steps, which SUMO does not report as an arrival, or an arrival the
    /// subscription missed.
    /// </summary>
    /// <remarks>
    /// The one release the lookahead cannot place: the vehicle is gone from SUMO before the bridge
    /// learns of it, so its body leaves wherever it happened to be, possibly in frame. Recorded under
    /// its own reason so a consumer can count the tracks that end that way rather than infer them.
    /// </remarks>
    Vanished,
}
