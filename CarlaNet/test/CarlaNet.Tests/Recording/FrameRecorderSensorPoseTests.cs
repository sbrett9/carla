// A capture's platform pose is the pose its own image was taken from, which is the camera's pose in
// the client's snapshot of the image's frame -- not the pose in the image's sensor header, and not
// wherever the camera has been moved to by the time the recorder handles the image. See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/12_Operator_Control_Surface.md §9.6.
//
// The header was once taken to be the image's pose, on the understanding that the server stamped it
// in the game-thread call that captures the frame. It did not, on this path: the camera's image is
// read back from the GPU asynchronously and the header was written in the read-back callback, from
// the camera as it stood then, with only the frame number put back to the image's. Measured live on
// Bahonar with a camera moved before every synchronous tick, 89 of 90 headers carried the pose
// commanded for the frame after the image's, while the pixels were their own frame's in all 90 and the
// snapshot of the image's frame held the camera where it was commanded for that frame. An earlier
// version of this test fed the recorder an image whose header held the right pose, which is exactly
// the assumption that was wrong, so it passed against the defect.
//
// The server now stamps the header at capture (ASensor::MakeCaptureHeader), and the recorder no longer
// relies on it: it takes the pose from the snapshot of the image's own frame, checks the header
// against it and counts a disagreement (SensorPoseCheck). A stand-in server here streams a real
// FrameRecorder the defect as it was measured -- an image of frame 100 whose header holds the pose the
// camera was moved to afterwards -- and the cases around it; the depth capture occlusion is measured
// against is checked the same way.
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Tests.Recording;

