// An RPC argument is written as the sequence it is, whatever collection type the caller built it as.
//
// MessagePack resolves a formatter from the argument's RUNTIME type. A collection expression handed
// to an IReadOnlyList<T> parameter produces a compiler-synthesized read-only list with no registered
// formatter, so a call made with `[id]` threw at serialisation while the same call with an array
// succeeded - and it threw inside the RPC layer, several frames from the caller that chose the
// collection type, so the stack did not name the cause.
//
// Not hypothetical, twice: the co-simulation bridge's first live run died on it at the first vehicle
// it tried to spawn (apply_batch), and a capture run died on it placing its first camera
// (get_actors_by_id). The encoder writes every read-only list by its element type, so no caller has
// to know.
using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Rpc.Commands;

namespace CarlaNet.Tests.Transport;

public class CollectionArgumentRpcTests
{
    // get_actors_by_id returns R<std::vector<Actor>>, whose success is [[1, value]]: a one-element
    // array holding [variant index, value]. These reproduce that envelope for a stand-in server.
    [MessagePackObject]
    public record struct SuccessVariant([property: Key(0)] int VariantIndex, [property: Key(1)] Actor[] Value);

    [MessagePackObject]
    public record struct SuccessResponse([property: Key(0)] SuccessVariant Data);

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

    /// <summary>One id as a collection expression makes it for a read-only list parameter.</summary>
    private static IReadOnlyList<ActorId> OneIdAsACollectionExpression(ActorId id) => [id];

    private static Command[] Batch() =>
    [
        new ApplyTransformCommand(11u, new Transform()),
        new ApplyTransformCommand(12u, new Transform()),
    ];

    [Fact]
    public void A_Collection_Expression_Is_Neither_An_Array_Nor_A_List_And_Has_No_Formatter_Of_Its_Own()
    {
        // The premise. If this stops holding, the hazard stops existing and so does this file.
        IReadOnlyList<ActorId> ids = OneIdAsACollectionExpression(7u);
        Assert.IsNotType<ActorId[]>(ids);
        Assert.IsNotType<List<ActorId>>(ids);
        Assert.ThrowsAny<MessagePackSerializationException>(
            () => MessagePackSerializer.Serialize(ids.GetType(), ids));
    }

    [Fact]
    public async Task Actors_Asked_For_By_A_Collection_Expression_Reach_The_Server_As_The_Ids_Given()
    {
        int port = FreeLoopbackPort();
        await using var server = new MsgPackRpcServer(IPAddress.Loopback, port);
        uint[]? received = null;
        server.RegisterHandler("get_actors_by_id", (uint[] ids) =>
        {
            received = ids;
            return new SuccessResponse(new SuccessVariant(1, []));
        });
        await server.StartAsync();
        await using var client = new CarlaClient("127.0.0.1", port, TimeSpan.FromSeconds(10));

        IReadOnlyList<Actor> actors = await client.GetActorsByIdAsync([7u]);

        Assert.Empty(actors);
        Assert.Equal([7u], received);
    }

    [Fact]
    public void Every_Shape_Of_One_Sequence_Is_Written_As_The_Same_Bytes()
    {
        // Wire-identical, so the encoder changes nothing for a caller that already passed an array
        // or a list: all three are a msgpack array of the elements.
        IReadOnlyList<ActorId> wrapped = OneIdAsACollectionExpression(7u);
        byte[] fromArray = MsgPackRpcClient.BuildRequest(1, "get_actors_by_id", [new ActorId[] { 7u }]);
        Assert.Equal(fromArray, MsgPackRpcClient.BuildRequest(1, "get_actors_by_id", [wrapped]));
        Assert.Equal(fromArray, MsgPackRpcClient.BuildRequest(1, "get_actors_by_id", [new List<ActorId> { 7u }]));

        Command[] batch = Batch();
        IReadOnlyList<Command> wrappedBatch = [batch[0], batch[1]];
        Assert.Equal(MsgPackRpcClient.BuildRequest(2, "apply_batch", [batch, false]),
                     MsgPackRpcClient.BuildRequest(2, "apply_batch", [wrappedBatch, false]));
    }

    /// <summary>What a stand-in server's handler answers: a collection expression, as a handler writes one.</summary>
    private static IReadOnlyList<double> StagingBoundsAsACollectionExpression() => [1.0, 2.0, 3.0, 4.0];

    [Fact]
    public async Task A_Stand_In_Server_Answering_With_A_Collection_Expression_Is_Answered()
    {
        // The same hazard on the other side of the wire: the server writes a handler's result by the
        // type MsgPackWireType gives it too.
        int port = FreeLoopbackPort();
        await using var server = new MsgPackRpcServer(IPAddress.Loopback, port);
        server.RegisterHandler("get_staging_bounds", StagingBoundsAsACollectionExpression);
        await server.StartAsync();
        await using var rpc = new MsgPackRpcClient("127.0.0.1", port, TimeSpan.FromSeconds(5));

        IReadOnlyList<double> answered = await rpc.CallRawAsync<IReadOnlyList<double>>("get_staging_bounds");

        Assert.Equal([1.0, 2.0, 3.0, 4.0], answered);
    }

    [Fact]
    public void A_Byte_Array_Is_Still_Written_As_A_Binary_Blob()
    {
        // The one sequence msgpack writes differently: a byte[] is bin, not an array of integers,
        // and the server's token arguments expect bin.
        byte[] token = [1, 2, 3];
        byte[] request = MsgPackRpcClient.BuildRequest(3, "is_sensor_enabled_for_ros", [token]);
        var reader = new MessagePackReader(request);
        reader.ReadArrayHeader();
        reader.ReadInt32();
        reader.ReadUInt32();
        reader.ReadString();
        reader.ReadArrayHeader();
        reader.Skip();
        Assert.Equal(MessagePackType.Binary, reader.NextMessagePackType);
    }
}
