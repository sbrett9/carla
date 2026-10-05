using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Rpc.Enums;
using CarlaNet.Types.Supervision;
using MessagePack;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What the connected world sends a server, read off a stand-in that answers as the CARLA server
/// does.
/// </summary>
public sealed class CarlaClientWorldTests : IAsyncLifetime
{
    // Every call here returns R<T>, whose success is [[1, value]]. These reproduce that envelope.
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    private readonly List<(uint[] Lent, string[] Vehicles, string[] Types, uint[] Parked)> _named = [];
    private readonly List<ActorDescription> _spawned = [];
    private MsgPackRpcServer? _server;
    private CarlaClient? _client;

    // Two vehicle definitions as the server publishes them: one declaring role_name as every vehicle
    // blueprint does, defaulting to the traffic manager's value, and one declaring none.
    private static readonly ActorDefinition[] Definitions =
    [
        new(17u, "vehicle.fuso.mitsubishi", "vehicle,fuso,mitsubishi",
        [
            new ActorAttribute("color", ActorAttributeType.RGBColor, "200,30,30", ["200,30,30"], true, false),
            new ActorAttribute("role_name", ActorAttributeType.String, "autopilot",
                               ["autopilot", "scenario", "ego_vehicle"], true, false),
            new ActorAttribute("number_of_wheels", ActorAttributeType.Int, "4", [], false, false),
        ]),
        new(18u, "vehicle.stand.in", "vehicle,stand,in",
        [
            new ActorAttribute("number_of_wheels", ActorAttributeType.Int, "4", [], false, false),
        ]),
    ];

    public async Task InitializeAsync()
    {
        int port = FreeLoopbackPort();
        _server = new MsgPackRpcServer(IPAddress.Loopback, port);
        _server.RegisterHandler("get_actor_definitions", () => Ok(Definitions));
        _server.RegisterHandler<ActorDescription, Transform, SuccessResponse<Actor>>(
            "spawn_actor", (description, _) =>
            {
                _spawned.Add(description);
                return Ok(new Actor((uint)(100 + _spawned.Count), 0u, description, new BoundingBox(), [], []));
            });
        await _server.StartAsync();
        _client = new CarlaClient("127.0.0.1", port, TimeSpan.FromSeconds(10));
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
    }

    [Fact]
    public void A_Change_To_The_Render_Set_Reaches_The_Server_As_The_Session_Named_It()
    {
        _server!.RegisterHandler<uint[], string[], string[], uint[], SuccessResponse<uint>>(
            "update_render_set", (lent, vehicles, types, parked) =>
            {
                _named.Add((lent, vehicles, types, parked));
                return Ok(2u);
            });
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        RenderSetWrite written = world.WriteRenderSet(
            [new LentBody(7, "escort_0", "military_truck"), new LentBody(9, "flow_3.12", "passenger")], [8]);

        Assert.True(written.Taken);
        Assert.Equal(2, written.BodiesFound);
        (uint[] lent, string[] vehicles, string[] types, uint[] parked) = Assert.Single(_named);
        Assert.Equal([7u, 9u], lent);
        Assert.Equal(["escort_0", "flow_3.12"], vehicles);
        Assert.Equal(["military_truck", "passenger"], types);
        Assert.Equal([8u], parked);
    }

    [Fact]
    public void A_Server_Without_The_Call_Is_A_Refusal_Carrying_Its_Words_Not_A_Failure()
    {
        // A server built before it carried a render set: the stand-in has no handler for the call,
        // and answers it with an error as the CARLA server does.
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        RenderSetWrite written = world.WriteRenderSet([new LentBody(7, "escort_0", "military_truck")], []);

        Assert.False(written.Taken);
        Assert.Equal(0, written.BodiesFound);
        Assert.Contains("update_render_set", written.Refusal);
    }

    [Fact]
    public void A_Draw_Distance_Reaches_The_Server_As_The_Bodies_Named_And_A_Double_Of_Metres()
    {
        // The server binds (std::vector<uint32>, double): the ids as an array of unsigned integers and
        // the distance as a msgpack float64, which is what a C# double is written as.
        List<(uint[] Bodies, object Metres)> sent = [];
        _server!.RegisterHandler<uint[], object, SuccessResponse<uint>>(
            "set_actors_max_draw_distance", (bodies, metres) =>
            {
                sent.Add((bodies, metres));
                return Ok((uint)bodies.Length - 1);
            });
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        DrawDistanceWrite written = world.WriteDrawDistance([7u, 9u, 11u], 250.0);

        Assert.True(written.Taken);
        Assert.Equal(2, written.BodiesFound);
        (uint[] bodies, object metres) = Assert.Single(sent);
        Assert.Equal([7u, 9u, 11u], bodies);
        Assert.Equal(250.0, Assert.IsType<double>(metres));

        // Zero clears it, and goes as a double too.
        world.WriteDrawDistance([7u], 0.0);
        Assert.Equal(0.0, Assert.IsType<double>(sent[^1].Metres));
    }

