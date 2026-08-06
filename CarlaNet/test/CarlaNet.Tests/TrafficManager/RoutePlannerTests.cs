// Offline (no engine, no server): the route planner must find real shortest routes, admit when
// there is none, terminate on cyclic road graphs, and return the same route every time.
//
// These four properties are what separate a planned route from the greedy walk it replaces. The
// greedy walk cannot see past the next junction, so it has no notion of shortest, cannot tell an
// unreachable destination from a distant one, circles a loop ramp indefinitely, and can pick
// differently between two near-equidistant junction exits from one run to the next.
//
// Every graph here is built by hand rather than parsed from OpenDRIVE, so each test states exactly
// the shape it is about. Searches over cyclic graphs run on a worker with a deadline, so a
// non-terminating regression fails the run instead of hanging it.
#nullable enable

using CarlaNet.Map.OpenDrive;
using CarlaNet.Map.Road.Element;
using CarlaNet.TrafficManager;
using CarlaNet.Types.Geom;
using Xunit;
using RoadMap = CarlaNet.Map.Road.Map;
using RouteStep = CarlaNet.TrafficManager.RoutePlanner.RouteStep;

namespace CarlaNet.Tests.TrafficManager;

public class RoutePlannerTests
{
    // Generous next to a bounded search over graphs of a few hundred nodes; only a search that
    // never terminates can exceed it.
    private static readonly TimeSpan SearchDeadline = TimeSpan.FromSeconds(10);

    // Well above every graph built here, so no test result is an artefact of the expansion budget.
    private const int Budget = 100_000;

    // ─────────────────────────────────────────────────────────────────────
    //                           Graph construction
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A waypoint at a chosen place, identified by a road id and an offset along it. Distinct
    /// (roadId, s) pairs make every node in these graphs distinguishable, which is what the
    /// planner's tie-break orders on.
    /// </summary>
    private static SimpleWaypoint Node(uint roadId, double s, float x, float y, int laneId = -1)
        => new(new Waypoint(roadId, sectionId: 0u, laneId: laneId, s: s),
               new Location(x, y, 0.0f), new Vector3D(1.0f, 0.0f, 0.0f));

    private static void Link(SimpleWaypoint from, params SimpleWaypoint[] to)
        => from.SetNextWaypoint(to);

    /// <summary>
    /// Link two waypoints as lane-change neighbours. Set directly rather than through
    /// SetLeftWaypoint / SetRightWaypoint, whose geometric side test is about how InMemoryMap
    /// derives the links from real lane geometry, not about what the planner does with them.
    /// </summary>
    private static void LinkLanes(SimpleWaypoint inner, SimpleWaypoint outer)
    {
        inner.RightWaypoint = outer;
        outer.LeftWaypoint = inner;
    }

    /// <summary>
    /// A chain of <paramref name="count"/> waypoints spaced <paramref name="spacing"/> apart along
    /// +x from (<paramref name="startX"/>, <paramref name="y"/>), each linked to the next.
    /// </summary>
    private static List<SimpleWaypoint> Chain(
        uint roadId, int count, float startX, float y, float spacing = 5.0f, int laneId = -1)
    {
        var chain = new List<SimpleWaypoint>(count);
        for (int i = 0; i < count; ++i)
            chain.Add(Node(roadId, i * spacing, startX + i * spacing, y, laneId));
        for (int i = 0; i < count - 1; ++i)
            Link(chain[i], chain[i + 1]);
        return chain;
    }

    /// <summary>A closed ring of waypoints — the shape a motorway loop ramp presents to a search.</summary>
    private static List<SimpleWaypoint> Ring(uint roadId, int count, float radius, float centreX = 0.0f)
    {
        var ring = new List<SimpleWaypoint>(count);
        for (int i = 0; i < count; ++i)
        {
            double angle = 2.0 * Math.PI * i / count;
            ring.Add(Node(roadId, i,
                          centreX + (float)(radius * Math.Cos(angle)),
                          (float)(radius * Math.Sin(angle))));
        }
        for (int i = 0; i < count; ++i)
            Link(ring[i], ring[(i + 1) % count]);
        return ring;
    }

