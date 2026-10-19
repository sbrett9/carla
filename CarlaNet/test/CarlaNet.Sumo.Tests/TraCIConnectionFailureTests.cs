using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// A TraCI server that goes silent is told apart from one that goes away: a bounded wait gives up on
/// the first, names the bound, and says the stream can no longer be trusted.
/// </summary>
/// <remarks>
/// The servers here are sockets that accept and then say nothing, or accept and hang up, because a
/// real SUMO does neither on request. What they establish is the client's side of it; that a hung SUMO
/// really does look like this is established against a suspended <c>sumo</c> in the co-simulation
/// session's tests.
/// </remarks>
public sealed class TraCIConnectionFailureTests
{
    [Fact]
    public void AServerThatNeverAnswersIsGivenUpOnAtTheBoundAndSaysSo()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TraCIConnection connection = TraCIConnection.Connect(
            "127.0.0.1", port, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
        using Socket accepted = listener.AcceptSocket();

        var clock = Stopwatch.StartNew();
        FatalTraCIError failure = Assert.Throws<FatalTraCIError>(() => connection.GetVersion());
        clock.Stop();

        Assert.Contains("SUMO did not answer within 0.2 s", failure.Message, StringComparison.Ordinal);
        Assert.True(connection.IsClosed);
        Assert.True(connection.StoppedAnswering);
        Assert.Equal(TimeSpan.FromMilliseconds(200), connection.ReceiveTimeout);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.15, 5.0);

        // Nothing further is read from a stream an answer might still arrive on.
        Assert.Throws<ObjectDisposedException>(() => connection.GetVersion());
    }

    [Fact]
    public void AServerThatHangsUpHasGoneRatherThanStoppedAnswering()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TraCIConnection connection = TraCIConnection.Connect(
            "127.0.0.1", port, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        listener.AcceptSocket().Close();

        FatalTraCIError failure = Assert.Throws<FatalTraCIError>(() => connection.GetVersion());

        Assert.DoesNotContain("did not answer", failure.Message, StringComparison.Ordinal);
        Assert.True(connection.IsClosed);
        Assert.False(connection.StoppedAnswering);
    }

    [Fact]
    public void WithNoBoundTheWaitIsUnbounded()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TraCIConnection connection = TraCIConnection.Connect("127.0.0.1", port, TimeSpan.FromSeconds(5));
        using Socket accepted = listener.AcceptSocket();

        Assert.Null(connection.ReceiveTimeout);
        accepted.Close();
    }

    [Fact]
    public void ClosingAConnectionWhoseServerNeverAnswersGivesUpAtTheCloseBound()
    {
        // No bound on answers at all: the close still does not wait on a server that is not there.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TraCIConnection connection = TraCIConnection.Connect("127.0.0.1", port, TimeSpan.FromSeconds(5));
        using Socket accepted = listener.AcceptSocket();

        var clock = Stopwatch.StartNew();
        Task closing = Task.Run(connection.Dispose);
        bool closed = SpinWait.SpinUntil(() => closing.IsCompleted, TimeSpan.FromSeconds(30));
        clock.Stop();

        Assert.True(closed, "the close was still waiting on the silent server after 30 s");
        Assert.True(connection.IsClosed);
        Assert.InRange(clock.Elapsed.TotalSeconds, TraCIConnection.CloseAnswerBound.TotalSeconds - 0.5,
                       TraCIConnection.CloseAnswerBound.TotalSeconds + 5.0);
    }

    [Fact]
    public void ClosingABoundedConnectionWaitsNoLongerThanItsBound()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TraCIConnection connection = TraCIConnection.Connect(
            "127.0.0.1", port, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
        using Socket accepted = listener.AcceptSocket();

        var clock = Stopwatch.StartNew();
        connection.Dispose();
        clock.Stop();

        Assert.True(connection.IsClosed);
        Assert.True(connection.StoppedAnswering);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.15, 3.0);
    }
}