    [Fact]
    public void A_Change_To_The_Supervision_Reaches_The_Server_In_The_Arrays_The_Server_Unpacks()
    {
        // The server binds one carla::rpc::SupervisionUpdate, a MSGPACK_DEFINE_ARRAY of
        // (fresh, plan_id, vocabulary_version, vocabulary_digest, actors, absences_opened, absences_closed),
        // each actor (actor_id, state, annotations), each annotation (instance_id, labels, phase, role) and
        // each absence (instance_id, labels, areas, phase). Read raw here, as nested arrays, so the order
        // is checked against the server's and not against this client's own reading of it.
        List<object?[]> sent = [];
        _server!.RegisterHandler<object, SuccessResponse<uint>>("update_supervision", update =>
        {
            sent.Add((object?[])update);
            return Ok(1u);
        });
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);
        var plan = new SupervisionPlanIdentity("Shahid_Bahonar_Port_PatternOfLife", 2, "e357");
        var lead = new AnnotationInForce("Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3",
                                         ["bahonar:coordinated_group_transit"], "transit", "bahonar:lead");
        var unmanned = new AbsenceInForce("Shahid_Bahonar_Port_PatternOfLife/pi_tower_relief_d4_h7_t3_unmanned",
                                          ["bahonar:post_unmanned"], ["tower_03"], "vacancy");

        SupervisionWrite written = world.WriteSupervision(new SupervisionChange(
            true, plan,
            [new BodySupervision(7, new SupervisionInForce(SupervisionState.Annotated, [lead])),
             new BodySupervision(9, SupervisionInForce.Unlabelled)],
            [unmanned], ["Shahid_Bahonar_Port_PatternOfLife/pi_earlier"]));

        Assert.True(written.Taken);
        Assert.Equal(1, written.BodiesApplied);
        object?[] update = Assert.Single(sent);
        Assert.Equal(7, update.Length);
        Assert.Equal(true, update[0]);
        Assert.Equal("Shahid_Bahonar_Port_PatternOfLife", update[1]);
        Assert.Equal(2u, Convert.ToUInt32(update[2]));
        Assert.Equal("e357", update[3]);

        object?[] actors = (object?[])update[4]!;
        Assert.Equal(2, actors.Length);
        object?[] annotated = (object?[])actors[0]!;
        Assert.Equal(7u, Convert.ToUInt32(annotated[0]));
        // The state as the vocabulary's core spells it.
        Assert.Equal("annotated", annotated[1]);
        object?[] annotation = (object?[])Assert.Single((object?[])annotated[2]!)!;
        Assert.Equal(lead.InstanceId, annotation[0]);
        Assert.Equal(["bahonar:coordinated_group_transit"], ((object?[])annotation[1]!).Cast<string>());
        Assert.Equal("transit", annotation[2]);
        Assert.Equal("bahonar:lead", annotation[3]);
        object?[] cleared = (object?[])actors[1]!;
        Assert.Equal(9u, Convert.ToUInt32(cleared[0]));
        Assert.Equal("unlabelled", cleared[1]);
        Assert.Empty((object?[])cleared[2]!);

        object?[] absence = (object?[])Assert.Single((object?[])update[5]!)!;
        Assert.Equal(unmanned.InstanceId, absence[0]);
        Assert.Equal(["bahonar:post_unmanned"], ((object?[])absence[1]!).Cast<string>());
        Assert.Equal(["tower_03"], ((object?[])absence[2]!).Cast<string>());
        Assert.Equal("vacancy", absence[3]);
        Assert.Equal(["Shahid_Bahonar_Port_PatternOfLife/pi_earlier"], ((object?[])update[6]!).Cast<string>());

