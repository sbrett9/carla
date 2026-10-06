// Every vehicle record a recorder writes says whether the vehicle is in this camera's picture, and,
// where its occlusion was not measured, why -- whether or not a depth camera is attached. The owner
// ruled (2026-10-05) that the five occlusion fields may no longer simply be absent: a reader could not
// tell "no occlusion value" from "not hidden". See Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md
// §5.1 and Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md §8.2.
//
// A stand-in server streams a real FrameRecorder a world of four vehicles under a camera looking
// straight down from 100 m -- one under the camera, one far outside the picture, one above the camera
// and one astride the picture's right edge -- and the recorder's sidecar is read once it has flushed.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Tests.Recording;

public sealed class FrameRecorderInFrameTests : IAsyncLifetime
{
    // Every call here returns R<T>, whose success is [[1, value]]. These reproduce that envelope.
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    private const uint ObserverStream = 1;
    private const uint CameraStream = 2;
    private const uint DepthStream = 3;
    private const ulong Frame = 100;
    private const double DeltaSeconds = 0.05;
    private const int Width = 64;
    private const int Height = 36;
    private const float HFovDeg = 90f;      // focal length = Width / 2 = 32 px
    private const int HeaderSize = 124, ActorSize = 119;
    // A recorder holds its camera's name for the whole process, so no other test class records under it.
    private const string RecordedCamera = "IN-FRAME";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly GeoLocation Origin = new(38.91108, -119.7645965, 1421.4);

    // 100 m up, looking straight down: the picture covers 200 m across (+Y to the right) and 112.5 m
    // along (+X up the picture) at the ground.
    private static readonly Transform Nadir = new(new Location(0f, 0f, 100f), new Rotation(-90f, 0f, 0f));

    // Under the camera; 300 m to its right, far outside the picture; 50 m above it; and a truck astride
    // the picture's right edge, which is 100 m to the right at the ground.
    private const ActorId Under = 11, FarRight = 12, Above = 13, AtTheEdge = 14;
    private static readonly (ActorId Id, Transform Pose)[] Vehicles =
    [
        (Under, new Transform(new Location(0f, 0f, 0f), default)),
        (FarRight, new Transform(new Location(0f, 300f, 0f), default)),
        (Above, new Transform(new Location(0f, 0f, 150f), default)),
        (AtTheEdge, new Transform(new Location(0f, 98.5f, 0f), default)),
    ];

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-in-frame-" + Guid.NewGuid().ToString("N"));
    private readonly StandInStreams _streams = new(Patience);
    private readonly SensorPlatformOptions _platform =
        new(HFovDeg, "a-f-A-M-F-Q", RecordedCamera, "CARLA-SENSOR-77");
    private MsgPackRpcServer? _rpc;
    private CarlaClient? _client;

