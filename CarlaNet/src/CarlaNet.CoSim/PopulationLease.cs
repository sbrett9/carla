namespace CarlaNet.CoSim;

/// <summary>
/// One holder's exclusive claim on generating the vehicles in a world, and on commanding its sun.
/// </summary>
/// <remarks>
/// Released by disposing it. A lease that is never released outlives its holder and locks the world
/// against the next session, which is why a session takes it in the same scope as the rest of its
/// resources rather than owning it from a field nothing disposes.
/// </remarks>
public sealed class PopulationLease : IDisposable
{
    private readonly WorldDriveAuthority _authority;
    private bool _released;

    internal PopulationLease(WorldDriveAuthority authority, PopulationMode mode, string holder)
    {
        _authority = authority;
        Mode = mode;
        Holder = holder;
        AcquiredAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Which mode holds it.</summary>
    public PopulationMode Mode { get; }

    /// <summary>Who holds it, in words a refusal can name -- a component and a process.</summary>
    public string Holder { get; }

    /// <summary>When it was taken, so a refusal can say how long the world has been held.</summary>
    public DateTimeOffset AcquiredAt { get; }

    /// <summary>Whether the lease has been given back.</summary>
    public bool IsReleased => _released;

    /// <summary>
    /// Whether this holder is the one permitted to command the world's sun.
    /// </summary>
    /// <remarks>
    /// Always true, and it is a property rather than a second lease on purpose. Solar command
    /// authority is world-scoped and exclusive, which makes it look like a lease of its own, but its
    /// only input is simulated elapsed time and that is already owned by exactly one component. Two
    /// mechanisms that can disagree about the same thing is the failure the lease exists to avoid.
    /// </remarks>
    public bool CommandsTheSun => true;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _authority.Release(this);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Mode} held by {Holder} since {AcquiredAt:u}";
}
