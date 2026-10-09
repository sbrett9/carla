// The truth any process reads during a SUMO drive lists the bodies a frame drew, named by their SUMO
// vehicles, and no parked body -- from the render set the server carries on every world-observer
// snapshot, not from a source in the driving process. See
// Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md §5.2 and
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/03_CoSimulation_Runtime.md §8.9.
//
// Before the server carried it, the live pull (get_vehicle_telemetry), and so the live CoT feed and a
// recorder in any process but the driving one, returned every vehicle actor: on a Gardnerville
// capture 1,385 of 2,608 records were bodies parked 300 m below the ground, and none named a SUMO
// vehicle. A stand-in server here streams a real client the snapshots a drive produces -- a body lent,
// a body parked and a vehicle no session named -- and the truth read from them is checked frame by
// frame, through the live pull and through a recorder given no source of its own.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using CarlaNet.Recording;
using CarlaNet.Tests.Sensors;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Streaming;
using static CarlaNet.Tests.Sensors.EpisodeStateRenderSetTests;

namespace CarlaNet.Tests.Recording;

public sealed class LiveTruthRenderSetTests : IAsyncLifetime
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
    private const string RecordedCamera = "LIVE-TRUTH";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly GeoLocation Origin = new(27.15012, 56.18065, 12.0);

    // Two pooled bodies and a vehicle no session named.
    private const ActorId BodyA = 21;
    private const ActorId BodyB = 22;
    private const ActorId Ambient = 23;

    private static readonly Transform OnTheRoad = new(new Location(100f, 200f, 1f), new Rotation(0f, 90f, 0f));
    private static readonly Transform ElsewhereOnTheRoad = new(new Location(140f, 260f, 1f), new Rotation(0f, 0f, 0f));
    private static readonly Transform InItsParkingSlot = new(new Location(5200f, 5200f, -300f), default);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-live-truth-" + Guid.NewGuid().ToString("N"));
    private readonly StandInStreams _streams = new(Patience);
    private readonly List<(uint[] Lent, string[] Vehicles, string[] Types, uint[] Parked)> _named = [];
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
        _rpc.RegisterHandler<uint[], string[], string[], uint[], SuccessResponse<uint>>(
            "update_render_set", (lent, vehicles, types, parked) =>
            {
                lock (_named)
                {
                    _named.Add((lent, vehicles, types, parked));
                }

                return Ok((uint)(lent.Length + parked.Length));
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
    public async Task The_Live_Pull_Lists_The_Bodies_The_Newest_Frame_Drew_Named_By_Vehicle_And_No_Parked_Body()
    {
        await Observe(100, Block(new Entry(BodyA, ObservedBodyState.Lent, 96, "escort_0", "military_truck"),
                                 new Entry(BodyB, ObservedBodyState.Parked, 0, "", "")),
                      (BodyA, OnTheRoad), (BodyB, InItsParkingSlot), (Ambient, ElsewhereOnTheRoad));

        IReadOnlyList<VehicleTelemetry> records = new VehicleTelemetryService(_client!).Compute(Origin);

        Assert.Equal([BodyA, Ambient], records.Select(record => record.Id).Order());
        VehicleTelemetry lent = records.Single(record => record.Id == BodyA);
        Assert.Equal(new RenderedVehicle(BodyA, "escort_0", "military_truck", 96), lent.Rendered);
        // The provenance the pool spawned the body under, read from the actor's own description.
        Assert.Equal("sumo", lent.RoleName);
        // The direction each body points, from its transform's yaw: +Y is south, +X east.
        Assert.Equal(180.0, lent.HeadingDeg, 6);
        Assert.Equal(90.0, records.Single(record => record.Id == Ambient).HeadingDeg, 6);
        // The server is told no SUMO angle, so the live pull carries none.
        Assert.Null(lent.Rendered!.SumoAngleDegrees);
        // A vehicle no session named is reported as it always was.
        Assert.Null(records.Single(record => record.Id == Ambient).Rendered);
    }

    [Fact]
    public async Task Each_Frame_Is_Read_With_The_Render_Set_Its_Own_Snapshot_Carried()
    {
        // Frame 100: A draws "first" and B stands parked. Frame 101: A has been given back and B is
        // lent to "second". The poses changed with the naming, and truth read as of either frame must
        // take both from that frame.
        await Observe(100, Block(new Entry(BodyA, ObservedBodyState.Lent, 90, "first", "car"),
                                 new Entry(BodyB, ObservedBodyState.Parked, 0, "", "")),
                      (BodyA, OnTheRoad), (BodyB, InItsParkingSlot));
        await Observe(101, Block(new Entry(BodyA, ObservedBodyState.Parked, 0, "", ""),
                                 new Entry(BodyB, ObservedBodyState.Lent, 101, "second", "car")),
                      (BodyA, InItsParkingSlot), (BodyB, OnTheRoad));
        var telemetry = new VehicleTelemetryService(_client!);

        IReadOnlyList<VehicleTelemetry>? atHundred = telemetry.ComputeAt(Origin, 100, out ObservedRenderSet setHundred,
                                                                         out _);
        IReadOnlyList<VehicleTelemetry>? atHundredOne = telemetry.ComputeAt(Origin, 101, out _, out _);
        IReadOnlyList<VehicleTelemetry> live = telemetry.Compute(Origin);

        Assert.NotNull(atHundred);
        Assert.False(setHundred.IsEmpty);
        Assert.Equal("first", Assert.Single(atHundred).Rendered!.SumoId);
        Assert.Equal(BodyA, atHundred[0].Id);

        Assert.NotNull(atHundredOne);
        Assert.Equal("second", Assert.Single(atHundredOne).Rendered!.SumoId);
        Assert.Equal(BodyB, atHundredOne[0].Id);

        // The live pull reads the newest frame, and its body and its naming from that same frame.
        Assert.Equal(BodyB, Assert.Single(live).Id);
        Assert.Equal("second", live[0].Rendered!.SumoId);

        // A frame the client does not hold has no truth: not the neighbour's, and not the newest.
        Assert.Null(telemetry.ComputeAt(Origin, 102, out ObservedRenderSet none, out _));
        Assert.True(none.IsEmpty);
    }

    [Fact]
    public async Task A_World_No_Session_Has_Named_A_Body_In_Is_Reported_As_Before()
    {
        await Observe(100, block: null,
                      (BodyA, OnTheRoad), (BodyB, InItsParkingSlot), (Ambient, ElsewhereOnTheRoad));

        IReadOnlyList<VehicleTelemetry> records = new VehicleTelemetryService(_client!)
            .Compute(Origin, out ObservedRenderSet renderSet);

        Assert.True(renderSet.IsEmpty);
        Assert.Equal([BodyA, BodyB, Ambient], records.Select(record => record.Id).Order());
        Assert.All(records, record => Assert.Null(record.Rendered));
    }

    [Fact]
    public async Task A_Recorder_Given_No_Source_Lists_The_Server_s_Render_Set_And_Says_So()
    {
        // A recorder in a process other than the driving one, which has no render set of its own.
        await Observe(100, Block(new Entry(BodyA, ObservedBodyState.Lent, 96, "escort_0", "military_truck"),
                                 new Entry(BodyB, ObservedBodyState.Parked, 0, "", "")),
                      (BodyA, OnTheRoad), (BodyB, InItsParkingSlot), (Ambient, ElsewhereOnTheRoad));

        XElement events = await RecordOneImage(100);

        Assert.Equal("rendered", (string?)events.Attribute("vehicles"));
        XElement[] vehicles = [.. events.Elements("event")];
        Assert.Equal(["CARLA-TRUTH-23", "CARLA-TRUTH-SUMO-escort_0"],
                     vehicles.Select(e => (string)e.Attribute("uid")!).Order(StringComparer.Ordinal));
        XElement escort = vehicles.Single(e => (string?)e.Attribute("uid") == "CARLA-TRUTH-SUMO-escort_0");
        XElement extras = escort.Element("detail")!.Element("_carla")!;
        Assert.Equal("21", (string?)extras.Attribute("actor_id"));
        Assert.Equal("escort_0", (string?)extras.Attribute("sumo_id"));
        Assert.Equal("military_truck", (string?)extras.Attribute("vtype_id"));
        Assert.Equal("96", (string?)extras.Attribute("admitted_tick"));
        Assert.Equal("sumo", (string?)extras.Attribute("role_name"));
        Assert.Equal("car-escort_0", (string?)escort.Element("detail")!.Element("contact")!.Attribute("callsign"));
    }

    [Fact]
    public async Task A_Recorder_In_A_World_No_Session_Named_A_Body_In_Writes_What_It_Always_Wrote()
    {
        await Observe(100, block: null,
                      (BodyA, OnTheRoad), (BodyB, InItsParkingSlot), (Ambient, ElsewhereOnTheRoad));

        XElement events = await RecordOneImage(100);

        Assert.Null(events.Attribute("vehicles"));
        Assert.Equal(["CARLA-TRUTH-21", "CARLA-TRUTH-22", "CARLA-TRUTH-23"],
                     events.Elements("event").Select(e => (string)e.Attribute("uid")!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_Recorder_Given_A_Draw_Distance_Marks_Every_Vehicle_Its_Camera_Did_Not_Draw()
    {
        // A recorder in another process, told the distance the drive's bodies are drawn under. The
        // camera hangs 49 m above the lent body and 87 m from the vehicle no session named, whose
        // 3.3 m bounding sphere lies wholly beyond 60 m.
        await Observe(100, Block(new Entry(BodyA, ObservedBodyState.Lent, 96, "escort_0", "military_truck"),
                                 new Entry(BodyB, ObservedBodyState.Parked, 0, "", "")),
                      (BodyA, OnTheRoad), (BodyB, InItsParkingSlot), (Ambient, ElsewhereOnTheRoad));
        var camera = new Transform(new Location(100f, 200f, 50f), new Rotation(-90f, 0f, 0f));

        (FrameRecorder recorder, XElement events) = await RecordOneImage(100, camera, drawDistance: 60.0);

        Assert.Equal("60", (string?)events.Attribute("draw_distance_m"));
        Dictionary<string, XElement> extras = events.Elements("event")
            .ToDictionary(e => (string)e.Attribute("uid")!, e => e.Element("detail")!.Element("_carla")!);
        // Both are listed: each is in the world, with its truth.
        Assert.Equal(["CARLA-TRUTH-23", "CARLA-TRUTH-SUMO-escort_0"], extras.Keys.Order(StringComparer.Ordinal));
        Assert.Null(extras["CARLA-TRUTH-SUMO-escort_0"].Attribute("beyond_draw_distance"));
        Assert.Equal("wholly", (string?)extras["CARLA-TRUTH-23"].Attribute("beyond_draw_distance"));
        Assert.Equal("87.2", (string?)extras["CARLA-TRUTH-23"].Attribute("camera_range_m"));
        // The lent body is in the picture, straight below the camera, so it carries its box: the range
        // to its center, 49 m, and its eight corners. The other is outside the picture and carries none.
        Assert.Equal("wholly", (string?)extras["CARLA-TRUTH-SUMO-escort_0"].Attribute("in_frame"));
        Assert.Equal("49.0", (string?)extras["CARLA-TRUTH-SUMO-escort_0"].Attribute("camera_range_m"));
        Assert.NotNull(extras["CARLA-TRUTH-SUMO-escort_0"].Attribute("box_px"));
        XElement[] corners = [.. extras["CARLA-TRUTH-SUMO-escort_0"].Parent!.Element("_box3d")!.Elements("corner")];
        Assert.Equal(8, corners.Length);
        // Its box is centered on its point, so the corners average to the point the record carries.
        XElement point = extras["CARLA-TRUTH-SUMO-escort_0"].Parent!.Parent!.Element("point")!;
        foreach (string axis in new[] { "lat", "lon" })
            Assert.InRange(corners.Average(c => (double)c.Attribute(axis)!) - (double)point.Attribute(axis)!, -2e-7, 2e-7);
        Assert.InRange(corners.Average(c => (double)c.Attribute("hae")!) - (double)point.Attribute("hae")!, -0.011, 0.011);
        Assert.Equal("none", (string?)extras["CARLA-TRUTH-23"].Attribute("in_frame"));
        Assert.Null(extras["CARLA-TRUTH-23"].Attribute("box_px"));
        Assert.Null(extras["CARLA-TRUTH-23"].Parent!.Element("_box3d"));
        Assert.Equal(1, recorder.DrawDistanceCaptures);
        Assert.Equal(1, recorder.VehiclesBeyondDrawDistance);
        Assert.Equal(0, recorder.VehiclesPartlyBeyondDrawDistance);
    }

    [Fact]
    public async Task A_Recorder_Paired_With_The_Session_Marks_By_The_Distance_Each_Frame_Was_Drawn_Under()
    {
        // The session's own frames say what each was drawn under, a distance the server refused
        // included: then nothing was culled, and nothing is marked, whatever the recorder was told.
        await Observe(100, Block(new Entry(BodyA, ObservedBodyState.Lent, 96, "escort_0", "military_truck"),
                                 new Entry(BodyB, ObservedBodyState.Parked, 0, "", "")),
                      (BodyA, OnTheRoad), (BodyB, InItsParkingSlot), (Ambient, ElsewhereOnTheRoad));
        var high = new Transform(new Location(100f, 200f, 500f), new Rotation(-90f, 0f, 0f));
        RenderedVehicle escort = new(BodyA, "escort_0", "military_truck", 96);

        (FrameRecorder limited, XElement drawn) = await RecordOneImage(
            100, high, source: new OneFrame(100, new RenderSet([escort]) { DrawDistanceMetres = 60.0 }));
        XElement extras = Assert.Single(drawn.Elements("event")).Element("detail")!.Element("_carla")!;
        Assert.Equal("60", (string?)drawn.Attribute("draw_distance_m"));
        Assert.Equal("wholly", (string?)extras.Attribute("beyond_draw_distance"));
        Assert.Equal(1, limited.VehiclesBeyondDrawDistance);

        (FrameRecorder refused, XElement unlimited) = await RecordOneImage(
            100, high, drawDistance: 60.0, source: new OneFrame(100, new RenderSet([escort])),
            stream: CameraStream + 1);
        Assert.Null(unlimited.Attribute("draw_distance_m"));
        Assert.Null(Assert.Single(unlimited.Elements("event")).Element("detail")!.Element("_carla")!
                        .Attribute("beyond_draw_distance"));
        Assert.Equal(0, refused.DrawDistanceCaptures);
    }

    [Fact]
    public async Task A_Change_Is_Named_To_The_Server_As_Given_Whatever_Collection_Carries_It()
    {
        uint found = await _client!.UpdateRenderSetAsync([BodyA], ["escort_0"], ["military_truck"], [BodyB]);

        Assert.Equal(2u, found);
        (uint[] lent, string[] vehicles, string[] types, uint[] parked) = Assert.Single(_named);
        Assert.Equal([BodyA], lent);
        Assert.Equal(["escort_0"], vehicles);
        Assert.Equal(["military_truck"], types);
        Assert.Equal([BodyB], parked);
    }

    [Fact]
    public async Task A_Change_Whose_Bodies_And_Vehicles_Do_Not_Pair_Is_Refused_Before_It_Is_Sent()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client!.UpdateRenderSetAsync([BodyA, BodyB], ["escort_0"], ["military_truck"], []));
        Assert.Empty(_named);
    }

    /// The world observer streams <paramref name="frame"/> with the given block and actors, and the
    /// client has it before this returns.
    private async Task Observe(ulong frame, byte[]? block, params (ActorId Id, Transform Pose)[] actors)
    {
        CarlaClient client = _client!;
        await client.StartWorldObserverAsync();
        await _streams.SendAsync(ObserverStream, frame, frame * DeltaSeconds, default, Snapshot(block, actors));
        await Until(() => client.LatestObservedFrame == frame, $"the observer reaching frame {frame}");
    }

    /// Records a camera under a draw distance or with a render-set source, streams it one image of
    /// <paramref name="frame"/> taken from <paramref name="camera"/>, and returns the recorder and the
    /// sidecar.
    private async Task<(FrameRecorder Recorder, XElement Events)> RecordOneImage(
        ulong frame, Transform camera, double? drawDistance = null, IRenderSetSource? source = null,
        uint stream = CameraStream)
    {
        string directory = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        var recorder = new FrameRecorder(_client!, _streams.Token(stream), directory, 2.0,
                                         renderSet: source, drawDistanceMetres: drawDistance,
                                         cameraName: RecordedCamera);
        try
        {
            await _streams.SendAsync(stream, frame, frame * DeltaSeconds, camera, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        return (recorder, XDocument.Load(Directory.GetFiles(directory, "*.xml").Single()).Root!);
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

    /// Records a camera, streams it one image of <paramref name="frame"/>, and returns the sidecar.
    private async Task<XElement> RecordOneImage(ulong frame)
    {
        var recorder = new FrameRecorder(_client!, _streams.Token(CameraStream), _dir, 2.0,
                                         cameraName: RecordedCamera);
        try
        {
            await _streams.SendAsync(CameraStream, frame, frame * DeltaSeconds, default, Image());
            await Until(() => recorder.Saved == 1, "the capture being written");
        }
        finally
        {
            recorder.Dispose();
        }

        return XDocument.Load(Directory.GetFiles(_dir, "*.xml").Single()).Root!;
    }

    /// A snapshot as the server writes it: the header of a server that carries the corrected
    /// elevation, the render set block where one is given, then each actor at its pose.
    private static byte[] Snapshot(byte[]? block, (ActorId Id, Transform Pose)[] actors)
    {
        byte[] bytes = EpisodeStateRenderSetTests.Snapshot(block, [.. actors.Select(actor => actor.Id)]);
        int first = WideHeaderSize + (block?.Length ?? 0);
        for (int i = 0; i < actors.Length; i++)
        {
            StandInStreams.WriteTransform(bytes.AsSpan(first + (ActorSize * i) + 5), actors[i].Pose);
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
