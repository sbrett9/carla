// The drive lease's four calls, as the client sends them and reads their answers: take_drive_lease
// and release_drive_lease answer R<void>, break_drive_lease and get_drive_lease answer R<std::string>
// with an empty string for "nobody". A stand-in server answers as CarlaServer.cpp does, holding one
// name, and records what it was asked.
using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc;
using CarlaNet.Transport.MsgPackRpc.Server;

namespace CarlaNet.Tests.Transport;

public sealed class DriveLeaseRpcTests : IAsyncLifetime
{
    // R<T>'s success is [[1, value]]; R<void>'s is [[false]], an optional with no error in it.
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    [MessagePackObject]
    public record struct VoidSuccess([property: Key(0)] bool HasError);

    [MessagePackObject]
    public record struct VoidResponse([property: Key(0)] VoidSuccess Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    private static VoidResponse Done() => new(new VoidSuccess(false));

    private readonly List<string> _asked = [];
    private string _holder = string.Empty;
    private MsgPackRpcServer? _server;
    private CarlaClient? _client;

    public async Task InitializeAsync()
    {
        int port = FreeLoopbackPort();
        _server = new MsgPackRpcServer(IPAddress.Loopback, port);
        _server.RegisterHandler<string, VoidResponse>("take_drive_lease", holder =>
        {
            _asked.Add("take " + holder);
            if (_holder.Length > 0)
            {
                throw new InvalidOperationException(
                    $"take_drive_lease: refused; {_holder} holds the drive lease on this world since frame 3");
            }

            _holder = holder;
            return Done();
        });
        _server.RegisterHandler<string, VoidResponse>("release_drive_lease", holder =>
        {
            _asked.Add("release " + holder);
            if (_holder != holder)
            {
                throw new InvalidOperationException(
                    $"release_drive_lease: refused; the drive lease is held by {_holder}, not by {holder}");
            }

            _holder = string.Empty;
            return Done();
        });
        _server.RegisterHandler("break_drive_lease", () =>
        {
            _asked.Add("break");
            string broken = _holder;
            _holder = string.Empty;
            return Ok(broken);
        });
        _server.RegisterHandler("get_drive_lease", () =>
        {
            _asked.Add("get");
            return Ok(_holder);
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
    public async Task A_Lease_Is_Taken_Under_Its_Name_Read_Back_And_Released_Under_The_Same_Name()
    {
        Assert.Null(await _client!.GetDriveLeaseHolderAsync());

        await _client.TakeDriveLeaseAsync("a drive (process 41 on HOST)");

        Assert.Equal("a drive (process 41 on HOST)", await _client.GetDriveLeaseHolderAsync());
        await _client.ReleaseDriveLeaseAsync("a drive (process 41 on HOST)");
        Assert.Null(await _client.GetDriveLeaseHolderAsync());
        Assert.Equal(["get", "take a drive (process 41 on HOST)", "get", "release a drive (process 41 on HOST)", "get"],
                     _asked);
    }

    [Fact]
    public async Task A_Second_Claim_Is_Refused_In_The_Servers_Words_Naming_The_Holder()
    {
        await _client!.TakeDriveLeaseAsync("the first drive");

        CarlaRpcException refused = await Assert.ThrowsAsync<CarlaRpcException>(
            () => _client.TakeDriveLeaseAsync("the second drive"));

        Assert.Contains("the first drive holds the drive lease", refused.Message);
        Assert.False(refused.NamesNoSuchFunction);
        Assert.Equal("the first drive", await _client.GetDriveLeaseHolderAsync());
    }

    [Fact]
    public async Task A_Release_Under_Another_Name_Is_Refused_And_The_Lease_Kept()
    {
        await _client!.TakeDriveLeaseAsync("the drive");

        CarlaRpcException refused = await Assert.ThrowsAsync<CarlaRpcException>(
            () => _client.ReleaseDriveLeaseAsync("somebody else"));

        Assert.Contains("held by the drive, not by somebody else", refused.Message);
        Assert.Equal("the drive", await _client.GetDriveLeaseHolderAsync());
    }

    [Fact]
    public async Task Breaking_Answers_The_Holder_Whose_Lease_Was_Ended_And_Null_Where_None_Was_Held()
    {
        Assert.Null(await _client!.BreakDriveLeaseAsync());

        await _client.TakeDriveLeaseAsync("a drive that died");

        Assert.Equal("a drive that died", await _client.BreakDriveLeaseAsync());
        Assert.Null(await _client.GetDriveLeaseHolderAsync());
    }

    [Fact]
    public async Task A_Server_Without_The_Lease_Answers_That_It_Has_No_Such_Function()
    {
        // A server built before it carried the lease binds none of the four calls.
        int port = FreeLoopbackPort();
        await using var older = new MsgPackRpcServer(IPAddress.Loopback, port);
        await older.StartAsync();
        await using var client = new CarlaClient("127.0.0.1", port, TimeSpan.FromSeconds(10));

        CarlaRpcException refused = await Assert.ThrowsAsync<CarlaRpcException>(
            () => client.TakeDriveLeaseAsync("a drive"));

        Assert.True(refused.NamesNoSuchFunction);
        Assert.Contains("take_drive_lease", refused.Message);
    }

    [Fact]
    public void The_Words_Of_Both_Servers_For_A_Missing_Function_Are_Recognised_And_A_Refusal_Is_Not()
    {
        // rpclib's wording, which the CARLA server answers with (rpclib dispatcher.cc), and this
        // server's, which stands in for it in tests.
        Assert.True(new CarlaRpcException(
            "rpclib: server could not find function 'take_drive_lease' with argument count 2.").NamesNoSuchFunction);
        Assert.True(new CarlaRpcException("unknown method 'take_drive_lease'").NamesNoSuchFunction);
        Assert.False(new CarlaRpcException(
            "take_drive_lease: refused; a drive holds the drive lease on this world since frame 3").NamesNoSuchFunction);
    }

    [Fact]
    public async Task A_Blank_Holder_Is_Refused_Before_It_Is_Sent()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _client!.TakeDriveLeaseAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => _client!.ReleaseDriveLeaseAsync(string.Empty));
        Assert.Empty(_asked);
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
