using CarlaNet.Recording;

namespace CarlaNet.CoSim;

/// <summary>
/// The illumination declaration of each frame the session rendered, kept for the recorder to write
/// beside the capture of that frame.
/// </summary>
/// <remarks>
/// <para>Written on the tick thread as each tick is audited, read from the recorder's workers as
/// each capture is encoded, so it is locked; the recorder asks by the capture's own frame, so a still
/// carries the audit of the tick that rendered it and not of whichever tick was newest.</para>
///
/// <para>Bounded, because a capture run renders a frame every tick for hours and the recorder only
/// needs the last few: an image arrives within a handful of ticks of its frame, and a declaration the
/// recorder never asks for is dropped once it is <see cref="Capacity"/> frames old.</para>
/// </remarks>
public sealed class IlluminationFrames : IIlluminationSource
{
    /// <summary>Frames kept: seconds of history at a 0.05 s tick, a few tens of kilobytes.</summary>
    public const int Capacity = 256;

    private readonly object _lock = new();
    private readonly Dictionary<ulong, IlluminationDeclaration> _byFrame = [];
    private readonly Queue<ulong> _order = new();
    private ulong? _newest;
    private long _declared;

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

    /// <summary>Frames a declaration was recorded for.</summary>
    public long Declared => Interlocked.Read(ref _declared);

    /// <inheritdoc/>
    public bool TryGetDeclaration(ulong frame, out IlluminationDeclaration declaration)
    {
        lock (_lock)
        {
            return _byFrame.TryGetValue(frame, out declaration!);
        }
    }

    /// <summary>Keep a frame's declaration, dropping the oldest past <see cref="Capacity"/>.</summary>
    internal void Record(ulong frame, IlluminationDeclaration declaration)
    {
        lock (_lock)
        {
            if (!_byFrame.ContainsKey(frame))
            {
                _order.Enqueue(frame);
            }

            _byFrame[frame] = declaration;
            while (_order.Count > Capacity)
            {
                _byFrame.Remove(_order.Dequeue());
            }

            if (_newest is not { } newest || frame > newest)
            {
                _newest = frame;
            }
        }

        Interlocked.Increment(ref _declared);
    }
}
