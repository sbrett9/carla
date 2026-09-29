// A reply holding millions of floats must come back in time proportional to its size.
//
// get_bare_earth_offset_grid and get_bare_earth_dtm_grid return one float per drape cell, which on
// the Bahonar world is 7,611,381 cells: a 38 MB reply. The client used to frame incoming messages
// with MessagePackStreamReader, which re-scans the whole buffered message from its first byte each
// time more bytes arrive, so framing cost grew with the square of the reply. That reply took 126 s
// against a stand-in server that sent it in 0.14 s, and each grid took ~150 s from the live server.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using Xunit.Abstractions;

namespace CarlaNet.Tests.Transport;

public class LargeReplyRpcTests(ITestOutputHelper output)
{
    /// Cells in the Bahonar world's drape grid.
    private const int BahonarCells = 7_611_381;

    // The server's return type is R<std::vector<float>>: a success is [[1, [floats]]].
    [MessagePackObject]
    public record struct GridVariant(
        [property: Key(0)] int VariantIndex,
        [property: Key(1)] float[] Value);

    [MessagePackObject]
    public record struct GridResponse([property: Key(0)] GridVariant Data);

    // R<std::vector<double>>, for get_view_readiness.
    [MessagePackObject]
    public record struct DoublesVariant(
        [property: Key(0)] int VariantIndex,
        [property: Key(1)] double[] Value);

    [MessagePackObject]
    public record struct DoublesResponse([property: Key(0)] DoublesVariant Data);

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
    public async Task A_Bahonar_Sized_Grid_Returns_In_Seconds()
    {
        var grid = new float[BahonarCells];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = 1600f + (i % 100_003) * 0.01f;

        int port = FreeLoopbackPort();
        await using var server = new MsgPackRpcServer(IPAddress.Loopback, port);
        server.RegisterHandler("get_bare_earth_dtm_grid", () => new GridResponse(new GridVariant(1, grid)));
        await server.StartAsync();

        // The call timeout is well above the bound asserted below, so a regression fails on the
        // bound, which names the time, rather than on a timeout.
        await using var client = new CarlaClient("127.0.0.1", port, TimeSpan.FromMinutes(5));

        var clock = Stopwatch.StartNew();
        IReadOnlyList<float> received = await client.GetBareEarthDtmGridAsync();
        clock.Stop();
        output.WriteLine($"{BahonarCells:N0} floats returned after {clock.Elapsed.TotalSeconds:F2} s");

        Assert.Equal(grid.Length, received.Count);
        for (int i = 0; i < grid.Length; i += 9_973)
            Assert.Equal(grid[i], received[i]);
        Assert.Equal(grid[^1], received[^1]);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30),
            $"the reply took {clock.Elapsed.TotalSeconds:F1} s to frame and decode");
    }

    [Fact]
    public async Task Concurrent_Calls_Each_Get_Their_Own_Reply_Around_A_Large_One()
    {
        // The server answers each request on its own task, so replies come back out of order and
        // back to back, small ones on either side of a grid too large to be copied out of the
        // receive buffer. Each must reach the call that asked for it, intact.
        var grid = new float[1_000_000];
        for (int i = 0; i < grid.Length; i++) grid[i] = i * 0.5f;

        int port = FreeLoopbackPort();
        await using var server = new MsgPackRpcServer(IPAddress.Loopback, port);
        server.RegisterHandler("get_bare_earth_dtm_grid", () => new GridResponse(new GridVariant(1, grid)));
        server.RegisterHandler<uint, DoublesResponse>("get_view_readiness",
            id => new DoublesResponse(new DoublesVariant(1, [id, id * 2.0, -id])));
        await server.StartAsync();

        await using var client = new CarlaClient("127.0.0.1", port, TimeSpan.FromSeconds(30));

        var small = Enumerable.Range(1, 200).Select(i => client.GetViewReadinessAsync((uint)i)).ToList();
        var large = client.GetBareEarthDtmGridAsync();
        small.AddRange(Enumerable.Range(201, 200).Select(i => client.GetViewReadinessAsync((uint)i)));

        IReadOnlyList<double>[] answers = await Task.WhenAll(small);
        IReadOnlyList<float> received = await large;

        for (int i = 0; i < answers.Length; i++)
            Assert.Equal(new double[] { i + 1, (i + 1) * 2.0, -(i + 1) }, answers[i]);
        Assert.Equal(grid, received);
    }
}
