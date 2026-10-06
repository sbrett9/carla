namespace CarlaNet.Transport;

/// <summary>
/// The actor snapshots of recent world-observer frames, kept by frame number and served for their own
/// frame only.
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
/// <para><b>A frame is served exactly or not at all.</b> Every vehicle moves every tick, so the
/// snapshot of a neighbouring frame is another instant's truth, and an image written beside it would
/// carry a mismatch nothing downstream is made to check. A frame not held answers null, and the
/// reader decides what to do without it; the recorder drops the still and counts it.</para>
///
/// <para><b>What is kept is decided by the readers, not by a count.</b> A reader that pairs
/// frame-stamped artefacts opens a <see cref="SnapshotHold"/> and, as it finishes with each frame,
/// says which frames it no longer needs (<see cref="SnapshotHold.Release"/>); a frame is dropped once
/// no open hold can still need it, and never while an artefact of that frame could still arrive at a
/// holder that has not reached it. A recorder capturing at 2 Hz off a 20 Hz tick keeps a frame for
/// the ten ticks to its next capture, the few ticks an image takes to arrive, and its margin: a
/// handful of frames. With no hold open only the newest <see cref="IdleFrames"/> are kept, enough
/// for the images in flight when a reader opens its hold. <see cref="Capacity"/> bounds the whole:
/// a holder whose stream has stopped while the world ticks on would otherwise pin every frame, and
/// an artefact of a frame the cap has dropped is one the holder refuses, counted, rather than
/// pairs.</para>
///
/// <para>Thread-safe.</para>
/// </summary>
public sealed class SnapshotHistory
{
    /// <summary>
    /// Frames kept while no hold is open. The longest image delivery measured was seven ticks, so an
    /// image in flight when a reader opens its hold still finds its frame.
    /// </summary>
    public const int DefaultIdleFrames = 16;

    /// <summary>
    /// The most frames kept under any holds: thirteen seconds at a 0.05 s tick, and at three hundred
    /// actors some tens of megabytes, reached only by a holder that has stopped releasing.
    /// </summary>
    public const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly int _idleFrames;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, IReadOnlyDictionary<ActorId, ActorSnapshot>> _byFrame = new();
    private readonly Dictionary<ulong, ObservedRenderSet> _renderSets = new();
    private readonly Dictionary<ulong, ObservedSupervision> _supervision = new();
    private readonly Queue<ulong> _order = new();
    private readonly HashSet<SnapshotHold> _holds = new();

