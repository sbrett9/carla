// A still is written with the truth of its own frame, or not at all (the owner's ruling of 2026-10-05:
// "close the door"). See Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md §8.2.
//
// A camera image arrives some ticks after the world observer's snapshot of its frame, and before this
// the recorder, finding the frame no longer held, served the nearest frame it did hold and stamped
// which as telemetry_tick. Every vehicle moves every tick, so that was another instant's truth beside
// the picture, with nothing downstream made to check the stamp. Now the recorder holds the client's
// snapshots open while it records and releases each frame once an image of a later frame has been
// prepared, so an image finds its own frame however late it arrives; a frame the client never held,
// or has dropped at its capacity, drops the still and counts it, and nothing is written.
//
// A stand-in server streams a real client the snapshots of a vehicle moving frame by frame, and a
// camera's images, and the recorder's files and counters are read once it has flushed.
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

public sealed class FrameRecorderFramePairingTests : IAsyncLifetime
{
    // Every call here returns R<T>, whose success is [[1, value]]. These reproduce that envelope.
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    private const uint ObserverStream = 1;
    private const uint CameraStream = 2;
    private const ActorId Camera = 77;
    private const ActorId Vehicle = 7;
    private const double DeltaSeconds = 0.05;
    private const int HeaderSize = 124;
    private const int ActorSize = 119;
    // A recorder holds its camera's name for the whole process, so no other test class records under it.
    private const string RecordedCamera = "FRAME-PAIRING";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly GeoLocation Origin = new(38.91108, -119.7645965, 1421.4);

    private static readonly Transform CameraPose =
        new(new Location(100f, 200f, 450f), new Rotation(-90f, 0f, 0f));

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-frame-pairing-" + Guid.NewGuid().ToString("N"));
    private readonly StandInStreams _streams = new(Patience);
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
        // The vehicle is the one actor with a truth record; the camera is not a vehicle.
        _rpc.RegisterHandler<uint[], SuccessResponse<Actor[]>>(
            "get_actors_by_id", ids => Ok(ids.Where(id => id == Vehicle).Select(Describe).ToArray()));
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
    public async Task An_Image_Whose_Frame_Is_Held_Is_Written_With_That_Frame_s_Truth_And_Names_No_Other_Frame()
    {
        // By the time the image of frame 100 arrives the observer has delivered 101 to 103, and the
        // vehicle has moved 4 m. The sidecar carries the vehicle where it stood on frame 100.
        await ObserveFrames(100, 103);

        (FrameRecorder recorder, string directory) = await RecordImages(100);

        XElement events = Sidecars(directory).Single();
        Assert.Equal("100", (string?)events.Attribute("tick"));
        Assert.Null(events.Attribute("telemetry_tick"));
        AssertVehicleAt(events, VehiclePose(100));
        Assert.DoesNotContain("telemetry_tick", PngText(Directory.GetFiles(directory, "*.png").Single(), "carla:capture"));
        Assert.Equal(1, recorder.Saved);
        Assert.Equal(0, recorder.FrameUnpaired);
        Assert.Equal(1, recorder.SensorPoseFromSnapshot);
    }

