using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session checks its scenario's compile lock, its teleport setting and how SUMO would edit its
/// population before SUMO is started, and records each on the run report.
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

        // The fixture's lock predates the compiler fixing a lane-change duration, and its configuration
        // leaves SUMO's instantaneous default; the report says both.
        Assert.Contains("  processing       time-to-teleport '-1', lanechange.duration '(not recorded)'", report);
        Assert.Contains("lane changes       INSTANTANEOUS: a lane width crossed inside one 0.05 s step "
                        + "(lanechange.duration not set, so SUMO's default of 0 s)", report);

        // Its lock names no plan, and the run binds none and says so.
        Assert.Null(session.Report.CompileLock.Plan);
        Assert.Contains("  supervision plan none: the lock names no supervision plan, so the run binds no "
                        + "supervision", report);

        // Its compile ran it in SUMO alone, and the report says what that found.
        Assert.Contains("  dry run          ran with SUMO 1.27.0 over 60 s: 4 vehicles loaded, 4 inserted, 0 "
                        + "discarded, 0 waiting at the end; 1 of 1 planned vehicles inserted; 0 collisions", report);
    }

    [RequiresSumoFact]
    public void ASessionOnAScenarioWhoseCompileSkippedTheDryRunIsRefusedBeforeSumoStartsUnlessAccepted()
    {
        // Verbose, so a SUMO that was launched would have written to the console.
        using CompiledFixture compiled = CompiledFixture.Write(configuration: VerboseConfiguration());
        JsonObject document = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        document["dry_run"] = new JsonObject
        {
            ["ran"] = false,
            ["reason"] = "skipped at the author's request: nothing established that every vehicle the plan names "
                         + "enters the run",
        };
        compiled.WriteLock(document);
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<string> console = [];
        SumoDriveSessionOptions options = Options(compiled.Scenario, world);
        options.TickWorld = null;
        options.World = carla;
        options.SumoOutput = console.Add;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Contains("was compiled without its SUMO-only run", refused.Message);
        Assert.Contains("skipped at the author's request", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Empty(console);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.LayerWrites);
        Assert.Empty(carla.SolarWrites);

        // Accepted, it runs, and the report and the run manifest say so in the lock's words.
        options.AcceptSkippedDryRun = true;
        options.RunManifestPath = Path.Combine(compiled.Directory, "truth", "manifest.jsonl");
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            Assert.True(session.Report.CompileLock.SkippedDryRunAccepted);
            Assert.False(session.Report.CompileLock.DryRunRan);
            Assert.Contains("  dry run          SKIPPED at the compile (skipped at the author's request: nothing "
                            + "established that every vehicle the plan names enters the run), accepted explicitly",
                            session.Report.ToString());
        }

        using JsonDocument opened = JsonDocument.Parse(File.ReadLines(options.RunManifestPath).First());
        JsonElement scenario = opened.RootElement.GetProperty("scenario");
        Assert.False(scenario.GetProperty("dry_run_ran").GetBoolean());
        Assert.True(scenario.GetProperty("skipped_dry_run_accepted").GetBoolean());

        // Started this time, and it said so: the empty console above was a SUMO never launched.
        Assert.Contains(console, line => line.Contains("Starting server", StringComparison.Ordinal));
    }

    [RequiresSumoFact]
    public void ASessionOnACompiledScenarioHoldsThePlanItsLockBindsOnItsReport()
    {
        using CompiledFixture compiled = CompiledFixture.Write(plan: true);
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        using SumoDriveSession session = SumoDriveSession.Start(Options(compiled.Scenario, world));
        Assert.True(session.Advance());

        string report = session.Report.ToString();
        _output.WriteLine(report);
        SupervisionPlan plan = Assert.IsType<SupervisionPlan>(session.Report.CompileLock.Plan);
        Assert.Equal("RightAngleTurn/turn_nominal", Assert.Single(plan.Instances).InstanceId);
        Assert.Contains("  supervision plan RightAngleTurn: 1 instance (0 annotated, 1 nominal, 0 absent), ", report);
    }

    [RequiresSumoFact]
    public void ASessionWhosePlanWasCompiledAgainstOtherFilesIsRefusedBeforeSumoStartsAndTouchesNothing()
    {
        // Verbose, so a SUMO that was launched would have written to the console (the control in the
        // lock refusal's test above).
        using CompiledFixture compiled = CompiledFixture.Write(configuration: VerboseConfiguration(), plan: true);
        JsonObject plan = compiled.PlanDocument();
        plan["routes_digest"] = new string('e', 64);
        compiled.WritePlan(plan);
        compiled.WriteLock(SolarLeaseTests.PortEpoch());
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<string> console = [];
        SumoDriveSessionOptions options = Options(compiled.Scenario, world);
        options.TickWorld = null;
        options.World = carla;
        options.SumoOutput = console.Add;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Contains("was not compiled against the files the scenario", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Empty(console);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.LayerWrites);
        Assert.Empty(carla.SolarWrites);
    }

    [RequiresSumoFact]
    public void ASessionRecordsTheLaneChangeDurationItsScenarioWasCompiledWithAndRuns()
    {
        string configuration = File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<time-to-teleport value=\"-1\"/>",
            "<time-to-teleport value=\"-1\"/>\n        <lanechange.duration value=\"3\"/>",
            StringComparison.Ordinal);
        using CompiledFixture compiled = CompiledFixture.Write(configuration: configuration);
        JsonObject document = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        document["traffic"]!["processing"]!["lanechange.duration"] = "3";
        compiled.WriteLock(document);
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        using SumoDriveSession session = SumoDriveSession.Start(Options(compiled.Scenario, world));
        Assert.True(session.Advance());

        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.Equal(3.0, session.Report.LaneChanges.Seconds);
        Assert.Contains("  processing       time-to-teleport '-1', lanechange.duration '3'", report);
        Assert.Contains("lane changes       spread over 3 s, moving across at a steady rate, at a 0.05 s step "
                        + "(lanechange.duration '3')", report);
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

    [RequiresSumoFact]
    public void ASessionOnAScenarioThatLetsSumoRemoveVehiclesIsRefusedBeforeSumoStartsAcceptedOrNot()
    {
        string removing = VerboseConfiguration().Replace(
            "<collision.action value=\"warn\"/>", "<collision.action value=\"remove\"/>", StringComparison.Ordinal);
        using CompiledFixture compiled = CompiledFixture.Write(configuration: removing);
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        List<string> console = [];
        SumoDriveSessionOptions options = Options(compiled.Scenario, world);
        options.SumoOutput = console.Add;

        // Accepting teleporting accepts no collision action.
        options.AllowTeleporting = true;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));
        _output.WriteLine(refused.Message);
        Assert.Contains("collision.action is 'remove', under which SUMO takes both vehicles out", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Empty(console);
    }

    [RequiresSumoFact]
    public void ASessionNamesTheDistributionEditsItRanUnderOnItsReport()
    {
        string configuration = File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<time-to-teleport value=\"-1\"/>",
            "<time-to-teleport value=\"-1\"/>\n        <time-to-teleport.highways value=\"5\"/>\n"
            + "        <max-depart-delay value=\"900\"/>",
            StringComparison.Ordinal);
        using CompiledFixture compiled = CompiledFixture.Write(configuration: configuration);
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(compiled.Scenario, world);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));
        Assert.Contains("time-to-teleport.highways is '5'", refused.Message);

        options.AllowTeleporting = true;
        using SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.True(session.Advance());

        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.True(session.Report.DistributionEdits.TeleportingAccepted);
        Assert.Contains("distribution edits collision.action 'warn'; a teleport trigger ENABLED, accepted "
                        + "explicitly; departures as declared, from the seed; demand as written; no vehicle "
                        + "limit; a vehicle not inserted within 900 s discarded", report);
        Assert.Contains("  collision action 'warn' (collision.action 'warn'); a run may carry only warn", report);
        Assert.Contains("  teleport paths   ENABLED, accepted explicitly: time-to-teleport.highways '5', ENABLED "
                        + "after 5 s; time-to-teleport.disconnected not set, so SUMO's default of -1 s; ", report);
        Assert.Contains("  depart offset    none: no departure moved by a random offset (random-depart-offset "
                        + "not set, so SUMO's default of 0 s)", report);
        Assert.Contains("  seeding          from the seed, so the traffic can be run again (random not set)", report);
        Assert.Contains("  demand scale     1, the demand as written (scale not set, so SUMO's default of 1); no "
                        + "vehicle type scales its own", report);
        Assert.Contains("  vehicle limit    none (max-num-vehicles not set, so SUMO's default of -1)", report);
        Assert.Contains("  depart delay     a vehicle not inserted within 900 s of its departure is DISCARDED, and "
                        + "recorded as not inserted (max-depart-delay '900')", report);
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
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };
}
