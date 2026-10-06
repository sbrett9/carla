namespace CarlaNet.Types.Streaming;

/// <summary>Where the pose a lent body was drawn at on a frame came from.</summary>
public enum PoseSource : byte
{
    /// <summary>SUMO's own step: where SUMO put the vehicle at one of its steps.</summary>
    Simulated = 1,

    /// <summary>Between two SUMO steps, interpolated along the lane by the co-simulation session.</summary>
    Interpolated = 2,

    /// <summary>Left where its last pose put it, because the session could not place it on the frame.</summary>
    Held = 3,
}

/// <summary>
/// The pose source a world-observer snapshot carried: the frames a co-simulation session's SUMO steps
/// fall on, and every lent body whose pose on the frame followed no step.
/// </summary>
/// <remarks>
/// <para><b>Declared once, read for any frame.</b> A session poses its bodies every world tick from SUMO
/// steps a whole number of ticks apart, so on the frame a step falls on a body stands where SUMO put it,
/// and on every other frame between two steps. The session declares the step to the server once
/// (<c>update_pose_source</c>) and the server carries it on every snapshot, so any reader computes from
/// the frame number which of the two a frame shows: nothing is sent per tick, and every client of the
/// world reads the same answer for the same frame (the owner's ruling of 2026-10-06).</para>
///
/// <para><b>Named where it follows neither.</b> A body the session placed at SUMO's own later step across
/// a discontinuity, or left where its last pose put it because it could not place it, is named to the
/// server as its case begins and ends, and the snapshot carries it while it lasts. A lent body with no
/// entry follows the step. An actor the same frame's render set does not name lent has no pose source:
/// no session placed it.</para>
///
/// <para>A snapshot that carries no block -- no session has declared its step, or the server was built
/// before it carried one -- reads as <see cref="None"/>, which answers no pose source for any body,
/// never a guessed one.</para>
///
/// <para>Immutable, so a reader on another thread never sees one half-built, and frames whose blocks are
/// byte for byte the same share one instance.</para>
/// </remarks>
public sealed class ObservedPoseSource
{
    private readonly Dictionary<uint, PoseSource> _named;
    private readonly byte[] _block;

    /// <param name="ticksPerStep">World ticks per SUMO step; zero where no step is declared.</param>
    /// <param name="stepFrame">A frame a SUMO step falls on; ignored where no step is declared.</param>
    /// <param name="named">Every lent body whose pose follows no step, with where it came from.</param>
    public ObservedPoseSource(uint ticksPerStep, ulong stepFrame, IEnumerable<KeyValuePair<uint, PoseSource>> named)
        : this(true, ticksPerStep, stepFrame, named, [])
    {
    }

    private ObservedPoseSource(bool carried, uint ticksPerStep, ulong stepFrame,
                               IEnumerable<KeyValuePair<uint, PoseSource>> named, byte[] block)
    {
        ArgumentNullException.ThrowIfNull(named);
        IsCarried = carried;
        TicksPerStep = ticksPerStep;
        StepFrame = ticksPerStep > 0 ? stepFrame : 0;
        _named = [];
        foreach ((uint actor, PoseSource source) in named)
        {
            _named[actor] = source;
        }

        _block = block;
    }

    /// <summary>The pose source of a snapshot that carried none: no body has one.</summary>
    public static ObservedPoseSource None { get; } = new(false, 0, 0, [], []);

    /// <summary>
    /// The pose source of a snapshot that said it carried a block this reader could not read: no body
    /// has one, and the frame is not to be read as one no session placed.
    /// </summary>
    public static ObservedPoseSource Unreadable { get; } = new(false, 0, 0, [], []);

    /// <summary>Whether the snapshot said it carried a block and the block could not be read.</summary>
    public bool IsUnreadable => ReferenceEquals(this, Unreadable);

    /// <summary>Whether the snapshot carried a pose source block.</summary>
    public bool IsCarried { get; }

    /// <summary>World ticks per SUMO step; zero where no session had declared its step.</summary>
    public uint TicksPerStep { get; }

    /// <summary>A frame a SUMO step falls on; zero where no step is declared.</summary>
    public ulong StepFrame { get; }

