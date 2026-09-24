// Tick handlers that need the Python interpreter lock used to run on the world-observer thread, and
// while one waited for that lock every later snapshot waited behind it. The dispatcher takes them off
// that thread; these tests pin the properties the observer relies on.
using CarlaNet.Transport;

namespace CarlaNet.Tests.Transport;

public class TickDispatcherTests
{
    private static TickTimestamp Tick(ulong frame) => new(frame, frame * 0.05, 0.05, 0.0);

    [Fact]
    public void Handlers_Run_On_A_Thread_Of_Their_Own()
    {
        using var d = new TickDispatcher();
        int? handlerThread = null;
        using var done = new ManualResetEventSlim();
        using var _ = d.Subscribe(_ => { handlerThread = Environment.CurrentManagedThreadId; done.Set(); });

        d.Publish(Tick(1));

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotNull(handlerThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, handlerThread);
    }

    [Fact]
    public void Publishing_Never_Waits_For_A_Slow_Handler()
    {
        using var d = new TickDispatcher();
        using var release = new ManualResetEventSlim();
        using var _ = d.Subscribe(_ => release.Wait());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (ulong f = 1; f <= 50; f++) d.Publish(Tick(f));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"publishing took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(50, d.Published);
        release.Set();
    }

    [Fact]
    public void Ticks_Arrive_In_Order()
    {
        using var d = new TickDispatcher();
        var seen = new List<ulong>();
        using var done = new CountdownEvent(20);
        using var _ = d.Subscribe(t => { lock (seen) seen.Add(t.Frame); done.Signal(); });

        for (ulong f = 1; f <= 20; f++) d.Publish(Tick(f));

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        lock (seen) Assert.Equal(Enumerable.Range(1, 20).Select(i => (ulong)i), seen);
    }

    [Fact]
    public void A_Stalled_Handler_Loses_The_Oldest_Ticks_Not_The_Newest()
    {
        using var d = new TickDispatcher(backlog: 4);
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var seen = new List<ulong>();
        using var _ = d.Subscribe(t =>
        {
            started.Set();
            release.Wait();
            lock (seen) seen.Add(t.Frame);
        });

        d.Publish(Tick(1));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));   // handler is now stuck on tick 1
        for (ulong f = 2; f <= 12; f++) d.Publish(Tick(f));    // eleven more, only four fit
        release.Set();

        SpinWait.SpinUntil(() => { lock (seen) return seen.Count >= 5; }, TimeSpan.FromSeconds(5));
        lock (seen)
        {
            Assert.Equal(1ul, seen[0]);
            Assert.Equal(12ul, seen[^1]);
            Assert.Equal(seen.OrderBy(x => x), seen);
        }
    }

    [Fact]
    public void A_Handler_That_Throws_Is_Reported_And_The_Others_Still_Run()
    {
        var errors = new List<Exception>();
        using var d = new TickDispatcher(ex => { lock (errors) errors.Add(ex); });
        using var done = new ManualResetEventSlim();
        using var _1 = d.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var _2 = d.Subscribe(_ => done.Set());

        d.Publish(Tick(1));

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        SpinWait.SpinUntil(() => { lock (errors) return errors.Count == 1; }, TimeSpan.FromSeconds(5));
        lock (errors) Assert.IsType<InvalidOperationException>(Assert.Single(errors));
    }

    [Fact]
    public void Disposing_The_Subscription_Stops_Deliveries_To_That_Handler()
    {
        using var d = new TickDispatcher();
        int calls = 0;
        using var done = new ManualResetEventSlim();
        var sub = d.Subscribe(_ => Interlocked.Increment(ref calls));
        using var _ = d.Subscribe(_ => done.Set());

        d.Publish(Tick(1));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(5));
        sub.Dispose();
        done.Reset();

        d.Publish(Tick(2));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1, d.HandlerCount);   // one handler left: the done-setter
    }

    [Fact]
    public void Dispose_Returns_Promptly_And_Drops_Later_Ticks()
    {
        var d = new TickDispatcher();
        using var _ = d.Subscribe(_ => { });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        d.Dispose();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2500);
        d.Publish(Tick(1));
        Assert.Equal(0, d.Published);
    }
}
