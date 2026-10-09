// get_vehicle_light_states: the wire name the client must send, and the shape the server replies
// with. Both were untested, and the name the client used was wrong.
//
// The server binds the SINGULAR name (CarlaServer.cpp, and LibCarla's own
// Client::GetVehiclesLightStates calls it that way); the plural is the Python API's World method
// name (PythonAPI/carla/src/World.cpp). The client sent the plural, so every call raised, and the
// stage's sole caller caught the exception without logging it - so nothing ever showed.
using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;

namespace CarlaNet.Tests.Transport;

public class VehicleLightStatesRpcTests
{
    // The server's return type is R<VehicleLightStateList> = Response<std::vector<std::pair<
    // ActorId, uint32_t>>>. Response is MSGPACK_DEFINE_ARRAY over a variant, so a success is
    // [[1, value]]: a one-element array holding [variant index, value]. The pairs inside are
    // two-element arrays. These two types reproduce that envelope for a stand-in server.
    [MessagePackObject]
    public record struct SuccessVariant(
        [property: Key(0)] int VariantIndex,
        [property: Key(1)] (uint, uint)[] Value);

    [MessagePackObject]
    public record struct SuccessResponse([property: Key(0)] SuccessVariant Data);

    private static readonly (uint Id, uint Flags)[] ReplyPairs =
    {
        (11u, (uint)(VehicleLightStateFlags.Position | VehicleLightStateFlags.LowBeam)),
        (12u, (uint)VehicleLightStateFlags.Brake),
    };

    private static SuccessResponse Reply() => new(new SuccessVariant(1, ReplyPairs));

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

    [Fact]
    public async Task Client_Calls_The_Name_The_Server_Binds()
    {
        int port = FreeLoopbackPort();
        await using var server = new MsgPackRpcServer(IPAddress.Loopback, port);
        bool reached = false;
        server.RegisterHandler("get_vehicle_light_states", () => { reached = true; return Reply(); });
        await server.StartAsync();

        await using var client = new CarlaClient("127.0.0.1", port, TimeSpan.FromSeconds(10));
        var states = await client.GetVehiclesLightStatesAsync();

        Assert.True(reached);
        Assert.Equal(2, states.Count);
        Assert.Equal(11u, states[0].Item1);
        Assert.Equal(VehicleLightStateFlags.Position | VehicleLightStateFlags.LowBeam,
            states[0].Item2);
        Assert.Equal(12u, states[1].Item1);
        Assert.Equal(VehicleLightStateFlags.Brake, states[1].Item2);
    }

    [Fact]
    public async Task An_Unbound_Name_Raises_Rather_Than_Returning_Nothing()
    {
        // The failure path the defect hid in. A server that does not bind the name the client sends
        // answers with an error, which surfaces as an exception - nothing returns an empty list. A
        // caller that catches and discards therefore cannot tell "no vehicles" from "wrong name",
        // which is why this survived: the plural name below is what the client used to send.
        int port = FreeLoopbackPort();
        await using var server = new MsgPackRpcServer(IPAddress.Loopback, port);
        server.RegisterHandler("get_vehicles_light_states", Reply);
        await server.StartAsync();

        await using var client = new CarlaClient("127.0.0.1", port, TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetVehiclesLightStatesAsync());
    }
}