public sealed class FrameRecorderSensorPoseTests : IAsyncLifetime
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
    private const ActorId Camera = 42;
    private const ActorId DepthCamera = 43;
    private const ulong RenderedFrame = 100;
    private const double DeltaSeconds = 0.05;
    private const int Width = 64;
    private const int Height = 36;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // A constant bare-earth record: the physical height less this is the hae a capture reports.
    private const double AlignOffsetMetres = 12.5;
    private static readonly GeoLocation Origin = new(38.91108, -119.7645965, 1421.4);

    // Where the image was taken from: the camera in the snapshot of its frame.
    private static readonly Transform Rendered =
        new(new Location(120f, -340f, 450f), new Rotation(-60f, 30f, 0f));

    // Where the camera was moved to afterwards: every later snapshot, the actor transform by the time
    // the image is handled, and the header a server stamping after the read-back wrote.
    private static readonly Transform MovedTo =
        new(new Location(-600f, 900f, 300f), new Rotation(-20f, 170f, 5f));

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-sensor-pose-" + Guid.NewGuid().ToString("N"));
    private readonly StandInStreams _streams = new(Patience);
    private readonly SensorPlatformOptions _platform =
        new(90.0, "a-f-A-M-F-Q", "OVERWATCH", $"CARLA-SENSOR-{Camera}");
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
                             () => Ok(new[] { AlignOffsetMetres, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }));
        // The cameras are the only actors, and neither is a vehicle, so no truth record is made.
        _rpc.RegisterHandler<uint[], SuccessResponse<int[]>>("get_actors_by_id", _ => Ok(Array.Empty<int>()));
        await _rpc.StartAsync();
        _client = new CarlaClient("127.0.0.1", port, Patience);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_rpc is not null) await _rpc.DisposeAsync();
        _streams.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task A_Header_Carrying_A_Later_Pose_Is_Overruled_By_The_Snapshot_Of_The_Image_s_Frame_And_Counted()
    {
        // Frame 100 is rendered with the camera at one pose; by frame 103 it has been flown elsewhere,
        // and the image of frame 100 arrives with a header that says it was taken from there.
        await ObserveTheCameraMovedAfter(RenderedFrame, holdRenderedFrame: true);
        Assert.Equal(MovedTo, _client!.GetActorTransform(Camera));

        (FrameRecorder recorder, XElement events) = await RecordOneImage(header: MovedTo);

        AssertPlacedAtRendered(events);
        Assert.Equal(1, recorder.SensorPoseFromSnapshot);
        Assert.Equal(1, recorder.SensorPoseHeaderDisagreed);
        Assert.Equal(0, recorder.SensorPoseFromHeader);

        // The capture is of the image's own frame, and its truth was read as of that frame too.
        Assert.Equal("100", (string?)events.Attribute("tick"));
        Assert.Equal("100", (string?)events.Attribute("telemetry_tick"));
    }

    [Fact]
    public async Task A_Header_That_Agrees_With_The_Snapshot_Of_The_Image_s_Frame_Is_Not_Counted()
    {
        await ObserveTheCameraMovedAfter(RenderedFrame, holdRenderedFrame: true);

        (FrameRecorder recorder, XElement events) = await RecordOneImage(header: Rendered);

        AssertPlacedAtRendered(events);
        Assert.Equal(1, recorder.SensorPoseFromSnapshot);
        Assert.Equal(0, recorder.SensorPoseHeaderDisagreed);
        Assert.Equal(0, recorder.SensorPoseFromHeader);
    }

    [Fact]
    public async Task A_Frame_The_Client_No_Longer_Holds_Is_Placed_From_Its_Header_Never_The_Nearest_Frame_And_Counted_Apart()
    {
        // The client holds 101 to 103, all with the camera moved; frame 100 has gone. The nearest frame
        // held is the camera somewhere else, so the header is the better witness.
        await ObserveTheCameraMovedAfter(RenderedFrame, holdRenderedFrame: false);

        (FrameRecorder recorder, XElement events) = await RecordOneImage(header: Rendered);

        AssertPlacedAtRendered(events);
        Assert.Equal(0, recorder.SensorPoseFromSnapshot);
        Assert.Equal(0, recorder.SensorPoseHeaderDisagreed);
        Assert.Equal(1, recorder.SensorPoseFromHeader);
    }

    [Fact]
    public async Task A_Depth_Capture_Whose_Header_Carries_A_Later_Pose_Is_Projected_From_The_Snapshot_Of_Its_Frame_And_Counted()
    {
        await ObserveTheCameraMovedAfter(RenderedFrame, holdRenderedFrame: true);
        using var occlusion = new OcclusionEstimator(_client!, _streams.Token(DepthStream), WaitForTheDepth,
                                                     DepthCamera);

        await _streams.SendAsync(DepthStream, RenderedFrame, Seconds(RenderedFrame), MovedTo, Image());
        DepthFrame? depth = occlusion.MatchTo(RenderedFrame, Seconds(RenderedFrame), Rendered);

        // Projected from the header, the vehicles of frame 100 would be measured against where the
        // depth camera went next -- or, as here, the pair refused as no longer co-located.
        Assert.NotNull(depth);
        Assert.Equal(Rendered, depth.Transform);
        Assert.Equal(1, occlusion.Matched);
        Assert.Equal(1, occlusion.DepthPoseFromSnapshot);
        Assert.Equal(1, occlusion.DepthPoseHeaderDisagreed);
        Assert.Equal(0, occlusion.DepthPoseFromHeader);
    }

    [Fact]
    public async Task A_Depth_Capture_Whose_Header_Agrees_With_The_Snapshot_Of_Its_Frame_Is_Not_Counted()
    {
        await ObserveTheCameraMovedAfter(RenderedFrame, holdRenderedFrame: true);
        using var occlusion = new OcclusionEstimator(_client!, _streams.Token(DepthStream), WaitForTheDepth,
                                                     DepthCamera);

        await _streams.SendAsync(DepthStream, RenderedFrame, Seconds(RenderedFrame), Rendered, Image());
        DepthFrame? depth = occlusion.MatchTo(RenderedFrame, Seconds(RenderedFrame), Rendered);

        Assert.NotNull(depth);
        Assert.Equal(Rendered, depth.Transform);
        Assert.Equal(1, occlusion.DepthPoseFromSnapshot);
        Assert.Equal(0, occlusion.DepthPoseHeaderDisagreed);
        Assert.Equal(0, occlusion.DepthPoseFromHeader);
    }

    // Long enough for the depth frame to cross the loopback before the match gives up on it.
    private static readonly OcclusionOptions WaitForTheDepth =
        OcclusionOptions.Default with { MatchWaitMilliseconds = (int)Patience.TotalMilliseconds };

    /// The world observer streams the cameras at <see cref="Rendered"/> for <paramref name="frame"/>,
    /// when it is to be held, and at <see cref="MovedTo"/> for the three frames after it.
    private async Task ObserveTheCameraMovedAfter(ulong frame, bool holdRenderedFrame)
    {
        CarlaClient client = _client!;
        await client.StartWorldObserverAsync();
        if (holdRenderedFrame)
            await _streams.SendAsync(ObserverStream, frame, Seconds(frame), MovedTo, EpisodeState(Rendered));
        for (ulong later = frame + 1; later <= frame + 3; later++)
            await _streams.SendAsync(ObserverStream, later, Seconds(later), MovedTo, EpisodeState(MovedTo));
        await Until(() => client.LatestObservedFrame == frame + 3, "the observer reaching frame 103");
    }

    /// Records the camera, streams it the image of frame 100 with <paramref name="header"/> in its
    /// sensor header, and returns the recorder, flushed, with the capture's sidecar.
    private async Task<(FrameRecorder Recorder, XElement Events)> RecordOneImage(Transform header)
    {
        var recorder = new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0, platform: _platform,
                                         cameraActorId: Camera);
        try
        {
            await _streams.SendAsync(CameraStream, RenderedFrame, Seconds(RenderedFrame), header, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        return (recorder, XDocument.Load(Directory.GetFiles(_dir, "*.xml").Single()).Root!);
    }

    /// The sidecar's platform point, hae, azimuth and elevation, and the still's own metadata, are
    /// <see cref="Rendered"/>'s -- and none of it is <see cref="MovedTo"/>'s.
    private void AssertPlacedAtRendered(XElement events)
    {
        XElement track = events.Elements("event").Single(e => (string?)e.Attribute("uid") == _platform.Uid);
        XElement point = track.Element("point")!;
        XElement sensor = track.Element("detail")!.Element("sensor")!;
        double lat = Number(point, "lat"), lon = Number(point, "lon"), hae = Number(point, "hae");
        double azimuth = Number(sensor, "azimuth"), elevation = Number(sensor, "elevation");

        GeoLocation where = Geodesy.CarlaLocalToGeodetic(Origin, Rendered.Location);
        GeoLocation moved = Geodesy.CarlaLocalToGeodetic(Origin, MovedTo.Location);
        Assert.Equal(where.Latitude, lat, 6);
        Assert.Equal(where.Longitude, lon, 6);
        Assert.Equal(where.Altitude - AlignOffsetMetres, hae, 2);
        // CARLA's yaw 30 looks 30 degrees south of east (y is south): a compass azimuth of 120.
        Assert.Equal(120.0, azimuth, 3);
        Assert.Equal(-60.0, elevation, 3);

        Assert.NotEqual(Math.Round(moved.Latitude, 6), Math.Round(lat, 6));
        Assert.NotEqual(Math.Round(moved.Longitude, 6), Math.Round(lon, 6));
        Assert.NotEqual(260.0, azimuth, 3);
        Assert.NotEqual(-20.0, elevation, 3);

        // The still carries the same pose in its own metadata, so the image alone says where it was
        // taken from.
        string chunk = PngText(Directory.GetFiles(_dir, "*.png").Single(), "carla:sensor");
        Assert.Contains($"\"lat\":{where.Latitude.ToString("0.0000000", CultureInfo.InvariantCulture)}", chunk);
        Assert.Contains("\"az_deg\":120,", chunk);
    }

    private static double Seconds(ulong frame) => frame * DeltaSeconds;

    private static double Number(XElement element, string attribute) =>
        double.Parse((string)element.Attribute(attribute)!, CultureInfo.InvariantCulture);

    /// A world observer's payload holding both cameras at <paramref name="cameras"/>: the 124-byte
    /// header of a server that measured no sun, then one 119-byte actor record for each.
    private static byte[] EpisodeState(Transform cameras)
    {
        const int headerSize = 124, actorSize = 119;
        ActorId[] actors = [Camera, DepthCamera];
        var payload = new byte[headerSize + actorSize * actors.Length];
        for (int i = 0; i < actors.Length; i++)
        {
            Span<byte> actor = payload.AsSpan(headerSize + actorSize * i, actorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(actor, actors[i]);
            actor[4] = (byte)ActorState.Active;
            WriteTransform(actor[5..], cameras);
        }
        return payload;
    }

    /// An image's payload -- RGB and depth alike: width, height and field of view, then BGRA pixels.
    private static byte[] Image()
    {
        var payload = new byte[12 + (Width * Height * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, Width);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), Height);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8), 90f);
        payload.AsSpan(12).Fill(0x80);
        return payload;
    }

    private static void WriteTransform(Span<byte> target, Transform transform)
    {
        BinaryPrimitives.WriteSingleLittleEndian(target, transform.Location.X);
        BinaryPrimitives.WriteSingleLittleEndian(target[4..], transform.Location.Y);
        BinaryPrimitives.WriteSingleLittleEndian(target[8..], transform.Location.Z);
        BinaryPrimitives.WriteSingleLittleEndian(target[12..], transform.Rotation.Pitch);
        BinaryPrimitives.WriteSingleLittleEndian(target[16..], transform.Rotation.Yaw);
        BinaryPrimitives.WriteSingleLittleEndian(target[20..], transform.Rotation.Roll);
    }

    /// The text of a PNG's tEXt chunk with this keyword.
    private static string PngText(string path, string keyword)
    {
        byte[] png = File.ReadAllBytes(path);
        for (int at = 8; at + 12 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = Encoding.ASCII.GetString(png, at + 4, 4);
            ReadOnlySpan<byte> data = png.AsSpan(at + 8, length);
            int split = data.IndexOf((byte)0);
            if (type == "tEXt" && split > 0 && Encoding.Latin1.GetString(data[..split]) == keyword)
                return Encoding.Latin1.GetString(data[(split + 1)..]);
            at += 12 + length;
        }
        throw new Xunit.Sdk.XunitException($"no {keyword} chunk in {path}");
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