    private static T RunBounded<T>(Func<T> search, string what)
    {
        var task = Task.Run(search);
        Assert.True(task.Wait(SearchDeadline),
            $"{what} did not terminate within {SearchDeadline.TotalSeconds:F0}s — the search is "
            + "unbounded on this graph.");
        return task.GetAwaiter().GetResult();
    }

    private static float PathLength(IReadOnlyList<RouteStep> steps)
    {
        float total = 0.0f;
        for (int i = 1; i < steps.Count; ++i)
        {
            Location a = steps[i - 1].Waypoint.Location;
            Location b = steps[i].Waypoint.Location;
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            total += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return total;
    }

    // ─────────────────────────────────────────────────────────────────────
    //                             Shortest path
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Takes_the_shorter_of_two_routes_to_the_same_place()
    {
        // start ─┬─ short detour (one node, +10 m) ─┬─ finish
        //        └─ long detour (nine nodes, +90 m) ┘
        var start = Node(1u, 0.0, 0.0f, 0.0f);
        var finish = Node(2u, 0.0, 100.0f, 0.0f);

        var shortWay = Chain(roadId: 10u, count: 1, startX: 50.0f, y: 10.0f);
        var longWay = Chain(roadId: 20u, count: 9, startX: 10.0f, y: -40.0f);

        Link(start, shortWay[0], longWay[0]);
        Link(shortWay[^1], finish);
        Link(longWay[^1], finish);

        List<RouteStep>? route = RoutePlanner.Search(start, finish, Budget);

        Assert.NotNull(route);
        Assert.Equal(3, route!.Count);                       // start, the one detour node, finish
        Assert.Same(shortWay[0], route[1].Waypoint);
        Assert.DoesNotContain(route, step => longWay.Contains(step.Waypoint));
    }

    [Fact]
    public void Route_runs_from_the_origin_to_the_destination_in_travel_order()
    {
        List<SimpleWaypoint> road = Chain(roadId: 1u, count: 21, startX: 0.0f, y: 0.0f);

        List<RouteStep>? route = RoutePlanner.Search(road[0], road[^1], Budget);

        Assert.NotNull(route);
        Assert.Equal(21, route!.Count);
        Assert.Same(road[0], route[0].Waypoint);
        Assert.Same(road[^1], route[^1].Waypoint);
        Assert.Equal(100.0f, PathLength(route), 3);
        for (int i = 0; i < route.Count; ++i)
            Assert.Same(road[i], route[i].Waypoint);
    }

    // ─────────────────────────────────────────────────────────────────────
    //                              No route
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reports_no_route_between_disconnected_roads()
    {
        List<SimpleWaypoint> here = Chain(roadId: 1u, count: 10, startX: 0.0f, y: 0.0f);
        List<SimpleWaypoint> elsewhere = Chain(roadId: 2u, count: 10, startX: 500.0f, y: 500.0f);

        Assert.Null(RoutePlanner.Search(here[0], elsewhere[^1], Budget));
    }

    [Fact]
    public void Reports_no_route_against_the_direction_of_travel()
    {
        // Successor links are one-way; a destination behind the vehicle on a one-way road is not
        // reachable, however close it is. Distinguishing that from "far away" is exactly what the
        // greedy walk cannot do — it would drive toward it forever.
        List<SimpleWaypoint> oneWay = Chain(roadId: 1u, count: 10, startX: 0.0f, y: 0.0f);

        Assert.Null(RoutePlanner.Search(oneWay[^1], oneWay[0], Budget));
    }

    // ─────────────────────────────────────────────────────────────────────
    //                        Cyclic road graphs
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Leaves_a_loop_ramp_by_its_exit_rather_than_circling_it()
    {
        // A ring with a single exit part-way round, and a destination down that exit. Reaching it
        // means going round as far as the exit and then leaving — the search must not keep lapping.
        const int ringSize = 40;
        const int exitAt = 12;
        List<SimpleWaypoint> ring = Ring(roadId: 100u, count: ringSize, radius: 20.0f);
        List<SimpleWaypoint> exit = Chain(roadId: 200u, count: 8, startX: 60.0f, y: 0.0f);
        Link(ring[exitAt], ring[exitAt + 1], exit[0]);

        List<RouteStep>? route = RunBounded(
            () => RoutePlanner.Search(ring[0], exit[^1], Budget), "search over a loop ramp");

        Assert.NotNull(route);
        // exitAt + 1 ring nodes (0 through the exit node) then the whole exit chain.
        Assert.Equal(exitAt + 1 + exit.Count, route!.Count);
        Assert.Equal(route.Count, route.Select(step => step.Waypoint).Distinct().Count());
        Assert.Same(exit[^1], route[^1].Waypoint);
    }

    [Fact]
    public void Terminates_when_a_loop_ramp_leads_nowhere_near_the_destination()
    {
        // The failure that motivated bounding the greedy walk: a ring the vehicle can never leave,
        // and a destination it can never reach. The search must give up, not lap forever.
        List<SimpleWaypoint> ring = Ring(roadId: 100u, count: 40, radius: 20.0f);
        var unreachable = Node(300u, 0.0, 10_000.0f, 10_000.0f);

        List<RouteStep>? route = RunBounded(
            () => RoutePlanner.Search(ring[0], unreachable, Budget), "search over an inescapable ring");

        Assert.Null(route);
    }

    [Fact]
    public void Gives_up_once_the_expansion_budget_is_spent()
    {
        // The backstop against a graph bigger than the topology it was derived from: a search that
        // would otherwise walk a very long way returns nothing rather than running unbounded.
        List<SimpleWaypoint> road = Chain(roadId: 1u, count: 500, startX: 0.0f, y: 0.0f);
        var unreachable = Node(2u, 0.0, 10_000.0f, 10_000.0f);

        Assert.Null(RoutePlanner.Search(road[0], unreachable, maxExpansions: 10));
        // The same graph is fine with a budget that fits it, so the null above is the budget and
        // not some other defect.
        Assert.NotNull(RoutePlanner.Search(road[0], road[^1], Budget));
    }

    // ─────────────────────────────────────────────────────────────────────
    //                             Determinism
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two routes of exactly equal cost between the same endpoints, built as mirror images so their
    /// lengths agree bit for bit. Which one the search returns is decided purely by the tie-break.
    /// </summary>
    private static (SimpleWaypoint Start, SimpleWaypoint Finish,
                    List<SimpleWaypoint> North, List<SimpleWaypoint> South)
        BuildEquidistantFork(bool northFirst)
    {
        var start = Node(1u, 0.0, 0.0f, 0.0f);
        var finish = Node(2u, 0.0, 100.0f, 0.0f);
        List<SimpleWaypoint> north = Chain(roadId: 30u, count: 5, startX: 20.0f, y: 25.0f);
        List<SimpleWaypoint> south = Chain(roadId: 40u, count: 5, startX: 20.0f, y: -25.0f);

        if (northFirst) Link(start, north[0], south[0]);
        else Link(start, south[0], north[0]);
        Link(north[^1], finish);
        Link(south[^1], finish);
        return (start, finish, north, south);
    }

    [Fact]
    public void Picks_the_same_route_whatever_order_the_successors_are_in()
    {
        var (startA, finishA, northA, _) = BuildEquidistantFork(northFirst: true);
        var (startB, finishB, northB, _) = BuildEquidistantFork(northFirst: false);

        List<RouteStep>? routeA = RoutePlanner.Search(startA, finishA, Budget);
        List<RouteStep>? routeB = RoutePlanner.Search(startB, finishB, Budget);

        Assert.NotNull(routeA);
        Assert.NotNull(routeB);
        Assert.Equal(7, routeA!.Count);
        Assert.Equal(PathLength(routeA), PathLength(routeB!), 3);

        // Both must resolve the tie the same way. The two graphs hold different objects, so compare
        // the road coordinates that identify each waypoint.
        Assert.Equal(routeA.Select(step => step.Waypoint.Waypoint).ToList(),
                     routeB!.Select(step => step.Waypoint.Waypoint).ToList());

        // And it must be a real tie, or the test proves nothing about tie-breaking: the branch not
        // taken is the same length as the one that was.
        Assert.Contains(routeA, step => northA.Contains(step.Waypoint));
    }

    [Fact]
    public void Repeating_a_search_returns_the_same_route()
    {
        var (start, finish, _, _) = BuildEquidistantFork(northFirst: true);

        List<RouteStep>? first = RoutePlanner.Search(start, finish, Budget);
        Assert.NotNull(first);

        for (int run = 0; run < 20; ++run)
        {
            List<RouteStep>? again = RoutePlanner.Search(start, finish, Budget);
            Assert.NotNull(again);
            Assert.Equal(first!.Select(step => step.Waypoint).ToList(),
                         again!.Select(step => step.Waypoint).ToList());
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //                            Lane changes
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two parallel lanes of the same carriageway. The slip road leaves the outer lane only, which
    /// is how a motorway exit is laid out — a vehicle in the inner lane has to change lane to take
    /// it, and a search without lane-change edges would call the exit unreachable.
    /// </summary>
    private static (List<SimpleWaypoint> Inner, List<SimpleWaypoint> Outer, List<SimpleWaypoint> Slip)
        BuildCarriagewayWithSlipRoad()
    {
        List<SimpleWaypoint> inner = Chain(roadId: 1u, count: 20, startX: 0.0f, y: 0.0f, laneId: -1);
        List<SimpleWaypoint> outer = Chain(roadId: 1u, count: 20, startX: 0.0f, y: 3.5f, laneId: -2);
        for (int i = 0; i < inner.Count; ++i) LinkLanes(inner[i], outer[i]);

        List<SimpleWaypoint> slip = Chain(roadId: 2u, count: 10, startX: 80.0f, y: 20.0f);
        Link(outer[15], outer[16], slip[0]);
        return (inner, outer, slip);
    }

    [Fact]
    public void Changes_lane_when_the_destination_can_only_be_reached_from_the_other_lane()
    {
        var (inner, outer, slip) = BuildCarriagewayWithSlipRoad();

        List<RouteStep>? route = RoutePlanner.Search(inner[0], slip[^1], Budget);

        Assert.NotNull(route);
        Assert.Same(slip[^1], route![^1].Waypoint);
        Assert.Contains(route, step => step.ReachedByLaneChange);
        // It starts in the inner lane, ends up in the outer one, and leaves by the slip road.
        Assert.Same(inner[0], route[0].Waypoint);
        Assert.Contains(route, step => outer.Contains(step.Waypoint));
    }

    /// <summary>
    /// A wide carriageway — five lanes — where the slip road leaves the outermost lane only, so a
    /// vehicle starting on the inside has to cross all five to reach it. This is the shape of a
    /// motorway exit, and the shape that produced routes no vehicle could follow.
    /// </summary>
    private static (List<List<SimpleWaypoint>> Lanes, List<SimpleWaypoint> Slip) BuildWideCarriageway()
    {
        var lanes = new List<List<SimpleWaypoint>>();
        for (int lane = 0; lane < 5; ++lane)
            lanes.Add(Chain(roadId: 1u, count: 40, startX: 0.0f, y: lane * 3.5f, laneId: -(lane + 1)));
        for (int lane = 0; lane + 1 < lanes.Count; ++lane)
            for (int i = 0; i < lanes[lane].Count; ++i)
                LinkLanes(lanes[lane][i], lanes[lane + 1][i]);

        List<SimpleWaypoint> slip = Chain(roadId: 2u, count: 10, startX: 160.0f, y: 40.0f);
        Link(lanes[4][31], lanes[4][32], slip[0]);
        return (lanes, slip);
    }

    [Fact]
    public void Never_asks_for_two_lane_changes_without_driving_between_them()
    {
        // Two lane changes in a row land at the same point along the road: several metres sideways,
        // none forward. The vehicle is steered by consuming route waypoints that come within about
        // 5.5 m of where it is heading, so a waypoint straight out to the side is never reached —
        // the vehicle carries on down the lane the route abandoned and is off it for good. Measured
        // on a generated interchange, a five-lane crossing was planned entirely at one s.
        var (lanes, slip) = BuildWideCarriageway();

        List<RouteStep>? route = RoutePlanner.Search(lanes[0][0], slip[^1], Budget);

        Assert.NotNull(route);
        Assert.Same(slip[^1], route![^1].Waypoint);
        // It really does cross the whole carriageway, or the test proves nothing.
        Assert.Equal(4, route.Count(step => step.ReachedByLaneChange));

        for (int i = 1; i < route.Count; ++i)
        {
            Assert.False(route[i].ReachedByLaneChange && route[i - 1].ReachedByLaneChange,
                $"route steps {i - 1} and {i} both change lane, so they sit at the same point along "
                + "the road and the vehicle is asked to move sideways without moving forward.");
        }

        // Every consecutive pair of breadcrumbs must be reachable by driving: near enough to be
        // consumed, and never a pure sideways step.
        PlannedRoute planned = RoutePlanner.Materialize(route, slip[^1].Location);
        for (int i = 1; i < planned.Path.Count; ++i)
        {
            float dx = planned.Path[i].X - planned.Path[i - 1].X;
            float dy = planned.Path[i].Y - planned.Path[i - 1].Y;
            Assert.True(MathF.Abs(dx) > 0.1f,
                $"breadcrumbs {i - 1} and {i} are {MathF.Sqrt(dx * dx + dy * dy):F1} m apart but make "
                + "no progress along the road.");
        }
    }

    [Fact]
    public void Stays_in_lane_when_changing_would_not_shorten_the_route()
    {
        // Both lanes run to the same place, so the lane-change penalty should keep the search in
        // the lane it started in rather than weaving for no gain.
        var (inner, outer, _) = BuildCarriagewayWithSlipRoad();

        List<RouteStep>? route = RoutePlanner.Search(inner[0], inner[^1], Budget);

        Assert.NotNull(route);
        Assert.Equal(inner.Count, route!.Count);
        Assert.DoesNotContain(route, step => step.ReachedByLaneChange);
        Assert.DoesNotContain(route, step => outer.Contains(step.Waypoint));
    }

    [Fact]
    public void A_lane_change_carries_the_vehicle_forward_as_well_as_across()
    {
        // A route step that moves sideways without moving along the road is not a manoeuvre a
        // vehicle can perform, and the horizon walk cannot follow it either: it appends the
        // breadcrumb to the vehicle's buffer, and everything reading that buffer — junction entry
        // and exit detection, the index-based steering look-ahead, and the collision look-ahead
        // that reuses that index — takes it for an unbroken run of road at one map resolution per
        // step. Measured on a real interchange, routes containing such a step put vehicles more
        // than 6 m off any lane five times as often as unrouted traffic on the same map at the
        // same speeds.
        var (inner, outer, slip) = BuildCarriagewayWithSlipRoad();

        List<RouteStep>? route = RoutePlanner.Search(inner[0], slip[^1], Budget);

        Assert.NotNull(route);
        Assert.Same(slip[^1], route![^1].Waypoint);
        Assert.Contains(route, step => step.ReachedByLaneChange);   // it really does cross lanes

        PlannedRoute planned = RoutePlanner.Materialize(route, slip[^1].Location);
        Assert.Equal(route.Count, planned.Path.Count);              // nothing is elided any more

        for (int i = 1; i < route.Count; ++i)
        {
            Location a = route[i - 1].Waypoint.Location, b = route[i].Waypoint.Location;
            float along = MathF.Abs(b.X - a.X);          // the carriageway runs along +x
            float across = MathF.Abs(b.Y - a.Y);
            Assert.True(along > 0.1f,
                $"step {i} moves {across:F1} m across the road and {along:F1} m along it — a "
                + "vehicle cannot travel sideways, and the buffer cannot describe it.");

            // Only lane changes are judged on span. An ordinary step is whatever the road graph
            // links, and this fixture draws its slip road well off the carriageway; on a real map
            // successors sit one map resolution apart.
            if (!route[i].ReachedByLaneChange) continue;
            float gap = MathF.Sqrt(along * along + across * across);
            Assert.True(gap < 2.0f * 5.0f,
                $"the lane change at step {i} spans {gap:F1} m, far enough past one map resolution "
                + "that readers of the buffer which assume that spacing would be misled.");
        }
    }

    [Fact]
    public void A_vehicle_that_has_changed_lane_still_counts_as_being_on_its_route()
    {
        // Automatic lane change is on by default and empties the horizon buffer, so a routed
        // vehicle overtaking a slow car lands in a lane the route never named. That is not a route
        // departure: the breadcrumbs are one lane over and it drifts back on its own.
        var (inner, outer, _) = BuildCarriagewayWithSlipRoad();
        List<RouteStep>? route = RoutePlanner.Search(inner[0], inner[^1], Budget);
        Assert.NotNull(route);

        PlannedRoute planned = RoutePlanner.Materialize(route!, inner[^1].Location);

        Assert.True(planned.Covers(inner[5]), "a waypoint on the route must count as on it.");
        Assert.True(planned.Covers(outer[5]),
            "the lane alongside the route must count as on it, or every overtake reads as a departure.");

        var elsewhere = Node(99u, 0.0, 1_000.0f, 1_000.0f);
        Assert.False(planned.Covers(elsewhere),
            "an unrelated waypoint must not count as on the route, or a departure is never noticed.");
    }

    // ─────────────────────────────────────────────────────────────────────
    //                    Against a real parsed road graph
    // ─────────────────────────────────────────────────────────────────────

    // One straight 100 m single-lane road: enough for the planner to run end to end over a graph
    // InMemoryMap built, rather than one the test wired up by hand.
    private const string StraightRoadXodr =
        "<?xml version=\"1.0\" standalone=\"yes\"?>\n" +
        "<OpenDRIVE>\n" +
        "  <header revMajor=\"1\" revMinor=\"4\" name=\"\" version=\"1\" north=\"0\" south=\"0\" east=\"0\" west=\"0\"/>\n" +
        "  <road name=\"Road 1\" length=\"100.0\" id=\"1\" junction=\"-1\">\n" +
        "    <planView>\n" +
        "      <geometry s=\"0.0\" x=\"0.0\" y=\"0.0\" hdg=\"0.0\" length=\"100.0\"><line/></geometry>\n" +
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

    [Fact]
    public void Plans_along_a_road_graph_built_from_OpenDRIVE()
    {
        RoadMap worldMap = OpenDriveParser.Load(StraightRoadXodr)
            ?? throw new InvalidOperationException("test .xodr failed to parse");
        var localMap = new InMemoryMap(worldMap);
        localMap.SetUp();

        IReadOnlyList<SimpleWaypoint> topology = localMap.GetDenseTopology();
        Assert.True(topology.Count > 10, "the parsed road produced too few waypoints to plan over.");

        var planner = new RoutePlanner(localMap);
        PlannedRoute? route = planner.Plan(topology[0].Location, topology[^1].Location);

        Assert.NotNull(route);
        Assert.Equal(topology.Count, route!.Path.Count);
        Assert.Equal(topology[^1].Location.X, route.Path[^1].X, 2);
        Assert.Equal(topology[^1].Location.Y, route.Path[^1].Y, 2);
        // A 100 m road sampled every 5 m: the route spans it end to end, not some fragment.
        Assert.InRange(route.LengthMetres, 90.0f, 100.0f);
    }

    [Fact]
    public void Reports_no_route_to_a_destination_off_the_road_network()
    {
        RoadMap worldMap = OpenDriveParser.Load(StraightRoadXodr)
            ?? throw new InvalidOperationException("test .xodr failed to parse");
        var localMap = new InMemoryMap(worldMap);
        localMap.SetUp();

        IReadOnlyList<SimpleWaypoint> topology = localMap.GetDenseTopology();
        var planner = new RoutePlanner(localMap);

        // Both ends snap onto the one road, so the only unreachable pair is back down a one-way lane.
        Assert.Null(planner.Plan(topology[^1].Location, topology[0].Location));
    }
}