    [Fact]
    public async Task An_Image_Arriving_Long_After_Its_Frame_Is_Paired_Exactly_While_The_Recorder_Holds_The_Frame()
    {
        // The recorder is recording when frames 100 to 200 are observed -- far more than the client keeps
        // idle -- and no image has arrived yet, so none of them may be dropped: the image of frame 100
        // could still arrive, and does. Once it is prepared, frames before it (less the margin) go; once
        // an image of frame 150 is prepared, frames before 146 go.
        CarlaClient client = _client!;
        await client.StartWorldObserverAsync();
        string directory = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        var recorder = new FrameRecorder(client, _streams.Token(CameraStream), directory, 2.0,
                                         cameraActorId: Camera, cameraName: RecordedCamera);
        try
        {
            await ObserveFrames(100, 200);
            Assert.Equal(101, client.RetainedSnapshotFrames);

            await _streams.SendAsync(CameraStream, 100, Seconds(100), CameraPose, Image());
            await Until(() => recorder.Saved == 1, "the first capture being written");
            Assert.Equal(101, client.RetainedSnapshotFrames);

            await _streams.SendAsync(CameraStream, 150, Seconds(150), CameraPose, Image());
            await Until(() => recorder.Saved == 2, "the second capture being written");
            Assert.Equal(200 - (150 - FrameRecorder.FramesKeptBehind) + 1, client.RetainedSnapshotFrames);
        }
        finally
        {
            recorder.Dispose();
        }

        XElement[] events = [.. Sidecars(directory)];
        Assert.Equal(["100", "150"], events.Select(e => (string?)e.Attribute("tick")).Order());
        AssertVehicleAt(events.Single(e => (string?)e.Attribute("tick") == "100"), VehiclePose(100));
        AssertVehicleAt(events.Single(e => (string?)e.Attribute("tick") == "150"), VehiclePose(150));
        Assert.Equal(0, recorder.FrameUnpaired);
        // The recorder's hold is closed with it, and the client keeps its idle few.
        Assert.Equal(SnapshotHistory.DefaultIdleFrames, client.RetainedSnapshotFrames);
    }