    public async Task InitializeAsync()
    {
        int port = FreeLoopbackPort();
        _rpc = new MsgPackRpcServer(IPAddress.Loopback, port);
        _rpc.RegisterHandler("get_cesium_origin", () => Ok(Origin));
        _rpc.RegisterHandler("get_episode_info",
                             () => Ok(new EpisodeInfo(1UL, new RawToken(_streams.Token(ObserverStream)))));
        _rpc.RegisterHandler("get_bare_earth_reference",
                             () => Ok(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }));
        _rpc.RegisterHandler<uint[], SuccessResponse<Actor[]>>(
            "get_actors_by_id", ids => Ok(ids.Select(Describe).ToArray()));
        await _rpc.StartAsync();
        _client = new CarlaClient("127.0.0.1", port, Patience);
        await _client.StartWorldObserverAsync();
        await _streams.SendAsync(ObserverStream, Frame, Frame * DeltaSeconds, default, Snapshot());
        await Until(() => _client.LatestObservedFrame == Frame, $"the observer reaching frame {Frame}");
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_rpc is not null) await _rpc.DisposeAsync();
        _streams.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task With_No_Depth_Camera_Every_Vehicle_Says_Where_It_Fell_And_Why_It_Is_Unmeasured()
    {
        (FrameRecorder recorder, Dictionary<string, XElement> extras) = await RecordOneImage();

        Assert.False(recorder.MeasuresOcclusion);
        Assert.Equal(4, extras.Count);

        Assert.Equal("wholly", InFrame(extras, Under));
        Assert.Equal("no_depth_camera", Unmeasured(extras, Under));
        Assert.True(int.Parse((string)extras[Uid(Under)].Attribute("apparent_width_px")!) > 0);
        Assert.True(int.Parse((string)extras[Uid(Under)].Attribute("apparent_height_px")!) > 0);

        Assert.Equal("none", InFrame(extras, FarRight));
        Assert.Equal("outside_frame", Unmeasured(extras, FarRight));
        Assert.NotNull(extras[Uid(FarRight)].Attribute("apparent_width_px"));

        Assert.Equal("behind_camera", InFrame(extras, Above));
        Assert.Equal("behind_camera", Unmeasured(extras, Above));
        Assert.Null(extras[Uid(Above)].Attribute("apparent_width_px"));

        Assert.Equal("partly", InFrame(extras, AtTheEdge));
        Assert.Equal("no_depth_camera", Unmeasured(extras, AtTheEdge));

        Assert.All(extras.Values, carla => Assert.Null(carla.Attribute("occlusion")));
        Assert.All(extras.Values, carla => Assert.Null(carla.Attribute("occlusion_level")));
        Assert.All(extras.Values, carla => Assert.Null(carla.Attribute("occlusion_samples")));
    }

    [Fact]
    public async Task A_Recorder_Given_No_Platform_Projects_With_The_Picture_s_Own_Field_Of_View()
    {
        (_, Dictionary<string, XElement> extras) = await RecordOneImage(platform: false);

        Assert.Equal(["wholly", "none", "behind_camera", "partly"],
                     new[] { Under, FarRight, Above, AtTheEdge }.Select(id => InFrame(extras, id)));
        Assert.All(extras.Values, carla => Assert.NotNull(carla.Attribute("occlusion_unmeasured")));
    }

    [Fact]
    public async Task With_A_Depth_Capture_Paired_The_Vehicles_In_The_Picture_Carry_The_Five_Fields_And_No_Reason()
    {
        (FrameRecorder recorder, Dictionary<string, XElement> extras) =
            await RecordOneImage(depth: (Frame, Nadir));

        Assert.True(recorder.MeasuresOcclusion);
        Assert.Equal(1, recorder.OcclusionMeasured);
        Assert.Equal(0, recorder.OcclusionUnmatched);

        // Nothing stands between the camera and the ground, so the vehicles in the picture are unhidden.
        foreach (ActorId id in new[] { Under, AtTheEdge })
        {
            XElement carla = extras[Uid(id)];
            Assert.Equal("0.000", (string?)carla.Attribute("occlusion"));
            Assert.Equal("0", (string?)carla.Attribute("occlusion_level"));
            Assert.True(int.Parse((string)carla.Attribute("occlusion_samples")!) > 0);
            Assert.NotNull(carla.Attribute("apparent_width_px"));
            Assert.NotNull(carla.Attribute("apparent_height_px"));
            Assert.Null(carla.Attribute("occlusion_unmeasured"));
        }
        Assert.Equal("wholly", InFrame(extras, Under));
        Assert.Equal("partly", InFrame(extras, AtTheEdge));

        // The vehicles the picture has no view of are unmeasured for that reason, depth capture or not.
        Assert.Equal("outside_frame", Unmeasured(extras, FarRight));
        Assert.Equal("behind_camera", Unmeasured(extras, Above));
        Assert.Null(extras[Uid(FarRight)].Attribute("occlusion"));
    }

    [Fact]
    public async Task With_No_Depth_Capture_At_All_Every_Vehicle_In_The_Picture_Says_So()
    {
        (FrameRecorder recorder, Dictionary<string, XElement> extras) = await RecordOneImage(depth: null, depthCamera: true);

        Assert.Equal(1, recorder.OcclusionNoDepthCaptures);
        Assert.Equal("no_depth_capture", Unmeasured(extras, Under));
        Assert.Equal("no_depth_capture", Unmeasured(extras, AtTheEdge));
        Assert.Equal("outside_frame", Unmeasured(extras, FarRight));
        Assert.Equal("behind_camera", Unmeasured(extras, Above));
        Assert.All(extras.Values, carla => Assert.Null(carla.Attribute("occlusion")));
    }

    [Fact]
    public async Task With_Only_A_Depth_Capture_Of_Another_Instant_Every_Vehicle_In_The_Picture_Says_So()
    {
        // The image is of frame 200, five seconds after the only depth capture held.
        (FrameRecorder recorder, Dictionary<string, XElement> extras) =
            await RecordOneImage(depth: (Frame, Nadir), imageFrame: 200);

        Assert.Equal(1, recorder.OcclusionDepthOutOfStep);
        Assert.Equal("depth_out_of_step", Unmeasured(extras, Under));
        Assert.Equal("depth_out_of_step", Unmeasured(extras, AtTheEdge));
        Assert.Equal("outside_frame", Unmeasured(extras, FarRight));
        Assert.All(extras.Values, carla => Assert.Null(carla.Attribute("occlusion")));
    }

    [Fact]
    public async Task With_The_Depth_Capture_Taken_From_Another_Pose_Every_Vehicle_In_The_Picture_Says_So()
    {
        var elsewhere = new Transform(new Location(500f, 0f, 100f), new Rotation(-90f, 0f, 0f));
        (FrameRecorder recorder, Dictionary<string, XElement> extras) =
            await RecordOneImage(depth: (Frame, elsewhere));

        Assert.Equal(1, recorder.OcclusionDepthWrongPose);
        Assert.Equal("depth_pose_mismatch", Unmeasured(extras, Under));
        Assert.Equal("depth_pose_mismatch", Unmeasured(extras, AtTheEdge));
        Assert.Equal("behind_camera", Unmeasured(extras, Above));
        Assert.All(extras.Values, carla => Assert.Null(carla.Attribute("occlusion")));
    }

    [Fact]
    public async Task A_Vehicle_Wholly_Beyond_The_Draw_Distance_Is_In_The_Picture_And_Unmeasured_For_That_Reason()
    {
        // A 50 m draw distance from a camera 100 m up: every vehicle on the ground is wholly beyond it,
        // and the distance falls across the one 50 m above the camera.
        (FrameRecorder recorder, Dictionary<string, XElement> extras) =
            await RecordOneImage(depth: (Frame, Nadir), drawDistanceMetres: 50.0);

        Assert.Equal(1, recorder.DrawDistanceCaptures);
        Assert.Equal(3, recorder.VehiclesBeyondDrawDistance);
        Assert.Equal(1, recorder.VehiclesPartlyBeyondDrawDistance);
        Assert.Equal("wholly", InFrame(extras, Under));
        Assert.Equal("wholly", (string?)extras[Uid(Under)].Attribute("beyond_draw_distance"));
        Assert.Equal("beyond_draw_distance", Unmeasured(extras, Under));
        Assert.Equal("beyond_draw_distance", Unmeasured(extras, AtTheEdge));
        // The picture's own geometry comes first: outside the picture, or behind the lens, says that.
        Assert.Equal("outside_frame", Unmeasured(extras, FarRight));
        Assert.Equal("behind_camera", Unmeasured(extras, Above));
        Assert.All(extras.Values, carla => Assert.Null(carla.Attribute("occlusion")));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(180.0)]
    [InlineData(-30.0)]
    [InlineData(double.NaN)]
    public void A_Platform_Whose_Field_Of_View_Cannot_Project_Is_Refused(double hFovDeg)
    {
        var platform = new SensorPlatformOptions(hFovDeg, "a-f-A-M-F-Q", RecordedCamera, "CARLA-SENSOR-77");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0, platform: platform));
    }

    private static string Uid(ActorId id) => $"CARLA-TRUTH-{id}";

    private static string? InFrame(Dictionary<string, XElement> extras, ActorId id) =>
        (string?)extras[Uid(id)].Attribute("in_frame");

    private static string? Unmeasured(Dictionary<string, XElement> extras, ActorId id) =>
        (string?)extras[Uid(id)].Attribute("occlusion_unmeasured");

    /// Records the camera, streams it the depth capture given, if any, and one image, and returns the
    /// recorder, flushed, with every vehicle's truth extras by uid.
    private async Task<(FrameRecorder Recorder, Dictionary<string, XElement> Extras)> RecordOneImage(
        bool platform = true, (ulong Frame, Transform Pose)? depth = null, bool depthCamera = false,
        ulong imageFrame = Frame, double? drawDistanceMetres = null)
    {
        bool withDepth = depth is not null || depthCamera;
        var recorder = new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0,
                                         platform: platform ? _platform : null,
                                         cameraName: platform ? null : RecordedCamera,
                                         depthStreamToken: withDepth ? _streams.Token(DepthStream) : null,
                                         occlusion: withDepth ? WaitForTheDepth : null,
                                         drawDistanceMetres: drawDistanceMetres);
        try
        {
            if (depth is { } capture)
                await _streams.SendAsync(DepthStream, capture.Frame, capture.Frame * DeltaSeconds, capture.Pose, Image());
            await _streams.SendAsync(CameraStream, imageFrame, imageFrame * DeltaSeconds, Nadir, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        XElement events = XDocument.Load(Directory.GetFiles(_dir, "*.xml").Single()).Root!;
        return (recorder, events.Elements("event")
            .Where(e => e.Element("detail")!.Element("_carla") is not null)
            .ToDictionary(e => (string)e.Attribute("uid")!, e => e.Element("detail")!.Element("_carla")!));
    }

    // Long enough for a depth capture to cross the loopback before the match gives up on it, short
    // enough that a frame with none to pair with is written in good time.
    private static readonly OcclusionOptions WaitForTheDepth =
        OcclusionOptions.Default with { MatchWaitMilliseconds = 1000 };

    /// A world observer's payload holding the four vehicles at their poses: the 124-byte header of a
    /// server that measured no sun, then one 119-byte actor record each.
    private static byte[] Snapshot()
    {
        var payload = new byte[HeaderSize + ActorSize * Vehicles.Length];
        for (int i = 0; i < Vehicles.Length; i++)
        {
            Span<byte> actor = payload.AsSpan(HeaderSize + ActorSize * i, ActorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(actor, Vehicles[i].Id);
            actor[4] = (byte)ActorState.Active;
            StandInStreams.WriteTransform(actor[5..], Vehicles[i].Pose);
        }
        return payload;
    }

    /// Three saloons and, at the edge, a truck wide enough for a sample ray to meet its in-picture part.
    private static Actor Describe(uint id) => new(
        id, 0u,
        new ActorDescription(id, id == AtTheEdge ? "vehicle.carlamotors.carlacola" : "vehicle.audi.tt",
        [
            new ActorAttributeValue("base_type", ActorAttributeType.String, id == AtTheEdge ? "truck" : "car"),
            new ActorAttributeValue("number_of_wheels", ActorAttributeType.Int, "4"),
            new ActorAttributeValue("color", ActorAttributeType.RGBColor, "10,20,30"),
            new ActorAttributeValue("role_name", ActorAttributeType.String, "autopilot"),
        ]),
        new BoundingBox(default, id == AtTheEdge ? new Vector3D(4f, 2f, 1.5f) : new Vector3D(2f, 1f, 0.75f), default),
        [], []);

    /// An image's payload -- RGB and depth alike: width, height and field of view, then BGRA pixels. A
    /// depth capture of these bytes reads about 502 m everywhere, farther than any vehicle here.
    private static byte[] Image()
    {
        var payload = new byte[12 + (Width * Height * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, Width);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), Height);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8), HFovDeg);
        payload.AsSpan(12).Fill(0x80);
        return payload;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow >= giveUp)
                throw new TimeoutException($"gave up waiting for {what}");
            await Task.Delay(5);
        }
    }

    /// An ephemeral loopback port. MsgPackRpcServer reports the port it was given rather than the
    /// one the OS bound, so the port is chosen here instead of passing 0.
    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
