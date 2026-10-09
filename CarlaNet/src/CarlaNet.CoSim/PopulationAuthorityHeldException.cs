namespace CarlaNet.CoSim;

/// <summary>
/// A second claim on a world's population, refused with the current holder named.
/// </summary>
/// <remarks>
/// <para><b>This is a failed start, not a warning.</b> A world with two components generating vehicles
/// produces imagery nobody can account for: the truth record carries the population one of them
/// simulated, and the pixels carry both. Nothing downstream can separate them, and a run that gets
/// this far has already spent the collect.</para>
///
/// <para>Raised by the process-local <see cref="WorldDriveAuthority"/>, for a second claim inside one
/// process, and by <see cref="DriveLease.Take"/>, for the server's refusal of a claim on the world's
/// drive lease while another client holds it; <see cref="HeldBy"/> names the holder either way.</para>
/// </remarks>
public sealed class PopulationAuthorityHeldException : CoSimSessionRefusedException
{
    public PopulationAuthorityHeldException(PopulationMode requested, PopulationLease held)
        : base(CoSimSessionStage.Authority,
               $"Population authority over this world is held by {held}. {requested} was refused: "
               + "two components generating vehicles in one world produce imagery carrying both "
               + "populations and a truth record carrying one, which nothing downstream can "
               + "separate. Stop the holder, or run against a different world.")
    {
        Requested = requested;
        HeldBy = held.Holder;
        HeldMode = held.Mode;
    }

    /// <summary>
    /// The server refused the claim on the world's drive lease: another client holds it.
    /// </summary>
    /// <param name="requested">The mode that was refused.</param>
    /// <param name="heldBy">Who holds the lease, as the server names it.</param>
    /// <param name="serverRefusal">The server's words.</param>
    public PopulationAuthorityHeldException(PopulationMode requested, string heldBy, string serverRefusal)
        : base(CoSimSessionStage.Authority,
               $"The drive lease on this world is held on the server by {heldBy}. {requested} was "
               + "refused: two traffic systems driving vehicles in one world produce imagery carrying "
               + "both populations and a truth record carrying one, which nothing downstream can "
               + "separate. Stop the holder, or run against a different world; where the holder is "
               + "gone and did not release the lease, break_drive_lease ends it. The server said: "
               + serverRefusal)
    {
        Requested = requested;
        HeldBy = heldBy;
        // A drive lease is only ever taken by a drive.
        HeldMode = PopulationMode.SumoDrivenPlayback;
    }

    /// <summary>The mode that was refused.</summary>
    public PopulationMode Requested { get; }

    /// <summary>Who holds the lease instead.</summary>
    public string HeldBy { get; }

    /// <summary>Which mode holds it.</summary>
    public PopulationMode HeldMode { get; }
}
