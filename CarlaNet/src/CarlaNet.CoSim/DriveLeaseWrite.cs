namespace CarlaNet.CoSim;

/// <summary>
/// What a world made of a session's claim on its drive lease.
/// </summary>
/// <param name="HeldBy">
/// Who holds the lease instead, where the world refused the claim because another holder has it;
/// <see langword="null"/> otherwise.
/// </param>
/// <param name="Refusal">
/// Why the world refused the claim, in the server's words, or <see langword="null"/> where it granted
/// the lease. A server built before it carried a drive lease refuses every claim and names no holder.
/// </param>
public readonly record struct DriveLeaseWrite(string? HeldBy, string? Refusal)
{
    /// <summary>Whether the world granted the lease.</summary>
    public bool Taken => Refusal is null;

    /// <summary>Whether the world has a lease and another holder has it.</summary>
    public bool HeldByAnother => HeldBy is not null;

    /// <summary>
    /// Whether the world granted no lease and names no holder: a server built before it carried a
    /// drive lease, on which nothing stops another traffic system driving vehicles.
    /// </summary>
    public bool Unavailable => Refusal is not null && HeldBy is null;
}
