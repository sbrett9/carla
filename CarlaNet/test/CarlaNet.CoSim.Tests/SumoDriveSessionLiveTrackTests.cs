using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using CarlaNet.Recording;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session given a live track samples the newest frame of each interval and makes, for each sampled
/// frame, one event per body the frame drew: the sidecar writer's event for that body's truth on that
/// frame, cut to the frame's render set, stamped with the frame's civil instant and drawn with its
/// population's affiliation.
/// </summary>
/// <remarks>
/// Run against a real SUMO on the fixture cross, one world tick to a SUMO step of 0.05 s, with the world
/// a <see cref="RecordedWorld"/> whose truth of a frame is the poses it held as it produced the frame.
/// </remarks>
public sealed class SumoDriveSessionLiveTrackTests
{
    private const int Ticks = 200;

    [RequiresSumoFact]
    public void EachSampleIsOneEventPerBodyTheFrameDrewWrittenFromItsTruthAndStampedWithItsCivilInstant()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var watcher = new FrameWatcher(carla);
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.RightAngleTurnScenario, carla,
                                                  new LiveTrackOptions(staleSeconds: 4.5));
        options.StepObservers.Add(watcher);

        List<JsonElement> samples;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            Assert.Equal(20, session.LiveTrack!.TicksPerSample);
            Assert.Equal(1.0, session.LiveTrack.SampleIntervalSeconds, 9);
            samples = Drive(session, Ticks);
            Assert.Equal(Ticks / 20, session.LiveTrack.Samples);
            Assert.Equal(0, session.LiveTrack.FramesUnpaired);
            Assert.Equal(0, session.LiveTrack.BodiesMissing);
        }

        Assert.Equal(Ticks / 20, samples.Count);
        Assert.Contains(samples, sample => sample.GetProperty("events").GetArrayLength() > 1);
        foreach (JsonElement sample in samples)
        {
            ulong frame = sample.GetProperty("frame").GetUInt64();
            RenderedFrameRecord rendered = watcher.Frames[frame];
            List<VehicleTelemetry> cut = watcher.Cut[frame];
            DateTime civil = rendered.Illumination!.CivilInstant!.Value.UtcDateTime;

            Assert.Equal(CotWriter.Timestamp(civil), sample.GetProperty("time_utc").GetString());
            Assert.Equal(rendered.SimulatedTimeSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                         sample.GetProperty("sim_time_s").GetRawText());
            Assert.True(sample.GetProperty("in_window").GetBoolean());
            Assert.Equal("none", sample.GetProperty("camera").GetString());
            Assert.Equal(JsonValueKind.Null, sample.GetProperty("camera_id").ValueKind);

            // One event per body the frame drew, and each the sidecar writer's for that body's truth.
            List<JsonElement> events = [.. sample.GetProperty("events").EnumerateArray()];
            Assert.Equal(rendered.RenderSet!.ByActor.Values.Select(vehicle => vehicle.SumoId).Order(),
                         events.Select(vehicle => vehicle.GetProperty("sumo_id").GetString()).Order());
            Assert.Equal(cut.Select(record => CotWriter.StreamEvent(record, "n", "CARLA-TRUTH", civil, 4.5)),
                         events.Select(vehicle => vehicle.GetProperty("xml").GetString()));
            foreach (JsonElement vehicle in events)
            {
                XElement xml = XElement.Parse(vehicle.GetProperty("xml").GetString()!);
                Assert.Equal($"CARLA-TRUTH-SUMO-{vehicle.GetProperty("sumo_id").GetString()}",
                             (string?)xml.Attribute("uid"));
                Assert.Equal(CotWriter.Timestamp(civil), (string?)xml.Attribute("time"));
                Assert.Equal(CotWriter.Timestamp(civil.AddSeconds(4.5)), (string?)xml.Attribute("stale"));
                Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("in_view").ValueKind);
            }
        }
    }

    [RequiresSumoTheory]
    [InlineData(1.0, 20)]
    [InlineData(2.0, 10)]
    [InlineData(0.3, 67)]
    [InlineData(50.0, 1)]
    public void TheNewestFrameOfEachIntervalOfWholeTicksIsSampled(double rateHz, long ticksPerSample)
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var watcher = new FrameWatcher(null);
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.RightAngleTurnScenario, carla,
                                                  new LiveTrackOptions(rateHz));
        options.StepObservers.Add(watcher);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.Equal(ticksPerSample, session.LiveTrack!.TicksPerSample);
        List<JsonElement> samples = Drive(session, 140);

        List<ulong> expected = [.. watcher.Frames.Values.Where(frame => (frame.TickIndex + 1) % ticksPerSample == 0)
                                    .Select(frame => frame.Frame)];
        Assert.NotEmpty(expected);
        Assert.Equal(expected, samples.Select(sample => sample.GetProperty("frame").GetUInt64()));
        // The truth is read for the sampled frames and no other.
        Assert.Equal(expected, carla.TruthFramesAsked);
    }

    [Fact]
    public void TheIntervalIsAWholeNumberOfTicksAndAtLeastOne()
    {
        Assert.Equal(20, LiveTrackFeed.TicksPerSampleAt(1.0, 0.05));
        Assert.Equal(1, LiveTrackFeed.TicksPerSampleAt(1000.0, 0.05));
        Assert.Equal(3, LiveTrackFeed.TicksPerSampleAt(1.0, 0.3));
        Assert.Equal(long.MaxValue, LiveTrackFeed.TicksPerSampleAt(1e-300, 0.05));
    }

    [RequiresSumoFact]
    public void AFrameWhoseTruthIsNotHeldIsCountedAndMakesNoSampleAndTheDriveGoesOn()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        carla.TruthNotHeld.Add(40);
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.RightAngleTurnScenario, carla, new LiveTrackOptions());

        using SumoDriveSession session = SumoDriveSession.Start(options);
        List<JsonElement> samples = Drive(session, 100);

        Assert.Equal([20UL, 60UL, 80UL, 100UL], samples.Select(sample => sample.GetProperty("frame").GetUInt64()));
        Assert.Equal(1, session.LiveTrack!.FramesUnpaired);
        Assert.Equal(4, session.LiveTrack.Samples);
        Assert.Null(session.Report.Stopped);
        Assert.Equal(100, session.Report.Ticks);
    }

    [RequiresSumoFact]
    public void EachVehicleIsDrawnWithItsPopulationSAffiliationItsClassOrElseItsType()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        using Populations scenario = Populations.Write();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var map = new Dictionary<string, string> { ["mil_truck"] = "f", ["plain_body"] = "h" };
        SumoDriveSessionOptions options = Options(world, scenario.ScenarioPath, carla,
                                                  new LiveTrackOptions(affiliation: "u", affiliationByPopulation: map));

        using SumoDriveSession session = SumoDriveSession.Start(options);
        List<JsonElement> samples = Drive(session, Ticks);

        Dictionary<string, HashSet<string?>> types = [];
        foreach (JsonElement vehicle in samples.SelectMany(sample => sample.GetProperty("events").EnumerateArray()))
        {
            string sumoId = vehicle.GetProperty("sumo_id").GetString()!;
            if (!types.TryGetValue(sumoId, out HashSet<string?>? seen))
            {
                seen = [];
                types[sumoId] = seen;
            }

            seen.Add((string?)XElement.Parse(vehicle.GetProperty("xml").GetString()!).Attribute("type"));
        }

        // turner's type names the class mil_truck; goer's names none, so its population is its type;
        // changer's class is one the map does not name.
        Assert.Equal(["a-f-G-E-V"], types["turner"]);
        Assert.Equal(["a-h-G-E-V"], types["goer"]);
        Assert.Equal(["a-u-G-E-V"], types["changer"]);
    }

    [RequiresSumoFact]
    public void APrewarmFrameIsSampledAndSaysItIsNotTheWindowS()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.RightAngleTurnScenario, carla, new LiveTrackOptions());
        options.WindowOpensAtSimulatedSecond = 2.0;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        List<JsonElement> samples = Drive(session, 80);

        Assert.Equal([false, false, true, true], samples.Select(sample => sample.GetProperty("in_window").GetBoolean()));
    }

    [RequiresSumoFact]
    public void SamplesBeyondTheBoundAreDroppedOldestFirstAndCounted()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.RightAngleTurnScenario, carla,
                                                  new LiveTrackOptions(rateHz: 20.0));

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int tick = 0; tick < LiveTrackFeed.Capacity + 6; tick++)
        {
            Assert.True(session.Advance());
        }

        List<JsonElement> samples = Take(session);
        Assert.Equal(LiveTrackFeed.Capacity, samples.Count);
        Assert.Equal(Enumerable.Range(7, LiveTrackFeed.Capacity).Select(frame => (ulong)frame),
                     samples.Select(sample => sample.GetProperty("frame").GetUInt64()));
        Assert.Equal(6, session.LiveTrack!.SamplesDropped);
        Assert.Equal(LiveTrackFeed.Capacity + 6, session.LiveTrack.Samples);
        Assert.Empty(Take(session));
    }

    [RequiresSumoFact]
    public void ASessionWithoutALiveTrackTellsTheObserversItWasGivenAndNoOther()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var given = new FrameWatcher(null);
        SumoDriveSessionOptions without = Options(world, CoSimFixtures.RightAngleTurnScenario,
                                                  new RecordedWorld { Loaded = world.AsLoaded() }, null);
        without.StepObservers.Add(given);
        using (SumoDriveSession session = SumoDriveSession.Start(without))
        {
            Assert.Null(session.LiveTrack);
            Assert.Equal(new ISumoStepObserver[] { given }, session.Observers);
        }

        // With one, the live track is told first, ahead of the caller's.
        SumoDriveSessionOptions with = Options(world, CoSimFixtures.RightAngleTurnScenario,
                                               new RecordedWorld { Loaded = world.AsLoaded() }, new LiveTrackOptions());
        with.StepObservers.Add(given);
        using (SumoDriveSession session = SumoDriveSession.Start(with))
        {
            Assert.Equal(new ISumoStepObserver[] { session.LiveTrack!, given }, session.Observers);
        }
    }

    [RequiresSumoFact]
    public void AVehicleACircleLeavesOutIsInNoSample()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var watcher = new FrameWatcher(null);
        List<AdmissionPass> passes = [];
        SumoDriveSessionOptions options = Options(world, CoSimFixtures.RightAngleTurnScenario, carla, new LiveTrackOptions());
        // Around the east arm, which only turner drives: goer goes north and changer west.
        options.RenderSet = new RegionRenderSetPolicy(60.0, 0.0, admitRadiusMetres: 15.0, hysteresisMetres: 5.0);
        options.OnAdmissionPass = passes.Add;
        options.StepObservers.Add(watcher);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        List<JsonElement> samples = Drive(session, Ticks);

        HashSet<string> sampled = [.. samples.SelectMany(sample => sample.GetProperty("events").EnumerateArray())
                                      .Select(vehicle => vehicle.GetProperty("sumo_id").GetString()!)];
        HashSet<string> drawn = [.. watcher.Frames.Values.SelectMany(frame => frame.RenderSet!.ByActor.Values)
                                    .Select(vehicle => vehicle.SumoId)];
        Assert.Contains("turner", sampled);
        Assert.Contains(passes, pass => pass.Population > pass.Admitted);
        Assert.DoesNotContain("goer", drawn);
        Assert.DoesNotContain("changer", drawn);
        Assert.Subset(drawn, sampled);
    }

    [Fact]
    public void ALiveTrackForASessionWithNoWorldIsRefusedBeforeAnythingStarts()
    {
        var options = new SumoDriveSessionOptions(CoSimFixtures.RightAngleTurnScenario, "no-package.cwp",
                                                  CoSimFixtures.VehicleCatalogue, "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            LiveTrack = new LiveTrackOptions(),
        };

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Contains("a live track and no world", refused.Message);
    }

    /// <summary>Advance the session up to a number of ticks, taking every sample it makes after each.</summary>
    internal static List<JsonElement> Drive(SumoDriveSession session, int ticks)
    {
        List<JsonElement> samples = [];
        for (int tick = 0; tick < ticks; tick++)
        {
            bool more = session.Advance();
            samples.AddRange(Take(session));
            if (!more)
            {
                break;
            }
        }

        return samples;
    }

    /// <summary>The samples waiting on the session's live track, parsed.</summary>
    internal static List<JsonElement> Take(SumoDriveSession session) =>
        [.. JsonDocument.Parse(session.LiveTrack!.TakeSamplesJson()).RootElement.Clone().EnumerateArray()];

    internal static SumoDriveSessionOptions Options(SyntheticWorld world, string scenario, RecordedWorld carla,
                                                    LiveTrackOptions? liveTrack) =>
        new(scenario, world.PackagePath, CoSimFixtures.VehicleCatalogue, "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            LiveTrack = liveTrack,
        };

    /// <summary>
    /// Every frame the session rendered, told after the live track; and, where it is given the world,
    /// each frame's truth cut to the frame's render set, as a recorder of the frame cuts it.
    /// </summary>
    internal sealed class FrameWatcher(RecordedWorld? carla) : ISumoStepObserver
    {
        public Dictionary<ulong, RenderedFrameRecord> Frames { get; } = [];

        public Dictionary<ulong, List<VehicleTelemetry>> Cut { get; } = [];

        public void OnSumoStep(SumoStepRecord step)
        {
        }

        public void OnFrameRendered(RenderedFrameRecord frame)
        {
            Frames[frame.Frame] = frame;
            if (carla?.VehicleTruthAt(frame.Frame) is { } truth && frame.RenderSet is { } renderSet)
            {
                Cut[frame.Frame] = [.. renderSet.Select(truth, out _)];
            }
        }

        public void OnSessionEnded(SessionEndRecord end)
        {
        }
    }

    /// <summary>
    /// The fixture's three measured vehicles on the cross, each of its own type: turner's names the class
    /// <c>mil_truck</c>, goer's names no class, and changer's names <c>civ_truck</c>.
    /// </summary>
    private sealed class Populations : IDisposable
    {
        private readonly string _directory;

        private Populations(string directory)
        {
            _directory = directory;
        }

        public string ScenarioPath => Path.Combine(_directory, "RightAngleTurn.sumocfg");

        public static Populations Write()
        {
            string directory = Path.Combine(Path.GetTempPath(), "carlanet-populations-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(directory);
            File.Copy(CoSimFixtures.RightAngleTurnScenario, Path.Combine(directory, "RightAngleTurn.sumocfg"));
            File.Copy(CoSimFixtures.RightAngleTurnNetwork, Path.Combine(directory, "RightAngleTurn.net.xml"));
            const string Body = "vClass=\"truck\" length=\"7.0184\" width=\"2.5074\" maxSpeed=\"30.00\" accel=\"2.60\" "
                                + "decel=\"4.50\" sigma=\"0\" tau=\"1.0\"><param key=\"carla:blueprint\" "
                                + "value=\"vehicle.fuso.mitsubishi\"/>";
            File.WriteAllText(Path.Combine(directory, "RightAngleTurn.rou.xml"), $"""
                <routes>
                    <vType id="mil_body" {Body}<param key="carla:class_id" value="mil_truck"/></vType>
                    <vType id="plain_body" {Body}</vType>
                    <vType id="civ_body" {Body}<param key="carla:class_id" value="civ_truck"/></vType>
                    <route id="right" edges="approach turn_east"/>
                    <route id="left" edges="approach turn_west"/>
                    <route id="straight" edges="approach ahead"/>
                    <vehicle id="turner" type="mil_body" route="right" depart="0.00" departLane="0" departSpeed="max"/>
                    <vehicle id="goer" type="plain_body" route="straight" depart="2.00" departLane="1" departSpeed="max"/>
                    <vehicle id="changer" type="civ_body" route="left" depart="6.00" departLane="0" departSpeed="max"/>
                </routes>
                """);
            return new Populations(directory);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A file SUMO still holds is the operating system's to clean up.
            }
        }
    }
}
