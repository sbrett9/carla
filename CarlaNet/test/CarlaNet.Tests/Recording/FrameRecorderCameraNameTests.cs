// Every still a recorder writes is named after its camera, and the camera's platform track carries the
// name as its callsign, so two cameras in one world never write files of one name or report under one
// callsign. Before cameras were named, every still was SCTMV_<local time> whatever camera took it, and
// the shim gave every platform track the callsign OVERWATCH. A stand-in server here streams a real
// FrameRecorder one image and the files it writes are read back; the refusals need no image at all.
// See CameraName for the rule a name must meet, and CameraNameTests for the rule on its own.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Tests.Recording;

public sealed class FrameRecorderCameraNameTests : IAsyncLifetime
{
    // Every call here returns R<T>, whose success is [[1, value]]. These reproduce that envelope.
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    private const uint CameraStream = 2;
    private const ActorId Camera = 4121;
    private const int Width = 64;
    private const int Height = 36;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly GeoLocation Origin = new(38.91108, -119.7645965, 1421.4);
    private static readonly Transform Looking = new(new Location(120f, -340f, 450f), new Rotation(-60f, 30f, 0f));

    // Names no other test class records under: a recorder holds its name for the whole process.
    private const string Deck = "DECK-I25_naming";
    private static readonly Regex StillStem = new(@"^(?<name>.+)_\d{4}\.\d{2}\.\d{2}_\d{2}\.\d{2}\.\d{2}\.\d{3}$");

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-camera-name-" + Guid.NewGuid().ToString("N"));
    private readonly StandInStreams _streams = new(Patience);
    private MsgPackRpcServer? _rpc;
    private CarlaClient? _client;

    public async Task InitializeAsync()
    {
        int port = FreeLoopbackPort();
        _rpc = new MsgPackRpcServer(IPAddress.Loopback, port);
        _rpc.RegisterHandler("get_cesium_origin", () => Ok(Origin));
        _rpc.RegisterHandler("get_bare_earth_reference",
                             () => Ok(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }));
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
    public async Task Every_Still_Is_Named_After_Its_Camera_And_Its_Platform_Track_Calls_Itself_By_The_Name()
    {
        var platform = new SensorPlatformOptions(90.0, "a-f-A-M-F-Q", Deck, CameraName.Default(Camera));

        (FrameRecorder recorder, string png, string xml) = await RecordOneImage(platform: platform);

        Assert.Equal(Deck, recorder.Name);
        Assert.Equal(Deck, StillStem.Match(Path.GetFileNameWithoutExtension(png)).Groups["name"].Value);
        Assert.Equal(Path.GetFileNameWithoutExtension(png), Path.GetFileNameWithoutExtension(xml));
        XElement track = XDocument.Load(xml).Root!.Elements("event")
            .Single(e => (string?)e.Attribute("uid") == "CARLA-SENSOR-4121");
        Assert.Equal(Deck, (string?)track.Element("detail")!.Element("contact")!.Attribute("callsign"));
        Assert.Contains($"\"callsign\":\"{Deck}\"", PngText(png, "carla:sensor"));
    }

    [Fact]
    public async Task A_Camera_Given_No_Name_By_A_Server_That_Names_None_Is_Recorded_Under_Its_Default()
    {
        (FrameRecorder recorder, string png, _) = await RecordOneImage(cameraActorId: Camera);

        Assert.Equal("CARLA-SENSOR-4121", recorder.Name);
        Assert.StartsWith("CARLA-SENSOR-4121_", Path.GetFileName(png));
    }

    [Fact]
    public async Task A_Camera_The_Server_Named_Is_Recorded_Under_The_Server_s_Name()
    {
        // The name the server issued the camera, read back from its attributes (CameraName.Of): no
        // client may choose it, and the recorder takes it as the camera's.
        const string issued = "Camera_4121";
        Assert.NotNull(CameraName.Problem(issued));
        var platform = new SensorPlatformOptions(90.0, "a-f-A-M-F-Q", issued, CameraName.Default(Camera));

        (FrameRecorder recorder, string png, string xml) = await RecordOneImage(platform: platform, cameraActorId: Camera);

        Assert.Equal(issued, recorder.Name);
        Assert.StartsWith("Camera_4121_", Path.GetFileName(png));
        XElement track = XDocument.Load(xml).Root!.Elements("event")
            .Single(e => (string?)e.Attribute("uid") == "CARLA-SENSOR-4121");
        Assert.Equal(issued, (string?)track.Element("detail")!.Element("contact")!.Attribute("callsign"));
    }

    [Fact]
    public void A_Platform_Track_Whose_Callsign_Is_Not_The_Camera_s_Name_Is_Refused()
    {
        var platform = new SensorPlatformOptions(90.0, "a-f-A-M-F-Q", "OVERWATCH", CameraName.Default(Camera));

        var refused = Assert.Throws<ArgumentException>(
            () => new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0, platform: platform,
                                    cameraName: "MISMATCH_naming"));

        Assert.Contains("'OVERWATCH' is not 'MISMATCH_naming'", refused.Message);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void A_Recorder_With_No_Name_And_No_Camera_To_Take_One_From_Is_Refused()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0));

        Assert.Contains("names every still after its camera", refused.Message);
    }

    [Fact]
    public void A_Name_The_Rule_Refuses_Is_Refused_Before_Anything_Is_Written()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0, cameraName: "Deck Cam 1"));

        Assert.Contains("holds a space, which a camera name cannot", refused.Message);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Two_Recorders_In_One_Process_Cannot_Record_Under_One_Name()
    {
        const string twin = "TWIN_naming";
        FrameRecorder first = new(_client!, _streams.Token(CameraStream), Path.Combine(_dir, "a"), 2.0,
                                  cameraName: twin, cameraActorId: Camera);
        try
        {
            var refused = Assert.Throws<InvalidOperationException>(
                () => new FrameRecorder(_client!, _streams.Token(CameraStream), Path.Combine(_dir, "b"), 2.0,
                                        cameraName: twin.ToLowerInvariant(), cameraActorId: Camera + 1));
            Assert.Contains("already being recorded in this process, by camera 4121", refused.Message);
            Assert.False(Directory.Exists(Path.Combine(_dir, "b")));
        }
        finally
        {
            first.Dispose();
        }

        // Given back when the first is disposed.
        using var second = new FrameRecorder(_client!, _streams.Token(CameraStream), Path.Combine(_dir, "b"),
                                             2.0, cameraName: twin, cameraActorId: Camera + 1);
        Assert.Equal(twin, second.Name);
    }

    /// Records the camera, streams it one image, and returns the recorder, flushed, with the still's
    /// image and sidecar.
    private async Task<(FrameRecorder Recorder, string Png, string Xml)> RecordOneImage(
        SensorPlatformOptions? platform = null, ActorId? cameraActorId = null)
    {
        var recorder = new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0, platform: platform,
                                         cameraActorId: cameraActorId);
        try
        {
            await _streams.SendAsync(CameraStream, 100, 5.0, Looking, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        return (recorder, Directory.GetFiles(_dir, "*.png").Single(), Directory.GetFiles(_dir, "*.xml").Single());
    }

    /// An image's payload: width, height and field of view, then BGRA pixels.
    private static byte[] Image()
    {
        var payload = new byte[12 + (Width * Height * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, Width);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), Height);
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
