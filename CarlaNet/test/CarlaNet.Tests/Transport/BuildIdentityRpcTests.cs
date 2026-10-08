// get_build_identity as the client sends it and reads its answer: R<std::map<std::string, std::string>>,
// a map of names to values each "unknown" where the server cannot know it. A stand-in server answers as
// CarlaServer.cpp does; one with no such call stands in for a server built before it, and the client then
// records the identity as not available, with what the older calls still say, and never fails.
using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Provenance;

namespace CarlaNet.Tests.Transport;

public sealed class BuildIdentityRpcTests
{
    // R<T>'s success is [[1, value]].
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    private static Dictionary<string, string> PackageAnswer() => new()
    {
        ["identity_version"] = "1",
        ["release"] = "0.10.0",
        ["world_interface"] = "1.0",
        ["build"] = "package",
        ["configuration"] = "Shipping",
        ["carla_commit"] = "025443a83eaf1bb82f18795d608fca50eb77a452",
        ["content_commit"] = "6bcd042a91a54d9a2f2f002869fbf1c75f3768f4",
        ["engine_commit"] = "e5e266de195a2400a6a74180402fb1a3e8f75472",
        ["commits_from"] = "version_file",
    };

    [Fact]
    public async Task A_Server_With_The_Call_Says_What_It_Was_Built_From()
    {
        int asked = 0;
        await using var server = await Serve(answer =>
        {
            answer.RegisterHandler("get_build_identity", () =>
            {
                asked++;
                return Ok(PackageAnswer());
            });
        });
        await using CarlaClient client = Connect(server);

        ServerBuildIdentity identity = await client.GetBuildIdentityAsync();

        Assert.True(identity.Available);
        Assert.Equal("0.10.0", identity.Release);
        Assert.Equal("1.0", identity.WorldInterface);
        Assert.Equal("package", identity.Build);
        Assert.Equal("Shipping", identity.Configuration);
        Assert.Equal("025443a83eaf1bb82f18795d608fca50eb77a452", identity.CarlaCommit);
        Assert.Equal("6bcd042a91a54d9a2f2f002869fbf1c75f3768f4", identity.ContentCommit);
        Assert.Equal("e5e266de195a2400a6a74180402fb1a3e8f75472", identity.EngineCommit);
        Assert.Equal("version_file", identity.CommitsFrom);
        Assert.Null(identity.Reason);

        // Asked once per connection: a server's build does not change while it runs.
        Assert.Same(identity, await client.GetBuildIdentityAsync());
        Assert.Same(identity, client.GetBuildIdentity());
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task An_Editor_Run_Says_Unknown_For_What_It_Cannot_Know()
    {
        await using var server = await Serve(answer => answer.RegisterHandler("get_build_identity", () =>
        {
            Dictionary<string, string> editor = PackageAnswer();
            editor["build"] = "editor";
            editor["content_commit"] = "unknown";
            editor["engine_commit"] = "unknown";
            editor["commits_from"] = "compiled";
            editor.Remove("configuration");
            return Ok(editor);
        }));
        await using CarlaClient client = Connect(server);

        ServerBuildIdentity identity = await client.GetBuildIdentityAsync();

        Assert.True(identity.Available);
        Assert.Equal("editor", identity.Build);
        Assert.Equal("compiled", identity.CommitsFrom);
        Assert.Equal("unknown", identity.ContentCommit);
        // A name a server leaves out is unknown, never guessed.
        Assert.Equal("unknown", identity.Configuration);
    }

    [Fact]
    public async Task A_Server_Built_Before_The_Call_Is_Recorded_As_Not_Saying_With_What_Its_Older_Calls_Say()
    {
        await using var server = await Serve(older =>
        {
            older.RegisterHandler("version", () => Ok("0.10.0"));
            older.RegisterHandler("get_world_interface_version", () => Ok("1.0"));
        });
        await using CarlaClient client = Connect(server);

        ServerBuildIdentity identity = await client.GetBuildIdentityAsync();

        Assert.False(identity.Available);
        Assert.Equal("0.10.0", identity.Release);
        Assert.Equal("1.0", identity.WorldInterface);
        Assert.Equal("unknown", identity.CarlaCommit);
        Assert.Contains("built before the call", identity.Reason);
        Assert.Equal("""{"available":false,"release":"0.10.0","world_interface":"1.0","reason":"the server answers no get_build_identity: it was built before the call"}""",
                     identity.ToJson());
    }

    [Fact]
    public async Task A_Server_With_None_Of_The_Calls_Leaves_Every_Value_Unknown_And_Never_Throws()
    {
        await using var server = await Serve(_ => { });
        await using CarlaClient client = Connect(server);

        ServerBuildIdentity identity = await client.GetBuildIdentityAsync();

        Assert.False(identity.Available);
        Assert.Equal("unknown", identity.Release);
        Assert.Equal("unknown", identity.WorldInterface);
    }

    [Fact]
    public async Task A_Call_That_Fails_Is_Recorded_As_Not_Saying_Never_Thrown_And_Asked_Again_Next_Time()
    {
        int asked = 0;
        await using var server = await Serve(answer => answer.RegisterHandler<SuccessResponse<Dictionary<string, string>>>(
            "get_build_identity", () =>
            {
                asked++;
                throw new InvalidOperationException("the identity could not be read");
            }));
        await using CarlaClient client = Connect(server);

        ServerBuildIdentity identity = await client.GetBuildIdentityAsync();

        Assert.False(identity.Available);
        Assert.StartsWith("get_build_identity failed", identity.Reason);
        Assert.Contains("the identity could not be read", identity.Reason);
        // Not kept: a call that failed this time may answer the next.
        Assert.NotSame(identity, await client.GetBuildIdentityAsync());
        Assert.Equal(2, asked);
    }

    private static async Task<MsgPackRpcServer> Serve(Action<MsgPackRpcServer> register)
    {
        var server = new MsgPackRpcServer(IPAddress.Loopback, FreeLoopbackPort());
        register(server);
        await server.StartAsync();
        return server;
    }

    private static CarlaClient Connect(MsgPackRpcServer server) =>
        new("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
