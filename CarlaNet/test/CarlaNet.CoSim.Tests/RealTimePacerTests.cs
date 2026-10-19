namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The pacing schedule and what it publishes, against a wall clock that moves only when told to.
/// </summary>
/// <remarks>
/// Each test drives the pacer the way a session does -- some wall clock for the tick's work, then
/// the call that comes immediately before the cue -- and reads the instant each cue went out off the
/// clock. The schedule is arithmetic, so the instants are asserted exactly.
/// </remarks>
public sealed class RealTimePacerTests
{
    private const double WorldDelta = 0.05;

    [Fact]
    public void AnUnpacedRunNeverWaitsAndSaysHowFastItWent()
    {
        var clock = new SteppedWallClock();
        var pacer = new RealTimePacer(0.0, windowSeconds: 1.0, WorldDelta, clock);

        for (int cue = 0; cue < 200; cue++)
        {
            pacer.BeforeTickCue(cue * WorldDelta);
            clock.AdvanceMilliseconds(10);
        }

        Assert.False(pacer.Paced);
        Assert.Empty(clock.Waits);

        // 199 intervals of 10 ms, each rendering 0.05 s: five times real time, measured rather than
        // assumed, and one window of a second closed on the hundredth cue.
        Assert.Equal(5.0, pacer.AchievedFactor!.Value, 12);
        Assert.Equal(1, pacer.CompletedWindows);
        Assert.Equal(5.0, pacer.LastWindowFactor!.Value, 12);
        Assert.Equal(0.0, pacer.BehindScheduleSeconds);
        Assert.Equal("not paced, as fast as the machine allows; achieved 5 x over 2.0 s of wall clock",
                     pacer.ToString());
    }

    [Theory]
    [InlineData(1.0, 3.0)]
    [InlineData(2.0, 3.0)]
    [InlineData(0.5, 3.0)]
    [InlineData(1.0, 49.0)]
    public void ARunThatIsAheadWaitsExactlyToTheAbsoluteTarget(double factor, double workMilliseconds)
    {
        var clock = new SteppedWallClock();
        var pacer = new RealTimePacer(factor, windowSeconds: 5.0, WorldDelta, clock);
        List<double> sentAt = [];

        for (int cue = 0; cue < 100; cue++)
        {
            clock.AdvanceMilliseconds(workMilliseconds);
            pacer.BeforeTickCue(cue * WorldDelta);
            sentAt.Add(clock.NowSeconds);
        }

        // The n-th cue goes out n intervals after the first, to the tick of the clock.
        double interval = WorldDelta / factor;
        for (int cue = 0; cue < sentAt.Count; cue++)
        {
            Assert.Equal(sentAt[0] + (cue * interval), sentAt[cue], 12);
        }

        // Nothing is due before the first cue, and every cue after it waited exactly the part of
        // the interval its work left.
        TimeSpan owed = TimeSpan.FromMilliseconds((interval * 1000.0) - workMilliseconds);
        Assert.Equal(99, clock.Waits.Count);
        Assert.All(clock.Waits, wait => Assert.Equal(owed, wait));

        Assert.Equal(factor, pacer.AchievedFactor!.Value, 12);
        Assert.Equal(0.0, pacer.WorstBehindScheduleSeconds);
    }

    [Fact]
    public void AnOverrunIsAbsorbedByTheTicksAfterItAndLeavesNoDrift()
    {
        var clock = new SteppedWallClock();
        var pacer = new RealTimePacer(1.0, windowSeconds: 5.0, WorldDelta, clock);
        List<double> sentAt = [];

        // Ten milliseconds of work per tick, except the one before cue 20, which takes 130: the cue
        // goes out 80 ms late.
        for (int cue = 0; cue < 200; cue++)
        {
            clock.AdvanceMilliseconds(cue == 20 ? 130 : 10);
            pacer.BeforeTickCue(cue * WorldDelta);
            sentAt.Add(clock.NowSeconds);
        }

        double Due(int cue) => 0.010 + (cue * WorldDelta);

        Assert.Equal(1.090, sentAt[20], 12);
        Assert.Equal(1.100, sentAt[21], 12);

        // Two cues later the run is back on its schedule, with no wait in between, and it stays
        // there: the two hundredth cue goes out when the first one's schedule said it would. A sleep
        // of one interval per tick would have it 80 ms late, and every overrun after that later
        // still.
        for (int cue = 22; cue < sentAt.Count; cue++)
        {
            Assert.Equal(Due(cue), sentAt[cue], 12);
        }

        Assert.Equal(Due(199), sentAt[199], 12);
        Assert.Equal(0.080, pacer.WorstBehindScheduleSeconds, 12);
        Assert.Equal(20 * WorldDelta, pacer.WorstBehindScheduleAtSeconds!.Value, 12);
        Assert.Equal(0.0, pacer.BehindScheduleSeconds);
    }

    [Fact]
    public void TheAchievedFactorOfARunThatFallsBehindIsWhatItHeldAndNotWhatItWasAskedFor()
    {
        var clock = new SteppedWallClock();
        var pacer = new RealTimePacer(1.0, windowSeconds: 1.0, WorldDelta, clock);

        // Sixty ticks that keep up, then sixty whose work takes 100 ms against a 50 ms interval.
        for (int cue = 0; cue < 120; cue++)
        {
            clock.AdvanceMilliseconds(cue < 60 ? 10 : 100);
            pacer.BeforeTickCue(cue * WorldDelta);
        }

        // Windows of at least a second: two held at 1.0, one straddling the slowdown, then five at
        // half of real time. The worst is the first of those, closed on cue 70.
        Assert.Equal(8, pacer.CompletedWindows);
        Assert.Equal(0.5, pacer.LastWindowFactor!.Value, 12);
        Assert.Equal(0.5, pacer.WorstWindowFactor!.Value, 12);
        Assert.Equal(70 * WorldDelta, pacer.WorstWindowClosedAtSeconds!.Value, 12);

        // The whole run: 119 ticks of simulated time between the first cue and the last, over the
        // wall clock they actually took.
        Assert.Equal(119 * WorldDelta, pacer.SimulatedSeconds, 12);
        Assert.Equal(8.950, pacer.WallSeconds, 12);
        Assert.Equal(119 * WorldDelta / 8.950, pacer.AchievedFactor!.Value, 12);

        // And how far behind the schedule the last cue went out: sixty ticks each 50 ms over.
        Assert.Equal(3.0, pacer.BehindScheduleSeconds, 12);
        Assert.Equal(3.0, pacer.WorstBehindScheduleSeconds, 12);
    }

    [Fact]
    public void AWindowStraddlingAChangeOfPaceIsMeasuredOverTheSpanItCovered()
    {
        var clock = new SteppedWallClock();
        var pacer = new RealTimePacer(1.0, windowSeconds: 1.0, WorldDelta, clock);

        for (int cue = 0; cue <= 60; cue++)
        {
            clock.AdvanceMilliseconds(cue < 60 ? 10 : 100);
            pacer.BeforeTickCue(cue * WorldDelta);
        }

        // Opened on cue 40 at 2.010 s, closed on cue 60 at 3.060 s: twenty ticks over 1.05 s.
        Assert.Equal(3, pacer.CompletedWindows);
        Assert.Equal(20 * WorldDelta / 1.05, pacer.LastWindowFactor!.Value, 12);
    }
}
