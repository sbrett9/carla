using System.Threading.Channels;

namespace CarlaNet.Transport;

/// <summary>
/// Delivers world ticks to handlers on a thread of its own, so that a slow handler holds up only
/// later handlers and never the stream that produces the ticks.
///
/// The world observer parses every snapshot on its single stream-reader thread, and each parse is
/// what keeps the client's actor state current. A handler invoked on that thread that cannot run
/// immediately stops the stream behind it: a Python callback is the usual case, since it needs the
/// interpreter lock, which the main Python thread holds for the whole of any blocking call it makes
/// into this library. Measured under load, that froze the actor state for several ticks at a time and
/// then let the backlog through in a burst. Handlers registered here are decoupled from the stream:
/// the observer only enqueues the tick and moves on.
///
/// The queue is bounded and drops the oldest tick when full, so a handler that stalls for a long
/// time is fed the recent ticks when it recovers rather than a long-stale backlog. Handlers see
/// ticks in order. A handler that throws is reported through the error callback and the rest still
/// run.
/// </summary>
public sealed class TickDispatcher : IDisposable
{
    /// <summary>Ticks queued ahead of a stalled handler before the oldest are dropped. At forty ticks a
    /// second this is over six seconds of stall before anything is lost.</summary>
    public const int DefaultBacklog = 256;

    private readonly Channel<TickTimestamp> _queue;
    private readonly Action<Exception>? _onError;
    private readonly object _lock = new();
    private readonly List<Action<TickTimestamp>> _handlers = new();
    private Action<TickTimestamp>[] _current = Array.Empty<Action<TickTimestamp>>();
    private readonly Thread _thread;
    private long _published, _delivered;
    private int _disposed;

    public TickDispatcher(Action<Exception>? onError = null, int backlog = DefaultBacklog)
    {
        _onError = onError;
        _queue = Channel.CreateBounded<TickTimestamp>(new BoundedChannelOptions(Math.Max(1, backlog))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _thread = new Thread(Run) { IsBackground = true, Name = "carlanet-tick-dispatch" };
        _thread.Start();
    }

    public int HandlerCount { get { lock (_lock) return _handlers.Count; } }

    /// <summary>Ticks accepted onto the queue.</summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary>Ticks handed to every handler.</summary>
    public long Delivered => Interlocked.Read(ref _delivered);

    /// <summary>Register a handler; disposing the result removes it.</summary>
    public IDisposable Subscribe(Action<TickTimestamp> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            _handlers.Add(handler);
            _current = _handlers.ToArray();
        }
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<TickTimestamp> handler)
    {
        lock (_lock)
        {
            _handlers.Remove(handler);
            _current = _handlers.ToArray();
        }
    }

    /// <summary>Queue a tick for delivery. Never blocks the caller.</summary>
    public void Publish(TickTimestamp tick)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_queue.Writer.TryWrite(tick))
            Interlocked.Increment(ref _published);
    }

    private void Run()
    {
        var reader = _queue.Reader;
        while (true)
        {
            bool more;
            try { more = reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) { return; }
            if (!more) return;
            while (reader.TryRead(out var tick))
            {
                foreach (var handler in _current)
                {
                    try { handler(tick); }
                    catch (Exception ex) { _onError?.Invoke(ex); }
                }
                Interlocked.Increment(ref _delivered);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class Subscription(TickDispatcher owner, Action<TickTimestamp> handler) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Unsubscribe(handler);
        }
    }
}
