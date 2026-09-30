using CarlaNet.Recording;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Each rendered frame's render set is published keyed by the frame the tick produced: the bodies the
/// frame drew, the SUMO vehicle each one drew and that vehicle's type, and nothing for a body parked.
/// </summary>
/// <remarks>
/// The fixture's three measured vehicles run at a render capacity of one, so one body is lent to each
/// of them in turn -- given back by one vehicle and lent to the next between two ticks -- and then
/// parked while the unmeasured vehicle holds the only place. That is the case a recorder reading the
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
        SumoDriveSessionOptions options = Options(world, computed);
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
        var handOvers = new List<(ulong Frame, uint Actor, string From, string To)>();
        bool parkedWhileAnotherIsAdmitted = false;
        RenderSet? before = null;
        for (ulong frame = oldestHeld; frame <= newest; frame++)
        {
            Assert.True(source.TryGetRenderSet(frame, out RenderSet renderSet), $"frame {frame} is not held");
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

            if (before is not null)
            {
                foreach ((uint actor, RenderedVehicle now) in renderSet.ByActor)
                {
                    if (before.TryGet(actor, out RenderedVehicle then) && then.SumoId != now.SumoId)
                    {
                        handOvers.Add((frame, actor, then.SumoId, now.SumoId));
                    }
                }
            }

            parkedWhileAnotherIsAdmitted |= renderSet.Count == 0 && carla.Spawned.Count > 0;
            before = renderSet;
        }

        foreach ((ulong frame, uint actor, string from, string to) in handOvers)
        {
            _output.WriteLine($"frame {frame}: body {actor} from {from} to {to}");
        }

        // A body changed hands between two held frames, and each frame names the vehicle it carried
        // on that frame rather than the one it carried later.
        (ulong handedOver, uint body, string first, string second) = Assert.Single(handOvers, entry => entry.To == "goer");
        Assert.Equal("turner", first);
        Assert.True(source.TryGetRenderSet(handedOver - 1, out RenderSet lastOfTurner));
        Assert.Equal("turner", lastOfTurner.ByActor[body].SumoId);
        Assert.True(source.TryGetRenderSet(handedOver, out RenderSet firstOfGoer));
        Assert.Equal("goer", firstOfGoer.ByActor[body].SumoId);
        Assert.Equal(handedOver, firstOfGoer.ByActor[body].AdmittedTick);
        Assert.Equal("goer", second);

        // And a body that stands parked is in no frame's set, although it is still in the world.
        Assert.True(parkedWhileAnotherIsAdmitted, "no frame had its only body parked");

        // A frame aged out of the history, and one not yet rendered, are answered with nothing.
        Assert.False(source.TryGetRenderSet(oldestHeld - 1, out _));
        Assert.False(source.TryGetRenderSet(newest + 1, out _));
    }

    [RequiresSumoFact]
    public void ASessionThatRendersNoWorldPublishesNoRenderSet()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(world, []);
        options.TickWorld = () => true;
        options.Illumination = null;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 50 && session.Advance(); step++)
        {
        }

        Assert.Null(session.RenderSet.NewestFrame);
        Assert.False(session.RenderSet.TryGetRenderSet(1, out _));
    }

    private static SumoDriveSessionOptions Options(SyntheticWorld world, List<CoSimPoseRecord> computed) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 60.0,
                                      hysteresisMetres: 15.0, capacity: 1))
        {
            OnPose = computed.Add,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };
}
