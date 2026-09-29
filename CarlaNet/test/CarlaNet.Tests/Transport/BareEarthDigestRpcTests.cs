// get_bare_earth_digest, and the client taking a world's bare-earth grids from a world package once
// the server's digests show they are its record's.
//
// The grids are the size of the drape -- 7,611,381 floats each on the Bahonar world, whose two
// fetches took 146 s and 153 s -- so a client that holds the world's package proves the package's
// grids are the record's by digest and keeps its own copy, and fetches the record's only where it
// cannot. A stand-in server answers as CarlaServer.cpp does, and records whether a grid was ever
// asked for.
using System.Net;
using System.Net.Sockets;
using CarlaNet.Map.WorldPackage;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;

namespace CarlaNet.Tests.Transport;

public sealed class BareEarthDigestRpcTests : IDisposable
{
    // Every call here returns R<T>, whose success is [[1, value]]: a one-element array holding
    // [variant index, value]. These reproduce that envelope for the stand-in server.
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    private const int Columns = 21;
    private const int Rows = 13;
    private const double MinX = -838.9;
    private const double MinY = -455.09;
    private const double Cell = 8.0;

    private readonly string _dir;
    private readonly float[] _offset;
    private readonly float[] _ground;
    private readonly string _package;

    public BareEarthDigestRpcTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "carlanet-bare-earth-digest-" + Guid.NewGuid().ToString("N"));
        _offset = new float[Columns * Rows];
        _ground = new float[Columns * Rows];
        for (int i = 0; i < _offset.Length; i++)
        {
            _offset[i] = (float)(-2.01 + (i % 37) * 0.013);
            _ground[i] = (float)(1400.0 + (i % 91) * 0.7);
        }

        var manifest = new WorldPackageManifest
        {
            MapName = "DigestArea",
            OriginLatitude = 38.91108,
            OriginLongitude = -119.7645965,
            OriginHeightMeters = 1418.61,
            HeightAlignMode = "drape",
            DrapeActive = true,
            HeightAlignOffsetMeters = 0.0,
            GridMinXMeters = MinX,
            GridMinYMeters = MinY,
            GridCellSizeMeters = Cell,
            GridNumCols = Columns,
            GridNumRows = Rows,
        };
        WorldPackage.Write(_dir, manifest, "<OpenDRIVE/>", string.Empty, _offset, _ground);
        _package = WorldPackage.PackagePath(_dir, manifest.MapName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// The record a server holding the package's world publishes: draped, on the package's grid.
    private static double[] DrapedRecord() => [0.0, 1.0, MinX, MinY, Cell, Columns, Rows];

    /// A stand-in server and what it was asked for.
    private sealed class Server
    {
        public required MsgPackRpcServer Rpc { get; init; }
        public required int Port { get; init; }
        public int GridFetches;
        public int DigestRequests;
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

    /// <summary>
    /// A server holding <paramref name="record"/> and these grids, answering the digests given, or
    /// binding no digest call at all where they are null -- a server built before it published them.
    /// </summary>
    private static async Task<Server> StartServer(double[] record, float[] offset, float[] ground,
                                                  string[]? digests)
    {
        int port = FreeLoopbackPort();
        var server = new Server { Rpc = new MsgPackRpcServer(IPAddress.Loopback, port), Port = port };
        server.Rpc.RegisterHandler("get_bare_earth_reference", () => Ok(record));
        server.Rpc.RegisterHandler("get_bare_earth_offset_grid", () =>
        {
            Interlocked.Increment(ref server.GridFetches);
            return Ok(offset);
        });
        server.Rpc.RegisterHandler("get_bare_earth_dtm_grid", () =>
        {
            Interlocked.Increment(ref server.GridFetches);
            return Ok(ground);
        });
        if (digests is not null)
        {
            server.Rpc.RegisterHandler("get_bare_earth_digest", () =>
            {
                Interlocked.Increment(ref server.DigestRequests);
                return Ok(digests);
            });
        }
        await server.Rpc.StartAsync();
        return server;
    }

    private static byte[] Bytes(float[] grid)
    {
        var bytes = new byte[grid.Length * sizeof(float)];
        Buffer.BlockCopy(grid, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public async Task The_Client_Calls_The_Name_The_Server_Binds_And_Reads_Both_Digests()
    {
        string[] digests = [WorldPackage.HashGrid(_offset), WorldPackage.HashGrid(_ground)];
        Server server = await StartServer(DrapedRecord(), _offset, _ground, digests);
        await using MsgPackRpcServer rpc = server.Rpc;
        await using var client = new CarlaClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));

        IReadOnlyList<string> answered = await client.GetBareEarthDigestAsync();

        Assert.Equal(digests, answered);
        Assert.Equal(1, server.DigestRequests);
    }

    [Fact]
    public async Task A_Package_Whose_Grids_The_Server_Digests_As_Its_Own_Is_Taken_Without_Fetching_Them()
    {
        Server server = await StartServer(DrapedRecord(), _offset, _ground,
                                          [WorldPackage.HashGrid(_offset), WorldPackage.HashGrid(_ground)]);
        await using MsgPackRpcServer rpc = server.Rpc;
        await using var client = new CarlaClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));

        Assert.True(client.AdoptBareEarthReference(_package));

        Assert.True(client.HasBareEarthReference);
        Assert.True(client.LastDrapeActive);
        Assert.Equal(0.0, client.LastHeightAlignOffset);
        Assert.Equal((MinX, MinY, Cell, Columns, Rows),
                     (client.LastDrapeMinX, client.LastDrapeMinY, client.LastDrapeCellSize,
                      client.LastDrapeNumCols, client.LastDrapeNumRows));
        Assert.Equal(Bytes(_offset), client.LastDrapedOffsetBytes);
        Assert.Equal(Bytes(_ground), client.LastDrapedDtmBytes);

        // And the truth telemetry asking for the reference afterwards gets it without a grid fetch.
        Assert.True(client.EnsureBareEarthReference());
        Assert.Equal(0, server.GridFetches);
    }

    [Fact]
    public async Task A_Package_The_Server_Digests_Otherwise_Is_Not_Taken_And_The_Grids_Are_Fetched()
    {
        // The server holds the package's ground with the lowest bit of one cell changed.
        float[] served = [.. _ground];
        served[100] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(served[100]) ^ 1);
        Server server = await StartServer(DrapedRecord(), _offset, served,
                                          [WorldPackage.HashGrid(_offset), WorldPackage.HashGrid(served)]);
        await using MsgPackRpcServer rpc = server.Rpc;
        await using var client = new CarlaClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));

        Assert.False(client.AdoptBareEarthReference(_package));
        Assert.False(client.HasBareEarthReference);
        Assert.Empty(client.LastDrapedDtmBytes);
        Assert.Equal(0, server.GridFetches);

        // Nothing was taken, so the telemetry fetches the record's own grids, as it always has.
        Assert.True(client.EnsureBareEarthReference());
        Assert.Equal(2, server.GridFetches);
        Assert.Equal(Bytes(served), client.LastDrapedDtmBytes);
    }

    [Fact]
    public async Task A_Server_That_Publishes_No_Digest_Leaves_The_Fetch_As_It_Was()
    {
        Server server = await StartServer(DrapedRecord(), _offset, _ground, digests: null);
        await using MsgPackRpcServer rpc = server.Rpc;
        await using var client = new CarlaClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));

        Assert.False(client.AdoptBareEarthReference(_package));
        Assert.False(client.HasBareEarthReference);

        Assert.True(client.EnsureBareEarthReference());
        Assert.Equal(2, server.GridFetches);
    }

    [Fact]
    public async Task A_Record_On_Another_Grid_Is_Not_Taken_Even_Where_The_Digests_Match()
    {
        // The same cells, described as a grid of other dimensions: the geometry the telemetry would
        // index them by is the server's, and it does not fit the package's grids.
        double[] transposed = [0.0, 1.0, MinX, MinY, Cell, Rows + 1, Columns];
        Server server = await StartServer(transposed, _offset, _ground,
                                          [WorldPackage.HashGrid(_offset), WorldPackage.HashGrid(_ground)]);
        await using MsgPackRpcServer rpc = server.Rpc;
        await using var client = new CarlaClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));

        Assert.False(client.AdoptBareEarthReference(_package));
        Assert.False(client.HasBareEarthReference);
    }

    [Fact]
    public async Task A_Surface_Shifted_By_A_Constant_Is_Taken_From_The_Record_Alone()
    {
        Server server = await StartServer([-2.01, 0.0, 0.0, 0.0, 0.0, 0, 0], [], [], digests: []);
        await using MsgPackRpcServer rpc = server.Rpc;
        await using var client = new CarlaClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));

        Assert.True(client.AdoptBareEarthReference(_package));

        Assert.True(client.HasBareEarthReference);
        Assert.False(client.LastDrapeActive);
        Assert.Equal(-2.01, client.LastHeightAlignOffset);
        Assert.Equal(0, server.DigestRequests);
        Assert.Equal(0, server.GridFetches);
    }

    [Fact]
    public async Task A_World_With_No_Record_Takes_Nothing()
    {
        Server server = await StartServer([], [], [], digests: []);
        await using MsgPackRpcServer rpc = server.Rpc;
        await using var client = new CarlaClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));

        Assert.False(client.AdoptBareEarthReference(_package));
        Assert.False(client.HasBareEarthReference);
        Assert.Equal(0, server.DigestRequests);
    }
}
