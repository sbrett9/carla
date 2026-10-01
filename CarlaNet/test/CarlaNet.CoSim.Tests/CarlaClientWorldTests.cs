using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Rpc.Actors;
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
    private MsgPackRpcServer? _server;
    private CarlaClient? _client;

    public async Task InitializeAsync()
    {
        int port = FreeLoopbackPort();
        _server = new MsgPackRpcServer(IPAddress.Loopback, port);
        _server.RegisterHandler("get_actor_definitions", () => Ok(Array.Empty<ActorDefinition>()));
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
