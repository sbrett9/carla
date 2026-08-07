// Offline (no engine, no server): what happens to a vehicle that stops following the route it was
// planned.
//
// A route is computed once, before the vehicle spawns. Everything after that can knock the vehicle
// off it — an automatic lane change to pass an obstacle (on by default, and it empties the horizon
// buffer outright), a shove from a collision, a junction the horizon walk took differently from the
// plan. The vehicle then replans from where it now is to the destination it was given, and if that
// keeps failing an operator-chosen policy decides between going on trying and handing the vehicle
// back to greedy steering.
//
// Every one of those transitions prints a line. That is a requirement, not a diagnostic: a vehicle
// silently not going where it was sent is the failure this subsystem exists to remove. The tests
// assert on those lines, so the reporting cannot be dropped without failing the run.
#nullable enable

using System.Text;
using CarlaNet.Map.OpenDrive;
using CarlaNet.TrafficManager;
using CarlaNet.Types.Geom;
using Xunit;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.TrafficManager;

public class RouteSupervisorTests
{
    private const ActorId Vehicle = 7u;

    // Replanning happens on its own thread, so results are awaited rather than assumed. Long
    // enough that a loaded machine does not fail the run; a working replan lands in milliseconds.
    private static readonly TimeSpan ReplanDeadline = TimeSpan.FromSeconds(10);

    // A straight 400 m single-lane road. One-way, which gives the tests a destination that is
    // reachable from one end and unreachable from the other without needing a second road.
    private const string StraightRoadXodr =
        "<?xml version=\"1.0\" standalone=\"yes\"?>\n" +
        "<OpenDRIVE>\n" +
        "  <header revMajor=\"1\" revMinor=\"4\" name=\"\" version=\"1\" north=\"0\" south=\"0\" east=\"0\" west=\"0\"/>\n" +
        "  <road name=\"Road 1\" length=\"400.0\" id=\"1\" junction=\"-1\">\n" +
        "    <planView>\n" +
        "      <geometry s=\"0.0\" x=\"0.0\" y=\"0.0\" hdg=\"0.0\" length=\"400.0\"><line/></geometry>\n" +
        "    </planView>\n" +
        "    <lanes>\n" +
        "      <laneSection s=\"0.0\">\n" +
        "        <center><lane id=\"0\" type=\"none\" level=\"false\"/></center>\n" +
        "        <right>\n" +
        "          <lane id=\"-1\" type=\"driving\" level=\"false\">\n" +
        "            <width sOffset=\"0.0\" a=\"3.5\" b=\"0.0\" c=\"0.0\" d=\"0.0\"/>\n" +
        "          </lane>\n" +
        "        </right>\n" +
        "      </laneSection>\n" +
        "    </lanes>\n" +
        "  </road>\n" +
        "</OpenDRIVE>\n";

