using CarlaNet.Recording;
using CarlaNet.Types.Geom;
using Xunit.Abstractions;

using ActorId = uint;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session given cameras renders what they look at: from the pass after a camera is registered, and
/// by the circle again from the pass after it is removed, with every vehicle first drawn clear of the
/// view and every frame's render set still exactly the bodies that frame drew.
/// </summary>
/// <remarks>
/// <para>One camera looks straight down from 30 m over the fixture's east arm, CARLA (70, 3), with 60
/// degrees across a square picture: the ground 17.3 m either side of that point. The lead is half a
/// second and the lag one, so on the fixture's 0.05 s step a vehicle is admitted within
/// 10.25 + 2 + v/2 m of the view -- the catalogue's longest body reaches 10.25 m from its bumper -- and
/// kept within 47.25 m of it. <c>turner</c> drives through that view; <c>goer</c>, straight on 51 m
/// west of it, and <c>changer</c>, turning west, never come within reach. The circle behind the camera
/// takes in the whole network.</para>
/// </remarks>
public sealed class SumoDriveSessionCameraTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionCameraTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void ARegisteredCameraDecidesTheRenderSetUntilItIsRemovedAndTheCircleDecidesEitherSide()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> computed = [];
        List<AdmissionPass> passes = [];
        List<RenderedVehicleInterval> released = [];
        SumoDriveSessionOptions options = Options(world, carla, computed, passes);
        options.OnRelease = released.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        ActorId camera = carla.PlaceCamera(Pose(70, 3, 30, -90, 0), 1000, 1000, 60.0);
        session.AddCamera(camera);
        Assert.Equal([camera], session.Cameras);

        int registeredAt = passes.Count;
        long firstCameraTick = session.Report.Ticks;
        for (int step = 0; step < 200 && session.Advance(); step++)
        {
        }

        Assert.Equal((ulong)session.Report.Ticks, carla.PoseFramesAsked[^1]);
        long lastCameraTick = session.Report.Ticks;
        int removedAt = passes.Count;
        Assert.True(session.RemoveCamera(camera));
        for (int step = 0; step < 100 && session.Advance(); step++)
        {
        }

        _output.WriteLine(session.Report.ToString());

        // The two passes made while starting, before anything could be registered, and the ones after
        // the camera went, are the circle's; every one between is the camera's.
        Assert.All(passes.Take(registeredAt), pass => Assert.Equal(RenderSetRule.Circle, pass.Rule));
        Assert.All(passes.Skip(registeredAt).Take(removedAt - registeredAt), pass =>
        {
            Assert.Equal(RenderSetRule.Cameras, pass.Rule);
            Assert.Equal(1, pass.Cameras);
        });
        Assert.All(passes.Skip(removedAt), pass => Assert.Equal(RenderSetRule.Circle, pass.Rule));
        Assert.Contains(passes.Skip(registeredAt).Take(removedAt - registeredAt), pass => pass.Held > 0);
        Assert.Contains(passes.Skip(removedAt), pass => pass.Admitted == pass.Subscribed && pass.Admitted > 1);

        // The report says what followed which camera, and where each view was capped.
        VehicleCatalogue catalogue = VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);
        CameraFootprint footprint = session.Report.CameraFootprints[camera];
        Assert.Equal(new CameraOptics(1000, 1000, 60.0).RangeAtWhichABodyCovers(catalogue.LongestBodyMetres, 2.0),
                     footprint.RangeCapMetres, 6);
        Assert.Equal(42, session.Report.RenderSetSeed);
        string report = session.Report.ToString();
        Assert.Contains("render set         cameras: inside or approaching", report);
        Assert.Contains($"  camera {camera}: 1000x1000, fov 60 deg; capped at ", report);
        int underTheCircle = registeredAt + (passes.Count - removedAt);
        Assert.Contains($"  seed             42; passes circle {underTheCircle}, cameras {removedAt - registeredAt}",
                        report);

        // Under the camera only the vehicle that drives through its view is ever drawn, and every span it
        // is drawn over opens with its body clear of the view.
        List<CoSimPoseRecord> underTheCamera = computed
            .Where(record => record.Actor != 0 && record.TickIndex > firstCameraTick + 40
                             && record.TickIndex < lastCameraTick)
            .ToList();
        Assert.Contains(underTheCamera, record => record.Pose.VehicleId == "turner");
        Assert.DoesNotContain(underTheCamera, record => record.Pose.VehicleId is "goer" or "changer");
        HashSet<(string, long)> drawn = computed.Where(record => record.Actor != 0)
            .Select(record => (record.Pose.VehicleId, record.TickIndex)).ToHashSet();
        List<CoSimPoseRecord> opened = underTheCamera
            .Where(record => !drawn.Contains((record.Pose.VehicleId, record.TickIndex - 1)))
            .ToList();
        Assert.NotEmpty(opened);

        // The circle's vehicle the camera does not see is held for the lag and then released as having
        // left the render set -- still subscribed while it was held, so not as having left the simulation.
        RenderedVehicleInterval left = Assert.Single(released, interval => interval.ReleaseReason
                                                                            == RenderSetReleaseReason.LeftTheRegion);
        Assert.Equal("turner", left.VehicleId);
        Assert.Equal(1.0, left.ReleasedAtSeconds - passes[registeredAt].SimulatedTimeSeconds, 6);
        Assert.All(opened, record => Assert.True(
            footprint.Now.DistanceTo(record.SumoX, -record.SumoY) > catalogue.BodyReachMetres,
            $"{record.Pose.VehicleId} was first drawn {footprint.Now.DistanceTo(record.SumoX, -record.SumoY):0.00} m "
            + $"from the view at tick {record.TickIndex}"));

        // And the truth is still exact: every held frame's set is the bodies posed for it, by vehicle.
        IRenderSetSource source = session.RenderSet;
        ulong newest = (ulong)session.Report.Ticks;
        for (ulong frame = newest - RenderSetFrames.Capacity + 1; frame <= newest; frame++)
        {
            Assert.True(source.TryGetRenderSet(frame, out RenderSet renderSet));
            Assert.Equal(computed.Where(record => record.Actor != 0 && (ulong)record.TickIndex + 1 == frame)
                             .Select(record => (record.Actor, record.Pose.VehicleId)).OrderBy(pair => pair.Actor),
                         renderSet.ByActor.Select(pair => (pair.Key, pair.Value.SumoId)).OrderBy(pair => pair.Key));
        }
    }

    [RequiresSumoFact]
    public void AnActorThatIsNotACameraIsRefusedAndACameraDestroyedWithoutRemovalLeavesTheCircleToDecide()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<AdmissionPass> passes = [];
        SumoDriveSessionOptions options = Options(world, carla, [], passes);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 5 && session.Advance(); step++)
        {
        }

        // Neither an id the world has never heard of nor one of the session's own bodies is a camera.
        Assert.Throws<ArgumentException>(() => session.AddCamera(9999));
        Assert.NotEmpty(carla.Spawned);
        Assert.Throws<ArgumentException>(() => session.AddCamera(1));
        Assert.Empty(session.Cameras);

        ActorId camera = carla.PlaceCamera(Pose(70, 3, 30, -90, 0), 1000, 1000, 60.0);
        session.AddCamera(camera);
        session.Advance();
        Assert.Equal(RenderSetRule.Cameras, passes[^1].Rule);

        carla.DestroyCamera(camera);
        session.Advance();
        Assert.Equal(RenderSetRule.Circle, passes[^1].Rule);
        Assert.Equal(1, session.Report.CameraPosesUnread);
        Assert.Equal([camera], session.Cameras);
    }

    [RequiresSumoFact]
    public void ASessionThatRendersNoWorldHasNoCameraToFollow()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        SumoDriveSessionOptions options = Options(world, null, [], []);
        options.TickWorld = () => true;
        options.Illumination = null;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.Throws<InvalidOperationException>(() => session.AddCamera(1));
        Assert.True(session.Advance());
        Assert.Equal(RenderSetRule.Circle, session.Report.LastAdmissionPass!.Rule);
    }

    private static SumoDriveSessionOptions Options(SyntheticWorld world, RecordedWorld? carla,
                                                   List<CoSimPoseRecord> computed, List<AdmissionPass> passes) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new CameraFootprintRenderSetPolicy(
                new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 400.0, hysteresisMetres: 15.0, capacity: 8),
                admitLeadSeconds: 0.5, releaseLagSeconds: 1.0))
        {
            World = carla,
            OnPose = computed.Add,
            OnAdmissionPass = passes.Add,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
        };

    private static Transform Pose(double x, double y, double z, double pitch, double yaw) =>
        new(new Location((float)x, (float)y, (float)z), new Rotation((float)pitch, (float)yaw, 0f));
}
