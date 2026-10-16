namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A wall clock that moves only when told to, and whose every wait takes exactly as long as it
/// asked for.
/// </summary>
/// <remarks>
/// <para>A pacing schedule asserted against the real clock is asserted within the system timer's
/// resolution, which on Windows is about sixteen milliseconds -- a third of a tick at the default
/// delta. This clock makes the schedule exact: the test advances it by the work each tick is meant
/// to take, and a wait advances it by exactly the wait, so a cue's instant is arithmetic and can be
/// compared for equality.</para>
///
/// <para>One timestamp is one tick of <see cref="TimeSpan"/>, so a whole number of milliseconds is
/// exactly representable and nothing is lost converting between the two.</para>
/// </remarks>
internal sealed class SteppedWallClock : TimeProvider
{
    private long _now;

    /// <summary>Every wait asked for, in order.</summary>
    public List<TimeSpan> Waits { get; } = [];

    /// <summary>Seconds since the clock was made.</summary>
    public double NowSeconds => (double)_now / TimeSpan.TicksPerSecond;

    /// <inheritdoc/>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc/>
    public override long GetTimestamp() => _now;

    /// <summary>Let wall time pass, as a tick's work or a caller's own would.</summary>
    public void Advance(TimeSpan by) => _now += by.Ticks;

    /// <summary>Let so many milliseconds of wall time pass.</summary>
    public void AdvanceMilliseconds(double milliseconds) =>
        Advance(TimeSpan.FromMilliseconds(milliseconds));

    /// <inheritdoc/>
    /// <remarks>A one-shot timer that has fired by the time it is returned.</remarks>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime,
                                       TimeSpan period)
    {
        Waits.Add(dueTime);
        _now += Math.Max(0L, dueTime.Ticks);
        callback(state);
        return new Fired();
    }

    private sealed class Fired : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
