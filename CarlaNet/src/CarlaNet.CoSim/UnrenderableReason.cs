namespace CarlaNet.CoSim;

/// <summary>Why a SUMO vehicle type has no rendered extent.</summary>
/// <remarks>
/// The same two values the catalogue's Python reader records, so a run's accounting of vehicles that
/// were simulated but not rendered reads the same whichever side produced it.
/// </remarks>
public enum UnrenderableReason
{
    /// <summary>The vType names no CARLA blueprint, so nothing says what body it stands for.</summary>
    NoBlueprint,

    /// <summary>
    /// It names a blueprint the catalogue does not hold a measurement for -- either because the
    /// blueprint was never swept, or because its measurement failed.
    /// </summary>
    UnknownExtent,
}
