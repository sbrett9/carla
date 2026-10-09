using System.Globalization;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session refuses a scenario whose configuration lets SUMO teleport a blocked vehicle -- a
/// positive <c>time-to-teleport</c>, or none, which SUMO defaults to 300 s -- unless the run accepts
/// it, and runs one that disables it.
/// </summary>
/// <remarks>
/// The thresholds are the ones measured against SUMO 1.27.0 on the fixture network with a vehicle
/// blocked behind a stopped one: <c>5</c>, <c>00:00:05</c> and <c>0.5</c> teleported it, the option
/// absent teleported it after 300 s, and <c>0</c> and <c>-1</c> never did.
/// </remarks>
public sealed class TeleportingCheckTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-teleport-" + Guid.NewGuid().ToString("n"));

    public TeleportingCheckTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("<time-to-teleport value=\"-1\"/>", -1.0)]
    [InlineData("<time-to-teleport value=\"0\"/>", 0.0)]
    [InlineData("<time-to-teleport v=\"-1\"/>", -1.0)]
    [InlineData("<time-to-teleport>-1</time-to-teleport>", -1.0)]
    [InlineData("<time-to-teleport value=\"00:00:00\"/>", 0.0)]
    [InlineData("<time-to-teleport value=\"0.0004\"/>", 0.0004)]
    public void AConfigurationThatDisablesTeleportingRuns(string option, double seconds)
    {
        TeleportingCheck check = TeleportingCheck.Require(Configuration(option), allowTeleporting: false);

        Assert.False(check.Enabled);
        Assert.False(check.Accepted);
        Assert.Equal(seconds, check.Seconds, 9);
        Assert.StartsWith("disabled (time-to-teleport '", check.ToString());
    }

    [Theory]
    [InlineData("<time-to-teleport value=\"5\"/>", "5", 5.0)]
    [InlineData("<time-to-teleport value=\"0.5\"/>", "0.5", 0.5)]
    [InlineData("<time-to-teleport value=\"00:00:05\"/>", "00:00:05", 5.0)]
    [InlineData("<time-to-teleport v=\"1:00:00:00\"/>", "1:00:00:00", 86400.0)]
    [InlineData("<time-to-teleport>300</time-to-teleport>", "300", 300.0)]
    public void AConfigurationThatEnablesTeleportingIsRefusedNamingTheValue(string option, string declared,
                                                                            double seconds)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => TeleportingCheck.Require(Configuration(option), allowTeleporting: false));

        Assert.Contains($"blocked for {seconds.ToString("0.###", CultureInfo.InvariantCulture)} s "
                        + $"(time-to-teleport is '{declared}')", refused.Message);
        Assert.Contains("SUMO has not been started", refused.Message);
        Assert.Contains("--allow-teleporting", refused.Message);
    }

    [Fact]
    public void AConfigurationThatSetsNoTimeToTeleportGetsSumoSDefaultAndIsRefused()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => TeleportingCheck.Require(Configuration(string.Empty), allowTeleporting: false));

        Assert.Contains("blocked for 300 s (it sets no time-to-teleport, and SUMO's default is 300 s)",
                        refused.Message);
    }

    [Fact]
    public void TeleportingAcceptedExplicitlyRunsAndIsRecordedAsEnabled()
    {
        TeleportingCheck check = TeleportingCheck.Require(Configuration(string.Empty), allowTeleporting: true);

        Assert.True(check.Enabled);
        Assert.True(check.Accepted);
        Assert.Null(check.Declared);
        Assert.Equal("ENABLED after 300 s of blocking (time-to-teleport not set, so SUMO's default of "
                     + "300 s), accepted explicitly", check.ToString());

        // Accepting teleporting where the scenario disables it accepts nothing.
        TeleportingCheck disabled = TeleportingCheck.Require(
            Configuration("<time-to-teleport value=\"-1\"/>"), allowTeleporting: true);
        Assert.False(disabled.Accepted);
    }

    [Fact]
    public void AnOptionSetTwiceOrToSomethingThatIsNotATimeIsRefused()
    {
        CoSimSessionRefusedException twice = Assert.Throws<CoSimSessionRefusedException>(
            () => TeleportingCheck.Require(Configuration(
                "<time-to-teleport value=\"-1\"/><time-to-teleport value=\"-1\"/>"), allowTeleporting: false));
        Assert.Contains("sets time-to-teleport 2 times", twice.Message);

        CoSimSessionRefusedException garbled = Assert.Throws<CoSimSessionRefusedException>(
            () => TeleportingCheck.Require(Configuration("<time-to-teleport value=\"soon\"/>"),
                                           allowTeleporting: false));
        Assert.Contains("'soon', which is not a time SUMO reads", garbled.Message);
    }

    [Fact]
    public void BothShippedScenariosDisableTeleporting()
    {
        foreach (string name in new[]
                 {
                     "Arapahoe_I25_UnderpassDwell.sumocfg",
                     "Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg",
                 })
        {
            TeleportingCheck check = TeleportingCheck.Require(
                ScenarioLockCheckTests.RepositoryFile("Import", name), allowTeleporting: false);
            Assert.Equal("-1", check.Declared);
            Assert.False(check.Enabled);
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
