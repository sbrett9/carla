using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session checks its scenario's compile lock and its teleport setting before SUMO is started,
/// and records both on the run report.
/// </summary>
public sealed class SumoDriveSessionLockTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionLockTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void ASessionOnACompiledScenarioRunsItAndRecordsTheLock()
    {
        using CompiledFixture compiled = CompiledFixture.Write();
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        using SumoDriveSession session = SumoDriveSession.Start(Options(compiled.Scenario, world));
        Assert.True(session.Advance());

        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.True(session.Report.CompileLock.Compiled);
        Assert.Contains("compile lock       RightAngleTurn, compiled by carlacontrol.ScenarioCompiler 1.0.0; "
                        + "configuration, route file, network and catalogue agree with it; so does the epoch",
                        report);
        Assert.Contains("  routed by        duarouter 1.27.0 against the world converter 'Eclipse SUMO "
                        + "netconvert 1.27.0' (SameRelease)", report);
        Assert.Contains("  compiled for     SyntheticSurface.cwp, map SyntheticSurface, network "
                        + CompiledFixture.WorldFingerprint, report);
        Assert.Contains("teleporting        disabled (time-to-teleport '-1')", report);
    }

    [RequiresSumoFact]
    public void ASessionOnAnUncompiledScenarioRunsAndSaysItIsUncompiled()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        using SumoDriveSession session = SumoDriveSession.Start(
            Options(CoSimFixtures.RightAngleTurnScenario, world));
        Assert.True(session.Advance());

        string report = session.Report.ToString();
        Assert.False(session.Report.CompileLock.Compiled);
        Assert.Contains("compile lock       none at ", report);
        Assert.Contains(": an uncompiled scenario, nothing compared", report);
        Assert.DoesNotContain("routed by", report);
    }

    [RequiresSumoFact]
    public void ASessionWhoseScenarioDisagreesWithItsLockIsRefusedBeforeSumoStartsAndTouchesNothing()
    {
        // A verbose configuration: SUMO announces its server the moment it is launched, so a console
        // that stays empty is a SUMO that was never started -- which the control below confirms.
        using CompiledFixture compiled = CompiledFixture.Write(configuration: VerboseConfiguration());
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        List<string> control = [];
        SumoDriveSessionOptions agreeing = Options(compiled.Scenario, world);
        agreeing.SumoOutput = control.Add;
        using (SumoDriveSession.Start(agreeing))
        {
        }

        Assert.Contains(control, line => line.Contains("Starting server", StringComparison.Ordinal));

        JsonObject document = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        document["catalogue"]!["catalogue_digest"] = new string('a', 64);
        compiled.WriteLock(document);

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<string> console = [];
        SumoDriveSessionOptions options = Options(compiled.Scenario, world);
        options.TickWorld = null;
        options.World = carla;
        options.SumoOutput = console.Add;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Contains("is not the one its compile lock", refused.Message);
        Assert.Empty(console);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.LayerWrites);
        Assert.Empty(carla.SolarWrites);
    }

    [RequiresSumoFact]
    public void ASessionOnAScenarioThatTeleportsIsRefusedBeforeSumoStartsUnlessAccepted()
    {
        string teleporting = VerboseConfiguration().Replace(
            "<time-to-teleport value=\"-1\"/>", string.Empty, StringComparison.Ordinal);
        using CompiledFixture compiled = CompiledFixture.Write(configuration: teleporting);
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        List<string> console = [];
        SumoDriveSessionOptions options = Options(compiled.Scenario, world);
        options.SumoOutput = console.Add;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));
        Assert.Contains("it sets no time-to-teleport, and SUMO's default is 300 s", refused.Message);
        Assert.Empty(console);

        options.AllowTeleporting = true;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            Assert.True(session.Report.Teleporting.Accepted);
            Assert.Contains("teleporting        ENABLED after 300 s of blocking (time-to-teleport not set, so "
                            + "SUMO's default of 300 s), accepted explicitly", session.Report.ToString());
        }

        // Started this time, and it said so: the empty console above was a SUMO never launched.
        Assert.Contains(console, line => line.Contains("Starting server", StringComparison.Ordinal));
    }

    /// <summary>
    /// The fixture configuration with SUMO's verbose output on, under which SUMO writes a line to its
    /// console the moment it is launched and before anything connects to it.
    /// </summary>
    private static string VerboseConfiguration() =>
        File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<report>", "<report>\n        <verbose value=\"true\"/>", StringComparison.Ordinal);

    private static SumoDriveSessionOptions Options(string scenario, SyntheticWorld world) =>
        new(scenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 60.0,
                                      hysteresisMetres: 15.0, capacity: 8))
        {
            TickWorld = () => true,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };
}
