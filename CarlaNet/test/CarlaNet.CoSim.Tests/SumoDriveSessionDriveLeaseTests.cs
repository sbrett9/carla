using System.Diagnostics;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The session and the world's drive lease: taken before SUMO is started, under a name the server can
/// print to the client it refuses, held for the run, given back on every path out, and -- where the
/// server has no lease to grant -- declared not in force on the report rather than assumed.
/// </summary>
/// <remarks>
/// A second SUMO drive session, or a traffic manager in another process, is what the lease exists to
/// refuse, and nothing in one process can see either; so what these establish is what the session does
/// at its own claim on each of the three answers the server can give, and that the lease is the first
/// thing written to the server and the last given back.
/// </remarks>
public sealed class SumoDriveSessionDriveLeaseTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-drive-lease-" + Guid.NewGuid().ToString("n"));

    public SumoDriveSessionDriveLeaseTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    [RequiresSumoFact]
    public void TheLeaseIsTakenUnderTheHoldersNameWithItsProcessAndHeldForTheRun()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla);
        options.Holder = "a test drive";

        using SumoDriveSession session = SumoDriveSession.Start(options);

        DriveLease lease = Assert.IsType<DriveLease>(session.Drive);
        Assert.True(lease.InForce);
        Assert.StartsWith("a test drive (process ", lease.Holder);
        Assert.Contains($"process {Environment.ProcessId} on {Environment.MachineName}", lease.Holder);
        Assert.Equal(lease.Holder, carla.DriveLeaseHolder);
        Assert.Equal([lease.Holder], carla.DriveLeaseClaims);
        Assert.Equal(lease.Holder, session.Report.DriveLeaseHolder);
        Assert.Null(session.Report.DriveLeaseRefused);
        Assert.Contains($"held as {lease.Holder}", session.Report.ToString());

        for (int step = 0; step < 20 && session.Advance(); step++)
        {
        }

        // Still held while the run goes, and not released by anything the run does.
        Assert.Equal(lease.Holder, carla.DriveLeaseHolder);
        Assert.Empty(carla.DriveLeaseReleases);
    }

    [RequiresSumoFact]
    public void TheLeaseIsGivenBackWhenTheSessionIsDisposedAfterTheWorldIsAsItWasFound()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla);
        SumoDriveSession session = SumoDriveSession.Start(options);
        string holder = session.Drive!.Holder;
        int settingsWritesBeforeDisposal = carla.SettingsWrites.Count;

        session.Dispose();

        Assert.Null(carla.DriveLeaseHolder);
        Assert.Equal([holder], carla.DriveLeaseReleases);
        Assert.True(session.Drive.IsReleased);
        // The settings were given back before the lease was: a traffic system admitted by the release
        // finds the world's clock as it was, not this run's.
        Assert.True(carla.SettingsWrites.Count > settingsWritesBeforeDisposal,
                    "the settings were not given back");
    }

    [RequiresSumoFact]
    public void ALeaseAnotherClientHoldsRefusesAtAuthorityNamingTheHolderBeforeSumoIsStarted()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld
        {
            Loaded = world.AsLoaded(),
            DriveLeaseHolder = "run_sumo_drive.py (process 7 on ELSEWHERE)",
        };
        SumoDriveSessionOptions options = Verbose(Driving(world, carla), out List<string> console);

        PopulationAuthorityHeldException refused = Assert.Throws<PopulationAuthorityHeldException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.Authority, refused.Stage);
        Assert.Equal("run_sumo_drive.py (process 7 on ELSEWHERE)", refused.HeldBy);
        Assert.Contains("run_sumo_drive.py (process 7 on ELSEWHERE)", refused.Message);
        Assert.Empty(refused.GiveBackFailures);

        // Nothing started and nothing written: SUMO, which would have announced itself on the console,
        // was never launched; the clock and the layers were never taken; the other holder keeps the
        // lease and this process's own lease is free again.
        Assert.Empty(console);
        Assert.Empty(carla.SettingsWrites);
        Assert.Empty(carla.LayerWrites);
        Assert.Equal("run_sumo_drive.py (process 7 on ELSEWHERE)", carla.DriveLeaseHolder);
        Assert.Empty(carla.DriveLeaseReleases);
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
    }

    [RequiresSumoFact]
    public void ASecondSessionOnTheSameWorldIsRefusedWhileTheFirstHoldsIt()
    {
        // Two sessions in one process against one world: the process-local lease refuses the second
        // first, naming the first. With that lease keyed otherwise -- two harnesses, as two processes
        // would be -- the server's lease refuses it instead, naming the first's holder name.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions first = Driving(world, carla);
        first.Holder = "the first drive";
        SumoDriveSessionOptions second = Driving(world, carla) with { WorldKey = "test://" + Guid.NewGuid().ToString("n") };
        second.Holder = "the second drive";

        using SumoDriveSession running = SumoDriveSession.Start(first);
        PopulationAuthorityHeldException refused = Assert.Throws<PopulationAuthorityHeldException>(
            () => SumoDriveSession.Start(second));

        Assert.Equal(CoSimSessionStage.Authority, refused.Stage);
        Assert.Equal(running.Drive!.Holder, refused.HeldBy);
        Assert.StartsWith("the first drive (process ", refused.HeldBy);
        // The first keeps driving: its lease was neither released nor retaken.
        Assert.Equal(running.Drive.Holder, carla.DriveLeaseHolder);
        Assert.Empty(carla.DriveLeaseReleases);
        Assert.True(running.Advance());
    }

    [RequiresSumoFact]
    public void AServerWithoutTheLeaseRunsWithTheLockoutDeclaredNotInForce()
    {
        // A server built before it carried the lease: the run is sound and goes on, and the report says
        // nothing on the server stopped another traffic system, which is the one fact a reader of that
        // run needs.
        using SyntheticWorld world = Fixture();
        const string refusal = "rpclib: server could not find function 'take_drive_lease' with argument count 2.";
        var carla = new RecordedWorld { Loaded = world.AsLoaded(), RefusesDriveLease = refusal };
        SumoDriveSessionOptions options = Driving(world, carla);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 20 && session.Advance(); step++)
        {
        }

        Assert.False(session.Drive!.InForce);
        Assert.Equal(refusal, session.Report.DriveLeaseRefused);
        Assert.NotNull(session.Report.DriveLeaseHolder);
        Assert.Null(session.Report.Stopped);
        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.Contains("NOT HELD", report);
        Assert.Contains(refusal, report);
        Assert.Single(carla.DriveLeaseClaims);

        // Nothing was granted, so nothing is given back at the end.
        session.Dispose();
        Assert.Empty(carla.DriveLeaseReleases);
    }

    [RequiresSumoFact]
    public void AStartRefusedAfterTheLeaseGivesItBack()
    {
        // The clock does not divide, which is found after SUMO starts: the lease was taken before SUMO
        // and is given back with everything else.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla);
        options.WorldDeltaSeconds = 0.03;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Equal(CoSimSessionStage.Launch, refused.Stage);
        Assert.Empty(refused.GiveBackFailures);
        Assert.Null(carla.DriveLeaseHolder);
        Assert.Equal(carla.DriveLeaseClaims, carla.DriveLeaseReleases);
        Assert.Single(carla.DriveLeaseReleases);
    }

    [Fact]
    public void AConnectionThatDropsWhileTheLeaseIsTakenIsRefusedAtAuthorityWithNothingToGiveBack()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var dropped = new IOException("An existing connection was forcibly closed by the remote host.");
        carla.SeverAt(nameof(ICarlaWorld.TakeDriveLease), dropped);
        SumoDriveSessionOptions options = Driving(world, carla);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.Authority, refused.Stage);
        Assert.Equal(CoSimStopCause.WorldConnectionLost, refused.Cause);
        Assert.Same(dropped, refused.InnerException);
        Assert.Contains("taking the world's drive lease", refused.Message);
        // The lease was never granted, so there is nothing on the server to give back, and this
        // process's own lease is free again.
        Assert.Empty(refused.GiveBackFailures);
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
    }

    [RequiresSumoFact]
    public void AReleaseTheServerRefusesIsOneOfTheShutdownsNamedFailures()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSession session = SumoDriveSession.Start(Driving(world, carla));
        // Broken under the run and retaken by someone else.
        carla.DriveLeaseHolder = "a drive that took it after it was broken";

        AggregateException shutdown = Assert.Throws<AggregateException>(session.Dispose);

        Exception failure = Assert.Single(shutdown.InnerExceptions);
        Assert.StartsWith("Could not give back the world's drive lease", failure.Message);
        Assert.Contains("a drive that took it after it was broken", failure.Message);
        // Everything else was given back regardless, SUMO included.
        Assert.True(SpinWait.SpinUntil(() => !ProcessIsAlive(session.Sumo.ProcessId), TimeSpan.FromSeconds(5)),
                    "SUMO was not stopped");
    }

    [RequiresSumoFact]
    public void ASessionWithNoWorldTakesNoLease()
    {
        using SyntheticWorld world = Fixture();
        SumoDriveSessionOptions options = Counting(world);

        using SumoDriveSession session = SumoDriveSession.Start(options);

        Assert.Null(session.Drive);
        Assert.Null(session.Report.DriveLeaseHolder);
        Assert.Null(session.Report.DriveLeaseRefused);
        Assert.DoesNotContain("drive lease", session.Report.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static bool ProcessIsAlive(int? processId)
    {
        if (processId is not { } id)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static SyntheticWorld Fixture() =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

    private static SumoDriveSessionOptions Counting(SyntheticWorld world) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };

    private static SumoDriveSessionOptions Driving(SyntheticWorld world, RecordedWorld carla)
    {
        SumoDriveSessionOptions options = Counting(world);
        options.TickWorld = null;
        options.World = carla;
        return options;
    }

    /// <summary>The same session on the fixture scenario with SUMO's verbose output on, captured.</summary>
    private SumoDriveSessionOptions Verbose(SumoDriveSessionOptions options, out List<string> console)
    {
        List<string> lines = [];
        console = lines;
        File.Copy(CoSimFixtures.RightAngleTurnNetwork, Path.Combine(_directory, "RightAngleTurn.net.xml"), true);
        File.Copy(Path.ChangeExtension(CoSimFixtures.RightAngleTurnScenario, ".rou.xml"),
                  Path.Combine(_directory, "RightAngleTurn.rou.xml"), true);
        string scenario = Path.Combine(_directory, Guid.NewGuid().ToString("n") + ".sumocfg");
        File.WriteAllText(scenario, File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<report>", "<report>\n        <verbose value=\"true\"/>", StringComparison.Ordinal));
        SumoDriveSessionOptions verbose = options with { ScenarioPath = scenario };
        verbose.SumoOutput = line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        };
        return verbose;
    }
}