    /// <summary>Collects the supervisor's reporting from whichever thread produced it.</summary>
    private sealed class Recorder : System.IO.TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_text) { _text.AppendLine(value); }
        }

        public override void Write(char value)
        {
            lock (_text) { _text.Append(value); }
        }

        public string Text { get { lock (_text) { return _text.ToString(); } } }

        public bool Saw(string fragment) => Text.Contains(fragment, StringComparison.Ordinal);

        public int Count(string fragment)
        {
            string text = Text;
            int found = 0, at = 0;
            while ((at = text.IndexOf(fragment, at, StringComparison.Ordinal)) >= 0)
            {
                found++;
                at += fragment.Length;
            }
            return found;
        }

        /// <summary>Wait for <paramref name="fragment"/> to be reported, or fail the test.</summary>
        public void Await(string fragment)
        {
            DateTime deadline = DateTime.UtcNow + ReplanDeadline;
            while (DateTime.UtcNow < deadline)
            {
                if (Saw(fragment)) return;
                Thread.Sleep(10);
            }
            Assert.Fail($"never reported \"{fragment}\". What was reported:\n{Text}");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public required InMemoryMap Map { get; init; }
        public required Parameters Parameters { get; init; }
        public required RouteSupervisor Supervisor { get; init; }
        public required Recorder Reported { get; init; }
        public required IReadOnlyList<SimpleWaypoint> Topology { get; init; }

        public void Dispose()
        {
            Supervisor.Dispose();
            Reported.Dispose();
        }
    }

    private static Fixture Build()
    {
        RoadMap worldMap = OpenDriveParser.Load(StraightRoadXodr)
            ?? throw new InvalidOperationException("test .xodr failed to parse");
        var map = new InMemoryMap(worldMap);
        map.SetUp();

        var parameters = new Parameters();
        var reported = new Recorder();
        return new Fixture
        {
            Map = map,
            Parameters = parameters,
            Reported = reported,
            Topology = map.GetDenseTopology(),
            Supervisor = new RouteSupervisor(new RoutePlanner(map), parameters, reported),
        };
    }

    /// <summary>
    /// A waypoint belonging to no road graph at all. Only for vehicles the supervisor is not
    /// tracking, where nothing is ever planned from it. Anywhere a replan is expected, use a real
    /// waypoint from the map: in the running system the graph position is always the head of the
    /// vehicle's horizon buffer, which is by definition a node of the graph with edges to follow.
    /// </summary>
    private static SimpleWaypoint SomewhereElse()
        => new(new CarlaNet.Map.Road.Element.Waypoint(999u, 0u, -1, 0.0),
               new Location(9_000.0f, 9_000.0f, 0.0f), new Vector3D(1.0f, 0.0f, 0.0f));

    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_vehicle_that_was_never_routed_is_left_alone()
    {
        using Fixture f = Build();

        f.Supervisor.Observe(Vehicle, f.Topology[0].Location, f.Topology[0]);
        f.Supervisor.Observe(Vehicle, SomewhereElse().Location, SomewhereElse());

        Assert.Equal(string.Empty, f.Reported.Text);
        Assert.Empty(f.Parameters.GetCustomPath(Vehicle));
        Assert.Equal(0, f.Supervisor.RoutedVehicleCount);
    }

    [Fact]
    public void Assigning_a_route_gives_the_vehicle_its_waypoints_to_follow()
    {
        using Fixture f = Build();
        PlannedRoute route = Plan(f, from: 0, to: ^1);

        f.Supervisor.Assign(Vehicle, route);

        Assert.Equal(route.Path, f.Parameters.GetCustomPath(Vehicle));
        Assert.True(route.Path.Count > 10, "the road should have produced a multi-waypoint route.");
        Assert.Equal(1, f.Supervisor.RoutedVehicleCount);
    }

    [Fact]
    public void Following_the_route_reports_nothing()
    {
        using Fixture f = Build();
        f.Supervisor.Assign(Vehicle, Plan(f, from: 0, to: ^1));

        for (int i = 0; i < f.Topology.Count; ++i)
            f.Supervisor.Observe(Vehicle, f.Topology[i].Location, f.Topology[i]);

        Assert.Equal(string.Empty, f.Reported.Text);
    }

    [Fact]
    public void Leaving_the_route_is_reported_and_replanned()
    {
        using Fixture f = Build();
        // A route that begins well ahead of where the vehicle actually sits on the graph, so the
        // waypoint it is judged by is not one the route names.
        PlannedRoute original = Plan(f, from: 20, to: ^1);
        f.Supervisor.Assign(Vehicle, original);
        SimpleWaypoint head = f.Topology[5];

        f.Supervisor.Observe(Vehicle, head.Location, head);

        f.Reported.Await("left its planned route");
        f.Reported.Await("replanned to");

        Assert.Contains($"vehicle {Vehicle}", f.Reported.Text, StringComparison.Ordinal);
        // The replan starts from the waypoint the vehicle was judged by, so it is longer than the
        // route it replaced — which began 15 waypoints further on.
        IReadOnlyList<Location> installed = f.Parameters.GetCustomPath(Vehicle);
        Assert.True(installed.Count > original.Path.Count,
            $"replanned path has {installed.Count} waypoints; the original had {original.Path.Count}, "
            + "so the replan did not start from the vehicle's position on the graph.");
        Assert.Equal(head.Location.X, installed[0].X, 1);
    }

    [Fact]
    public void Rejoining_the_route_under_its_own_power_is_reported()
    {
        using Fixture f = Build();
        // Destination near the start of a one-way road: from the far end there is no route back, so
        // the replan fails and the vehicle stays marked as having left. It then rejoins by driving.
        f.Supervisor.Assign(Vehicle, Plan(f, from: 0, to: 5));

        f.Supervisor.Observe(Vehicle, f.Topology[^1].Location, f.Topology[^1]);
        f.Reported.Await("has no route from");

        f.Supervisor.Observe(Vehicle, f.Topology[3].Location, f.Topology[3]);

        Assert.Contains("back on its planned route", f.Reported.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replan_puts_the_vehicle_back_on_route_without_departing_again()
    {
        // The route is judged against the head of the vehicle's horizon buffer, which is NOT the
        // waypoint nearest the vehicle — the localization stage tolerates the two being up to
        // MAX_START_DISTANCE apart. Planning from the position while judging by the head produced a
        // route that did not contain the node it was about to be judged by, so the vehicle was
        // declared off its route on the tick after it was put on one, forever: measured against a
        // real map, a route planned from the vehicle's position covered as few as 10% of the
        // waypoints the head could legitimately be. A replan must be anchored to the head.
        using Fixture f = Build();

        // A route that starts well ahead of where the vehicle actually is on the graph.
        f.Supervisor.Assign(Vehicle, Plan(f, from: 20, to: ^1));
        SimpleWaypoint head = f.Topology[5];

        f.Supervisor.Observe(Vehicle, head.Location, head);
        f.Reported.Await("left its planned route");
        f.Reported.Await("replanned to");

        // The same head, unchanged — as it would be for a vehicle held at a light or in traffic.
        f.Supervisor.Observe(Vehicle, head.Location, head);

        Assert.Equal(1, f.Reported.Count("left its planned route"));
        Assert.Equal(1, f.Reported.Count("replanned to"));

        // Observing it repeatedly must stay quiet rather than replanning every tick.
        for (int tick = 0; tick < 50; ++tick)
            f.Supervisor.Observe(Vehicle, head.Location, head);
        Assert.Equal(1, f.Reported.Count("left its planned route"));
        Assert.Equal(1, f.Reported.Count("replanned to"));
    }

    [Fact]
    public void A_vehicle_that_cannot_be_replanned_keeps_trying_by_default()
    {
        using Fixture f = Build();
        // Destination near the start of a one-way road, vehicle at the far end: no route back.
        PlannedRoute route = Plan(f, from: 0, to: 5);
        f.Supervisor.Assign(Vehicle, route);

        f.Supervisor.Observe(Vehicle, f.Topology[^1].Location, f.Topology[^1]);

        f.Reported.Await("has no route from");
        f.Reported.Await("retrying in");

        Assert.False(f.Reported.Saw("steering greedily"),
            "the greedy fallback is off by default; the vehicle should still be trying to plan.");
        // Nothing was installed over the route it already had.
        Assert.Equal(route.Path, f.Parameters.GetCustomPath(Vehicle));
    }

    [Fact]
    public void The_greedy_fallback_takes_over_once_the_attempt_limit_is_reached()
    {
        using Fixture f = Build();
        f.Parameters.SetRouteGreedyFallbackEnabled(true);
        f.Parameters.SetRouteReplanAttemptLimit(1);

        PlannedRoute route = Plan(f, from: 0, to: 5);
        f.Supervisor.Assign(Vehicle, route);

        f.Supervisor.Observe(Vehicle, f.Topology[^1].Location, f.Topology[^1]);

        f.Reported.Await("steering greedily toward it instead");

        // Greedy steering is what an unplanned routed vehicle has always been given: the bare
        // destination, followed junction by junction.
        IReadOnlyList<Location> installed = f.Parameters.GetCustomPath(Vehicle);
        Assert.Single(installed);
        Assert.Equal(route.Destination.X, installed[0].X, 2);
        Assert.Equal(route.Destination.Y, installed[0].Y, 2);
    }

    [Fact]
    public void An_attempt_limit_of_zero_never_reaches_the_fallback()
    {
        using Fixture f = Build();
        f.Parameters.SetRouteGreedyFallbackEnabled(true);
        f.Parameters.SetRouteReplanAttemptLimit(0);

        PlannedRoute route = Plan(f, from: 0, to: 5);
        f.Supervisor.Assign(Vehicle, route);

        f.Supervisor.Observe(Vehicle, f.Topology[^1].Location, f.Topology[^1]);

        f.Reported.Await("has no route from");
        Assert.False(f.Reported.Saw("steering greedily"),
            "a limit of zero means the fallback is never reached, however often replanning fails.");
        Assert.Equal(route.Path, f.Parameters.GetCustomPath(Vehicle));
    }

    [Fact]
    public void Driving_the_whole_route_retires_the_vehicle()
    {
        using Fixture f = Build();
        f.Supervisor.Assign(Vehicle, Plan(f, from: 0, to: ^1));

        // The localization stage drops the path once the last waypoint has been consumed.
        f.Parameters.RemoveUploadPath(Vehicle, removePath: true);
        f.Supervisor.Observe(Vehicle, f.Topology[^1].Location, f.Topology[^1]);

        Assert.Contains("reached the end of its planned route", f.Reported.Text, StringComparison.Ordinal);
        Assert.Equal(0, f.Supervisor.RoutedVehicleCount);
    }

    [Fact]
    public void A_destroyed_vehicle_is_forgotten_rather_than_resumed()
    {
        // A despawned vehicle never comes back, so there is no route left to carry over. Observing
        // its id again must do nothing at all rather than resurrect the old plan.
        using Fixture f = Build();
        f.Supervisor.Assign(Vehicle, Plan(f, from: 0, to: ^1));

        f.Supervisor.RemoveActor(Vehicle);
        f.Supervisor.Observe(Vehicle, f.Topology[0].Location, SomewhereElse());

        Assert.Equal(string.Empty, f.Reported.Text);
        Assert.Equal(0, f.Supervisor.RoutedVehicleCount);
    }

    [Fact]
    public void Destroying_a_vehicle_takes_its_route_out_of_the_parameter_store()
    {
        // Nothing else removes a vehicle's path when the actor is destroyed. A planned route is
        // hundreds of waypoints, and staging traffic spawns and despawns for the length of a run,
        // so an abandoned route per vehicle would accumulate for as long as the scenario ran.
        using Fixture f = Build();
        f.Supervisor.Assign(Vehicle, Plan(f, from: 0, to: ^1));
        Assert.NotEmpty(f.Parameters.GetCustomPath(Vehicle));

        f.Supervisor.RemoveActor(Vehicle);

        Assert.Empty(f.Parameters.GetCustomPath(Vehicle));
        Assert.False(f.Parameters.GetUploadPath(Vehicle));
    }

    [Fact]
    public void Resetting_takes_every_route_out_of_the_parameter_store()
    {
        using Fixture f = Build();
        PlannedRoute route = Plan(f, from: 0, to: ^1);
        for (ActorId id = 1u; id <= 5u; ++id) f.Supervisor.Assign(id, route);
        Assert.Equal(5, f.Supervisor.RoutedVehicleCount);

        f.Supervisor.Reset();

        Assert.Equal(0, f.Supervisor.RoutedVehicleCount);
        for (ActorId id = 1u; id <= 5u; ++id) Assert.Empty(f.Parameters.GetCustomPath(id));
    }

    private static PlannedRoute Plan(Fixture f, Index from, Index to)
    {
        var planner = new RoutePlanner(f.Map);
        PlannedRoute? route = planner.Plan(f.Topology[from].Location, f.Topology[to].Location);
        Assert.NotNull(route);
        return route!;
    }
}
