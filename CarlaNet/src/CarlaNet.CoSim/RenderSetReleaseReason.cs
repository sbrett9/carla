namespace CarlaNet.CoSim;

/// <summary>Why a vehicle stopped holding a rendered actor.</summary>
/// <remarks>
/// Recorded rather than inferred. A track that stops mid-scene because the render set was full
/// means something different to a consumer than one that stops because the vehicle reached its
/// destination, and nothing downstream can tell the two apart from the imagery.
/// </remarks>
public enum RenderSetReleaseReason
{
    /// <summary>The render-set predicate stopped holding for it.</summary>
    LeftTheRegion,

    /// <summary>More vehicles passed the predicate than the capacity allows, and this one ranked out.</summary>
    Capacity,

    /// <summary>SUMO removed the vehicle: it arrived, or a route error took it out.</summary>
    LeftTheSimulation,

    /// <summary>The session ended while the vehicle was still rendered.</summary>
    SessionEnded,
}
