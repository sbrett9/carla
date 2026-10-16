// The orbit mover's four calls, as the client sends them and reads their answers: set_orbit,
// set_orbit_enabled and set_orbit_paused answer R<void>, get_orbit_state answers R<OrbitState>. A
// stand-in server answers as CarlaServer.cpp does -- one mover per actor, configured, enabled, paused,
// refused where there is none -- and advances each enabled mover by a tick on request, by the rule the
// plugin flies (UOrbitMoverComponent), so what the client predicts can be held against it.
using System.Net;
using System.Net.Sockets;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Rpc.Orbit;

namespace CarlaNet.Tests.Transport;

public sealed class OrbitRpcTests : IAsyncLifetime
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

    private const double Delta = 0.05;

    /// The mover the stand-in server puts on an actor: the circle, the angle, enabled, paused.
    private sealed class Mover
    {
        public OrbitParameters Circle;
        public double Angle;
        public bool Enabled;
        public bool Paused;
        public Transform Pose;

        public void Place() => Pose = Circle.PoseAt(Angle);
    }

    private readonly List<string> _asked = [];
    private readonly Dictionary<uint, Mover> _movers = [];
    private readonly HashSet<uint> _actors = [7u, 8u];
    private MsgPackRpcServer? _server;
    private CarlaClient? _client;

    public async Task InitializeAsync()
    {
        int port = FreeLoopbackPort();
        _server = new MsgPackRpcServer(IPAddress.Loopback, port);
        _server.RegisterHandler<uint, OrbitParameters, VoidResponse>("set_orbit", (actor, circle) =>
        {
            _asked.Add($"set_orbit {actor}");
            if (!_actors.Contains(actor))
            {
                throw new InvalidOperationException(
                    $"Responding error from function set_orbit: Actor could not be found in the registry. Actor Id: {actor}");
            }

            if (circle.RadiusMetres <= 0.0)
            {
                throw new InvalidOperationException("set_orbit: the radius is a positive number of metres");
            }

            if (!_movers.TryGetValue(actor, out Mover? mover))
            {
                mover = new Mover();
                _movers[actor] = mover;
            }

            mover.Circle = circle;
            mover.Angle = circle.AngleAfter(0.0);
            mover.Paused = false;
            mover.Enabled = circle.Enabled;
            if (mover.Enabled) mover.Place();
            return Done();
        });
        _server.RegisterHandler<uint, bool, VoidResponse>("set_orbit_enabled", (actor, enabled) =>
        {
            _asked.Add($"set_orbit_enabled {actor} {enabled}");
            if (!_actors.Contains(actor))
            {
                throw new InvalidOperationException(
                    $"Responding error from function set_orbit_enabled: Actor could not be found in the registry. Actor Id: {actor}");
            }

            if (!_movers.TryGetValue(actor, out Mover? mover))
            {
                if (!enabled) return Done();
                throw new InvalidOperationException(
                    "set_orbit_enabled: the actor has no orbit; give it one with set_orbit first");
            }

            mover.Enabled = enabled;
            if (enabled) mover.Place();
            return Done();
        });
        _server.RegisterHandler<uint, bool, VoidResponse>("set_orbit_paused", (actor, paused) =>
        {
            _asked.Add($"set_orbit_paused {actor} {paused}");
            if (!_movers.TryGetValue(actor, out Mover? mover))
            {
                throw new InvalidOperationException(
                    "set_orbit_paused: the actor has no orbit; give it one with set_orbit first");
            }

            mover.Paused = paused;
            return Done();
        });
        _server.RegisterHandler<uint, SuccessResponse<OrbitState>>("get_orbit_state", actor =>
        {
            _asked.Add($"get_orbit_state {actor}");
            if (!_actors.Contains(actor))
            {
                throw new InvalidOperationException(
                    $"Responding error from function get_orbit_state: Actor could not be found in the registry. Actor Id: {actor}");
            }

            return Ok(_movers.TryGetValue(actor, out Mover? mover)
                ? new OrbitState(mover.Angle, mover.Enabled, mover.Paused)
                : new OrbitState(0.0, false, false));
        });
        await _server.StartAsync();
        _client = new CarlaClient("127.0.0.1", port, TimeSpan.FromSeconds(10));
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
    }

    /// The server's tick: every enabled mover advances by the fixed delta, as TG_PrePhysics does.
    private void Tick(int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            foreach (Mover mover in _movers.Values)
            {
                if (!mover.Enabled) continue;
                if (!mover.Paused)
                {
                    mover.Angle = Wrap(mover.Angle + mover.Circle.AngularRateRadiansPerSecond * Delta);
                }

                mover.Place();
            }
        }
    }

    private static double Wrap(double angle)
    {
        angle %= 2.0 * Math.PI;
        return angle < 0.0 ? angle + 2.0 * Math.PI : angle;
    }

    private static readonly OrbitParameters Circle = new(50.0, -80.0, 0.0, 200.0, 518.2, 240.0);

    [Fact]
    public async Task A_Circle_Sent_Once_Is_Flown_By_The_Server_From_Its_Start_Angle()
    {
        await _client!.SetOrbitAsync(7u, Circle);

        OrbitState state = await _client.GetOrbitStateAsync(7u);
        Assert.Equal(new OrbitState(0.0, true, false), state);
        Assert.Equal(Circle.PoseAt(0.0), _movers[7u].Pose);

        // Twenty ticks of 0.05 s is one simulated second: 2 pi / 240 radians, by the tick's delta and
        // nothing else; the client sent nothing meanwhile.
        Tick(20);
        state = await _client.GetOrbitStateAsync(7u);
        Assert.Equal(Circle.AngleAfter(1.0), state.AngleRadians, 1e-9);
        Assert.Equal(Circle.PoseAt(Circle.AngleAfter(1.0)), _movers[7u].Pose);
        Assert.Equal(["set_orbit 7", "get_orbit_state 7", "get_orbit_state 7"], _asked);
    }

    [Fact]
    public async Task A_Circle_Configured_Held_Leaves_The_Actor_Where_It_Is_Until_Enabled()
    {
        OrbitParameters held = Circle with { Enabled = false };
        await _client!.SetOrbitAsync(7u, held);

        Tick(20);
        Assert.Equal(new OrbitState(0.0, false, false), await _client.GetOrbitStateAsync(7u));
        Assert.Equal(default, _movers[7u].Pose);

        await _client.SetOrbitEnabledAsync(7u, true);
        Assert.Equal(held.PoseAt(0.0), _movers[7u].Pose);
        Tick(10);
        OrbitState state = await _client.GetOrbitStateAsync(7u);
        Assert.True(state.Enabled);
        Assert.Equal(held.AngleAfter(0.5), state.AngleRadians, 1e-9);
    }

    [Fact]
    public async Task Pausing_Holds_The_Angle_And_Resuming_Advances_It_Again()
    {
        await _client!.SetOrbitAsync(7u, Circle);
        Tick(20);

        await _client.SetOrbitPausedAsync(7u, true);
        Tick(40);
        OrbitState paused = await _client.GetOrbitStateAsync(7u);
        Assert.True(paused.Paused);
        Assert.Equal(Circle.AngleAfter(1.0), paused.AngleRadians, 1e-9);
        // The mover still holds the actor on the circle while paused.
        Assert.Equal(Circle.PoseAt(Circle.AngleAfter(1.0)), _movers[7u].Pose);

        await _client.SetOrbitPausedAsync(7u, false);
        Tick(20);
        OrbitState resumed = await _client.GetOrbitStateAsync(7u);
        Assert.False(resumed.Paused);
        Assert.Equal(Circle.AngleAfter(2.0), resumed.AngleRadians, 1e-9);
    }

    [Fact]
    public async Task Disabling_Lets_The_Actor_Go_And_Disabling_An_Actor_With_No_Orbit_Is_Nothing_To_Do()
    {
        await _client!.SetOrbitAsync(7u, Circle);
        Tick(20);
        await _client.SetOrbitEnabledAsync(7u, false);
        Transform where = _movers[7u].Pose;

        Tick(20);
        Assert.Equal(where, _movers[7u].Pose);
        Assert.Equal(Circle.AngleAfter(1.0), (await _client.GetOrbitStateAsync(7u)).AngleRadians, 1e-9);

        await _client.SetOrbitEnabledAsync(8u, false);
        Assert.Equal(new OrbitState(0.0, false, false), await _client.GetOrbitStateAsync(8u));
    }

    [Fact]
    public async Task Enabling_Or_Pausing_An_Actor_With_No_Orbit_Is_Refused_In_The_Servers_Words()
    {
        CarlaRpcException refused = await Assert.ThrowsAsync<CarlaRpcException>(
            () => _client!.SetOrbitEnabledAsync(8u, true));
        Assert.Contains("has no orbit; give it one with set_orbit first", refused.Message);
        Assert.False(refused.NamesNoSuchFunction);

        refused = await Assert.ThrowsAsync<CarlaRpcException>(() => _client!.SetOrbitPausedAsync(8u, true));
        Assert.Contains("has no orbit", refused.Message);
    }

    [Fact]
    public async Task An_Actor_The_Server_Does_Not_Have_Is_Refused()
    {
        CarlaRpcException refused = await Assert.ThrowsAsync<CarlaRpcException>(
            () => _client!.SetOrbitAsync(99u, Circle));
        Assert.Contains("Actor could not be found", refused.Message);
        Assert.Contains("Actor Id: 99", refused.Message);

        await Assert.ThrowsAsync<CarlaRpcException>(() => _client!.GetOrbitStateAsync(99u));
    }

    [Fact]
    public async Task A_Circle_The_Server_Would_Refuse_Is_Refused_Before_It_Is_Sent()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client!.SetOrbitAsync(7u, Circle with { RadiusMetres = 0.0 }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client!.SetOrbitAsync(7u, Circle with { PeriodSeconds = -1.0 }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client!.SetOrbitAsync(7u, Circle with { CentreYMetres = double.NaN }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client!.SetOrbitAsync(7u, Circle with { AltitudeMetres = double.PositiveInfinity }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client!.SetOrbitAsync(7u, Circle with { StartAngleRadians = double.NaN }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client!.SetOrbitAsync(7u, Circle with { PitchOverridden = true, PitchDegrees = double.NaN }));
        Assert.Empty(_asked);
        // A pitch that is not used may be anything.
        await _client!.SetOrbitAsync(7u, Circle with { PitchOverridden = false, PitchDegrees = double.NaN });
    }

    [Fact]
    public async Task A_Server_Without_The_Mover_Answers_That_It_Has_No_Such_Function()
    {
        int port = FreeLoopbackPort();
        await using var older = new MsgPackRpcServer(IPAddress.Loopback, port);
        await older.StartAsync();
        await using var client = new CarlaClient("127.0.0.1", port, TimeSpan.FromSeconds(10));

        CarlaRpcException refused = await Assert.ThrowsAsync<CarlaRpcException>(
            () => client.SetOrbitAsync(7u, Circle));

        Assert.True(refused.NamesNoSuchFunction);
        Assert.Contains("set_orbit", refused.Message);
    }

    // -- the pose rule, which the plugin and the client share -----------------------------------------

    [Fact]
    public void The_Pose_At_Angle_Zero_Is_East_Of_The_Centre_Looking_Back_And_Down_At_It()
    {
        // 200 m east of (50, -80), 518.2 m up: yaw 180 (west, back to the centre), pitch the depression
        // atan(518.2 / 200) = 68.896 degrees, worked by hand.
        Transform pose = Circle.PoseAt(0.0);
        Assert.Equal(250.0f, pose.Location.X, 1e-3f);
        Assert.Equal(-80.0f, pose.Location.Y, 1e-3f);
        Assert.Equal(518.2f, pose.Location.Z, 1e-3f);
        Assert.Equal(180.0f, Math.Abs(pose.Rotation.Yaw), 1e-3f);
        Assert.Equal(-68.896f, pose.Rotation.Pitch, 1e-3f);
        Assert.Equal(0.0f, pose.Rotation.Roll);
    }

    [Fact]
    public void A_Quarter_Turn_Clockwise_Is_South_Of_The_Centre_Looking_North()
    {
        // The angle increasing carries the camera to +y, south in a world where -y is north, and the
        // boresight turns to -y: yaw -90.
        Transform pose = Circle.PoseAt(Circle.AngleAfter(60.0));
        Assert.Equal(50.0f, pose.Location.X, 1e-3f);
        Assert.Equal(120.0f, pose.Location.Y, 1e-3f);
        Assert.Equal(-90.0f, pose.Rotation.Yaw, 1e-3f);

        OrbitParameters anticlockwise = Circle with { Clockwise = false };
        Transform other = anticlockwise.PoseAt(anticlockwise.AngleAfter(60.0));
        Assert.Equal(-280.0f, other.Location.Y, 1e-3f);
        Assert.Equal(90.0f, other.Rotation.Yaw, 1e-3f);
    }

    [Fact]
    public void A_Pitch_Override_Replaces_The_Depression_And_Nothing_Else()
    {
        OrbitParameters level = Circle with { PitchOverridden = true, PitchDegrees = -30.0 };
        Transform pose = level.PoseAt(1.0);
        Assert.Equal(-30.0f, pose.Rotation.Pitch);
        Assert.Equal(Circle.PoseAt(1.0).Location, pose.Location);
        Assert.Equal(Circle.PoseAt(1.0).Rotation.Yaw, pose.Rotation.Yaw);
    }

    [Fact]
    public void The_Angle_Returns_To_Its_Start_After_One_Period_And_Wraps_Inside_A_Turn()
    {
        Assert.Equal(0.0, Circle.AngleAfter(240.0), 1e-9);
        Assert.Equal(Math.PI, Circle.AngleAfter(120.0), 1e-9);
        OrbitParameters begun = Circle with { StartAngleRadians = 3.0 * Math.PI / 2.0 };
        Assert.Equal(Math.PI / 2.0, begun.AngleAfter(120.0), 1e-9);
        OrbitParameters backwards = Circle with { Clockwise = false };
        Assert.Equal(3.0 * Math.PI / 2.0, backwards.AngleAfter(60.0), 1e-9);
        // 2.7 simulated seconds is 2.7 seconds of angle at any pace: the rule reads the clock, never the wall.
        Assert.Equal(2.0 * Math.PI / 240.0 * 2.7, Circle.AngleAfter(2.7), 1e-12);
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
