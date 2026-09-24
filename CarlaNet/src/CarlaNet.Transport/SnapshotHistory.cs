namespace CarlaNet.Transport;

/// <summary>
/// The actor snapshots of the last few world-observer frames, kept by frame number.
///
/// The observer cache on <see cref="CarlaClient"/> always holds the newest frame, which is the right
/// thing for a controller acting on the world now. It is the wrong thing for anything that pairs actor
/// state with an artefact stamped with a frame number, because that artefact arrives later than the
/// snapshot of its own frame: a camera image is read back from the GPU asynchronously and travels on
/// a separate stream, so by the time its callback runs the cache has moved on by however many ticks
/// that took. Measured on a loaded world, that was one tick with the camera still and three or more
/// with it moving, and it also runs the other way when the observer thread is held up while images
/// keep arriving. Reading the snapshot of the image's own frame removes the offset in both
/// directions and in both synchronous and asynchronous mode.
///
/// Frames are retained newest-last up to <see cref="Capacity"/>; asking for a frame no longer held
/// returns the nearest one still held and says which, so a consumer can record what it actually got.
/// Thread-safe.
/// </summary>
public sealed class SnapshotHistory
{
    /// <summary>
    /// Frames kept by default. The longest image delivery measured was seven ticks; at forty ticks a
    /// second this is over a second and a half of history, at three hundred actors a few megabytes.
    /// </summary>
    public const int DefaultCapacity = 64;

    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, IReadOnlyDictionary<ActorId, ActorSnapshot>> _byFrame = new();
    private readonly Queue<ulong> _order = new();

    public SnapshotHistory(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "at least one frame must be kept");
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count { get { lock (_lock) return _byFrame.Count; } }

    /// <summary>The newest frame retained, or null when nothing has been retained yet.</summary>
    public ulong? NewestFrame
    {
        get
        {
            lock (_lock)
            {
                ulong? newest = null;
                foreach (var f in _order) if (newest is null || f > newest) newest = f;
                return newest;
            }
        }
    }

    /// <summary>Keep the actors of <paramref name="frame"/>, dropping the oldest frame once over capacity.
    /// Retaining a frame already held replaces it without disturbing the order.</summary>
    public void Retain(ulong frame, IReadOnlyDictionary<ActorId, ActorSnapshot> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        lock (_lock)
        {
            if (_byFrame.ContainsKey(frame))
            {
                _byFrame[frame] = actors;
                return;
            }
            _byFrame[frame] = actors;
            _order.Enqueue(frame);
            while (_order.Count > _capacity)
                _byFrame.Remove(_order.Dequeue());
        }
    }

    /// <summary>
    /// The actors of <paramref name="frame"/> when it is held, otherwise those of the retained frame
    /// closest to it; <paramref name="servedFrame"/> says which. Null, with <paramref name="servedFrame"/>
    /// zero, when nothing has been retained.
    /// </summary>
    public IReadOnlyDictionary<ActorId, ActorSnapshot>? Nearest(ulong frame, out ulong servedFrame)
    {
        lock (_lock)
        {
            servedFrame = 0;
            if (_byFrame.Count == 0) return null;
            if (_byFrame.TryGetValue(frame, out var exact))
            {
                servedFrame = frame;
                return exact;
            }
            ulong best = 0, bestGap = ulong.MaxValue;
            foreach (var f in _order)
            {
                ulong gap = f > frame ? f - frame : frame - f;
                if (gap < bestGap) { bestGap = gap; best = f; }
            }
            servedFrame = best;
            return _byFrame[best];
        }
    }

    /// <summary>Whether <paramref name="frame"/> itself is held.</summary>
    public bool Holds(ulong frame) { lock (_lock) return _byFrame.ContainsKey(frame); }

    public void Clear()
    {
        lock (_lock) { _byFrame.Clear(); _order.Clear(); }
    }
}
