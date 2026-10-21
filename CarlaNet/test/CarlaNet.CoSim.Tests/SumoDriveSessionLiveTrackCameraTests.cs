using System.Text.Json;
using CarlaNet.Recording;
using CarlaNet.Types.Geom;

using ActorId = uint;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A live track following a camera says, on each sample, which of the frame's vehicles are in that
/// camera's picture: the recorder's draw-distance and projection marks, made on the same records from the
/// camera's pose and optics, with nothing sent for a frame whose snapshot does not hold the camera, and
/// nothing about which vehicles get a body changed.
/// </summary>
/// <remarks>
/// Every tick is sampled, so a vehicle crossing the edge of a picture or a draw distance is sampled on
/// the frames it does. The fixture cross's four arms are 100 m long and meet at the origin.
/// </remarks>
public sealed class SumoDriveSessionLiveTrackCameraTests
{
    /// <summary>More ticks than the fixture scenario runs for, so each drive runs to its end.</summary>
    private const int Ticks = 1000;

    [RequiresSumoFact]
    public void TheInViewFlagsAreTheProjectionOfTheSameRecordsThroughTheFollowedCamera()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var watcher = new SumoDriveSessionLiveTrackTests.FrameWatcher(carla);
        SumoDriveSessionOptions options = SumoDriveSessionLiveTrackTests.Options(
            world, CoSimFixtures.RightAngleTurnScenario, carla, new LiveTrackOptions(rateHz: 20.0));
        options.StepObservers.Add(watcher);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        // Thirty metres over the east arm, looking straight down: the ground 17.3 m either side.
        Transform pose = Pose(70, 3, 30, -90);
        ActorId camera = carla.PlaceCamera(pose, 1000, 1000, 60.0);
        session.LiveTrack!.Follow(camera);
        Assert.Equal(camera, session.LiveTrack.FollowedCamera);
        Assert.Equal(new CameraOptics(1000, 1000, 60.0), session.LiveTrack.FollowedOptics);
        List<JsonElement> samples = SumoDriveSessionLiveTrackTests.Drive(session, Ticks);

        var picture = new PinholeCamera(pose, 1000, 1000, 60.0);
        List<bool> flags = [];
        foreach (JsonElement sample in samples)
        {
            ulong frame = sample.GetProperty("frame").GetUInt64();
            Assert.Equal("followed", sample.GetProperty("camera").GetString());
            Assert.Equal(camera, sample.GetProperty("camera_id").GetUInt32());
            IReadOnlyList<VehicleTelemetry> marked = BoxProjector.Mark(watcher.Cut[frame], picture);
            List<JsonElement> events = [.. sample.GetProperty("events").EnumerateArray()];
            Assert.Equal(marked.Select(record => record.Rendered!.SumoId),
                         events.Select(vehicle => vehicle.GetProperty("sumo_id").GetString()));
            Assert.Equal(marked.Select(record => record.InFrame is InFrame.Wholly or InFrame.Partly),
                         events.Select(vehicle => vehicle.GetProperty("in_view").GetBoolean()));
            // The marking decides the flag and adds nothing to the event.
            DateTime civil = watcher.Frames[frame].Illumination!.CivilInstant!.Value.UtcDateTime;
            Assert.Equal(watcher.Cut[frame].Select(record => CotWriter.StreamEvent(record, "n", "CARLA-TRUTH", civil, 3.0)),
                         events.Select(vehicle => vehicle.GetProperty("xml").GetString()));
            flags.AddRange(events.Select(vehicle => vehicle.GetProperty("in_view").GetBoolean()));
        }