    /// <param name="capacity">The most frames ever kept; <see cref="DefaultCapacity"/> by default.</param>
    /// <param name="idleFrames">Frames kept while no hold is open; <see cref="DefaultIdleFrames"/> by
    /// default, and never more than <paramref name="capacity"/>.</param>
    public SnapshotHistory(int capacity = DefaultCapacity, int idleFrames = DefaultIdleFrames)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "at least one frame must be kept");
        if (idleFrames < 1) throw new ArgumentOutOfRangeException(nameof(idleFrames), "at least one frame must be kept");
        _capacity = capacity;
        _idleFrames = Math.Min(idleFrames, capacity);
    }

    /// <summary>The most frames ever kept.</summary>
    public int Capacity => _capacity;

    /// <summary>Frames kept while no hold is open.</summary>
    public int IdleFrames => _idleFrames;

    public int Count { get { lock (_lock) return _byFrame.Count; } }

    /// <summary>Holds open on this history.</summary>
    public int OpenHolds { get { lock (_lock) return _holds.Count; } }

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

    /// <summary>The oldest frame retained, or null when nothing has been retained yet.</summary>
    public ulong? OldestFrame
    {
        get
        {
            lock (_lock)
            {
                ulong? oldest = null;
                foreach (var f in _order) if (oldest is null || f < oldest) oldest = f;
                return oldest;
            }
        }
    }

    /// <summary>
    /// Open a hold: from now until it is disposed, no frame is dropped that the holder has not released,
    /// within <see cref="Capacity"/>. The frames held at this moment are kept too, for the artefacts of
    /// those frames still in flight.
    /// </summary>
    public SnapshotHold Hold()
    {
        var hold = new SnapshotHold(this);
        lock (_lock)
        {
            _holds.Add(hold);
        }

        return hold;
    }

    /// <summary>Keep the actors of <paramref name="frame"/>. Retaining a frame already held replaces it
    /// without disturbing the order.</summary>
    public void Retain(ulong frame, IReadOnlyDictionary<ActorId, ActorSnapshot> actors)
        => Retain(frame, actors, ObservedRenderSet.None);

    /// <summary>
    /// Keep the actors of <paramref name="frame"/> and the render set its snapshot carried, together,
    /// so a reader of either has the other as of the same frame.
    /// </summary>
    public void Retain(ulong frame, IReadOnlyDictionary<ActorId, ActorSnapshot> actors, ObservedRenderSet renderSet)
        => Retain(frame, actors, renderSet, ObservedSupervision.None);

    /// <summary>
    /// Keep the actors of <paramref name="frame"/>, the render set its snapshot carried and the
    /// supervision it carried, together, so a reader of any of them has the others as of the same frame.
    /// </summary>
    public void Retain(ulong frame, IReadOnlyDictionary<ActorId, ActorSnapshot> actors, ObservedRenderSet renderSet,
                       ObservedSupervision supervision)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(renderSet);
        ArgumentNullException.ThrowIfNull(supervision);
        lock (_lock)
        {
            if (_byFrame.ContainsKey(frame))
            {
                _byFrame[frame] = actors;
                _renderSets[frame] = renderSet;
                _supervision[frame] = supervision;
                return;
            }
            _byFrame[frame] = actors;
            _renderSets[frame] = renderSet;
            _supervision[frame] = supervision;
            _order.Enqueue(frame);
            Trim();
        }
    }

    /// <summary>
    /// The render set the snapshot of <paramref name="frame"/> carried, where that frame itself is
    /// held; null otherwise. <see cref="ObservedRenderSet.None"/> for a held frame that carried none.
    /// </summary>
    public ObservedRenderSet? RenderSetOf(ulong frame)
    {
        lock (_lock)
        {
            return _renderSets.TryGetValue(frame, out ObservedRenderSet? renderSet) ? renderSet : null;
        }
    }

    /// <summary>
    /// The supervision the snapshot of <paramref name="frame"/> carried, where that frame itself is
    /// held; null otherwise. <see cref="ObservedSupervision.None"/> for a held frame that carried none.
    /// </summary>
    public ObservedSupervision? SupervisionOf(ulong frame)
    {
        lock (_lock)
        {
            return _supervision.TryGetValue(frame, out ObservedSupervision? supervision) ? supervision : null;
        }
    }

    /// <summary>The actors of <paramref name="frame"/> where it is held; null otherwise.</summary>
    public IReadOnlyDictionary<ActorId, ActorSnapshot>? Of(ulong frame)
    {
        lock (_lock)
        {
            return _byFrame.TryGetValue(frame, out var actors) ? actors : null;
        }
    }

    /// <summary>
    /// The actors of <paramref name="frame"/> together with the render set that same frame's snapshot
    /// carried, read under one lock so the two are always of one frame; null, with
    /// <paramref name="renderSet"/> <see cref="ObservedRenderSet.None"/>, where the frame is not held.
    /// </summary>
    public IReadOnlyDictionary<ActorId, ActorSnapshot>? Of(ulong frame, out ObservedRenderSet renderSet)
    {
        lock (_lock)
        {
            IReadOnlyDictionary<ActorId, ActorSnapshot>? actors = Of(frame);
            renderSet = actors is not null && _renderSets.TryGetValue(frame, out ObservedRenderSet? held)
                ? held
                : ObservedRenderSet.None;
            return actors;
        }
    }

    /// <summary>
    /// The actors of <paramref name="frame"/> together with the render set and the supervision that
    /// same frame's snapshot carried, read under one lock so all three are of one frame; null, with
    /// <paramref name="renderSet"/> <see cref="ObservedRenderSet.None"/> and <paramref name="supervision"/>
    /// <see cref="ObservedSupervision.None"/>, where the frame is not held.
    /// </summary>
    public IReadOnlyDictionary<ActorId, ActorSnapshot>? Of(ulong frame, out ObservedRenderSet renderSet,
                                                          out ObservedSupervision supervision)
    {
        lock (_lock)
        {
            IReadOnlyDictionary<ActorId, ActorSnapshot>? actors = Of(frame, out renderSet);
            supervision = actors is not null && _supervision.TryGetValue(frame, out ObservedSupervision? held)
                ? held
                : ObservedSupervision.None;
            return actors;
        }
    }

    /// <summary>Whether <paramref name="frame"/> itself is held.</summary>
    public bool Holds(ulong frame) { lock (_lock) return _byFrame.ContainsKey(frame); }

    public void Clear()
    {
        lock (_lock) { _byFrame.Clear(); _renderSets.Clear(); _supervision.Clear(); _order.Clear(); }
    }

    // Called by a hold under no lock of its own.
    internal void Released(SnapshotHold hold)
    {
        lock (_lock)
        {
            if (_holds.Contains(hold)) Trim();
        }
    }

    internal void Closed(SnapshotHold hold)
    {
        lock (_lock)
        {
            if (_holds.Remove(hold)) Trim();
        }
    }

    // Under the lock. Frames are enqueued as the observer delivers them, which is in frame order, so
    // the front of the queue is the oldest frame.
    private void Trim()
    {
        int limit = _holds.Count == 0 ? _idleFrames : _capacity;
        while (_order.Count > limit) Drop(_order.Dequeue());

        if (_holds.Count == 0) return;
        ulong floor = ulong.MaxValue;
        foreach (SnapshotHold hold in _holds)
        {
            // A hold that has released nothing yet may still need every frame held.
            if (hold.Floor is not { } released) return;
            if (released < floor) floor = released;
        }

        while (_order.Count > 0 && _order.Peek() < floor) Drop(_order.Dequeue());
    }

    private void Drop(ulong frame)
    {
        _byFrame.Remove(frame);
        _renderSets.Remove(frame);
        _supervision.Remove(frame);
    }
}

