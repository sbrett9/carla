namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What SUMO will do about a collision is read from the configuration as SUMO reads it, and said on
/// the run report: whether it registers one at all, and what it does to the vehicles.
/// </summary>
public sealed class SumoCollisionHandlingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-collisions-" + Guid.NewGuid().ToString("n"));

    public SumoCollisionHandlingTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("<collision.action value=\"warn\"/>", "warn", true, "registered, warned and carried on")]
    [InlineData("<collision.action value=\"remove\"/>", "remove", true, "registered, and both vehicles taken out")]
    [InlineData("<collision.action v=\"teleport\"/>", "teleport", true,
                "registered, and the collider moved to the next edge of its route")]
    [InlineData("<collision.action>none</collision.action>", "none", false, "not registered, so none can be recorded")]
    public void TheActionTheConfigurationNamesIsTheOneRecorded(string option, string action, bool registered,
                                                               string said)
    {
        SumoCollisionHandling handling = SumoCollisionHandling.Read(Configuration(option));

        Assert.Equal(action, handling.Declared);
        Assert.Equal(action, handling.Action);
        Assert.Equal(registered, handling.Registered);
        Assert.Equal($"{said} (collision.action '{action}')", handling.ToString());
    }

    [Fact]
    public void AConfigurationThatNamesNoActionGetsSumoSDefault()
    {
        SumoCollisionHandling handling = SumoCollisionHandling.Read(Configuration("<seed value=\"42\"/>"));

        Assert.Null(handling.Declared);
        Assert.Equal("teleport", handling.Action);
        Assert.True(handling.Registered);
        Assert.EndsWith("(collision.action not set, so SUMO's default 'teleport')", handling.ToString());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("on")]
    public void IgnoringAccidentsMeansNoneIsRegisteredWhateverTheAction(string value)
    {
        SumoCollisionHandling handling = SumoCollisionHandling.Read(Configuration(
            $"<collision.action value=\"warn\"/><ignore-accidents value=\"{value}\"/>"));

        Assert.True(handling.Ignored);
        Assert.False(handling.Registered);
        Assert.StartsWith("not checked: ignore-accidents is set", handling.ToString());
    }

    [Fact]
    public void TheCompiledScenarioSetsWarn()
    {
        SumoCollisionHandling handling = SumoCollisionHandling.Read(ScenarioLockCheckTests.RepositoryFile(
            "Import", "Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg"));

        Assert.Equal("warn", handling.Action);
        Assert.True(handling.Registered);
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