        Assert.Contains(true, flags);
        Assert.Contains(false, flags);
        // The pose is each sampled frame's, the optics were read once, and the camera decides no body.
        Assert.Equal(samples.Select(sample => sample.GetProperty("frame").GetUInt64()), carla.PoseFramesAsked);
        Assert.Equal(1, carla.CameraDescriptions);
        Assert.Empty(session.Cameras);
        Assert.Equal(0, session.LiveTrack.SamplesCameraMissing);
    }

    [RequiresSumoFact]
    public void ABodyWhollyBeyondTheDrawDistanceIsNotInViewAndOnePartlyBeyondIs()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        var watcher = new SumoDriveSessionLiveTrackTests.FrameWatcher(carla);
        SumoDriveSessionOptions options = SumoDriveSessionLiveTrackTests.Options(
            world, CoSimFixtures.RightAngleTurnScenario, carla, new LiveTrackOptions(rateHz: 20.0));
        options.DrawDistanceMetres = 110.0;
        options.StepObservers.Add(watcher);

        using SumoDriveSession session = SumoDriveSession.Start(options);
        // A hundred metres over the junction, 120 degrees across: the whole cross is in the picture, and
        // a body more than about 55 m from the junction is beyond the distance.
        Transform pose = Pose(0, 0, 100, -90);
        session.LiveTrack!.Follow(carla.PlaceCamera(pose, 1000, 1000, 120.0));
        List<JsonElement> samples = SumoDriveSessionLiveTrackTests.Drive(session, Ticks);

        var picture = new PinholeCamera(pose, 1000, 1000, 120.0);
        List<(DrawDistanceReach Reach, bool InPicture, bool InView)> seen = [];
        foreach (JsonElement sample in samples)
        {
            ulong frame = sample.GetProperty("frame").GetUInt64();
            // The distance the frame's own set says it was drawn under: none before a body is spawned to
            // carry it.
            double? drawDistance = watcher.Frames[frame].RenderSet!.DrawDistanceMetres;
            IReadOnlyList<VehicleTelemetry> marked = BoxProjector.Mark(
                DrawDistanceCheck.Mark(watcher.Cut[frame], pose.Location, drawDistance, out _, out _), picture);
            List<JsonElement> events = [.. sample.GetProperty("events").EnumerateArray()];
            Assert.Equal(marked.Count, events.Count);
            if (events.Count > 0)
            {
                Assert.Equal(110.0, drawDistance);
            }

            for (int index = 0; index < marked.Count; index++)
            {
                bool inPicture = marked[index].InFrame is InFrame.Wholly or InFrame.Partly;
                bool inView = events[index].GetProperty("in_view").GetBoolean();
                Assert.Equal(inPicture && marked[index].DrawDistance != DrawDistanceReach.Beyond, inView);
                seen.Add((marked[index].DrawDistance, inPicture, inView));
            }
        }

        Assert.Contains((DrawDistanceReach.Beyond, true, false), seen);
        Assert.Contains((DrawDistanceReach.Partly, true, true), seen);
        Assert.Contains((DrawDistanceReach.Inside, true, true), seen);
    }

    [RequiresSumoFact]
    public void ACameraThatGoesIsMissingWithNoFlagsUntilAnotherIsFollowed()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = SumoDriveSessionLiveTrackTests.Options(
            world, CoSimFixtures.RightAngleTurnScenario, carla, new LiveTrackOptions(rateHz: 20.0));

        using SumoDriveSession session = SumoDriveSession.Start(options);
        LiveTrackFeed feed = session.LiveTrack!;
        ActorId first = carla.PlaceCamera(Pose(0, 0, 100, -90), 1000, 1000, 120.0);
        feed.Follow(first);
        List<JsonElement> followed = SumoDriveSessionLiveTrackTests.Drive(session, 40);

        carla.DestroyCamera(first);
        List<JsonElement> missing = SumoDriveSessionLiveTrackTests.Drive(session, 10);

        ActorId second = carla.PlaceCamera(Pose(0, 0, 100, -90), 1280, 720, 90.0);
        feed.Follow(second);
        Assert.Equal(new CameraOptics(1280, 720, 90.0), feed.FollowedOptics);
        List<JsonElement> resumed = SumoDriveSessionLiveTrackTests.Drive(session, 10);

        Assert.True(feed.Unfollow());
        Assert.False(feed.Unfollow());
        List<JsonElement> unfollowed = SumoDriveSessionLiveTrackTests.Drive(session, 10);

        Assert.All(followed, sample => Assert.Equal("followed", sample.GetProperty("camera").GetString()));
        Assert.Contains(followed, sample => sample.GetProperty("events").GetArrayLength() > 0);
        Assert.All(missing, sample =>
        {
            Assert.Equal("missing", sample.GetProperty("camera").GetString());
            Assert.Equal(first, sample.GetProperty("camera_id").GetUInt32());
            Assert.All(sample.GetProperty("events").EnumerateArray(),
                       vehicle => Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("in_view").ValueKind));
        });
        Assert.Contains(missing, sample => sample.GetProperty("events").GetArrayLength() > 0);
        Assert.All(resumed, sample =>
        {
            Assert.Equal("followed", sample.GetProperty("camera").GetString());
            Assert.Equal(second, sample.GetProperty("camera_id").GetUInt32());
            Assert.All(sample.GetProperty("events").EnumerateArray(),
                       vehicle => Assert.Contains(vehicle.GetProperty("in_view").ValueKind,
                                                  new[] { JsonValueKind.True, JsonValueKind.False }));
        });
        Assert.Contains(resumed, sample => sample.GetProperty("events").EnumerateArray()
                                               .Any(vehicle => vehicle.GetProperty("in_view").GetBoolean()));
        Assert.All(unfollowed, sample =>
        {
            Assert.Equal("none", sample.GetProperty("camera").GetString());
            Assert.Equal(JsonValueKind.Null, sample.GetProperty("camera_id").ValueKind);
            Assert.All(sample.GetProperty("events").EnumerateArray(),
                       vehicle => Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("in_view").ValueKind));
        });

        Assert.Equal(missing.Count, feed.SamplesCameraMissing);
        Assert.Equal(2, carla.CameraDescriptions);
        Assert.Null(feed.FollowedCamera);
        Assert.Null(session.Report.Stopped);
    }

    [RequiresSumoFact]
    public void AnActorThatIsNotACameraIsRefusedAndNothingIsFollowed()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = SumoDriveSessionLiveTrackTests.Options(
            world, CoSimFixtures.RightAngleTurnScenario, carla, new LiveTrackOptions());

        using SumoDriveSession session = SumoDriveSession.Start(options);
        SumoDriveSessionLiveTrackTests.Drive(session, 5);

        // Neither an id the world has never heard of nor one of the session's own bodies is a camera.
        Assert.Throws<ArgumentException>(() => session.LiveTrack!.Follow(9999));
        Assert.NotEmpty(carla.Spawned);
        Assert.Throws<ArgumentException>(() => session.LiveTrack!.Follow(1));
        Assert.Null(session.LiveTrack!.FollowedCamera);
    }

    private static Transform Pose(double x, double y, double z, double pitch) =>
        new(new Location((float)x, (float)y, (float)z), new Rotation((float)pitch, 0f, 0f));
}
