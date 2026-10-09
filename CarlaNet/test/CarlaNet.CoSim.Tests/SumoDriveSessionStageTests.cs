using CarlaNet.Types.Rpc.Environment;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Every refusal a session raises says how far it had got, so a caller can map it onto an outcome
/// without reading the message; and whatever the stage, the world is given back as it was found.
/// </summary>
public sealed class SumoDriveSessionStageTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-stage-" + Guid.NewGuid().ToString("n"));

    public SumoDriveSessionStageTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void ARefusalOfTheDeclarationsIsAtValidationAndTouchesNothing()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla);
        options.Illumination = null;

        CoSimSessionRefusedException refused = Refusal(options);

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Equal("Validation", refused.StageName);
        Assert.Empty(carla.SettingsWrites);
    }

    [Fact]
    public void ARefusalOfAWorldPackageThatIsNotTheLoadedWorldIsAtValidation()
    {
        using SyntheticWorld world = Fixture();
        using SyntheticWorld rebuilt = SyntheticWorld.Write(
            at => at.X > 0.0 ? 1.0 : 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = rebuilt.AsLoaded() };

        CoSimSessionRefusedException refused = Refusal(Driving(world, carla));

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Empty(carla.SettingsWrites);
    }

    [Fact]
    public void AWorldPackageOfANewerFormatIsRefusedAtValidationByItsVersionAndTouchesNothing()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        // The package as a later release would write it: a manifest format this session does not read.
        using (var archive = System.IO.Compression.ZipFile.Open(world.PackagePath,
                                                                System.IO.Compression.ZipArchiveMode.Update))
        {
            System.IO.Compression.ZipArchiveEntry entry = archive.GetEntry("world.json")!;
            string text;
            using (var reader = new StreamReader(entry.Open()))
            {
                text = reader.ReadToEnd();
            }

            System.Text.Json.Nodes.JsonObject manifest = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
            manifest["FormatVersion"] = 2;
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("world.json",
                                                                    System.IO.Compression.CompressionLevel.NoCompression).Open());
            writer.Write(manifest.ToJsonString());
        }

        CoSimSessionRefusedException refused = Refusal(Driving(world, carla));

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Contains(world.PackagePath, refused.Message);
        Assert.Contains("declares FormatVersion 2", refused.Message);
        Assert.Contains("supports FormatVersion 1 and earlier", refused.Message);
        Assert.Empty(carla.SettingsWrites);
    }

    [RequiresSumoFact]
    public void ANetworkOutsideTheWorldSFrameIsRefusedAtValidationBeforeSumoStarts()
    {
        // Settled from the package alone, so it is refused before SUMO is launched: a verbose
        // configuration would have announced SUMO's server on the console.
        using SyntheticWorld elsewhere = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork,
            "+proj=tmerc +lat_0=39.59431 +lon_0=-104.88449 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs");
        List<string> console = [];
        SumoDriveSessionOptions options = Counting(elsewhere);
        options = options with { ScenarioPath = VerboseScenario() };
        options.SumoOutput = console.Add;

        CoSimSessionRefusedException refused = Refusal(options);

        Assert.Contains("projects as", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Empty(console);
    }

    [RequiresSumoFact]
    public void ASumoThatCannotLoadTheScenarioIsRefusedAtLaunchQuotingSumo()
    {
        // A route file that is not there passes every check that reads the configuration, and SUMO
        // refuses it the moment it loads.
        string scenario = Scenario(File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<route-files value=\"RightAngleTurn.rou.xml\"/>", "<route-files value=\"missing.rou.xml\"/>",
            StringComparison.Ordinal));
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla) with { ScenarioPath = scenario };

        CoSimSessionRefusedException refused = Refusal(options);

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.Launch, refused.Stage);
        Assert.Contains("SUMO could not be started on the scenario", refused.Message);
        Assert.Contains("missing.rou.xml", refused.Message);
        Assert.Empty(carla.SettingsWrites);
    }

    [RequiresSumoFact]
    public void AClockThatDoesNotDivideIsRefusedAtLaunchAndTheWorldGivenBack()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla);
        options.WorldDeltaSeconds = 0.03;

        CoSimSessionRefusedException refused = Refusal(options);

        Assert.Equal(CoSimSessionStage.Launch, refused.Stage);
        Assert.Equal(before, carla.Settings);
    }

    [RequiresSumoFact]
    public void APopulationHeldByAnotherIsRefusedAtAuthority()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Driving(world, carla);
        using PopulationLease ambient = WorldDriveAuthority.ForWorld(options.WorldKey)
            .Acquire(PopulationMode.TrafficManagerAmbient, "TrafficController on the same world");

        PopulationAuthorityHeldException refused = Assert.Throws<PopulationAuthorityHeldException>(
            () => SumoDriveSession.Start(options));

        Assert.Equal(CoSimSessionStage.Authority, refused.Stage);
        Assert.Equal("TrafficController on the same world", refused.HeldBy);
    }

    [Fact]
    public void AHeldPopulationSaysAuthorityOutsideASessionTooAndADeclarationSaysValidation()
    {
        // Ambient traffic checking before it spawns anything, with no session to assign a stage.
        string key = "test://" + Guid.NewGuid().ToString("n");
        using PopulationLease driving = WorldDriveAuthority.ForWorld(key)
            .Acquire(PopulationMode.SumoDrivenPlayback, "a SUMO drive");
        PopulationAuthorityHeldException held = Assert.Throws<PopulationAuthorityHeldException>(
            () => WorldDriveAuthority.ForWorld(key).RequireAvailable(PopulationMode.TrafficManagerAmbient));
        Assert.Equal(CoSimSessionStage.Authority, held.Stage);

        // A declaration refused where it is built, before any session exists.
        CoSimSessionRefusedException declared = Assert.Throws<CoSimSessionRefusedException>(
            () => IlluminationPolicy.Advance(0.0));
        Assert.Equal(CoSimSessionStage.Validation, declared.Stage);
    }

    [RequiresSumoFact]
    public void ASunThatReadsBackOtherThanItWasWrittenIsRefusedAtPreRollAndEverythingGivenBack()
    {
        // A sun that takes the date and the clock and keeps its own zone: the read-back after the
        // lease disagrees on the zone.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        carla.Sun!.IgnoresTimeZoneWrites = true;
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla);

        CoSimSessionRefusedException refused = Refusal(options);

        _output.WriteLine(refused.Message);
        Assert.Contains("did not take the sun the session wrote", refused.Message);
        Assert.Equal(CoSimSessionStage.PreRoll, refused.Stage);
        AssertGivenBack(carla, before, options);
    }

    [RequiresSumoFact]
    public void ASunThatDisagreesWhenTheWindowOpensIsRefusedAtPreRoll()
    {
        // The package's origin is 0, 0; the world's sun is computed for the port.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        carla.Sun!.Latitude = 27.15012;
        carla.Sun.Longitude = 56.18065;
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla);

        SolarAuditFailedException refused = Assert.Throws<SolarAuditFailedException>(
            () => SumoDriveSession.Start(options));

        Assert.Equal(CoSimSessionStage.PreRoll, refused.Stage);
        AssertGivenBack(carla, before, options);
    }

    [RequiresSumoFact]
    public void ASumoThatFailsDuringTheFastForwardIsRefusedAtPreRollQuotingSumo()
    {
        // SUMO reads its route file a few seconds ahead of the clock, and meets a route it cannot
        // build at t = 20: it quits, and the fast-forward to 40 s finds the connection gone.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        EpisodeSettings before = carla.Settings;
        SumoDriveSessionOptions options = Driving(world, carla) with { ScenarioPath = LateRouteScenario() };
        options.WarmUpToSimulatedSecond = 40.0;

        CoSimSessionRefusedException refused = Refusal(options);

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.PreRoll, refused.Stage);
        Assert.Contains("SUMO failed while it was fast-forwarded to 40 s", refused.Message);
        Assert.Contains("The edge 'nowhere' within the route for vehicle 'late' is not known", refused.Message);
        Assert.IsType<Sumo.FatalTraCIError>(refused.InnerException);
        AssertGivenBack(carla, before, options);
    }

    [RequiresSumoFact]
    public void AWorldThatStopsProducingFramesStopsTheRunInTheWindow()
    {
        using SyntheticWorld world = Fixture();
        using SumoDriveSession session = SumoDriveSession.Start(Counting(world, () => false));

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(() => session.Advance());

        Assert.Equal(CoSimSessionStage.Window, refused.Stage);
    }

    [RequiresSumoFact]
    public void ASunAnotherClientMovesStopsTheRunInTheWindow()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        using SumoDriveSession session = SumoDriveSession.Start(Driving(world, carla));
        session.Advance();

        carla.Sun!.SolarTime += 1.0 / 60.0;
        SolarAuditFailedException refused = Assert.Throws<SolarAuditFailedException>(() => session.Advance());

        Assert.Equal(CoSimSessionStage.Window, refused.Stage);
    }

    [RequiresSumoFact]
    public void ASumoThatFailsMidRunStopsTheRunInTheWindowQuotingSumo()
    {
        // The same route SUMO cannot build at t = 20, met by a session that started at zero.
        using SyntheticWorld world = Fixture();
        using SumoDriveSession session = SumoDriveSession.Start(
            Counting(world) with { ScenarioPath = LateRouteScenario() });

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(() =>
        {
            for (int step = 0; step < 1000; step++)
            {
                session.Advance();
            }
        });

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.Window, refused.Stage);
        Assert.StartsWith("SUMO failed at simulated ", refused.Message);
        Assert.Contains("The edge 'nowhere' within the route for vehicle 'late' is not known", refused.Message);
        Assert.IsType<Sumo.FatalTraCIError>(refused.InnerException);
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

    private static void AssertGivenBack(RecordedWorld carla, EpisodeSettings before,
                                        SumoDriveSessionOptions options)
    {
        Assert.Equal(before, carla.Settings);
        Assert.Equal(0, carla.Ticks);
        Assert.Null(WorldDriveAuthority.ForWorld(options.WorldKey).CurrentHolder);
        Assert.Equal((13.0, 2019, 9, 21), (carla.Sun!.SolarTime, carla.Sun.Year, carla.Sun.Month,
                                           carla.Sun.Day));
    }

    private static CoSimSessionRefusedException Refusal(SumoDriveSessionOptions options) =>
        Assert.Throws<CoSimSessionRefusedException>(() => SumoDriveSession.Start(options));

    private static SyntheticWorld Fixture() =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

    /// <summary>A configuration beside copies of the fixture's network and routes; its full path.</summary>
    private string Scenario(string configuration)
    {
        File.Copy(CoSimFixtures.RightAngleTurnNetwork, Path.Combine(_directory, "RightAngleTurn.net.xml"), true);
        File.Copy(Path.ChangeExtension(CoSimFixtures.RightAngleTurnScenario, ".rou.xml"),
                  Path.Combine(_directory, "RightAngleTurn.rou.xml"), true);
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("n") + ".sumocfg");
        File.WriteAllText(path, configuration);
        return path;
    }

    /// <summary>
    /// A scenario on the fixture network whose route file carries, behind a vehicle departing at
    /// t = 20, one whose route names an edge the network does not have. SUMO reads route files a few
    /// seconds ahead of its clock, so it meets that route mid-run -- measured at t = 20 -- and quits
    /// with an error naming it.
    /// </summary>
    private string LateRouteScenario()
    {
        string routes = Path.Combine(_directory, "late.rou.xml");
        File.WriteAllText(routes,
            "<routes>\n"
            + "    <vType id=\"measured_truck\" vClass=\"truck\" length=\"7.0184\" width=\"2.5074\" "
            + "maxSpeed=\"30.00\" sigma=\"0\" tau=\"1.0\"><param key=\"carla:blueprint\" "
            + "value=\"vehicle.fuso.mitsubishi\"/></vType>\n"
            + "    <vehicle id=\"early\" type=\"measured_truck\" depart=\"0\" departLane=\"0\">"
            + "<route edges=\"approach ahead\"/></vehicle>\n"
            + "    <vehicle id=\"fence\" type=\"measured_truck\" depart=\"20\" departLane=\"0\">"
            + "<route edges=\"approach ahead\"/></vehicle>\n"
            + "    <vehicle id=\"late\" type=\"measured_truck\" depart=\"30\" departLane=\"0\">"
            + "<route edges=\"approach nowhere\"/></vehicle>\n"
            + "</routes>\n");
        return Scenario(File.ReadAllText(CoSimFixtures.RightAngleTurnScenario)
            .Replace("<route-files value=\"RightAngleTurn.rou.xml\"/>", "<route-files value=\"late.rou.xml\"/>",
                     StringComparison.Ordinal)
            .Replace("<step-length value=\"0.05\"/>",
                     "<step-length value=\"0.05\"/>\n        <route-steps value=\"5\"/>",
                     StringComparison.Ordinal));
    }

    /// <summary>The fixture scenario with SUMO's verbose output on, which announces its launch.</summary>
    private string VerboseScenario() =>
        Scenario(File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<report>", "<report>\n        <verbose value=\"true\"/>", StringComparison.Ordinal));

    private static SumoDriveSessionOptions Counting(SyntheticWorld world, Func<bool>? tick = null) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = tick ?? (() => true),
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
}
