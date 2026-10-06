// Each capture's truth sidecar carries the supervision in force on its own frame, as the server carried it
// on that frame's world-observer snapshot -- never from a source in the recording process, so a recorder in
// any process writes the same thing (the owner's ruling of 2026-10-05). See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md §8.2 and D6.41.
//
// A stand-in server streams a real client the snapshots a supervised drive produces -- the render set and
// the supervision block inside it -- and a camera's images, and the recorder's files and counters are read
// once it has flushed.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Tests.Sensors;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Streaming;
using static CarlaNet.Tests.Sensors.EpisodeStateRenderSetTests;
using static CarlaNet.Tests.Sensors.EpisodeStateSupervisionTests;

namespace CarlaNet.Tests.Recording;

public sealed class FrameRecorderSupervisionTests : IAsyncLifetime
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
    private const int ActorSize = 119;
    // A recorder holds its camera's name for the whole process, so no other test class records under it.
    private const string RecordedCamera = "SIDECAR-SUPERVISION";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly GeoLocation Origin = new(27.15012, 56.18065, 12.0);

    // The escort's lead, a guard on its posting, an ambient flow vehicle and a body parked out of sight.
    private const ActorId EscortBody = 21;
    private const ActorId GuardBody = 22;
    private const ActorId AmbientBody = 23;
    private const ActorId ParkedBody = 24;

    private static readonly Transform OnTheRoad = new(new Location(100f, 200f, 1f), new Rotation(0f, 90f, 0f));
    private static readonly Transform AtThePost = new(new Location(120f, 210f, 1f), new Rotation(0f, 0f, 0f));
    private static readonly Transform Further = new(new Location(140f, 260f, 1f), new Rotation(0f, 0f, 0f));
    private static readonly Transform InItsParkingSlot = new(new Location(5200f, 5200f, -300f), default);

    private static readonly Entry[] Bodies =
    [
        new(EscortBody, ObservedBodyState.Lent, 96, "escort_0", "military_truck"),
        new(GuardBody, ObservedBodyState.Lent, 40, "guard_d4_h15_t3", "guard"),
        new(AmbientBody, ObservedBodyState.Lent, 90, "corridor_d0_p0_h6.12", "car"),
        new(ParkedBody, ObservedBodyState.Parked, 0, "", ""),
    ];

    private static readonly (ActorId Id, Transform Pose)[] Poses =
        [(EscortBody, OnTheRoad), (GuardBody, AtThePost), (AmbientBody, Further), (ParkedBody, InItsParkingSlot)];

    private static readonly Row EscortRow = new(EscortBody, 1,
        new Annotation("Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3", "transit", "bahonar:lead",
                       "bahonar:coordinated_group_transit", "bahonar:destination_off_pattern"));

    private static readonly Row GuardRow = new(GuardBody, 2,
        new Annotation("Shahid_Bahonar_Port_PatternOfLife/pi_tower_posting_d4_h15_t3", "dwell", "bahonar:guard",
                       "bahonar:tower_posting"));

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-sidecar-supervision-" + Guid.NewGuid().ToString("N"));
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
        _rpc.RegisterHandler<uint[], SuccessResponse<Actor[]>>(
            "get_actors_by_id", ids => Ok(ids.Select(Describe).ToArray()));
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
    public async Task A_Capture_Carries_The_Supervision_The_Server_Held_On_Its_Own_Frame()
    {
        await Observe(100, Supervised(SupervisionBlock([EscortRow, GuardRow])));

        (FrameRecorder recorder, XElement events, _) = await RecordOneImage(100);

        Assert.Equal(PlanId, (string?)events.Attribute("plan_id"));
        Assert.Equal("3", (string?)events.Attribute("vocabulary"));
        Assert.Equal(VocabularyDigest, (string?)events.Attribute("vocabulary_digest"));
        Assert.Null(events.Attribute("supervision"));

        // Nothing for the world apart from the plan on the container: every label follows a vehicle, so
        // every other supervision element is inside a vehicle's event (06 §3.5).
        Assert.Empty(events.Elements("_supervision"));
        Assert.Equal(3, events.Elements("event").Count());

        // Every vehicle the frame drew, by its SUMO vehicle, and no parked body.
        Dictionary<string, XElement> vehicles = events.Elements("event")
            .ToDictionary(e => (string)e.Attribute("uid")!, e => e.Element("detail")!.Element("_supervision")!);
        Assert.Equal(["CARLA-TRUTH-SUMO-corridor_d0_p0_h6.12", "CARLA-TRUTH-SUMO-escort_0",
                      "CARLA-TRUTH-SUMO-guard_d4_h15_t3"], vehicles.Keys.Order(StringComparer.Ordinal));

        XElement escort = vehicles["CARLA-TRUTH-SUMO-escort_0"];
        Assert.Equal("annotated", (string?)escort.Attribute("state"));
        Assert.Equal("3", (string?)escort.Attribute("vocabulary"));
        XElement lead = Assert.Single(escort.Elements("annotation"));
        Assert.Equal("Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3", (string?)lead.Attribute("instance"));
        Assert.Equal("bahonar:coordinated_group_transit bahonar:destination_off_pattern",
                     (string?)lead.Attribute("labels"));
        Assert.Equal("transit", (string?)lead.Attribute("phase"));
        Assert.Equal("bahonar:lead", (string?)lead.Attribute("role"));

        // A nominal vehicle with the ordinary behaviour it is named.
        XElement guard = vehicles["CARLA-TRUTH-SUMO-guard_d4_h15_t3"];
        Assert.Equal("nominal", (string?)guard.Attribute("state"));
        Assert.Equal("bahonar:tower_posting", (string?)Assert.Single(guard.Elements("annotation")).Attribute("labels"));

        // An unlabelled vehicle: the bare state, written as every other is.
        XElement ambient = vehicles["CARLA-TRUTH-SUMO-corridor_d0_p0_h6.12"];
        Assert.Equal("unlabelled", (string?)ambient.Attribute("state"));
        Assert.Empty(ambient.Elements());

        Assert.Equal(1, recorder.SupervisionPaired);
        Assert.Equal(0, recorder.SupervisionUnpaired);
    }

    [Fact]
    public async Task A_Still_s_Text_Chunks_Carry_No_Supervision()
    {
        // The owner's ruling: a still is an observation artifact and carries none, however much supervision
        // the frame had in force; it is in the sidecar beside it.
        await Observe(100, Supervised(SupervisionBlock([EscortRow, GuardRow])));

        (_, XElement events, string stem) = await RecordOneImage(100);

        IReadOnlyList<(string Keyword, string Text)> chunks = PngTextChunks(stem + ".png");
        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Contains(chunk.Keyword, new[]
        {
            "carla:capture", "carla:solar", "carla:illumination", "carla:sensor",
        }));
        string text = string.Join("\n", chunks.Select(chunk => chunk.Keyword + "=" + chunk.Text));
        foreach (string leak in new[] { "supervision", "annotated", "nominal", "unlabelled", "bahonar:",
                                        PlanId, VocabularyDigest, "pi_escort_drydock_d3" })
        {
            Assert.DoesNotContain(leak, text);
        }

        // While the sidecar beside it carries the frame's supervision.
        Assert.Equal(PlanId, (string?)events.Attribute("plan_id"));
    }

    [Fact]
    public async Task A_Capture_Whose_Frame_Is_No_Longer_Held_Is_Written_With_Supervision_Unknown()
    {
        // The image is of frame 300 and the client holds only frame 100, whose snapshot carried a plan: its
        // vehicles are read from frame 100 and the sidecar names it, but frame 100's supervision is not
        // frame 300's, so none is written and the sidecar says it is unknown.
        await Observe(100, Supervised(SupervisionBlock([EscortRow, GuardRow])));

        (FrameRecorder recorder, XElement events, _) = await RecordOneImage(300);

        Assert.Equal("100", (string?)events.Attribute("telemetry_tick"));
        Assert.Equal("unknown", (string?)events.Attribute("supervision"));
        Assert.Null(events.Attribute("plan_id"));
        Assert.Null(events.Attribute("vocabulary_digest"));
        Assert.Empty(events.Descendants("_supervision"));
        Assert.Equal(0, recorder.SupervisionPaired);
        Assert.Equal(1, recorder.SupervisionUnpaired);
    }

    [Fact]
    public async Task A_Capture_Whose_Frame_s_Supervision_Could_Not_Be_Read_Is_Written_With_It_Unknown()
    {
        // The block says it holds two rows and carries one.
        byte[] supervision = SupervisionBlock([EscortRow]);
        int rowCountAt = 4 + 2 + PlanId.Length + 4 + 2 + VocabularyDigest.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(supervision.AsSpan(rowCountAt), 2u);
        await Observe(100, Supervised(supervision));

        (FrameRecorder recorder, XElement events, _) = await RecordOneImage(100);

        Assert.Equal("unknown", (string?)events.Attribute("supervision"));
        Assert.Empty(events.Descendants("_supervision"));
        // The vehicles are read all the same: the render set block's size says where they are.
        Assert.Equal(3, events.Elements("event").Count());
        Assert.Equal(1, recorder.SupervisionUnpaired);
        Assert.Equal(1, _client!.SupervisionBlocksUnreadable);
    }

    [Fact]
    public async Task A_Capture_Of_A_Frame_No_Plan_Was_In_Force_On_Carries_No_Supervision()
    {
        // A drive that bound no plan: the render set and nothing after it.
        await Observe(100, Snapshot(Block(Bodies), [.. Poses.Select(pose => pose.Id)]));

        (FrameRecorder recorder, XElement events, _) = await RecordOneImage(100);

        Assert.Null(events.Attribute("plan_id"));
        Assert.Null(events.Attribute("supervision"));
        Assert.Empty(events.Descendants("_supervision"));
        Assert.Equal("rendered", (string?)events.Attribute("vehicles"));
        Assert.Equal(0, recorder.SupervisionPaired);
        Assert.Equal(0, recorder.SupervisionUnpaired);
    }

    [Fact]
    public async Task A_Recorder_Beside_The_Session_Writes_The_Server_s_Supervision_As_One_With_No_Source_Does()
    {
        // A recorder given the session's render set pairs its vehicles in process; its supervision is still
        // the server's, so it writes exactly what a recorder in another process writes for the frame.
        await Observe(100, Supervised(SupervisionBlock([EscortRow, GuardRow])));
        RenderSet inProcess = new(
        [
            new RenderedVehicle(EscortBody, "escort_0", "military_truck", 96),
            new RenderedVehicle(GuardBody, "guard_d4_h15_t3", "guard", 40),
            new RenderedVehicle(AmbientBody, "corridor_d0_p0_h6.12", "car", 90),
        ]);

        (_, XElement beside, _) = await RecordOneImage(100, new OneFrame(100, inProcess));
        (_, XElement elsewhere, _) = await RecordOneImage(100, stream: CameraStream + 1);

        Assert.Equal(Supervision(elsewhere), Supervision(beside));
        Assert.Contains("state=annotated", Supervision(beside));
    }

    /// The sidecar's supervision, every element and attribute, in document order, as one string.
    private static string Supervision(XElement events) =>
        string.Join("\n",
            new[] { $"plan={events.Attribute("plan_id")?.Value} vocabulary={events.Attribute("vocabulary")?.Value} "
                    + $"digest={events.Attribute("vocabulary_digest")?.Value}" }
            .Concat(events.Descendants("_supervision").SelectMany(element =>
                new[] { Describe(element) }.Concat(element.Elements().Select(Describe)))));

    private static string Describe(XElement element) =>
        element.Name + " " + string.Join(" ", element.Attributes().Select(a => $"{a.Name}={a.Value}"));

    /// A supervised drive's snapshot: the render set's four bodies, then the given supervision block, then
    /// each body at its pose.
    private static byte[] Supervised(byte[] supervision)
    {
        byte[] block = RenderSetWithSupervision(Bodies, supervision);
        byte[] bytes = SupervisedSnapshot(block, [.. Poses.Select(pose => pose.Id)]);
        PlacePoses(bytes, WideHeaderSize + block.Length);
        return bytes;
    }

    /// A snapshot with the render set block given and no supervision, each body at its pose.
    private static byte[] Snapshot(byte[] block, uint[] actors)
    {
        byte[] bytes = EpisodeStateRenderSetTests.Snapshot(block, actors);
        PlacePoses(bytes, WideHeaderSize + block.Length);
        return bytes;
    }

    private static void PlacePoses(byte[] snapshot, int firstActor)
    {
        for (int i = 0; i < Poses.Length; i++)
        {
            StandInStreams.WriteTransform(snapshot.AsSpan(firstActor + (ActorSize * i) + 5), Poses[i].Pose);
        }
    }

    /// The world observer streams <paramref name="frame"/>, and the client has it before this returns.
    private async Task Observe(ulong frame, byte[] snapshot)
    {
        CarlaClient client = _client!;
        await client.StartWorldObserverAsync();
        await _streams.SendAsync(ObserverStream, frame, frame * DeltaSeconds, default, snapshot);
        await Until(() => client.LatestObservedFrame == frame, $"the observer reaching frame {frame}");
    }

    /// Records a camera, streams it one image of <paramref name="frame"/>, and returns the flushed recorder,
    /// the sidecar and the path of the capture without its extension.
    private async Task<(FrameRecorder Recorder, XElement Events, string Stem)> RecordOneImage(
        ulong frame, IRenderSetSource? source = null, uint stream = CameraStream)
    {
        string directory = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        var recorder = new FrameRecorder(_client!, _streams.Token(stream), directory, 2.0, renderSet: source,
                                         cameraName: RecordedCamera);
        try
        {
            await _streams.SendAsync(stream, frame, frame * DeltaSeconds, default, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        string xml = Directory.GetFiles(directory, "*.xml").Single();
        return (recorder, XDocument.Load(xml).Root!, Path.Combine(directory, Path.GetFileNameWithoutExtension(xml)));
    }

    /// Every tEXt chunk of a PNG, keyword and text, read chunk by chunk.
    private static IReadOnlyList<(string Keyword, string Text)> PngTextChunks(string path)
    {
        byte[] png = File.ReadAllBytes(path);
        var chunks = new List<(string, string)>();
        int at = 8;
        while (at + 8 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            string type = Encoding.ASCII.GetString(png, at + 4, 4);
            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                ReadOnlySpan<byte> data = png.AsSpan(at + 8, length);
                int separator = data.IndexOf((byte)0);
                Assert.Equal("tEXt", type);
                chunks.Add((Encoding.Latin1.GetString(data[..separator]), Encoding.Latin1.GetString(data[(separator + 1)..])));
            }

            at += 12 + length;
        }

        return chunks;
    }

    /// <summary>A render-set source holding one frame, as a session holds the frames it rendered.</summary>
    private sealed class OneFrame(ulong held, RenderSet renderSet) : IRenderSetSource
    {
        public ulong? NewestFrame => held;

        public bool TryGetRenderSet(ulong frame, out RenderSet answered)
        {
            answered = renderSet;
            return frame == held;
        }
    }

    /// Every actor here is a four-wheeled vehicle with a body of its own.
    private static Actor Describe(uint id) => new(
        id, 0u,
        new ActorDescription(id, "vehicle.mercedes.sprinter",
        [
            new ActorAttributeValue("base_type", ActorAttributeType.String, "car"),
            new ActorAttributeValue("number_of_wheels", ActorAttributeType.Int, "4"),
            new ActorAttributeValue("color", ActorAttributeType.RGBColor, "10,20,30"),
            new ActorAttributeValue("role_name", ActorAttributeType.String, "sumo"),
        ]),
        new BoundingBox(default, new Vector3D(2.9f, 1.0f, 1.3f), default),
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
