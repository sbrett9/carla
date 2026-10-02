using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Rpc.Enums;
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
