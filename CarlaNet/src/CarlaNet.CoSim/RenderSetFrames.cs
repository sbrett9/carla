using CarlaNet.Recording;

namespace CarlaNet.CoSim;

/// <summary>
/// The render set of each frame the session rendered -- which bodies were lent, and to which SUMO
/// vehicle -- kept for the recorder to list beside the capture of that frame.
/// </summary>
/// <remarks>
/// <para>Written on the tick thread as each tick returns its frame, read from the recorder's
/// preparation task as each capture is prepared, so it is locked; the recorder asks by the frame its
/// truth describes, so a still lists the bodies of the tick that rendered it, named for the vehicles
/// they carried then, and not whichever lending was newest.</para>
///
/// <para>Bounded, because a capture run renders a frame every tick for hours and the recorder only
/// needs the last few: an image arrives within a handful of ticks of its frame, and a set the recorder
/// never asks for is dropped once it is <see cref="Capacity"/> frames old. A frame asked for after
/// that is answered with nothing, and the recorder lists no vehicle for it rather than a guess. The
/// client keeps the world's snapshots for fewer frames than this
/// (<c>SnapshotHistory.DefaultCapacity</c>), so any frame whose truth is still held has its set held
/// too.</para>
///
/// <para>A frame's set is the same object as the frame before's until a body is lent or given back,
/// so the history costs a reference per frame between those moments.</para>
/// </remarks>
public sealed class RenderSetFrames : IRenderSetSource
{
    /// <summary>Frames kept: seconds of history at a 0.05 s tick.</summary>
    public const int Capacity = 256;

    private readonly object _lock = new();
    private readonly Dictionary<ulong, RenderSet> _byFrame = [];
    private readonly Queue<ulong> _order = new();
    private ulong? _newest;
    private long _recorded;

    /// <inheritdoc/>
    public ulong? NewestFrame
    {
        get
        {
            lock (_lock)
            {
                return _newest;
            }
        }
    }

    /// <summary>Frames a render set was recorded for.</summary>
    public long Recorded => Interlocked.Read(ref _recorded);

    /// <inheritdoc/>
    public bool TryGetRenderSet(ulong frame, out RenderSet renderSet)
    {
        lock (_lock)
        {
            return _byFrame.TryGetValue(frame, out renderSet!);
        }
    }

    /// <summary>Keep a frame's render set, dropping the oldest past <see cref="Capacity"/>.</summary>
    internal void Record(ulong frame, RenderSet renderSet)
    {
        lock (_lock)
        {
            if (!_byFrame.ContainsKey(frame))
            {
                _order.Enqueue(frame);
            }

            _byFrame[frame] = renderSet;
            while (_order.Count > Capacity)
            {
                _byFrame.Remove(_order.Dequeue());
            }

            if (_newest is not { } newest || frame > newest)
            {
                _newest = frame;
            }
        }

        Interlocked.Increment(ref _recorded);
    }
}
