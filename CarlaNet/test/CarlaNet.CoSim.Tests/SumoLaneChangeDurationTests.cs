namespace CarlaNet.CoSim.Tests;

/// <summary>
/// How long a lane change takes is read from the configuration as SUMO reads it, and said on the run
/// report: spread over time where it is longer than the step, made inside one step otherwise.
/// </summary>
public sealed class SumoLaneChangeDurationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-lane-changes-" + Guid.NewGuid().ToString("n"));

    public SumoLaneChangeDurationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("<lanechange.duration value=\"3\"/>", "3", 3.0, "3")]
    [InlineData("<lanechange.duration v=\"4.5\"/>", "4.5", 4.5, "4.5")]
    [InlineData("<lanechange.duration>00:00:05</lanechange.duration>", "00:00:05", 5.0, "5")]
    public void TheDurationTheConfigurationSetsIsTheOneRecorded(string option, string declared, double seconds,
                                                                string said)
    {
        SumoLaneChangeDuration duration = SumoLaneChangeDuration.Read(Configuration(option));

        Assert.Equal(declared, duration.Declared);
        Assert.Equal(seconds, duration.Seconds, 9);
        Assert.True(duration.IsSpreadOverTimeAt(0.05));
        Assert.Equal($"spread over {said} s, moving across at a steady rate, at a 0.05 s step "
                     + $"(lanechange.duration '{declared}')", duration.Describe(0.05));
    }

    [Fact]
    public void AConfigurationThatSetsNoneGetsSumoSInstantaneousDefault()
    {
        SumoLaneChangeDuration duration = SumoLaneChangeDuration.Read(CoSimFixtures.RightAngleTurnScenario);

        Assert.Null(duration.Declared);
        Assert.Equal(0.0, duration.Seconds);
        Assert.False(duration.IsSpreadOverTimeAt(0.05));
        Assert.Equal("INSTANTANEOUS: a lane width crossed inside one 0.05 s step (lanechange.duration not "
                     + "set, so SUMO's default of 0 s)", duration.Describe(0.05));
    }

    [Theory]
    [InlineData("1", 1.0, false)]
    [InlineData("0.9", 1.0, false)]
    [InlineData("3", 1.0, true)]
    [InlineData("0.05", 0.05, false)]
    public void ADurationNoLongerThanTheStepIsMadeInsideOneStep(string declared, double step, bool spread)
    {
        // SUMO spreads a lane change only where the duration exceeds the step
        // (MSAbstractLaneChangeModel::startLaneChangeManeuver, gLaneChangeDuration > DELTA_T).
        SumoLaneChangeDuration duration = SumoLaneChangeDuration.Read(
            Configuration($"<lanechange.duration value=\"{declared}\"/>"));

        Assert.Equal(spread, duration.IsSpreadOverTimeAt(step));
    }

    [Fact]
    public void ADurationSetTwiceOrNotATimeIsRefusedAsSumoWouldRefuseIt()
    {
        CoSimSessionRefusedException twice = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoLaneChangeDuration.Read(Configuration(
                "<lanechange.duration value=\"3\"/><lanechange.duration value=\"4\"/>")));
        Assert.Contains("sets lanechange.duration 2 times ('3', '4')", twice.Message);

        CoSimSessionRefusedException notATime = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoLaneChangeDuration.Read(Configuration("<lanechange.duration value=\"slowly\"/>")));
        Assert.Contains("sets lanechange.duration to 'slowly', which is not a time SUMO reads", notATime.Message);
    }

    [Theory]
    [InlineData("Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg", 0.05)]
    [InlineData("Arapahoe_I25_UnderpassDwell.sumocfg", 0.05)]
    public void TheRecompiledScenariosSpreadTheirLaneChangesOverThreeSeconds(string scenario, double step)
    {
        SumoLaneChangeDuration duration = SumoLaneChangeDuration.Read(
            ScenarioLockCheckTests.RepositoryFile("Import", scenario));

        Assert.Equal(3.0, duration.Seconds);
        Assert.True(duration.IsSpreadOverTimeAt(step));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still open somewhere is not worth failing a test over; the temporary directory
            // is the operating system's to clean up.
        }
    }

    /// <summary>A configuration whose processing section holds the given options; its full path.</summary>
    private string Configuration(string processing)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("n") + ".sumocfg");
        File.WriteAllText(path,
                          "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<configuration>\n"
                          + $"    <processing>{processing}</processing>\n</configuration>\n");
        return path;
    }
}