        // A withdrawal goes as a change naming no plan, and carrying nothing else.
        world.WriteSupervision(SupervisionChange.Withdrawal);
        object?[] withdrawal = sent[^1];
        Assert.Equal(false, withdrawal[0]);
        Assert.Equal(string.Empty, withdrawal[1]);
        Assert.Empty((object?[])withdrawal[4]!);
        Assert.Empty((object?[])withdrawal[5]!);
        Assert.Empty((object?[])withdrawal[6]!);
    }

    [Fact]
    public void A_Server_Without_The_Supervision_Call_Is_A_Refusal_Carrying_Its_Words()
    {
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        SupervisionWrite written = world.WriteSupervision(new SupervisionChange(
            true, new SupervisionPlanIdentity("plan", 2, "digest"), [], [], []));

        Assert.False(written.Taken);
        Assert.Equal(0, written.BodiesApplied);
        Assert.Contains("update_supervision", written.Refusal);
    }

    [Fact]
    public void A_Server_Without_The_Draw_Distance_Call_Is_A_Refusal_Carrying_Its_Words()
    {
        // A server built before it carried the call: the stand-in has no handler for it, and answers
        // with an error as the CARLA server does.
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        DrawDistanceWrite written = world.WriteDrawDistance([7u], 250.0);

        Assert.False(written.Taken);
        Assert.Equal(0, written.BodiesFound);
        Assert.Contains("set_actors_max_draw_distance", written.Refusal);
    }

    [Fact]
    public void A_Draw_Distance_That_Is_Not_Zero_Or_Positive_Is_Never_Sent()
    {
        int calls = 0;
        _server!.RegisterHandler<uint[], double, SuccessResponse<uint>>(
            "set_actors_max_draw_distance", (_, _) =>
            {
                calls++;
                return Ok(0u);
            });
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        Assert.Throws<ArgumentOutOfRangeException>(() => world.WriteDrawDistance([7u], -1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.WriteDrawDistance([7u], double.NaN));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_Camera_Is_Described_From_The_Attributes_The_Server_Gives_It()
    {
        // The session reads a camera it is told to follow with one get_actors_by_id, asked with a
        // collection expression -- which once failed at serialisation and refused a capture run placing
        // its first camera, and now reaches the server as the ids given.
        List<uint[]> asked = [];
        _server!.RegisterHandler<uint[], SuccessResponse<Actor[]>>("get_actors_by_id", ids =>
        {
            asked.Add(ids);
            return Ok(ids.Where(id => id is 41u or 42u).Select(id => new Actor(
                id, 0u,
                new ActorDescription(id, id == 41u ? "sensor.camera.rgb" : "vehicle.fuso.mitsubishi",
                    id == 41u
                        ? [
                            new ActorAttributeValue("image_size_x", ActorAttributeType.Int, "1280"),
                            new ActorAttributeValue("image_size_y", ActorAttributeType.Int, "720"),
                            new ActorAttributeValue("fov", ActorAttributeType.Float, "90"),
                        ]
                        : [new ActorAttributeValue("number_of_wheels", ActorAttributeType.Int, "4")]),
                new BoundingBox(), [], [])).ToArray());
        });
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        Assert.Equal(new CameraOptics(1280, 720, 90.0), world.DescribeCamera(41u));
        Assert.Null(world.DescribeCamera(42u));
        Assert.Null(world.DescribeCamera(43u));
        Assert.Equal([[41u], [42u], [43u]], asked);
    }

    [Fact]
    public void A_Body_Is_Spawned_Under_The_Role_It_Is_Given_In_Place_Of_The_Blueprint_s_Default()
    {
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        uint actor = world.Spawn("vehicle.fuso.mitsubishi", new Transform(), VehicleBodyPool.RoleName);

        Assert.Equal(101u, actor);
        ActorDescription sent = Assert.Single(_spawned);
        Assert.Equal(17u, sent.Uid);
        Assert.Equal("vehicle.fuso.mitsubishi", sent.Id);
        // The role replaces the default, and every other attribute goes as the definition gives it.
        Assert.Equal(
            [("color", "200,30,30"), ("role_name", "sumo"), ("number_of_wheels", "4")],
            sent.Attributes.Select(attribute => (attribute.Id, attribute.Value)));
        Assert.Equal(ActorAttributeType.String, sent.Attributes.Single(a => a.Id == "role_name").Type);
    }

    [Fact]
    public void A_Blueprint_Declaring_No_Role_Is_Given_One_Rather_Than_Spawned_With_No_Provenance()
    {
        CarlaClientWorld world = CarlaClientWorld.Attach(_client!, startWorldObserver: false);

        world.Spawn("vehicle.stand.in", new Transform(), VehicleBodyPool.RoleName);

        ActorDescription sent = Assert.Single(_spawned);
        Assert.Equal([("number_of_wheels", "4"), ("role_name", "sumo")],
                     sent.Attributes.Select(attribute => (attribute.Id, attribute.Value)));
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
