// A capture's platform pose is the pose its own image was rendered from, not wherever the camera has
// been moved to by the time the recorder handles the image. See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/12_Operator_Control_Surface.md §9.6.
//
// A camera flown by hand is moved on its own thread while the world ticks, and an image reaches the
// recorder several ticks after its frame, so by then the camera's actor transform -- what the world
// observer last reported, and what get_transform answers -- is somewhere else. The server stamps the
// transform into the image's sensor header in the same game-thread call that captures the frame
// (PixelReader.h, SendPixelsInRenderThread), and the recorder derives the pose from that header.
// A stand-in server here streams exactly that situation to a real FrameRecorder: a world observer
// whose latest snapshot has the camera moved on, then an image of an earlier frame whose header
// carries the pose it was rendered from.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Transport.Streaming;
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
    private const ActorId Camera = 42;
    private const ulong RenderedFrame = 100;
    private const double DeltaSeconds = 0.05;
    private const int Width = 64;
    private const int Height = 36;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // A constant bare-earth record: the physical height less this is the hae a capture reports.
    private const double AlignOffsetMetres = 12.5;
    private static readonly GeoLocation Origin = new(38.91108, -119.7645965, 1421.4);

    // Where the image was rendered: its sensor header, and the camera in the snapshot of its frame.
    private static readonly Transform Rendered =
        new(new Location(120f, -340f, 450f), new Rotation(-60f, 30f, 0f));

    // Where the camera was moved to afterwards: every later snapshot, and the actor transform by the
    // time the image is handled.
    private static readonly Transform MovedTo =
        new(new Location(-600f, 900f, 300f), new Rotation(-20f, 170f, 5f));

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-sensor-pose-" + Guid.NewGuid().ToString("N"));
    private readonly StreamServer _streams = new();
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
        // The camera is the only actor, and it is not a vehicle, so no truth record is made from it.
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
    public async Task A_Capture_Records_The_Pose_Its_Image_Was_Rendered_From_Not_Where_The_Camera_Has_Since_Been_Moved()
    {
        CarlaClient client = _client!;
        await client.StartWorldObserverAsync();

        // Frame 100 is rendered with the camera at one pose; by frame 103 it has been flown elsewhere.
        await _streams.SendAsync(ObserverStream, RenderedFrame, Seconds(RenderedFrame), MovedTo,
                                 EpisodeState(Rendered));
        for (ulong frame = RenderedFrame + 1; frame <= RenderedFrame + 3; frame++)
            await _streams.SendAsync(ObserverStream, frame, Seconds(frame), MovedTo, EpisodeState(MovedTo));
        await Until(() => client.LatestObservedFrame == RenderedFrame + 3, "the observer reaching frame 103");
        Assert.Equal(MovedTo, client.GetActorTransform(Camera));

        var platform = new SensorPlatformOptions(90.0, "a-f-A-M-F-Q", "OVERWATCH", $"CARLA-SENSOR-{Camera}");
        var recorder = new FrameRecorder(client, _streams.Token(CameraStream), _dir, 2.0, platform: platform);
        try
        {
            // The image of frame 100 arrives now, carrying the transform it was captured at.
            await _streams.SendAsync(CameraStream, RenderedFrame, Seconds(RenderedFrame), Rendered, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        XElement events = XDocument.Load(Directory.GetFiles(_dir, "*.xml").Single()).Root!;
        XElement track = events.Elements("event").Single(e => (string?)e.Attribute("uid") == platform.Uid);
        XElement point = track.Element("point")!;
        XElement sensor = track.Element("detail")!.Element("sensor")!;
        double lat = Number(point, "lat"), lon = Number(point, "lon"), hae = Number(point, "hae");
        double azimuth = Number(sensor, "azimuth"), elevation = Number(sensor, "elevation");

        GeoLocation rendered = Geodesy.CarlaLocalToGeodetic(Origin, Rendered.Location);
        GeoLocation moved = Geodesy.CarlaLocalToGeodetic(Origin, MovedTo.Location);
        Assert.Equal(rendered.Latitude, lat, 6);
        Assert.Equal(rendered.Longitude, lon, 6);
        Assert.Equal(rendered.Altitude - AlignOffsetMetres, hae, 2);
        // CARLA's yaw 30 looks 30 degrees south of east (y is south): a compass azimuth of 120.
        Assert.Equal(120.0, azimuth, 3);
        Assert.Equal(-60.0, elevation, 3);

        // And none of it is the pose the camera had been moved to.
        Assert.NotEqual(Math.Round(moved.Latitude, 6), Math.Round(lat, 6));
        Assert.NotEqual(Math.Round(moved.Longitude, 6), Math.Round(lon, 6));
        Assert.NotEqual(260.0, azimuth, 3);
        Assert.NotEqual(-20.0, elevation, 3);

        // The capture is of the image's own frame, and its truth was read as of that frame too.
        Assert.Equal("100", (string?)events.Attribute("tick"));
        Assert.Equal("100", (string?)events.Attribute("telemetry_tick"));

        // The still carries the same pose in its own metadata, so the image alone says where it was
        // taken from.
        string chunk = PngText(Directory.GetFiles(_dir, "*.png").Single(), "carla:sensor");
        Assert.Contains($"\"lat\":{rendered.Latitude.ToString("0.0000000", CultureInfo.InvariantCulture)}", chunk);
        Assert.Contains("\"az_deg\":120,", chunk);
    }

    private static double Seconds(ulong frame) => frame * DeltaSeconds;

    private static double Number(XElement element, string attribute) =>
        double.Parse((string)element.Attribute(attribute)!, CultureInfo.InvariantCulture);

    /// A world observer's payload holding the camera alone at <paramref name="camera"/>: the
    /// 124-byte header of a server that measured no sun, then one 119-byte actor record.
    private static byte[] EpisodeState(Transform camera)
    {
        const int headerSize = 124, actorSize = 119;
        var payload = new byte[headerSize + actorSize];
        Span<byte> actor = payload.AsSpan(headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(actor, Camera);
        actor[4] = (byte)ActorState.Active;
        WriteTransform(actor[5..], camera);
        return payload;
    }

    /// An RGB image's payload: width, height and field of view, then BGRA pixels.
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

    /// <summary>
    /// The server's streaming side: a subscriber connects, names its stream by id, and is sent
    /// length-prefixed frames of a 48-byte sensor header and a payload.
    /// </summary>
    private sealed class StreamServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<NetworkStream>> _subscribers = new();
        private readonly ConcurrentBag<TcpClient> _connections = [];
        private readonly CancellationTokenSource _stop = new();

        public StreamServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        /// The 24-byte token a client subscribes to this stream with.
        public byte[] Token(uint streamId)
        {
            var token = new byte[StreamToken.SizeBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(token, streamId);
            BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(4), (ushort)Port);
            token[6] = (byte)StreamProtocol.Tcp;
            token[7] = (byte)StreamAddressType.Ipv4;
            IPAddress.Loopback.GetAddressBytes().CopyTo(token, 8);
            return token;
        }

        public async Task SendAsync(uint streamId, ulong frame, double timestamp, Transform sensor, byte[] payload)
        {
            NetworkStream stream = await Subscriber(streamId).Task.WaitAsync(Patience);
            var message = new byte[4 + SensorFrame.HeaderSize + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)(SensorFrame.HeaderSize + payload.Length));
            Span<byte> header = message.AsSpan(4, SensorFrame.HeaderSize);
            BinaryPrimitives.WriteUInt64LittleEndian(header[8..], frame);
            BinaryPrimitives.WriteDoubleLittleEndian(header[16..], timestamp);
            WriteTransform(header[24..], sensor);
            payload.CopyTo(message.AsSpan(4 + SensorFrame.HeaderSize));
            await stream.WriteAsync(message);
        }

        private TaskCompletionSource<NetworkStream> Subscriber(uint streamId) =>
            _subscribers.GetOrAdd(streamId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(connection);
                    NetworkStream stream = connection.GetStream();
                    var id = new byte[4];
                    await stream.ReadExactlyAsync(id, _stop.Token);
                    Subscriber(BinaryPrimitives.ReadUInt32LittleEndian(id)).TrySetResult(stream);
                }
            }
            catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException
                                                or SocketException or IOException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (TcpClient connection in _connections) connection.Dispose();
            _stop.Dispose();
        }
    }
}
