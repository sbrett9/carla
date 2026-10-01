using System.Globalization;
using System.Text;
using CarlaNet.Recording;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Each rendered frame's render set is published keyed by the frame the tick produced: the bodies the
/// frame drew, the SUMO vehicle each one drew and that vehicle's type, and nothing for a body parked.
/// Every vehicle SUMO has is drawn, however many there are.
/// </summary>
/// <remarks>
/// The succession fixture runs one measured vehicle after another, so the body the first gives back is
/// lent to the second between two ticks, and in between the only vehicle on the network is one whose
/// type names no measured body, so the body stands parked. That is the case a recorder reading the
/// newest set, or the world's whole actor list, gets wrong: a body named for the vehicle it carries
/// now rather than the one it carried on the frame, and a parked body listed as a vehicle.
/// </remarks>
public sealed class SumoDriveSessionRenderSetTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionRenderSetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void EveryRenderedFrameNamesTheBodiesItDrewAndTheVehicleEachOneDrew()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world, computed);
        options.World = carla;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        IRenderSetSource source = session.RenderSet;
        ulong newest = (ulong)session.Report.Ticks;
        Assert.Equal(newest, source.NewestFrame);
        Assert.True(newest > RenderSetFrames.Capacity, "the run is too short to age a frame out");

        // What each frame drew, from the poses the session wrote: a tick's pose records are the bodies
        // posed for the frame that tick produced, and the recorded world numbers its frames by tick.
        // Every body lent in this run is posed on every tick it is lent, since the fixture's ground
        // covers its whole network.
        Dictionary<ulong, Dictionary<uint, string>> drawn = computed
            .Where(record => record.Actor != 0)
            .GroupBy(record => (ulong)record.TickIndex + 1)
            .ToDictionary(frame => frame.Key,
                          frame => frame.ToDictionary(record => record.Actor, record => record.Pose.VehicleId));

        ulong oldestHeld = newest - RenderSetFrames.Capacity + 1;
        List<(ulong Frame, RenderSet Set)> frames = [];
        bool parkedWhileSumoHasAVehicle = false;
        for (ulong frame = oldestHeld; frame <= newest; frame++)
        {
            Assert.True(source.TryGetRenderSet(frame, out RenderSet renderSet), $"frame {frame} is not held");
            frames.Add((frame, renderSet));
            Dictionary<uint, string> expected = drawn.GetValueOrDefault(frame) ?? [];

            // Exactly the bodies the frame drew, each named for the vehicle it drew on that frame.
            Assert.Equal(expected.OrderBy(pair => pair.Key),
                         renderSet.ByActor.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.SumoId))
                             .OrderBy(pair => pair.Key));
            Assert.All(renderSet.ByActor.Values, vehicle => Assert.Equal("measured_truck", vehicle.VehicleTypeId));

            // A vehicle's rendered span opens on the first frame its body was drawn for it.
            foreach (RenderedVehicle vehicle in renderSet.ByActor.Values)
            {
                ulong opened = frame;
                while (drawn.TryGetValue(opened - 1, out Dictionary<uint, string>? earlier)
                       && earlier.TryGetValue(vehicle.ActorId, out string? held) && held == vehicle.SumoId)
                {
                    opened--;
                }

                Assert.Equal(opened, vehicle.AdmittedTick);
            }

            parkedWhileSumoHasAVehicle |= renderSet.Count == 0 && carla.Spawned.Count > 0;
        }

        // One body, handed from the first vehicle to the second, and each frame names the vehicle it
        // carried on that frame rather than the one it carried later.
        Assert.Single(carla.Spawned);
        (ulong lastOfFirst, RenderSet lastSetOfFirst) = frames.Last(
            entry => entry.Set.ByActor.Values.Any(vehicle => vehicle.SumoId == "first"));
        (ulong firstOfSecond, RenderSet firstSetOfSecond) = frames.First(
            entry => entry.Set.ByActor.Values.Any(vehicle => vehicle.SumoId == "second"));
        _output.WriteLine($"first last drawn on frame {lastOfFirst}, second first drawn on frame {firstOfSecond}");
        uint body = Assert.Single(lastSetOfFirst.ByActor.Keys);
        Assert.Equal("first", lastSetOfFirst.ByActor[body].SumoId);
        Assert.Equal("second", firstSetOfSecond.ByActor[body].SumoId);
        Assert.Equal(firstOfSecond, firstSetOfSecond.ByActor[body].AdmittedTick);

        // And in between the body stood parked: in no frame's set, although it was still in the world
        // and SUMO still had a vehicle -- the one whose type names no measured body.
        Assert.True(firstOfSecond > lastOfFirst + 1, "the body was never parked between its two vehicles");
        Assert.True(parkedWhileSumoHasAVehicle, "no frame had its only body parked");

        // A frame aged out of the history, and one not yet rendered, are answered with nothing.
        Assert.False(source.TryGetRenderSet(oldestHeld - 1, out _));
        Assert.False(source.TryGetRenderSet(newest + 1, out _));
    }

    [RequiresSumoFact]
    public void APopulationAboveAnyFixedCeilingIsDrawnInFullAndThePoolGrowsToHoldIt()
    {
        // Three hundred vehicles on the cross at once, every one parked off its lane at a stop it was
        // inserted at: more than the 128 vehicles and 192 bodies the session once limited a run to.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        using ParkedFleet fleet = ParkedFleet.Write(300);
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<AdmissionPass> passes = [];
        SumoDriveSessionOptions options = Options(fleet.ScenarioPath, world, []);
        options.World = carla;
        options.OnAdmissionPass = passes.Add;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 20 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());

            // Every pass after SUMO inserted the fleet counts all of it, and admits none twice.
            Assert.All(passes.Skip(1), pass => Assert.Equal(300, pass.Population));
            Assert.Equal(300, session.Report.Admissions);
            Assert.Equal(300, session.RenderedVehicleIds.Count);

            // And the newest frame drew every one of them, each on a body of its own.
            Assert.True(session.RenderSet.TryGetRenderSet(session.RenderSet.NewestFrame!.Value, out RenderSet newest));
            Assert.Equal(300, newest.Count);
            Assert.Equal(fleet.VehicleIds.Order(StringComparer.Ordinal),
                         newest.ByActor.Values.Select(vehicle => vehicle.SumoId).Order(StringComparer.Ordinal));
            Assert.Equal(300, session.Report.BodiesSpawned);
            Assert.Equal(0, session.Report.VehicleTicksWithNoMeasuredBody);
            Assert.Contains(LaneInterpolationCase.OffLane, session.Report.InterpolationCases.Keys);
        }

        Assert.Equal(300, carla.Spawned.Count);

        // Every body was spawned as SUMO-driven, so the truth record names SUMO, not the traffic
        // manager, as the authority behind each of them (doc 04 D4.9).
        Assert.Equal(300, carla.SpawnedRoleNames.Count);
        Assert.All(carla.SpawnedRoleNames, role => Assert.Equal("sumo", role));
    }

    [RequiresSumoFact]
    public void EveryFrameTheServerCarriesNamesTheBodiesTheRecorderListsAndParksTheRest()
    {
        // The same run as above, read from the server's side: what the world was told, and what each
        // frame's snapshot carried as a result. A reader in another process -- the live pull, the CoT
        // feed -- sees only that, so it has to be the recorder's set, frame for frame.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world, []);
        options.World = carla;

        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            IRenderSetSource source = session.RenderSet;
            ulong newest = (ulong)session.Report.Ticks;
            ulong oldestHeld = newest - RenderSetFrames.Capacity + 1;

            // The first tick each body was named on: from the next frame on, every snapshot names it.
            Dictionary<uint, long> firstNamed = [];
            foreach ((IReadOnlyList<LentBody> lent, _, long atTick) in carla.RenderSetWrites)
            {
                foreach (LentBody body in lent)
                {
                    firstNamed.TryAdd(body.Actor, atTick);
                }
            }

            HashSet<long> namedAtTick = [.. carla.RenderSetWrites.Select(write => write.AtTick)];
            bool sawAParkedBody = false;
            for (ulong frame = oldestHeld; frame <= newest; frame++)
            {
                Assert.True(source.TryGetRenderSet(frame, out RenderSet recorded));
                PublishedRenderSet? published = carla.PublishedRenderSetOf(frame);
                var lentOnFrame = published?.Lent
                    ?? new Dictionary<uint, (string VehicleId, string VehicleTypeId, ulong AdmittedFrame)>();
                IReadOnlySet<uint> parkedOnFrame = published?.Parked ?? new HashSet<uint>();

                // Lent: exactly the recorder's bodies, each named for the same vehicle and type, with
                // the same first frame of its span.
                Assert.Equal(
                    recorded.ByActor.Values
                        .Select(vehicle => (vehicle.ActorId, vehicle.SumoId, vehicle.VehicleTypeId, vehicle.AdmittedTick))
                        .OrderBy(entry => entry.ActorId),
                    lentOnFrame
                        .Select(pair => (pair.Key, pair.Value.VehicleId, pair.Value.VehicleTypeId, pair.Value.AdmittedFrame))
                        .OrderBy(entry => entry.Key));

                // Parked: every other body the session had named by then, and nothing it never named.
                Assert.Empty(parkedOnFrame.Intersect(lentOnFrame.Keys));
                HashSet<uint> namedByThen = [.. firstNamed.Where(pair => pair.Value < (long)frame).Select(pair => pair.Key)];
                Assert.Equal(namedByThen.Order(), parkedOnFrame.Union(lentOnFrame.Keys).Order());
                sawAParkedBody |= parkedOnFrame.Count > 0;

                // Named only on a change: the tick before a frame named something exactly when that
                // frame's set differs from the frame before's.
                if (frame > oldestHeld)
                {
                    Assert.True(source.TryGetRenderSet(frame - 1, out RenderSet before));
                    Assert.Equal(!SameBodies(before, recorded), namedAtTick.Contains((long)frame - 1));
                }
            }

            Assert.True(sawAParkedBody, "no held frame had a body parked");
            Assert.Equal(carla.RenderSetWrites.Count, session.Report.RenderSetUpdates);
            Assert.True(session.Report.RenderSetUpdates < session.Report.Ticks / 10,
                        $"{session.Report.RenderSetUpdates} changes named over {session.Report.Ticks} ticks");
            Assert.Equal(0, session.Report.RenderSetBodiesNotFound);
            Assert.Null(session.Report.RenderSetRefused);
        }

        // Disposed: every body destroyed, and its naming with it, so the server hides nothing.
        Assert.Equal(0, carla.NamedBodies);
    }

    [RequiresSumoFact]
    public void AServerThatRefusesTheRenderSetIsAskedOnceAndTheRunGoesOn()
    {
        // A server built before it carried a render set has no such call. The run's own truth is cut
        // to the render set in process either way, so the session says what the server said and goes
        // on, rather than asking again on every change or stopping a run whose recording is sound.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        const string refusal = "unknown method 'update_render_set'";
        var carla = new RecordedWorld { Loaded = world.AsLoaded(), RefusesRenderSet = refusal };
        SumoDriveSessionOptions options = Options(CoSimFixtures.SuccessionScenario, world, []);
        options.World = carla;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 400 && session.Advance(); step++)
        {
        }

        Assert.Single(carla.RenderSetWrites);
        Assert.Equal(refusal, session.Report.RenderSetRefused);
        Assert.Equal(0, session.Report.RenderSetUpdates);
        Assert.Null(session.Report.Stopped);
        Assert.Contains(refusal, session.Report.ToString());
        Assert.True(session.RenderSet.TryGetRenderSet(session.RenderSet.NewestFrame!.Value, out _));
    }

    [RequiresSumoFact]
    public void ASessionThatRendersNoWorldPublishesNoRenderSet()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(CoSimFixtures.RightAngleTurnScenario, world, []);
        options.TickWorld = () => true;
        options.Illumination = null;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 50 && session.Advance(); step++)
        {
        }

        Assert.Null(session.RenderSet.NewestFrame);
        Assert.False(session.RenderSet.TryGetRenderSet(1, out _));
    }

    /// <summary>Whether two frames' sets hold the same bodies drawing the same vehicles.</summary>
    private static bool SameBodies(RenderSet first, RenderSet second) =>
        first.Count == second.Count
        && first.ByActor.All(pair => second.TryGet(pair.Key, out RenderedVehicle other)
                                     && other.SumoId == pair.Value.SumoId);

    private static SumoDriveSessionOptions Options(string scenario, SyntheticWorld world,
                                                   List<CoSimPoseRecord> computed) =>
        new(scenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            OnPose = computed.Add,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };

    /// <summary>
    /// A scenario on the fixture's network whose vehicles are all inserted at once, each parked off its
    /// lane at its stop, written to a temporary directory beside a copy of the network.
    /// </summary>
    /// <remarks>
    /// The cross is too small to hold three hundred vehicles on its lanes, and a parked vehicle is off
    /// every lane, so parking is how the fleet fits. Inserted at its stop with no insertion checks, every
    /// vehicle is on the network from the first step; SUMO's collision checks are off because vehicles
    /// inserted at one stop overlap until they have pulled off the lane.
    /// </remarks>
    private sealed class ParkedFleet : IDisposable
    {
        private readonly string _directory;

        private ParkedFleet(string directory, string scenarioPath, IReadOnlyList<string> vehicleIds)
        {
            _directory = directory;
            ScenarioPath = scenarioPath;
            VehicleIds = vehicleIds;
        }

        public string ScenarioPath { get; }

        public IReadOnlyList<string> VehicleIds { get; }

        public static ParkedFleet Write(int count)
        {
            string directory = Path.Combine(Path.GetTempPath(), "carlanet-fleet-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(directory);
            File.Copy(CoSimFixtures.RightAngleTurnNetwork, Path.Combine(directory, "RightAngleTurn.net.xml"));

            string[] lanes = ["approach_0", "approach_1", "ahead_0"];
            List<string> ids = [];
            var routes = new StringBuilder();
            routes.AppendLine("<routes>");
            routes.AppendLine("    <vType id=\"measured_truck\" vClass=\"truck\" length=\"7.0184\" width=\"2.5074\" "
                              + "maxSpeed=\"30.00\"><param key=\"carla:blueprint\" value=\"vehicle.fuso.mitsubishi\"/></vType>");
            routes.AppendLine("    <route id=\"straight\" edges=\"approach ahead\"/>");
            for (int index = 0; index < count; index++)
            {
                string id = $"parked_{index:000}";
                double start = 5.0 + ((index / lanes.Length) % 80);
                ids.Add(id);
                routes.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"    <vehicle id=\"{id}\" type=\"measured_truck\" route=\"straight\" depart=\"0\" "
                    + $"departPos=\"stop\" insertionChecks=\"none\"><stop lane=\"{lanes[index % lanes.Length]}\" "
                    + $"startPos=\"{start:0.0}\" endPos=\"{start + 1.0:0.0}\" parking=\"true\" duration=\"3600\"/></vehicle>"));
            }

            routes.AppendLine("</routes>");
            File.WriteAllText(Path.Combine(directory, "ParkedFleet.rou.xml"), routes.ToString());

            string scenario = Path.Combine(directory, "ParkedFleet.sumocfg");
            File.WriteAllText(scenario, """
                <configuration>
                    <input>
                        <net-file value="RightAngleTurn.net.xml"/>
                        <route-files value="ParkedFleet.rou.xml"/>
                    </input>
                    <time>
                        <begin value="0"/>
                        <end value="3600"/>
                        <step-length value="0.05"/>
                    </time>
                    <processing>
                        <seed value="42"/>
                        <time-to-teleport value="-1"/>
                        <collision.action value="none"/>
                    </processing>
                    <report>
                        <no-step-log value="true"/>
                    </report>
                </configuration>
                """);
            return new ParkedFleet(directory, scenario, ids);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A file still open somewhere is not worth failing a test over.
            }
        }
    }
}