/// <summary>
/// A reader's claim on a <see cref="SnapshotHistory"/>: while it is open, no frame it has not released
/// is dropped, within the history's capacity. Opened by <see cref="SnapshotHistory.Hold"/>, released
/// frame by frame as the reader finishes, and disposed when the reader is done.
/// </summary>
public sealed class SnapshotHold : IDisposable
{
    private readonly SnapshotHistory _history;
    private readonly object _lock = new();
    private ulong? _floor;
    private bool _closed;

    internal SnapshotHold(SnapshotHistory history) => _history = history;

    /// <summary>
    /// The frame before which this holder needs nothing: every frame at or after it may still be
    /// asked for. Null until the first release, when every frame held is still needed.
    /// </summary>
    public ulong? Floor { get { lock (_lock) return _floor; } }

    /// <summary>
    /// Say that this holder has finished with every frame before <paramref name="frame"/>. A release
    /// below an earlier one changes nothing: frames are finished with in order, and a frame once
    /// released is not asked for again.
    /// </summary>
    public void Release(ulong frame)
    {
        lock (_lock)
        {
            if (_closed || (_floor is { } floor && frame <= floor)) return;
            _floor = frame;
        }

        _history.Released(this);
    }

    /// <summary>Close the hold: this holder needs no frame any more.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
        }

        _history.Closed(this);
    }
}
