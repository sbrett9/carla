// The truth record's base_type and special_type come from the vehicle catalogue. See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md D6.18 and
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/04_Contracts.md §3.4.3.
//
// The content build's own base_type and special_type are hand-edited: when the catalogue was first
// swept six saloons declared themselves buses, a van declared nothing, and every special_type was
// empty, so the ambulance, the fire appliance and the police car read as ordinary cars. The catalogue
// curates both per class, and the owner ruled that the truth takes both from it. A SUMO drive session
// hands its catalogue's tables to the client it drives through; every truth reader on that client
// then reports a catalogued blueprint with the catalogue's base type and kind, an empty kind included,
// and any other blueprint with what it declares -- its base type from its wheel count where it
// declares none. A stand-in server streams a real client the snapshots of four vehicles that declare
// what their catalogue does not give them, and the truth is read through the live pull and through a
// recorder.
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

public sealed class CatalogueVehicleKindTests : IAsyncLifetime
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
    private const string RecordedCamera = "CATALOGUE-KIND";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly GeoLocation Origin = new(27.15012, 56.18065, 12.0);

    // A taxi whose blueprint declares itself a bus and no kind, as the content once declared six
    // saloons buses and every blueprint no kind; a saloon whose blueprint declares a base type and a
    // kind its class does not curate; a blueprint the catalogue does not hold, declaring its own; and
    // another it does not hold that declares no base type, on two wheels.
    private const ActorId Taxi = 31;
    private const ActorId Saloon = 32;
    private const ActorId Unlisted = 33;
    private const ActorId TwoWheeled = 34;

    private const string TaxiBlueprint = "vehicle.taxi.ford";
    private const string SaloonBlueprint = "vehicle.ue4.ford.crown";
    private const string UnlistedBlueprint = "vehicle.tesla.cybertruck";
    private const string TwoWheeledBlueprint = "vehicle.vespa.zx125";

    // The base types and kinds the shipped catalogue's taxi and civ_car classes curate for these two.
    private static readonly Dictionary<string, string> CataloguedBaseTypes = new()
    {
        [TaxiBlueprint] = "car",
        [SaloonBlueprint] = "car",
    };

    private static readonly Dictionary<string, string> CataloguedKinds = new()
    {
        [TaxiBlueprint] = "taxi",
        [SaloonBlueprint] = string.Empty,
    };

    private static readonly Transform OnTheRoad = new(new Location(100f, 200f, 1f), new Rotation(0f, 90f, 0f));
    private static readonly Transform BehindIt = new(new Location(100f, 210f, 1f), new Rotation(0f, 90f, 0f));
    private static readonly Transform AcrossTheJunction = new(new Location(140f, 260f, 1f), default);
    private static readonly Transform BesideIt = new(new Location(145f, 260f, 1f), default);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "carlanet-catalogue-kind-" + Guid.NewGuid().ToString("N"));
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
    public async Task The_Live_Pull_Gives_A_Catalogued_Blueprint_The_Catalogue_s_Kinds_And_Any_Other_Its_Own()
    {
        _client!.AdoptCatalogueBaseTypes(CataloguedBaseTypes);
        _client.AdoptCatalogueSpecialTypes(CataloguedKinds);
        await Observe(100, block: null, (Taxi, OnTheRoad), (Saloon, BehindIt), (Unlisted, AcrossTheJunction),
                      (TwoWheeled, BesideIt));

        IReadOnlyList<VehicleTelemetry> records = new VehicleTelemetryService(_client).Compute(Origin);

        Assert.Equal(new Dictionary<uint, (string, string)>
                     {
                         // The catalogue's car, not the bus the blueprint declares.
                         [Taxi] = ("car", "taxi"),
                         // The catalogue's car and empty kind, not the van and "electric" it declares.
                         [Saloon] = ("car", string.Empty),
                         // No catalogue curates these, so what they declare stands, and a base type
                         // declared by nobody comes from the wheel count.
                         [Unlisted] = ("truck", "electric"),
                         [TwoWheeled] = ("motorcycle", string.Empty),
                     },
                     records.ToDictionary(record => record.Id, record => (record.BaseType, record.SpecialType)));
    }

    [Fact]
    public async Task A_Connection_That_Adopted_No_Catalogue_Reports_What_Each_Blueprint_Declares()
    {
        await Observe(100, block: null, (Taxi, OnTheRoad), (Saloon, BehindIt), (Unlisted, AcrossTheJunction),
                      (TwoWheeled, BesideIt));

        IReadOnlyList<VehicleTelemetry> records = new VehicleTelemetryService(_client!).Compute(Origin);

        Assert.Empty(_client!.CatalogueBaseTypes);
        Assert.Empty(_client.CatalogueSpecialTypes);
        Assert.Equal(new Dictionary<uint, (string, string)>
                     {
                         [Taxi] = ("bus", string.Empty),
                         [Saloon] = ("van", "electric"),
                         [Unlisted] = ("truck", "electric"),
                         [TwoWheeled] = ("motorcycle", string.Empty),
                     },
                     records.ToDictionary(record => record.Id, record => (record.BaseType, record.SpecialType)));
    }

    [Fact]
    public async Task A_Recorder_Writes_Each_SUMO_Vehicle_With_The_Kinds_Its_Body_s_Catalogue_Class_Curates()
    {
        // A SUMO drive: two bodies lent, each named by its vehicle, and two vehicles no session named.
        _client!.AdoptCatalogueBaseTypes(CataloguedBaseTypes);
        _client.AdoptCatalogueSpecialTypes(CataloguedKinds);
        await Observe(100, Block(new Entry(Taxi, ObservedBodyState.Lent, 96, "cab_0", "civ_taxi.vehicle.taxi.ford"),
                                 new Entry(Saloon, ObservedBodyState.Lent, 90, "car_7", "civ_car.vehicle.ue4.ford.crown")),
                      (Taxi, OnTheRoad), (Saloon, BehindIt), (Unlisted, AcrossTheJunction), (TwoWheeled, BesideIt));

        XElement events = await RecordOneImage(100);

        // The base type is in the truth extras and in the callsign built from it.
        Dictionary<string, (string?, string?, string?)> kinds = events.Elements("event").ToDictionary(
            e => (string)e.Attribute("uid")!,
            e => ((string?)e.Element("detail")!.Element("_carla")!.Attribute("base_type"),
                  (string?)e.Element("detail")!.Element("_carla")!.Attribute("special_type"),
                  (string?)e.Element("detail")!.Element("contact")!.Attribute("callsign")));
        Assert.Equal(new Dictionary<string, (string?, string?, string?)>
                     {
                         ["CARLA-TRUTH-SUMO-cab_0"] = ("car", "taxi", "car-cab_0"),
                         ["CARLA-TRUTH-SUMO-car_7"] = ("car", string.Empty, "car-car_7"),
                         ["CARLA-TRUTH-33"] = ("truck", "electric", "truck-33"),
                         ["CARLA-TRUTH-34"] = ("motorcycle", string.Empty, "motorcycle-34"),
                     },
                     kinds);
    }

    [Fact]
    public void An_Adopted_Table_Is_Held_As_It_Was_Given_Until_Another_Replaces_It()
    {
        var kinds = new Dictionary<string, string>(CataloguedKinds);
        var baseTypes = new Dictionary<string, string>(CataloguedBaseTypes);
        _client!.AdoptCatalogueSpecialTypes(kinds);
        _client.AdoptCatalogueBaseTypes(baseTypes);

        // A change to the caller's dictionaries afterwards is not a change to what is reported.
        kinds[SaloonBlueprint] = "electric";
        kinds[UnlistedBlueprint] = "electric";
        baseTypes[SaloonBlueprint] = "van";
        baseTypes[UnlistedBlueprint] = "truck";
        Assert.Equal(string.Empty, _client.CatalogueSpecialTypes[SaloonBlueprint]);
        Assert.False(_client.CatalogueSpecialTypes.ContainsKey(UnlistedBlueprint));
        Assert.Equal("car", _client.CatalogueBaseTypes[SaloonBlueprint]);
        Assert.False(_client.CatalogueBaseTypes.ContainsKey(UnlistedBlueprint));

        // A later adoption replaces the table whole rather than merging into it, and each table only
        // its own.
        _client.AdoptCatalogueSpecialTypes(new Dictionary<string, string> { [UnlistedBlueprint] = "electric" });
        _client.AdoptCatalogueBaseTypes(new Dictionary<string, string> { [UnlistedBlueprint] = "truck" });
        Assert.Equal([UnlistedBlueprint], _client.CatalogueSpecialTypes.Keys);
        Assert.Equal([UnlistedBlueprint], _client.CatalogueBaseTypes.Keys);
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

    /// Each vehicle as its blueprint describes it, with the base_type and special_type the blueprint
    /// declares and its wheel count.
    private static Actor Describe(uint id)
    {
        (string blueprint, string baseType, string kind, string wheels) = id switch
        {
            Taxi => (TaxiBlueprint, "bus", "", "4"),
            Saloon => (SaloonBlueprint, "van", "electric", "4"),
            TwoWheeled => (TwoWheeledBlueprint, "", "", "2"),
            _ => (UnlistedBlueprint, "truck", "electric", "4"),
        };
        return new Actor(
            id, 0u,
            new ActorDescription(id, blueprint,
            [
                new ActorAttributeValue("base_type", ActorAttributeType.String, baseType),
                new ActorAttributeValue("special_type", ActorAttributeType.String, kind),
                new ActorAttributeValue("number_of_wheels", ActorAttributeType.Int, wheels),
                new ActorAttributeValue("role_name", ActorAttributeType.String, "sumo"),
            ]),
            new BoundingBox(default, new Vector3D(2.6f, 0.9f, 0.8f), default),
            [], []);
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
