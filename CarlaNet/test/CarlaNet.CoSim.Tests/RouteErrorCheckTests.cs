namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session refuses a scenario whose configuration tells SUMO to carry on past a route it cannot
/// follow, reading <c>ignore-route-errors</c> as SUMO reads a boolean.
/// </summary>
/// <remarks>
/// Measured against SUMO 1.27.0 on the fixture network: with the option set, a vehicle routed across a
/// missing connection was inserted and stood at the end of the last edge it could reach until the run
/// ended, with nothing on SUMO's console; without it, SUMO stopped with an error naming the vehicle and
/// both edges.
/// </remarks>
public sealed class RouteErrorCheckTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-route-errors-" + Guid.NewGuid().ToString("n"));

    public RouteErrorCheckTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("x")]
    [InlineData("t")]
    public void AConfigurationThatCarriesOnPastARouteErrorIsRefused(string value)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => RouteErrorCheck.Require(Configuration($"<ignore-route-errors value=\"{value}\"/>")));

        Assert.Contains($"sets ignore-route-errors to '{value}'", refused.Message);
        Assert.Contains("SUMO has not been started", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
    }

    [Theory]
    [InlineData("<ignore-route-errors value=\"false\"/>", "false")]
    [InlineData("<ignore-route-errors value=\"0\"/>", "0")]
    [InlineData("<ignore-route-errors value=\"no\"/>", "no")]
    [InlineData("<ignore-route-errors value=\"off\"/>", "off")]
    [InlineData("<ignore-route-errors value=\"-\"/>", "-")]
    [InlineData("<ignore-route-errors v=\"f\"/>", "f")]
    [InlineData("<ignore-route-errors>False</ignore-route-errors>", "False")]
    public void AConfigurationThatLeavesRouteErrorsToStopSumoRuns(string option, string declared)
    {
        RouteErrorCheck check = RouteErrorCheck.Require(Configuration(option));

        Assert.Equal(declared, check.Declared);
        Assert.Equal($"a route SUMO cannot follow stops the run (ignore-route-errors '{declared}')", check.ToString());
    }

    [Fact]
    public void AConfigurationThatDoesNotSetItRunsAndSaysSo()
    {
        RouteErrorCheck check = RouteErrorCheck.Require(Configuration("<time-to-teleport value=\"-1\"/>"));

        Assert.Null(check.Declared);
        Assert.Equal("a route SUMO cannot follow stops the run (ignore-route-errors not set)", check.ToString());
    }

    [Fact]
    public void AValueSumoDoesNotReadAsABooleanIsRefused()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => RouteErrorCheck.Require(Configuration("<ignore-route-errors value=\"sometimes\"/>")));

        Assert.Contains("which SUMO does not read as true or false", refused.Message);
    }

    [Fact]
    public void AnOptionSetTwiceIsRefused()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => RouteErrorCheck.Require(Configuration(
                "<ignore-route-errors value=\"false\"/><ignore-route-errors value=\"true\"/>")));

        Assert.Contains("sets ignore-route-errors 2 times", refused.Message);
    }

    [Fact]
    public void BothShippedScenariosAndTheFixtureLeaveRouteErrorsToStopSumo()
    {
        foreach (string scenario in new[]
                 {
                     ScenarioLockCheckTests.RepositoryFile("Import", "Arapahoe_I25_UnderpassDwell.sumocfg"),
                     ScenarioLockCheckTests.RepositoryFile("Import", "Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg"),
                     CoSimFixtures.RightAngleTurnScenario,
                 })
        {
            Assert.Null(RouteErrorCheck.Require(scenario).Declared);
        }
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
                          "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<configuration>\n    <input>\n"
                          + "        <net-file value=\"RightAngleTurn.net.xml\"/>\n    </input>\n"
                          + $"    <processing>{processing}</processing>\n</configuration>\n");
        return path;
    }
}
