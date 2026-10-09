// The server issues camera names and refuses duplicates, and a client reads a camera's name back from
// the spawned actor's attributes. A stand-in server here answers spawn_actor as CarlaServer.cpp does
// (FPimpl::SettleCameraName): a camera spawned with no role_name, or with its blueprint's default, is
// Camera_<n> from a counter the server keeps for its lifetime; a client-given name a live camera holds
// is refused, and so is one of the server's form. A second stand-in answers as a server built before it
// named cameras, which hands the description back as it was sent. The client side under test is
// CameraName.Of and NamedByServer, which every front end names its files and callsign by.
using System.Net;
using System.Net.Sockets;
using CarlaNet.Recording;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Rpc.Enums;

namespace CarlaNet.Tests.Recording;

public sealed class SpawnedCameraNameTests : IAsyncLifetime
{
    // spawn_actor returns R<Actor>: success is [[1, actor]], a refusal [[0, [message]]].
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    [MessagePackObject]
    public record struct ErrorVariant([property: Key(0)] int VariantIndex, [property: Key(1)] string[] What);

    [MessagePackObject]
    public record struct ErrorResponse([property: Key(0)] ErrorVariant Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));
    private static ErrorResponse Refused(string message) => new(new ErrorVariant(0, [message]));

    private const uint RgbBlueprint = 17;
    private const string Rgb = "sensor.camera.rgb";
    private const string BlueprintDefaultRole = "front";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly Transform Anywhere = new(new Location(0f, 0f, 100f), new Rotation(-90f, 0f, 0f));

    /// A stand-in CARLA server's spawn_actor, naming cameras as the real one does.
    private sealed class NamingServer
    {
        private readonly Dictionary<ActorId, ActorDescription> _live = [];
        private ActorId _nextActor = 4120;
        private ulong _camerasNamed;

        public object Spawn(ActorDescription description, Transform _)
        {
            if (description.Id.StartsWith("sensor.camera.", StringComparison.Ordinal))
            {
                string given = description.Attributes.FirstOrDefault(a => a.Id == "role_name").Value ?? string.Empty;
                if (given.Length == 0 || given == BlueprintDefaultRole)
                {
                    description = WithRole(description, "Camera_" + (++_camerasNamed));
                }
                else if (CameraName.IsServerIssued(given))
                {
                    return Refused($"camera name '{given}' has the form the server gives every camera spawned "
                                   + "without one, Camera_<n>, which a client cannot claim; choose another, such as Overwatch_1");
                }
                else
                {
                    foreach ((ActorId id, ActorDescription held) in _live)
                    {
                        string? name = held.Attributes.FirstOrDefault(a => a.Id == "role_name").Value;
                        if (held.Id.StartsWith("sensor.camera.", StringComparison.Ordinal) && CameraName.Same(name, given))
                        {
                            return Refused($"camera name '{given}' is already held in this world by camera {id} ({held.Id}): "
                                           + "two cameras under one name would write files of one name and report under "
                                           + "one callsign; choose another");
                        }
                    }
                }
            }

            ActorId actor = ++_nextActor;
            _live[actor] = description;
            return Ok(new Actor(actor, 0u, description, default, [], new byte[24]));
        }
    }

    /// A server built before it named cameras: the description comes back as it was sent.
    private static object SpawnWithoutNaming(ActorDescription description, Transform _) =>
        Ok(new Actor(9000u, 0u, description, default, [], new byte[24]));

    private MsgPackRpcServer? _rpc;
    private CarlaClient? _client;

    public async Task InitializeAsync()
    {
        int port = FreeLoopbackPort();
        _rpc = new MsgPackRpcServer(IPAddress.Loopback, port);
        var server = new NamingServer();
        _rpc.RegisterHandler<ActorDescription, Transform, object>("spawn_actor", server.Spawn);
        await _rpc.StartAsync();
        _client = new CarlaClient("127.0.0.1", port, Patience);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_rpc is not null) await _rpc.DisposeAsync();
    }

    [Fact]
    public async Task A_Camera_Spawned_With_No_Name_Is_Named_By_The_Server_And_The_Client_Reads_The_Name_Back()
    {
        Actor first = await _client!.SpawnActorAsync(CameraBlueprint(BlueprintDefaultRole), Anywhere);
        Actor second = await _client.SpawnActorAsync(CameraBlueprint(roleName: null), Anywhere);

        Assert.Equal("Camera_1", CameraName.RoleNameOf(first));
        Assert.True(CameraName.NamedByServer(first));
        Assert.Equal("Camera_1", CameraName.Of(first));
        Assert.Equal("Camera_2", CameraName.Of(second));
        // The default form is the platform track's uid, not the name, on a server that names cameras.
        Assert.NotEqual(CameraName.Default(first.Id), CameraName.Of(first));
    }

    [Fact]
    public async Task A_Name_The_Client_Gives_Is_Kept_As_Given()
    {
        Actor named = await _client!.SpawnActorAsync(CameraBlueprint("DECK-I25"), Anywhere);

        Assert.Equal("DECK-I25", CameraName.Of(named));
        Assert.False(CameraName.NamedByServer(named));
    }

    [Fact]
    public async Task A_Name_A_Live_Camera_Holds_Is_Refused_By_The_Server_In_Any_Case()
    {
        Actor holder = await _client!.SpawnActorAsync(CameraBlueprint("Overwatch_1"), Anywhere);

        var refused = await Assert.ThrowsAsync<CarlaRpcException>(
            () => _client.SpawnActorAsync(CameraBlueprint("overwatch_1"), Anywhere));

        Assert.Contains($"already held in this world by camera {holder.Id} ({Rgb})", refused.Message);
        // A vehicle's role_name is not a camera's name: the same word spawns a vehicle.
        Actor vehicle = await _client.SpawnActorAsync(
            new ActorDescription(3, "vehicle.audi.tt", [Role("Overwatch_1")]), Anywhere);
        Assert.Equal("Overwatch_1", CameraName.RoleNameOf(vehicle));
    }

    [Fact]
    public async Task A_Client_Cannot_Claim_A_Name_Of_The_Server_s_Form()
    {
        // Refused before any round trip by the rule every front end applies to a chosen name...
        Assert.Contains("which a client cannot claim", CameraName.Problem("Camera_9"));

        // ...and by the server, for a client that skipped the rule.
        var refused = await Assert.ThrowsAsync<CarlaRpcException>(
            () => _client!.SpawnActorAsync(CameraBlueprint("Camera_9"), Anywhere));

        Assert.Contains("which a client cannot claim", refused.Message);
    }

    [Fact]
    public async Task A_Server_Built_Before_It_Named_Cameras_Leaves_An_Unnamed_Camera_Its_Default()
    {
        int port = FreeLoopbackPort();
        await using var older = new MsgPackRpcServer(IPAddress.Loopback, port);
        older.RegisterHandler<ActorDescription, Transform, object>("spawn_actor", SpawnWithoutNaming);
        await older.StartAsync();
        await using var client = new CarlaClient("127.0.0.1", port, Patience);

        Actor unnamed = await client.SpawnActorAsync(CameraBlueprint(BlueprintDefaultRole), Anywhere);
        Actor named = await client.SpawnActorAsync(CameraBlueprint("DECK-I25"), Anywhere);

        // The blueprint's role name came back: the server named nothing, and the client says so.
        Assert.Equal(BlueprintDefaultRole, CameraName.RoleNameOf(unnamed));
        Assert.False(CameraName.NamedByServer(unnamed));
        Assert.Equal("CARLA-SENSOR-9000", CameraName.Of(unnamed));
        // A name the client gave is held by such a server as by any other.
        Assert.Equal("DECK-I25", CameraName.Of(named));
    }

    private static ActorDescription CameraBlueprint(string? roleName) => new(
        RgbBlueprint, Rgb,
        roleName is null
            ? [new ActorAttributeValue("image_size_x", ActorAttributeType.Int, "640")]
            : [new ActorAttributeValue("image_size_x", ActorAttributeType.Int, "640"), Role(roleName)]);

    private static ActorAttributeValue Role(string roleName) =>
        new(CameraName.RoleNameAttribute, ActorAttributeType.String, roleName);

    private static ActorDescription WithRole(ActorDescription description, string roleName)
    {
        var attributes = new List<ActorAttributeValue>(description.Attributes.Count + 1);
        bool named = false;
        foreach (ActorAttributeValue attribute in description.Attributes)
        {
            if (attribute.Id == CameraName.RoleNameAttribute)
            {
                attributes.Add(attribute with { Value = roleName });
                named = true;
            }
            else
            {
                attributes.Add(attribute);
            }
        }

        if (!named)
        {
            attributes.Add(Role(roleName));
        }

        return description with { Attributes = attributes };
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
