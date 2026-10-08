// Every capture carries the sun of the snapshot nearest its pixels, with the illumination band that
// sun falls in, in the sidecar's <_solar> and the PNG's carla:solar chunk alike; and a capture written
// without one is counted rather than silently left out. See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/11_Time_And_Illumination.md §8.2-§8.4 and
// 06_Truth_And_Annotation.md §8.2.
//
// A stand-in server streams a real client one world-observer snapshot and one camera image, and the
// recorder's files and counters are read once it has flushed.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Sensors;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Provenance;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Tests.Recording;

public sealed class FrameRecorderSolarBlockTests : IAsyncLifetime
{
    // Every call here returns R<T>, whose success is [[1, value]]. These reproduce that envelope.
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    private const uint ObserverStream = 1;
    private const uint CameraStream = 2;
    private const double DeltaSeconds = 0.05;
    private const int WideHeaderSize = 132;
    private const int SolarOffset = 36;
    // A recorder holds its camera's name for the whole process, so no other test class records under it.
    private const string RecordedCamera = "SOLAR-BLOCK";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly GeoLocation Origin = new(27.15012, 56.18065, 12.0);

    // At the port at 06:00, just below the horizon: refraction lifts the light just above it, so the
    // band the frame was lit in (golden) is not the band of the geometric sun (civil twilight).
    private static readonly double[] Dawn =
        [6.0, 2026, 3, 21, 3.5, 27.15012, 56.18065, -0.25, 95.4, 0.0, 0.0, 0.31];

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-solar-block-" + Guid.NewGuid().ToString("N"));
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
        _rpc.RegisterHandler("get_build_identity", () => Ok(new Dictionary<string, string>
        {
            ["identity_version"] = "1",
            ["release"] = "0.10.0",
            ["world_interface"] = "1.0",
            ["build"] = "editor",
            ["configuration"] = "Development",
            ["carla_commit"] = "025443a83eaf1bb82f18795d608fca50eb77a452",
            ["content_commit"] = "unknown",
            ["engine_commit"] = "unknown",
            ["commits_from"] = "compiled",
        }));
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
    public async Task A_Capture_Carries_Its_Sun_And_The_Band_Its_Light_Fell_In_In_Both_Files()
    {
        await Observe(100, Dawn);

        (FrameRecorder recorder, string stem) = await RecordOneImage(100);

        XElement solar = XDocument.Load(stem + ".xml").Root!.Element("_solar")!;
        Assert.Equal("-0.25", (string?)solar.Attribute("sun_elevation_deg"));
        Assert.Equal("0.31", (string?)solar.Attribute("sun_corrected_elevation_deg"));
        Assert.Equal("golden", (string?)solar.Attribute("illumination_band"));
        Assert.Equal("refraction_corrected", (string?)solar.Attribute("illumination_band_elevation"));

        // The PNG's text chunks are stored uncompressed, so the sun's JSON is in its bytes verbatim.
        string png = Encoding.Latin1.GetString(File.ReadAllBytes(stem + ".png"));
        Assert.Contains("carla:solar\0{", png);
        Assert.Contains("\"illumination_band\":\"golden\",\"illumination_band_elevation\":\"refraction_corrected\"}",
                        png);

        Assert.Equal(1, recorder.Saved);
        Assert.Equal(0, recorder.SolarBlockMissing);
    }

    [Fact]
    public async Task A_Capture_Written_Without_A_Sun_Is_Counted()
    {
        // A snapshot that says it measured no sun: the client caches none, and the capture of its frame
        // is written with no _solar element and no carla:solar chunk.
        await Observe(100, sun: null);

        (FrameRecorder recorder, string stem) = await RecordOneImage(100);

        Assert.Null(XDocument.Load(stem + ".xml").Root!.Element("_solar"));
        Assert.DoesNotContain("carla:solar", Encoding.Latin1.GetString(File.ReadAllBytes(stem + ".png")));
        Assert.Equal(1, recorder.Saved);
        Assert.Equal(1, recorder.SolarBlockMissing);
    }

