using CarlaNet.CoSim;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What the clock accepts and what it refuses. Every refusal here is a condition that produces a
/// run whose imagery and truth record describe different simulated instants, which is why each one
/// is exercised rather than only the accepting path.
/// </summary>
public sealed class CoSimClockTests
{
    [Fact]
    public void AcceptsAScenarioSteppingAtTheWorldRate()
    {
        // The shipped Arapahoe scenario: step-length 0.05 s against the default fixed delta.
        CoSimClock clock = CoSimClock.ForSession(0.05, 0.05, 2.0, worldIsSynchronous: true);

        Assert.Equal(1, clock.WorldTicksPerSumoStep);
        Assert.Equal(10, clock.WorldTicksPerCapture);
        Assert.Equal(0.05, clock.RenderedSecondsPerSumoStep, 12);
    }

    [Fact]
    public void AcceptsASecondLongStepOverTwentyWorldTicks()
    {
        // The shipped Bahonar scenario: step-length 1.0 s against the same fixed delta.
        CoSimClock clock = CoSimClock.ForSession(1.0, 0.05, 2.0, worldIsSynchronous: true);

        Assert.Equal(20, clock.WorldTicksPerSumoStep);
        Assert.Equal(10, clock.WorldTicksPerCapture);
    }

    [Fact]
    public void RefusesAnAsynchronousWorld()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => CoSimClock.ForSession(0.05, 0.05, 2.0, worldIsSynchronous: false));

        Assert.Contains("asynchronous", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesAVariableWorldDelta()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => CoSimClock.ForSession(0.05, 0.0, 2.0, worldIsSynchronous: true));

        Assert.Contains("fixed_delta_seconds", refused.Message);
    }

    [Fact]
    public void RefusesAStepThatIsNotAWholeNumberOfTicksAndNamesTwoThatWouldBe()
    {
        // 0.3 s of SUMO against 0.04 s of world is 7.5 ticks.
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => CoSimClock.ForSession(0.3, 0.04, 2.0, worldIsSynchronous: true));

        Assert.Contains("7.5", refused.Message);
        Assert.Contains("0.0375", refused.Message);   // 0.3 / 8
        Assert.Contains("0.042857", refused.Message); // 0.3 / 7
    }

    [Fact]
    public void RefusesACaptureRateThatDoesNotLandOnATick()
    {
        // 3 Hz is one frame every 0.3333... s, which is 6.67 world ticks of 0.05 s.
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => CoSimClock.ForSession(0.05, 0.05, 3.0, worldIsSynchronous: true));

        Assert.Contains("capture rate", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesASumoStepShorterThanAWorldTick()
    {
        // Half a world tick rounds to a ratio of zero, which is not a positive whole number of
        // ticks however close to one it is.
        Assert.Throws<CoSimSessionRefusedException>(
            () => CoSimClock.ForSession(0.025, 0.05, 2.0, worldIsSynchronous: true));
    }

    [Fact]
    public void TheDecimalStepLengthsAScenarioIsWrittenInAllDivide()
    {
        // 1.0 / 0.05 is 20.000000000000004 in binary and 0.1 / 0.05 is 2.0000000000000004. An exact
        // test would refuse both, and both are the step lengths scenarios are actually authored at.
        foreach ((double step, int expected) in new[] { (0.05, 1), (0.1, 2), (0.2, 4), (0.5, 10), (1.0, 20) })
        {
            CoSimClock clock = CoSimClock.ForSession(step, 0.05, 2.0, worldIsSynchronous: true);
            Assert.Equal(expected, clock.WorldTicksPerSumoStep);
        }
    }

    [Fact]
    public void TheInterpolationFractionCoversTheStepWithoutEverReachingOne()
    {
        CoSimClock clock = CoSimClock.ForSession(1.0, 0.05, 2.0, worldIsSynchronous: true);

        Assert.Equal(0.0, clock.InterpolationFraction(0));
        Assert.Equal(0.95, clock.InterpolationFraction(19), 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.InterpolationFraction(20));
    }

    [Fact]
    public void ACaptureLandsOnTheFirstTickAndEveryTenthAfterIt()
    {
        CoSimClock clock = CoSimClock.ForSession(0.05, 0.05, 2.0, worldIsSynchronous: true);

        Assert.True(clock.IsCaptureTick(0));
        Assert.False(clock.IsCaptureTick(9));
        Assert.True(clock.IsCaptureTick(10));
    }
}
