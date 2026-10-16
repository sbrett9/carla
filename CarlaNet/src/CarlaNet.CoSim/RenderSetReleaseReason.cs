namespace CarlaNet.CoSim;

/// <summary>Why a vehicle stopped holding a rendered actor.</summary>
/// <remarks>
/// Recorded rather than inferred. By default every vehicle SUMO has is rendered, so a track ends only
/// because SUMO removed the vehicle or the session stopped rendering, and a consumer has to know which:
/// a vehicle that reached its destination and a capture that ended around it look alike in the
/// imagery. Under an optional limit a track can also end because the vehicle left the circle or every
/// camera's footprint, or ranked out under a capacity -- each its own reason, because a track that
/// stops mid-scene for the limit means something different from one whose vehicle arrived.
/// </remarks>
public enum RenderSetReleaseReason
{
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
    /// The one release SUMO's step does not explain: the vehicle is gone from SUMO without SUMO having
    /// removed it. Its body is drawn on the frame of its last step where SUMO last reported it, as an
    /// arrival's is, and is gone from the next frame, possibly in the middle of the picture. Recorded under
    /// its own reason so a consumer can count the tracks that end that way rather than infer them.
    /// </remarks>
    Vanished,

    /// <summary>
    /// Under an optional limit, the render-set predicate stopped holding for it: it left the circle, or
    /// every camera's footprint and its margin, and stayed out for the policy's release lag. SUMO still
    /// has it; it holds no body from here.
    /// </summary>
    LeftTheRegion,

    /// <summary>
    /// Under an optional capacity, more vehicles passed the predicate than the capacity allows, and
    /// this one ranked out. SUMO still has it; it holds no body from here.
    /// </summary>
    Capacity,
}
