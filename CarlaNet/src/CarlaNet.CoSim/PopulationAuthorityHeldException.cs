namespace CarlaNet.CoSim;

/// <summary>
/// A second claim on a world's population, refused with the current holder named.
/// </summary>
/// <remarks>
/// <b>This is a failed start, not a warning.</b> A world with two components generating vehicles
/// produces imagery nobody can account for: the truth record carries the population one of them
/// simulated, and the pixels carry both. Nothing downstream can separate them, and a run that gets
/// this far has already spent the collect.
/// </remarks>
public sealed class PopulationAuthorityHeldException : CoSimSessionRefusedException
{
    public PopulationAuthorityHeldException(PopulationMode requested, PopulationLease held)
        : base($"Population authority over this world is held by {held}. {requested} was refused: "
               + "two components generating vehicles in one world produce imagery carrying both "
               + "populations and a truth record carrying one, which nothing downstream can "
               + "separate. Stop the holder, or run against a different world.")
    {
        Requested = requested;
        HeldBy = held.Holder;
        HeldMode = held.Mode;
    }

    /// <summary>The mode that was refused.</summary>
    public PopulationMode Requested { get; }

    /// <summary>Who holds the lease instead.</summary>
    public string HeldBy { get; }

    /// <summary>Which mode holds it.</summary>
    public PopulationMode HeldMode { get; }
}
