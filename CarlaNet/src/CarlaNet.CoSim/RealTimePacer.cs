namespace CarlaNet.CoSim;

/// <summary>
/// Holds a session's world tick cues to the wall clock at a declared ratio, and measures the ratio
/// the run actually held.
/// </summary>
/// <remarks>
/// <para><b>An absolute schedule, not a sleep per tick.</b> The cue for the n-th tick since pacing
/// began is due at the wall instant pacing began plus n world deltas divided by the factor, and the
/// session waits only when it is early for it. A tick that overruns sends the next cue late, and the
/// cues after it go out as soon as they can until the schedule is met again, so an overrun is
/// absorbed by the ticks that follow it rather than carried forward: after an hour of wall clock at
/// a factor of 1.0, the world has rendered an hour, however uneven the hour was. A sleep of one
/// interval per tick adds every overrun to the run and never gives it back. The pattern is
/// <c>SumoCotBridge.run</c>'s (<c>CarlaControl/src/carlacontrol/SumoCotBridge.py</c>).</para>
///
/// <para><b>Timed whether or not the run is paced.</b> The declared factor is what was asked for;
/// what the run held is a separate fact about it, and a run that fell behind has to say so rather
/// than look like one that kept up. The pattern this is copied from waits when it is ahead and
/// records nothing when it is behind -- its only figure is the whole run's, logged at the end -- so a
/// run that held for an hour and then slipped reads the same as one a little slow throughout. So
/// every cue is timed, and three figures are published from the timings: the achieved
/// factor over each fixed window of wall clock, over the whole run, and for the worst window --
/// with, for a paced run, how far the latest cue went out behind the instant it was due. An unpaced
/// run publishes the same figures, which is how fast "as fast as the machine allows" was.</para>
///
/// <para><b>Measured between cues.</b> Simulated time between two cues is exactly the world ticks
/// between them times the world delta, and wall time is read at the moment each cue goes out, so a
/// run that holds its schedule measures exactly its declared factor. Anything that happens between
/// two cues counts against the run's pace -- the bridge's own work, the world's frame, and whatever
/// the caller does between advances -- because all of it is wall clock the run took.</para>
///
/// <para><b>It never stops a run for falling behind.</b> Whether a live exercise has a floor below
/// which it has failed, and where, is undecided -- the operator surface's
/// <c>pacing.min_achieved_factor</c> has no default -- so a threshold here would be a number nobody
/// chose. It publishes; a reader decides.</para>
///
/// <para><b>One wall clock, read through a <see cref="TimeProvider"/>.</b> A test stands in a clock
/// that advances only when told to and asserts the schedule to the tick. Nothing else in a session
/// reads wall time to decide when a tick goes out.</para>
/// </remarks>
public sealed class RealTimePacer
{
    private readonly TimeProvider _wallClock;
    private readonly double _worldDeltaSeconds;
    private readonly double _timestampsPerSecond;
    private readonly long _windowTimestamps;

    private long _startedAt;
    private long _cues;
    private long _windowOpenedAt;
    private long _windowOpenedOnCue;

    /// <param name="realTimeFactor">
    /// Simulated seconds per wall-clock second the cues are held to, or 0 to hold them to nothing.
    /// </param>
    /// <param name="windowSeconds">Wall-clock seconds each published window of the achieved factor spans.</param>
    /// <param name="worldDeltaSeconds">Simulated seconds one world tick renders.</param>
    /// <param name="wallClock">The wall clock the cues are timed and held against.</param>
    internal RealTimePacer(double realTimeFactor, double windowSeconds, double worldDeltaSeconds,
                           TimeProvider wallClock)
    {
        ArgumentNullException.ThrowIfNull(wallClock);
        DeclaredFactor = realTimeFactor;
        WindowSeconds = windowSeconds;
        _worldDeltaSeconds = worldDeltaSeconds;
        _wallClock = wallClock;
        _timestampsPerSecond = wallClock.TimestampFrequency;
        _windowTimestamps = (long)Math.Round(windowSeconds * _timestampsPerSecond);
    }

    /// <summary>
    /// Simulated seconds per wall-clock second the session was declared to hold, as it was declared
    /// when the session started; 0 where it was held to nothing.
    /// </summary>
    public double DeclaredFactor { get; }

    /// <summary>Whether the session's cues are held to the wall clock at all.</summary>
    public bool Paced => DeclaredFactor > 0.0;

    /// <summary>Wall-clock seconds each window of <see cref="LastWindowFactor"/> spans, at least.</summary>
    /// <remarks>
    /// A window closes on the first cue at or after this much wall clock since it opened, so it is
    /// this long plus at most one cue interval, and the figure is taken over exactly the span it
    /// covered.
    /// </remarks>
    public double WindowSeconds { get; }

    /// <summary>Tick cues timed so far.</summary>
    public long Cues => _cues;

    /// <summary>Simulated seconds rendered between the first cue and the latest.</summary>
    public double SimulatedSeconds { get; private set; }

    /// <summary>Wall-clock seconds between the first cue and the latest.</summary>
    public double WallSeconds { get; private set; }