    [Fact]
    public async Task A_Still_Says_What_Made_It_In_Its_Sidecar_And_Its_Capture_Chunk_Alike()
    {
        await Observe(100, Dawn);

        (FrameRecorder recorder, string stem) = await RecordOneImage(100, sumoVersion: "1.27.0");

        Assert.True(recorder.ServerIdentity.Available);
        XElement events = XDocument.Load(stem + ".xml").Root!;
        Assert.Equal("1", (string?)events.Attribute("format_version"));
        // First under the container, so it is the first thing a reader meets.
        XElement producer = events.Elements().First();
        Assert.Equal("_producer", producer.Name.LocalName);
        Assert.Equal(Producer.CarlaNetVersion, (string?)producer.Attribute("carlanet"));
        Assert.Equal("1.27.0", (string?)producer.Attribute("sumo"));
        Assert.NotNull(producer.Attribute("tool"));
        XElement server = Assert.Single(producer.Elements("_server"));
        Assert.Equal("editor", (string?)server.Attribute("build"));
        Assert.Equal("compiled", (string?)server.Attribute("commits_from"));
        Assert.Equal("025443a83eaf1bb82f18795d608fca50eb77a452", (string?)server.Attribute("carla_commit"));

        // The PNG keeps its chunks; the capture chunk carries its format and the same record, and every
        // other chunk its format.
        Dictionary<string, string> chunks = TextChunks(stem + ".png");
        Assert.Equal(["carla:capture", "carla:solar"], chunks.Keys.Order().ToArray());
        using JsonDocument capture = JsonDocument.Parse(chunks["carla:capture"]);
        Assert.Equal(1, capture.RootElement.GetProperty("format_version").GetInt32());
        Assert.Equal(100UL, capture.RootElement.GetProperty("tick").GetUInt64());
        ProducerRecord stillRecord = ProducerRecord.ReadJson(capture.RootElement.GetProperty("producer"));
        Assert.Equal((string?)producer.Attribute("written_utc"), ProducerRecord.Iso(stillRecord.WrittenUtc!.Value));
        Assert.Equal("1.27.0", stillRecord.Sumo);
        Assert.Equal("compiled", stillRecord.Server!.CommitsFrom);
        using JsonDocument solar = JsonDocument.Parse(chunks["carla:solar"]);
        Assert.Equal(1, solar.RootElement.GetProperty("format_version").GetInt32());
    }

    /// Every tEXt chunk of a PNG, by keyword.
    private static Dictionary<string, string> TextChunks(string path)
    {
        byte[] png = File.ReadAllBytes(path);
        var chunks = new Dictionary<string, string>();
        for (int at = 8; at + 8 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at, 4));
            string type = Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "tEXt")
            {
                ReadOnlySpan<byte> data = png.AsSpan(at + 8, length);
                int separator = data.IndexOf((byte)0);
                chunks[Encoding.Latin1.GetString(data[..separator])] = Encoding.Latin1.GetString(data[(separator + 1)..]);
            }

            at += 12 + length;
        }

        return chunks;
    }

    /// The world observer streams <paramref name="frame"/> with the given sun, or with none, and the
    /// client has it before this returns.
    private async Task Observe(ulong frame, double[]? sun)
    {
        CarlaClient client = _client!;
        await client.StartWorldObserverAsync();
        await _streams.SendAsync(ObserverStream, frame, frame * DeltaSeconds, default, Snapshot(sun));
        await Until(() => client.LatestObservedFrame == frame, $"the observer reaching frame {frame}");
    }

    /// Records the camera, streams it one image of <paramref name="frame"/>, and returns the flushed
    /// recorder and the path of the capture without its extension.
    private async Task<(FrameRecorder Recorder, string Stem)> RecordOneImage(ulong frame, string? sumoVersion = null)
    {
        var recorder = new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0,
                                         cameraName: RecordedCamera, sumoVersion: sumoVersion);
        try
        {
            await _streams.SendAsync(CameraStream, frame, frame * DeltaSeconds, default, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        string xml = Directory.GetFiles(_dir, "*.xml").Single();
        return (recorder, Path.Combine(_dir, Path.GetFileNameWithoutExtension(xml)));
    }

    /// A snapshot with no actors as a server that carries the corrected elevation writes it, the sun
    /// marked measured where one is given.
    private static byte[] Snapshot(double[]? sun)
    {
        var bytes = new byte[WideHeaderSize];
        bytes[32] = (byte)(SimulationState.SolarCorrectedElevationCarried
                           | (sun is null ? SimulationState.None : SimulationState.SolarStateValid));
        for (int k = 0; k < (sun?.Length ?? 0); k++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(SolarOffset + (k * 8)),
                                                    BitConverter.DoubleToInt64Bits(sun![k]));
        }

        return bytes;
    }

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
