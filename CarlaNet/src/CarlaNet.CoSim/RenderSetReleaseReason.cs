namespace CarlaNet.CoSim;

/// <summary>Why a vehicle stopped holding a rendered actor.</summary>
/// <remarks>
/// Recorded rather than inferred. Every vehicle SUMO has is rendered, so a track ends only because
/// SUMO removed the vehicle or the session stopped rendering, and a consumer has to know which: a
/// vehicle that reached its destination and a capture that ended around it look alike in the imagery.
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
    /// The one release the lookahead cannot place: the vehicle is gone from SUMO before the bridge
    /// learns of it, so its body leaves wherever it happened to be, possibly in frame. Recorded under
    /// its own reason so a consumer can count the tracks that end that way rather than infer them.
    /// </remarks>
    Vanished,
}