    /// <summary>
    /// Simulated seconds per wall-clock second over the whole run so far, or
    /// <see langword="null"/> before a second cue has gone out.
    /// </summary>
    public double? AchievedFactor => WallSeconds > 0.0 ? SimulatedSeconds / WallSeconds : null;

    /// <summary>Windows closed so far.</summary>
    public long CompletedWindows { get; private set; }

    /// <summary>
    /// The achieved factor over the most recently closed window, or <see langword="null"/> before
    /// one has closed.
    /// </summary>
    public double? LastWindowFactor { get; private set; }

    /// <summary>The lowest achieved factor any closed window measured.</summary>
    public double? WorstWindowFactor { get; private set; }

    /// <summary>The simulated instant of the cue that closed the worst window.</summary>
    public double? WorstWindowClosedAtSeconds { get; private set; }

    /// <summary>
    /// How far the latest cue went out after the instant it was due, in seconds; 0 where it was on
    /// time, and always 0 for a run that is not paced, which has no schedule to be behind.
    /// </summary>
    /// <remarks>
    /// The run's simulated clock is this far behind the wall clock's, at the declared factor. It is
    /// recovered by the cues that follow wherever the machine can outrun the schedule, which is what
    /// an absolute schedule buys.
    /// </remarks>
    public double BehindScheduleSeconds { get; private set; }

    /// <summary>The furthest behind its due instant any cue went out.</summary>
    public double WorstBehindScheduleSeconds { get; private set; }

    /// <summary>The simulated instant of the cue that went out furthest behind.</summary>
    public double? WorstBehindScheduleAtSeconds { get; private set; }

    /// <summary>
    /// Wait until the next tick cue is due, if the run is paced and early for it, then time the cue.
    /// </summary>
    /// <param name="renderedTimeSeconds">
    /// The simulated instant the cue renders, which is how a published worst is located in the run.
    /// </param>
    /// <remarks>
    /// Called immediately before each cue and nowhere else, so the cue goes out at the instant this
    /// returns. The first call starts the schedule: nothing is due before it, so it never waits.
    /// </remarks>
    internal void BeforeTickCue(double renderedTimeSeconds)
    {
        long now = _wallClock.GetTimestamp();
        long cue = _cues;
        if (cue == 0)
        {
            _startedAt = now;
            _windowOpenedAt = now;
            _windowOpenedOnCue = 0;
            _cues = 1;
            return;
        }

        if (Paced)
        {
            long due = _startedAt + (long)Math.Round(
                cue * _worldDeltaSeconds / DeclaredFactor * _timestampsPerSecond);
            if (now < due)
            {
                WaitUntil(due);
                now = _wallClock.GetTimestamp();
            }

            BehindScheduleSeconds = Math.Max(0.0, (now - due) / _timestampsPerSecond);
            if (BehindScheduleSeconds > WorstBehindScheduleSeconds)
            {
                WorstBehindScheduleSeconds = BehindScheduleSeconds;
                WorstBehindScheduleAtSeconds = renderedTimeSeconds;
            }
        }

        SimulatedSeconds = cue * _worldDeltaSeconds;
        WallSeconds = (now - _startedAt) / _timestampsPerSecond;
        if (now - _windowOpenedAt >= _windowTimestamps)
        {
            double factor = (cue - _windowOpenedOnCue) * _worldDeltaSeconds
                            / ((now - _windowOpenedAt) / _timestampsPerSecond);
            CompletedWindows++;
            LastWindowFactor = factor;
            if (WorstWindowFactor is not { } worst || factor < worst)
            {
                WorstWindowFactor = factor;
                WorstWindowClosedAtSeconds = renderedTimeSeconds;
            }

            _windowOpenedAt = now;
            _windowOpenedOnCue = cue;
        }

        _cues = cue + 1;
    }

    /// <summary>
    /// Block until the wall clock reaches <paramref name="due"/>.
    /// </summary>
    /// <remarks>
    /// Through the provider's own timer, so a stand-in clock sees the wait and can advance by exactly
    /// it. The system timer truncates its due time to a whole millisecond and fires on the timer
    /// resolution after that, so a wait can end short or long of its target: short is waited out
    /// again here, and long makes this cue late by that much and the next wait shorter by the same,
    /// which is the absolute schedule doing its job.
    /// </remarks>
    private void WaitUntil(long due)
    {
        TimeSpan remaining = _wallClock.GetElapsedTime(_wallClock.GetTimestamp(), due);
        while (remaining > TimeSpan.Zero)
        {
            using var woken = new ManualResetEventSlim();
            using (_wallClock.CreateTimer(static state => ((ManualResetEventSlim)state!).Set(), woken,
                                          remaining, Timeout.InfiniteTimeSpan))
            {
                woken.Wait();
            }

            remaining = _wallClock.GetElapsedTime(_wallClock.GetTimestamp(), due);
        }
    }

    /// <summary>What was declared and what was achieved, on one line.</summary>
    public override string ToString()
    {
        string declared = Paced
            ? $"held to {DeclaredFactor:0.###} x real time"
            : "not paced, as fast as the machine allows";
        string achieved = AchievedFactor is { } whole
            ? $"achieved {whole:0.####} x over {WallSeconds:0.0} s of wall clock"
            : "nothing timed yet";
        return $"{declared}; {achieved}";
    }
}
