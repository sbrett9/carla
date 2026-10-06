// Each vehicle in the picture carries the lights commanded on for it and, where a SUMO drive lent it a
// body, where its drawn pose came from -- both from the world-observer snapshot of the capture's own
// frame, never from the recording process (the owner's rulings of 2026-10-05 and 2026-10-06). See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/08_Collection_And_EPoL.md §5.1 and §6.4.
//
// A stand-in server streams a real client the snapshots a drive produces, and a recorder given no source
// of its own writes the sidecar: the pose source is computed from the frame's own number against the
// step the snapshot carried, the lights read from each vehicle's record, and a server that carries
// neither is recorded with neither and says so.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Tests.Sensors;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Rpc.Lighting;
using CarlaNet.Types.Streaming;
using static CarlaNet.Tests.Sensors.EpisodeStateRenderSetTests;

namespace CarlaNet.Tests.Recording;

public sealed class LiveTruthLightsAndPoseSourceTests : IAsyncLifetime
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
    private const byte NamedHeld = 2;
    // A recorder holds its camera's name for the whole process, so no other test class records under it.
    private const string RecordedCamera = "LIVE-LIGHTS";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly GeoLocation Origin = new(27.15012, 56.18065, 12.0);

    // Two pooled bodies and a vehicle no session named.
    private const ActorId BodyA = 21;
    private const ActorId BodyB = 22;
    private const ActorId Ambient = 23;

    // The camera hangs 49 m above A, with B and the ambient vehicle a few meters from it, all in the
    // picture; elsewhere is outside it.
    private static readonly Transform Camera = new(new Location(100f, 200f, 50f), new Rotation(-90f, 0f, 0f));
    private static readonly Transform BelowTheCamera = new(new Location(100f, 200f, 1f), new Rotation(0f, 90f, 0f));
    private static readonly Transform BesideIt = new(new Location(106f, 203f, 1f), new Rotation(0f, 0f, 0f));
    private static readonly Transform AcrossFromIt = new(new Location(94f, 196f, 1f), new Rotation(0f, 180f, 0f));
    private static readonly Transform Elsewhere = new(new Location(140f, 260f, 1f), new Rotation(0f, 0f, 0f));

    private const VehicleLightStateFlags DuskBrakingLeft = VehicleLightStateFlags.Position | VehicleLightStateFlags.LowBeam
                                                           | VehicleLightStateFlags.Brake | VehicleLightStateFlags.LeftBlinker;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-live-lights-" + Guid.NewGuid().ToString("N"));
    private readonly StandInStreams _streams = new(Patience);
    private readonly List<(bool Declare, uint TicksPerStep, uint[] Simulated, uint[] Held, uint[] Cleared)> _put = [];
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
        _rpc.RegisterHandler<bool, uint, uint[], uint[], uint[], SuccessResponse<uint>>(
            "update_pose_source", (declare, ticksPerStep, simulated, held, cleared) =>
            {
                lock (_put)
                {
                    _put.Add((declare, ticksPerStep, simulated, held, cleared));
                }

                return Ok((uint)(simulated.Length + held.Length + cleared.Length));
            });
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
    public async Task A_Vehicle_In_The_Picture_Carries_Its_Lights_And_Where_Its_Pose_Came_From()
    {
        // Frame 120 of a drive stepping SUMO every twenty ticks from frame 100: a step falls on it, so the
        // lent body in the picture stands at SUMO's own step. The ambient vehicle is in the picture with its
        // lights, and no session placed it; B is lent and outside the picture.
        await Observe(120, ServerCarrying(PoseSource(20, 100)),
                      (BodyA, BelowTheCamera, DuskBrakingLeft), (BodyB, Elsewhere, VehicleLightStateFlags.Brake),
                      (Ambient, BesideIt, VehicleLightStateFlags.None));

        (FrameRecorder recorder, XElement events) = await RecordOneImage(120);

        Dictionary<string, XElement> extras = Extras(events);
        XElement a = extras["CARLA-TRUTH-SUMO-escort_0"];
        Assert.Equal("wholly", (string?)a.Attribute("in_frame"));
        Assert.Equal("position low_beam brake left_blinker", (string?)a.Attribute("lights"));
        Assert.Equal("simulated", (string?)a.Attribute("pose_source"));

        // A vehicle no session lent has lights, and no pose source: nothing placed it.
        XElement ambient = extras["CARLA-TRUTH-23"];
        Assert.Equal("none", (string?)ambient.Attribute("lights"));
        Assert.Null(ambient.Attribute("pose_source"));

        // Outside the picture: neither, though the snapshot carried both.
        XElement b = extras["CARLA-TRUTH-SUMO-guard_0"];
        Assert.Equal("none", (string?)b.Attribute("in_frame"));
        Assert.Null(b.Attribute("lights"));
        Assert.Null(b.Attribute("pose_source"));

        // The snapshot carried both, so the capture says neither is unknown.
        Assert.Null(events.Attribute("lights"));
        Assert.Null(events.Attribute("pose_source"));
        Assert.Equal(0, recorder.LightsUnknown);
        Assert.Equal(0, recorder.PoseSourceUnknown);
    }

    [Fact]
    public async Task A_Frame_Between_Two_Steps_Is_Interpolated_And_A_Body_The_Session_Could_Not_Place_Is_Held()
    {
        // Frame 107 lies between the steps at 100 and 120; B was named held, and stands in the picture.
        await Observe(107, ServerCarrying(PoseSource(20, 100, (BodyB, NamedHeld))),
                      (BodyA, BelowTheCamera, VehicleLightStateFlags.Position), (BodyB, AcrossFromIt, VehicleLightStateFlags.Reverse),
                      (Ambient, Elsewhere, VehicleLightStateFlags.None));

        (_, XElement events) = await RecordOneImage(107);

        Dictionary<string, XElement> extras = Extras(events);
        Assert.Equal("interpolated", (string?)extras["CARLA-TRUTH-SUMO-escort_0"].Attribute("pose_source"));
        Assert.Equal("position", (string?)extras["CARLA-TRUTH-SUMO-escort_0"].Attribute("lights"));
        Assert.Equal("held", (string?)extras["CARLA-TRUTH-SUMO-guard_0"].Attribute("pose_source"));
        Assert.Equal("reverse", (string?)extras["CARLA-TRUTH-SUMO-guard_0"].Attribute("lights"));
    }

    [Fact]
    public async Task A_Server_That_Carries_Neither_Is_Recorded_With_Neither_And_The_Capture_Says_They_Are_Unknown()
    {
        // A server built before the light state and the pose source: its vehicle bytes there are zero,
        // which must not be written as every light off, and no frame says where a pose came from.
        await Observe(120, OlderServer(),
                      (BodyA, BelowTheCamera, VehicleLightStateFlags.None), (BodyB, Elsewhere, VehicleLightStateFlags.None),
                      (Ambient, BesideIt, VehicleLightStateFlags.None));

        (FrameRecorder recorder, XElement events) = await RecordOneImage(120);

        Dictionary<string, XElement> extras = Extras(events);
        Assert.Equal("wholly", (string?)extras["CARLA-TRUTH-SUMO-escort_0"].Attribute("in_frame"));
        Assert.All(extras.Values, record =>
        {
            Assert.Null(record.Attribute("lights"));
            Assert.Null(record.Attribute("pose_source"));
        });
        Assert.Equal("unknown", (string?)events.Attribute("lights"));
        Assert.Equal("unknown", (string?)events.Attribute("pose_source"));
        Assert.Equal(1, recorder.LightsUnknown);
        Assert.Equal(1, recorder.PoseSourceUnknown);
    }

    [Fact]
    public async Task A_Change_Is_Put_To_The_Server_As_Given_And_A_Withdrawal_Naming_Bodies_Is_Never_Sent()
    {
        uint applied = await _client!.UpdatePoseSourceAsync(true, 20, [BodyA], [BodyB], [Ambient]);

        Assert.Equal(3u, applied);
        (bool declare, uint ticksPerStep, uint[] simulated, uint[] held, uint[] cleared) = Assert.Single(_put);
        Assert.True(declare);
        Assert.Equal(20u, ticksPerStep);
        Assert.Equal([BodyA], simulated);
        Assert.Equal([BodyB], held);
        Assert.Equal([Ambient], cleared);

        await Assert.ThrowsAsync<ArgumentException>(() => _client.UpdatePoseSourceAsync(true, 0, [], [BodyB], []));
        Assert.Single(_put);
    }

    /// <summary>What a snapshot's render set block carries, and whether its vehicles carry their lights.</summary>
    private sealed record Carried(byte[] Block, bool PoseSourceCarried, bool LightsCarried);

    /// A server that carries the render set, the pose source given and every vehicle's lights.
    private static Carried ServerCarrying(byte[] poseSource) =>
        new(EpisodeStatePoseSourceTests.RenderSetWith(Lending(), poseSource), true, true);

    /// A server that carries the render set and nothing of this change.
    private static Carried OlderServer() => new(Block([.. Lending()]), false, false);

    private static Entry[] Lending() =>
    [
        new Entry(BodyA, ObservedBodyState.Lent, 96, "escort_0", "military_truck"),
        new Entry(BodyB, ObservedBodyState.Lent, 98, "guard_0", "car"),
    ];

    private static byte[] PoseSource(uint ticksPerStep, ulong stepFrame, params (uint Actor, byte State)[] named) =>
        EpisodeStatePoseSourceTests.PoseSourceBlock(ticksPerStep, stepFrame, named);

    private static Dictionary<string, XElement> Extras(XElement events) => events.Elements("event")
        .ToDictionary(e => (string)e.Attribute("uid")!, e => e.Element("detail")!.Element("_carla")!);

    /// The world observer streams <paramref name="frame"/> with the given block and actors, and the
    /// client has it before this returns.
    private async Task Observe(ulong frame, Carried carried,
                               params (ActorId Id, Transform Pose, VehicleLightStateFlags Lights)[] actors)
    {
        CarlaClient client = _client!;
        await client.StartWorldObserverAsync();
        await _streams.SendAsync(ObserverStream, frame, frame * DeltaSeconds, default, Snapshot(carried, actors));
        await Until(() => client.LatestObservedFrame == frame, $"the observer reaching frame {frame}");
    }

    /// Records a camera given no source of its own, streams it one image of <paramref name="frame"/>
    /// taken from above A, and returns the recorder and the sidecar.
    private async Task<(FrameRecorder Recorder, XElement Events)> RecordOneImage(ulong frame)
    {
        string directory = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        var recorder = new FrameRecorder(_client!, _streams.Token(CameraStream), directory, 2.0,
                                         cameraName: RecordedCamera);
        try
        {
            await _streams.SendAsync(CameraStream, frame, frame * DeltaSeconds, Camera, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        return (recorder, XDocument.Load(Directory.GetFiles(directory, "*.xml").Single()).Root!);
    }

    /// A snapshot as the server writes it: the header with the flags it carries, the render set block,
    /// then each actor at its pose with its lights in its vehicle record.
    private static byte[] Snapshot(Carried carried, (ActorId Id, Transform Pose, VehicleLightStateFlags Lights)[] actors)
    {
        byte[] bytes = EpisodeStateRenderSetTests.Snapshot(carried.Block, [.. actors.Select(actor => actor.Id)]);
        if (carried.PoseSourceCarried) bytes[32] |= EpisodeStateLayout.PoseSourceCarried;
        if (carried.LightsCarried) bytes[32] |= EpisodeStateLayout.VehicleLightStateCarried;
        int first = WideHeaderSize + carried.Block.Length;
        for (int i = 0; i < actors.Length; i++)
        {
            Span<byte> actor = bytes.AsSpan(first + (ActorSize * i), ActorSize);
            StandInStreams.WriteTransform(actor[5..], actors[i].Pose);
            // VehicleData::light_state, at offset 30 of the type-dependent union that starts at 65. A server
            // that carries none leaves these bytes zero, as here.
            if (carried.LightsCarried)
                BinaryPrimitives.WriteUInt32LittleEndian(actor[(65 + 30)..], (uint)actors[i].Lights);
        }

        return bytes;
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