    /// <summary>Every lent body whose pose on the frame followed no step, by actor id.</summary>
    public IReadOnlyDictionary<uint, PoseSource> Named => _named;

    /// <summary>
    /// Whether a SUMO step falls on <paramref name="frame"/>: true or false where a step is declared and
    /// the frame is at or after the frame it was declared to fall on, and <see langword="null"/>
    /// otherwise -- a frame before the declaration is not one the step says anything about.
    /// </summary>
    public bool? StepFallsOn(ulong frame)
    {
        if (TicksPerStep == 0 || frame < StepFrame)
        {
            return null;
        }

        return (frame - StepFrame) % TicksPerStep == 0;
    }

    /// <summary>
    /// Where the pose a lent body was drawn at on <paramref name="frame"/> came from: the session's name
    /// for it where it has one, and otherwise the step's -- <see cref="PoseSource.Simulated"/> on a frame
    /// a step falls on, <see cref="PoseSource.Interpolated"/> on every other. <see langword="null"/> where
    /// the snapshot carried no block, or the body has no name and the step says nothing of the frame.
    /// </summary>
    /// <remarks>
    /// Read it for a body the same frame's render set names lent, with that frame's number. Any other
    /// actor was placed by no session; <see cref="ForLentBody"/> makes that join.
    /// </remarks>
    public PoseSource? Of(uint actorId, ulong frame)
    {
        if (!IsCarried)
        {
            return null;
        }

        if (_named.TryGetValue(actorId, out PoseSource named))
        {
            return named;
        }

        return StepFallsOn(frame) switch
        {
            true => PoseSource.Simulated,
            false => PoseSource.Interpolated,
            null => null,
        };
    }

    /// <summary>
    /// <see cref="Of"/> for an actor the same frame's render set names lent, and <see langword="null"/>
    /// for any other: an actor no session lent was placed by no session.
    /// </summary>
    /// <param name="renderSet">The render set the same snapshot carried.</param>
    /// <param name="actorId">The actor.</param>
    /// <param name="frame">The frame the snapshot is of.</param>
    public PoseSource? ForLentBody(ObservedRenderSet renderSet, uint actorId, ulong frame)
    {
        ArgumentNullException.ThrowIfNull(renderSet);
        return renderSet.TryGetLent(actorId, out _) ? Of(actorId, frame) : null;
    }

    /// <summary>
    /// Read a pose source block, or answer <paramref name="previous"/> itself where the block is the same
    /// bytes as the one it was read from: the step is declared once and a body is named only as its case
    /// begins and ends, so nearly every frame carries the block the frame before did.
    /// </summary>
    /// <param name="block">The block after its size field: the step, the frame, the count and entries.</param>
    /// <param name="previous">The pose source read from the frame before, if any.</param>
    /// <exception cref="InvalidDataException">The block ends part-way through what it holds, or names a
    /// state this reader does not know.</exception>
    internal static ObservedPoseSource Read(ReadOnlySpan<byte> block, ObservedPoseSource? previous)
    {
        if (previous is not null && previous.IsCarried && block.SequenceEqual(previous._block))
        {
            return previous;
        }

        var reader = new SnapshotBlockReader(block, "pose source");
        uint ticksPerStep = reader.UInt32();
        ulong stepFrame = reader.UInt64();
        uint count = reader.UInt32();
        var named = new List<KeyValuePair<uint, PoseSource>>((int)Math.Min(count, 65536u));
        for (uint index = 0; index < count; index++)
        {
            uint actorId = reader.UInt32();
            byte state = reader.Byte();
            PoseSource source = state switch
            {
                // EpisodeStateSerializer::PoseSourceEntryState.
                1 => PoseSource.Simulated,
                2 => PoseSource.Held,
                _ => throw new InvalidDataException(
                    $"The pose source block names actor {actorId} in state {state}, which this reader does "
                    + "not know: it is not read as following the step."),
            };
            named.Add(KeyValuePair.Create(actorId, source));
        }

        return new ObservedPoseSource(true, ticksPerStep, stepFrame, named, block.ToArray());
    }
}