    [Fact]
    public async Task An_Image_Of_A_Frame_The_Client_Never_Held_Is_Dropped_And_Counted_And_Nothing_Is_Written()
    {
        // Frame 99 was never observed, so no truth of it exists; frame 110 was, and is written after it
        // (half a second on, past the 2 Hz decimation the dropped image still counts for).
        await ObserveFrames(100, 110);

        string directory = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        var recorder = new FrameRecorder(_client!, _streams.Token(CameraStream), directory, 2.0,
                                         cameraActorId: Camera, cameraName: RecordedCamera);
        try
        {
            await _streams.SendAsync(CameraStream, 99, Seconds(99), CameraPose, Image());
            await Until(() => recorder.FrameUnpaired == 1, "the capture being dropped");
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Equal(0, recorder.Saved);
            Assert.Equal(0, recorder.Dropped);

            await _streams.SendAsync(CameraStream, 110, Seconds(110), CameraPose, Image());
            await Until(() => recorder.Saved == 1, "the next capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        XElement events = Sidecars(directory).Single();
        Assert.Equal("110", (string?)events.Attribute("tick"));
        AssertVehicleAt(events, VehiclePose(110));
        Assert.Equal(1, recorder.FrameUnpaired);
        Assert.Equal(1, recorder.Saved);
    }

    [Fact]
    public async Task The_Client_Keeps_Its_Idle_Few_Frames_Until_A_Recorder_Holds_Them_And_Again_After()
    {
        CarlaClient client = _client!;
        await ObserveFrames(100, 130);
        Assert.Equal(SnapshotHistory.DefaultIdleFrames, client.RetainedSnapshotFrames);
        Assert.Null(client.GetSnapshotFrame(100));
        Assert.NotNull(client.GetSnapshotFrame(130));

        var recorder = new FrameRecorder(client, _streams.Token(CameraStream), Path.Combine(_dir, "hold"), 2.0,
                                         cameraActorId: Camera, cameraName: RecordedCamera);
        try
        {
            await ObserveFrames(131, 160);
            Assert.Equal(SnapshotHistory.DefaultIdleFrames + 30, client.RetainedSnapshotFrames);
        }
        finally
        {
            recorder.Dispose();
        }

        Assert.Equal(SnapshotHistory.DefaultIdleFrames, client.RetainedSnapshotFrames);
        Assert.NotNull(client.GetSnapshotFrame(160));
    }

    /// The vehicle's pose on <paramref name="frame"/>: it drives east 1.35 m a tick, as a vehicle at
    /// 27 m/s does on a 0.05 s tick.
    private static Transform VehiclePose(ulong frame) =>
        new(new Location(100f + 1.35f * (frame - 100), 200f, 1f), new Rotation(0f, 0f, 0f));

    /// The world observer streams frames <paramref name="first"/> to <paramref name="last"/>, the vehicle
    /// moving and the camera still, and the client has the last before this returns.
    private async Task ObserveFrames(ulong first, ulong last)
    {
        CarlaClient client = _client!;
        await client.StartWorldObserverAsync();
        for (ulong frame = first; frame <= last; frame++)
            await _streams.SendAsync(ObserverStream, frame, Seconds(frame), CameraPose, EpisodeState(frame));
        await Until(() => client.LatestObservedFrame == last, $"the observer reaching frame {last}");
    }

    /// Records the camera, streams it one image per frame given, and returns the flushed recorder and
    /// its directory.
    private async Task<(FrameRecorder Recorder, string Directory)> RecordImages(params ulong[] frames)
    {
        string directory = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        var recorder = new FrameRecorder(_client!, _streams.Token(CameraStream), directory, 2.0,
                                         cameraActorId: Camera, cameraName: RecordedCamera);
        try
        {
            foreach (ulong frame in frames)
                await _streams.SendAsync(CameraStream, frame, Seconds(frame), CameraPose, Image());
            await Until(() => recorder.Saved + recorder.FrameUnpaired == frames.Length, "every capture being handled");
        }
        finally
        {
            recorder.Dispose();
        }

        return (recorder, directory);
    }

    private static IEnumerable<XElement> Sidecars(string directory) =>
        Directory.GetFiles(directory, "*.xml").Select(path => XDocument.Load(path).Root!);

    /// The sidecar's one vehicle record stands where <paramref name="pose"/> puts it.
    private static void AssertVehicleAt(XElement events, Transform pose)
    {
        XElement vehicle = Assert.Single(events.Elements("event"));
        Assert.Equal($"CARLA-TRUTH-{Vehicle}", (string?)vehicle.Attribute("uid"));
        XElement point = vehicle.Element("point")!;
        GeoLocation where = Geodesy.CarlaLocalToGeodetic(Origin, pose.Location);
        Assert.Equal(where.Latitude, Number(point, "lat"), 6);
        Assert.Equal(where.Longitude, Number(point, "lon"), 6);
    }

    private static double Seconds(ulong frame) => frame * DeltaSeconds;

    private static double Number(XElement element, string attribute) =>
        double.Parse((string)element.Attribute(attribute)!, CultureInfo.InvariantCulture);

    /// A world observer's payload for <paramref name="frame"/>: the 124-byte header of a server that
    /// measured no sun, then the camera and the vehicle, each a 119-byte actor record.
    private static byte[] EpisodeState(ulong frame)
    {
        (ActorId Id, Transform Pose)[] actors = [(Camera, CameraPose), (Vehicle, VehiclePose(frame))];
        var payload = new byte[HeaderSize + ActorSize * actors.Length];
        for (int i = 0; i < actors.Length; i++)
        {
            Span<byte> actor = payload.AsSpan(HeaderSize + ActorSize * i, ActorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(actor, actors[i].Id);
            actor[4] = (byte)ActorState.Active;
            StandInStreams.WriteTransform(actor[5..], actors[i].Pose);
        }
        return payload;
    }

    /// The vehicle, a four-wheeled car.
    private static Actor Describe(uint id) => new(
        id, 0u,
        new ActorDescription(id, "vehicle.audi.tt",
        [
            new ActorAttributeValue("base_type", ActorAttributeType.String, "car"),
            new ActorAttributeValue("number_of_wheels", ActorAttributeType.Int, "4"),
            new ActorAttributeValue("color", ActorAttributeType.RGBColor, "0,0,0"),
            new ActorAttributeValue("role_name", ActorAttributeType.String, "autopilot"),
        ]),
        new BoundingBox(default, new Vector3D(2.25f, 1.0f, 0.7f), default),
        [], []);

    /// An image's payload: width, height and field of view, then BGRA pixels.
    private static byte[] Image()
    {
        const int width = 32, height = 18;
        var payload = new byte[12 + (width * height * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, width);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), height);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8), 90f);
        payload.AsSpan(12).Fill(0x80);
        return payload;
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
