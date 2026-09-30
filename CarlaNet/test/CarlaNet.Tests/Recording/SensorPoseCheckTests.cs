// The pose a sensor's image was taken from is the sensor's in the snapshot of the image's own frame,
// with the image header checked against it: see SensorPoseCheck, and FrameRecorderSensorPoseTests for
// the same property through a real recorder.
using CarlaNet.Recording;
using CarlaNet.Transport;

namespace CarlaNet.Tests.Recording;

public class SensorPoseCheckTests
{
    private const ActorId Sensor = 42;

    private static readonly Transform AtFrame =
        new(new Location(120f, -340f, 450f), new Rotation(-60f, 30f, 0f));

    private static readonly Transform NextFrame =
        new(new Location(-600f, 900f, 300f), new Rotation(-20f, 170f, 5f));

    private static SnapshotHistory Holding(ulong frame, Transform sensor)
    {
        var history = new SnapshotHistory();
        history.Retain(frame, new Dictionary<ActorId, ActorSnapshot>
        {
            [Sensor] = new ActorSnapshot { Id = Sensor, State = ActorState.Active, Transform = sensor },
        });
        return history;
    }

    [Fact]
    public void A_Header_Carrying_Another_Pose_Yields_The_Snapshot_s_And_Is_Counted()
    {
        var check = new SensorPoseCheck(Holding(100, AtFrame).Nearest, Sensor);

        Assert.Equal(AtFrame, check.Resolve(100, NextFrame));
        Assert.Equal((1L, 1L, 0L), (check.FromSnapshot, check.HeaderDisagreed, check.FromHeader));
    }

    [Fact]
    public void A_Header_That_Agrees_Is_Not_Counted()
    {
        var check = new SensorPoseCheck(Holding(100, AtFrame).Nearest, Sensor);

        Assert.Equal(AtFrame, check.Resolve(100, AtFrame));
        Assert.Equal((1L, 0L, 0L), (check.FromSnapshot, check.HeaderDisagreed, check.FromHeader));
    }

    [Fact]
    public void A_Frame_Not_Held_Exactly_Yields_The_Header_Not_The_Nearest_Frame()
    {
        // Frame 101 is held, with the sensor elsewhere; the image is of frame 100.
        var check = new SensorPoseCheck(Holding(101, NextFrame).Nearest, Sensor);

        Assert.Equal(AtFrame, check.Resolve(100, AtFrame));
        Assert.Equal((0L, 0L, 1L), (check.FromSnapshot, check.HeaderDisagreed, check.FromHeader));
    }

    [Fact]
    public void A_Frame_Held_Without_The_Sensor_In_It_Yields_The_Header()
    {
        var check = new SensorPoseCheck(Holding(100, AtFrame).Nearest, Sensor + 1);

        Assert.Equal(NextFrame, check.Resolve(100, NextFrame));
        Assert.Equal((0L, 0L, 1L), (check.FromSnapshot, check.HeaderDisagreed, check.FromHeader));
    }

    [Fact]
    public void One_Orientation_Written_With_Different_Angles_Agrees()
    {
        var location = new Location(10f, 20f, 300f);
        // A yaw of 180 is a yaw of -180.
        Assert.True(Agree(new(location, new Rotation(-45f, 180f, 0f)),
                          new(location, new Rotation(-45f, -180f, 0f))));
        // Looking straight down, yaw and roll trade one for the other: only their sum orients the view.
        Assert.True(Agree(new(location, new Rotation(-90f, 30f, 0f)),
                          new(location, new Rotation(-90f, 0f, 30f))));
        // But a view turned about the vertical is a different view.
        Assert.False(Agree(new(location, new Rotation(-90f, 30f, 0f)),
                           new(location, new Rotation(-90f, 0f, 0f))));
    }

    [Fact]
    public void The_Tolerances_Separate_Encoding_Noise_From_A_Different_Pose()
    {
        var rotation = new Rotation(-60f, 30f, 0f);
        var here = new Transform(new Location(1200f, -3400f, 450f), rotation);
        Assert.True(Agree(here, new(new Location(1200.005f, -3400f, 450f), rotation)));
        Assert.False(Agree(here, new(new Location(1200.02f, -3400f, 450f), rotation)));
        Assert.True(Agree(here, new(here.Location, new Rotation(-60.005f, 30f, 0f))));
        Assert.False(Agree(here, new(here.Location, new Rotation(-60.02f, 30f, 0f))));
    }

    private static bool Agree(Transform a, Transform b) =>
        SensorPoseCheck.Agree(a, b, SensorPoseCheck.DefaultToleranceMetres, SensorPoseCheck.DefaultToleranceDegrees);
}
