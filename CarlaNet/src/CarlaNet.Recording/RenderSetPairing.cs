namespace CarlaNet.Recording;

/// <summary>A capture's vehicle records once paired with a render set, and which vehicles they are.</summary>
/// <param name="Records">The records the sidecar lists.</param>
/// <param name="Vehicles">What that list is: the frame's render set, or nothing because it was unknown.</param>
/// <param name="DrawDistanceMetres">
/// The draw distance the frame's set says it was drawn under, or null where it drew every body at any
/// range or the set was unknown.
/// </param>
public readonly record struct PairedTruth(IReadOnlyList<VehicleTelemetry> Records, SidecarVehicles Vehicles,
                                          double? DrawDistanceMetres = null);

/// <summary>
/// Pairs each capture's truth records with the render set of the frame they describe, and counts the
/// captures it could not pair.
/// </summary>
/// <remarks>
/// <para><b>The frame the records describe, never the newest.</b> Bodies are lent and given back
/// between ticks, and an image arrives several ticks after its frame, so the newest set is routinely
/// one in which a body has changed hands: joined to it, a body's pose would be named for the vehicle
/// it carries now rather than the one it carried when the image was taken, or a body just given back
/// would be listed as the vehicle it no longer renders. The records are those of the image's own frame
/// whenever the client still held it, and of the neighbouring frame the sidecar names in
/// <c>telemetry_tick</c> when it did not, so asking for the records' frame is asking for the image's
/// in every case where the two can be paired at all.</para>
///
/// <para><b>A frame whose set is no longer held is refused, not guessed.</b> The capture keeps its
/// image, its sun and its sensor pose, and lists no vehicle, marked <see cref="SidecarVehicles.Unknown"/>
/// so the empty list is never read as an empty scene; the refusal is counted in
/// <see cref="Unpaired"/>. The nearest set still held would be a guess of exactly the kind the
/// pairing exists to prevent, and the whole world's actor list is the defect itself: every parked body
/// listed as a vehicle.</para>
///
/// <para>A capture can arrive before the source has recorded its frame, so a lookup waits a bounded
/// moment for it, and gives up at once where the source is already past the frame without an answer.
/// Called from the recorder's preparation task, which may wait; never from a stream thread.</para>
/// </remarks>
public sealed class RenderSetPairing
{
    /// <summary>
    /// How long a capture waits for its frame's set when it arrives before the source has recorded
    /// that frame. The image is read back from the GPU after the tick that rendered it has returned,
    /// and the set is recorded as that tick returns, so it is not expected to wait at all; the bound
    /// is what keeps a frame the source will never answer for from holding the recorder.
    /// </summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMilliseconds(500);

    private readonly IRenderSetSource _source;
    private readonly TimeSpan _wait;
    private long _paired, _unpaired, _bodiesMissing;

    /// <param name="source">What lent the bodies, answering frame by frame.</param>
    /// <param name="wait">How long a lookup waits for a frame the source has not reached yet;
    /// <see cref="DefaultWait"/> when null.</param>
    public RenderSetPairing(IRenderSetSource source, TimeSpan? wait = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _wait = wait ?? DefaultWait;
    }

    /// <summary>Captures whose vehicle list is their frame's render set.</summary>
    public long Paired => Interlocked.Read(ref _paired);

    /// <summary>
    /// Captures written with no vehicle list, because the source no longer held, or never had, the
    /// render set of the frame their records describe. Every one is a still whose truth is missing
    /// rather than wrong.
    /// </summary>
    public long Unpaired => Interlocked.Read(ref _unpaired);

    /// <summary>
    /// Bodies a paired capture's render set held that no truth record described, summed over the
    /// captures: vehicles in the picture with nothing beside them in the sidecar.
    /// </summary>
    public long BodiesMissing => Interlocked.Read(ref _bodiesMissing);

    /// <summary>
    /// The records a capture lists: those of the bodies its frame rendered, each carrying the
    /// vehicle it rendered, or none where the frame's set is not to be had.
    /// </summary>
    /// <param name="records">Every vehicle actor's truth record, as of <paramref name="frame"/>.</param>
    /// <param name="frame">The frame the records describe.</param>
    public PairedTruth Pair(IReadOnlyList<VehicleTelemetry> records, ulong frame)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (!TryWaitFor(frame, out RenderSet renderSet))
        {
            Interlocked.Increment(ref _unpaired);
            return new PairedTruth([], SidecarVehicles.Unknown);
        }

        IReadOnlyList<VehicleTelemetry> selected = renderSet.Select(records, out int missing);
        Interlocked.Increment(ref _paired);
        if (missing > 0)
        {
            Interlocked.Add(ref _bodiesMissing, missing);
        }

        return new PairedTruth(selected, SidecarVehicles.Rendered, renderSet.DrawDistanceMetres);
    }

    private bool TryWaitFor(ulong frame, out RenderSet renderSet)
    {
        DateTime giveUp = DateTime.UtcNow + _wait;
        while (true)
        {
            if (_source.TryGetRenderSet(frame, out renderSet))
            {
                return true;
            }

            // A source already past this frame without an answer for it will not have one later.
            if ((_source.NewestFrame is { } newest && newest >= frame) || DateTime.UtcNow >= giveUp)
            {
                return false;
            }

            Thread.Sleep(2);
        }
    }
}
