namespace CarlaNet.CoSim;

/// <summary>
/// The session's hold on the world's drive lease: the server-held claim to be the one traffic system
/// that drives the world's vehicles, taken before SUMO is started and given back when the session
/// lets go.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> While the server holds the lease for this session, it refuses every
/// call that would have another traffic system drive a vehicle -- the autopilot flag, vehicle control,
/// Ackermann control and physics control, direct or in a batch -- for every actor and every client,
/// naming this holder. A traffic manager started against the same server, from this process or
/// another, moves nothing; a second drive session is refused at its own claim, before it starts
/// anything. The process-local <see cref="PopulationLease"/> gives the same refusal a legible place
/// inside one process; this is the one that reaches a second process.</para>
///
/// <para><b>A server without the lease.</b> One built before it carried the call refuses the claim
/// with an error naming the call. The session goes on -- the run itself is sound -- and records the
/// refusal (<see cref="Refusal"/>, <see cref="CoSimRunReport.DriveLeaseRefused"/>), because on such a
/// server nothing stops another traffic system driving vehicles into the capture, and the run report
/// has to say so.</para>
///
/// <para><b>Given back.</b> Disposing releases the lease under the name it was taken with. The server
/// gives no notice of a disconnect, so a session that dies without disposing leaves the lease held
/// until the world is reloaded or <c>break_drive_lease</c> ends it, which the server logs.</para>
/// </remarks>
public sealed class DriveLease : IDisposable
{
    private readonly ICarlaWorld _world;
    private bool _released;

    private DriveLease(ICarlaWorld world, string holder, string? refusal)
    {
        _world = world;
        Holder = holder;
        Refusal = refusal;
    }

    /// <summary>The name the lease was taken under, which the server names in every refusal it gives.</summary>
    public string Holder { get; }

    /// <summary>
    /// Why the world granted no lease, in the server's words, or <see langword="null"/> where it
    /// holds one for this session.
    /// </summary>
    public string? Refusal { get; }

    /// <summary>Whether the server holds the lease for this session: the lockout is in force.</summary>
    public bool InForce => Refusal is null && !_released;

    /// <summary>Whether the lease has been given back.</summary>
    public bool IsReleased => _released;

    /// <summary>
    /// Take the world's drive lease under a name, or refuse naming whoever holds it.
    /// </summary>
    /// <exception cref="PopulationAuthorityHeldException">
    /// Another holder has the lease. The world is exactly as it was found: nothing was taken.
    /// </exception>
    public static DriveLease Take(ICarlaWorld world, string holder)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);

        DriveLeaseWrite written = world.TakeDriveLease(holder);
        if (written.HeldByAnother)
        {
            throw new PopulationAuthorityHeldException(PopulationMode.SumoDrivenPlayback, written.HeldBy!,
                                                       written.Refusal!);
        }

        return new DriveLease(world, holder, written.Refusal);
    }

    /// <summary>
    /// Give the lease back. Doing it twice does nothing the second time; a lease the world never
    /// granted has nothing to give back.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The world refused the release -- the lease is no longer held, or is held under another name,
    /// which means something broke or retook it under this run -- with the server's words.
    /// </exception>
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (Refusal is not null)
        {
            return;
        }

        if (_world.ReleaseDriveLease(Holder) is { } refused)
        {
            throw new InvalidOperationException(
                $"The drive lease taken as {Holder} was not given back: {refused}");
        }
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Refusal is { } refusal
            ? $"not held; the server refused the lease: {refusal}"
            : _released
                ? $"given back by {Holder}"
                : $"held by {Holder}";
}
