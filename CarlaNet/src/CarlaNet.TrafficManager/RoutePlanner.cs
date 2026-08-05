// Shortest-path search over the dense waypoint graph InMemoryMap builds.
//
// This is the piece that turns a destination into a plan. Without it, a vehicle handed a far-away
// destination is steered toward it a waypoint at a time by LocalizationStage.ImportPath: at every
// junction that walk probes each successor and keeps whichever ends up nearest the destination. That
// is a bearing, not a route — it cannot see past the next junction, so it commits to roads that do
// not lead where the vehicle was sent, and two runs of the same scenario can diverge on a junction
// whose candidates are close to equidistant.
//
// The search here runs A* from the origin waypoint to the destination waypoint before the vehicle is
// spawned, and emits the whole path as a dense list of locations. ImportPath then consumes those
// locations as breadcrumbs — it takes an entry once a candidate lands within ~5.5 m of it and pushes
// the entry itself onto the horizon buffer — so the same greedy code follows the plan instead of
// guessing at it. Each junction choice is constrained by a breadcrumb a few metres away rather than
// by a destination kilometres away.
//
// Determinism: the search has no random input, and ties in the priority queue are broken on the
// waypoint's road / section / lane / s coordinates, which are unique. Two searches over the same map
// between the same endpoints therefore expand nodes in the same order and return the same route,
// whatever order the graph's successor lists happen to be in.
//
// Cost: run on the caller's thread, never on the traffic-manager tick. A search over a large sandbox
// touches every reachable waypoint in the worst case, which is far too much work to sit inside a
// per-tick stage — and the tick holds the registration lock, so a slow tick blocks the thread that
// owns world.tick().
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// A route computed over the dense waypoint graph: the ordered breadcrumbs to hand to
/// <c>SetCustomPath</c>, plus the identity of every waypoint the route passes through so a vehicle
/// can be told whether it is still on the route it was given.
/// </summary>
public sealed class PlannedRoute
{
    /// <summary>
    /// The breadcrumbs, in travel order, ending at the destination waypoint. Spaced one map
    /// resolution apart along the lane centreline.
    /// </summary>
    public IReadOnlyList<Location> Path { get; }

    /// <summary>The destination this route was planned to — the target of any later replan.</summary>
    public Location Destination { get; }

    /// <summary>Distance along the route in metres (the sum of its edge lengths).</summary>
    public float LengthMetres { get; }

    /// <summary>Number of waypoints the route traverses, including any elided by a lane change.</summary>
    public int WaypointCount => _waypoints.Count;

    // Reference identity, not the waypoint id: SimpleWaypoint.Id is a 32-bit hash widened to 64
    // bits, and on a graph with hundreds of thousands of nodes some pair of distinct waypoints will
    // share one. InMemoryMap holds exactly one instance per dense waypoint and every edge points at
    // those instances, so the reference is an exact key.
    private readonly HashSet<SimpleWaypoint> _waypoints;

    internal PlannedRoute(
        IReadOnlyList<Location> path, Location destination, float lengthMetres,
        HashSet<SimpleWaypoint> waypoints)
    {
        Path = path;
        Destination = destination;
        LengthMetres = lengthMetres;
        _waypoints = waypoints;
    }

    /// <summary>
    /// True if <paramref name="waypoint"/> counts as being on this route. A waypoint on the route
    /// obviously qualifies; so does one whose lane-change neighbour is on the route, because a
    /// vehicle that changed lane to pass an obstacle is still following the plan — the breadcrumbs
    /// are one lane over and well inside the radius at which ImportPath consumes them, so it drifts
    /// back on its own. Without that allowance every routine overtake would read as a route
    /// departure.
    /// </summary>
    internal bool Covers(SimpleWaypoint waypoint)
    {
        if (_waypoints.Contains(waypoint)) return true;
        SimpleWaypoint? left = waypoint.GetLeftWaypoint();
        if (left != null && _waypoints.Contains(left)) return true;
        SimpleWaypoint? right = waypoint.GetRightWaypoint();
        if (right != null && _waypoints.Contains(right)) return true;
        return false;
    }
}

/// <summary>
/// A* shortest-path search over <see cref="InMemoryMap"/>'s dense waypoint graph.
/// </summary>
internal sealed class RoutePlanner
{
    /// <summary>
    /// What a lane change adds to the cost of a route, in metres of equivalent driving. Lane-change
    /// edges have to be searchable — a motorway exit usually leaves from the outermost lane only, so
    /// a vehicle in an inner lane has no route to it at all without them. The penalty keeps the
    /// search from weaving between lanes when staying put is just as short: it changes lane when the
    /// destination requires it, or when doing so saves more than this much distance.
    /// </summary>
    internal const float LaneChangeCostMetres = 40.0f;

    /// <summary>
    /// A node is expanded at most once, so a search can never expand more nodes than the graph
    /// holds; this is a backstop against a graph larger than the topology it was derived from, and
    /// it bounds the search's memory the way MaxHorizonWalkSteps bounds the horizon walk's.
    /// </summary>
    private const int ExpansionHeadroom = 1024;

    private readonly InMemoryMap _localMap;

    public RoutePlanner(InMemoryMap localMap)
    {
        _localMap = localMap ?? throw new ArgumentNullException(nameof(localMap));
    }

    /// <summary>
    /// Plan a route from the road position nearest <paramref name="origin"/> to the one nearest
    /// <paramref name="destination"/>. Returns null when the destination is not reachable from the
    /// origin by driving — a one-way network, a lane that only leaves the sandbox, a destination on
    /// a disconnected component.
    /// </summary>
    /// <remarks>
    /// Runs on the calling thread. <see cref="InMemoryMap"/> is immutable once built, so this is
    /// safe to call concurrently with the traffic-manager tick.
    /// </remarks>
    public PlannedRoute? Plan(Location origin, Location destination)
    {
        SimpleWaypoint originWaypoint = _localMap.GetWaypoint(origin);
        SimpleWaypoint destinationWaypoint = _localMap.GetWaypoint(destination);
        int maxExpansions = _localMap.GetDenseTopology().Count + ExpansionHeadroom;

        List<RouteStep>? steps = Search(originWaypoint, destinationWaypoint, maxExpansions);
        if (steps is null) return null;

        return Materialize(steps, destinationWaypoint.Location);
    }

    /// <summary>One waypoint on a route, and whether the search reached it by changing lane.</summary>
    internal readonly record struct RouteStep(SimpleWaypoint Waypoint, bool ReachedByLaneChange);

    /// <summary>
    /// A* from <paramref name="origin"/> to <paramref name="destination"/> over successor and
    /// lane-change edges. Returns the route in travel order (origin first, destination last), or
    /// null if no route exists or the expansion budget ran out.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Plan"/> and taking bare waypoints so it can be driven over a
    /// hand-built graph in a test without an OpenDRIVE map behind it.
    ///
    /// The heuristic is the straight-line distance to the destination. Every edge costs at least its
    /// own straight-line length, so the heuristic never overestimates and A* returns a true shortest
    /// route. Ties are broken on the waypoint's road coordinates so the expansion order does not
    /// depend on the order of the successor lists.
    /// </remarks>
    internal static List<RouteStep>? Search(
        SimpleWaypoint origin, SimpleWaypoint destination, int maxExpansions)
    {
        if (ReferenceEquals(origin, destination))
            return new List<RouteStep> { new(origin, ReachedByLaneChange: false) };

        Location goal = destination.Location;
        var bestCost = new Dictionary<SimpleWaypoint, float> { [origin] = 0.0f };
        var cameFrom = new Dictionary<SimpleWaypoint, RouteStep>();
        var settled = new HashSet<SimpleWaypoint>();
        var frontier = new PriorityQueue<SimpleWaypoint, RouteSearchKey>();
        frontier.Enqueue(origin, KeyFor(origin, Distance(origin.Location, goal)));

        int expansions = 0;
        while (frontier.TryDequeue(out SimpleWaypoint? current, out _))
        {
            // A stale queue entry left behind when a cheaper route to this node was found later.
            if (!settled.Add(current)) continue;

            if (ReferenceEquals(current, destination))
                return Reconstruct(origin, current, cameFrom);

            if (++expansions > maxExpansions) return null;

            float costHere = bestCost[current];

            IReadOnlyList<SimpleWaypoint> nexts = current.GetNextWaypoint();
            for (int i = 0; i < nexts.Count; ++i)
                Relax(current, nexts[i], costHere, extraCost: 0.0f, byLaneChange: false);

            // Lane-change neighbours are only linked between lanes running the same way (InMemoryMap
            // requires a matching lane-id sign), so following one can never plan a wrong-way route.
            SimpleWaypoint? left = current.GetLeftWaypoint();
            if (left != null) Relax(current, left, costHere, LaneChangeCostMetres, byLaneChange: true);
            SimpleWaypoint? right = current.GetRightWaypoint();
            if (right != null) Relax(current, right, costHere, LaneChangeCostMetres, byLaneChange: true);
        }

        return null;

        void Relax(SimpleWaypoint from, SimpleWaypoint to, float costAtFrom, float extraCost, bool byLaneChange)
        {
            if (settled.Contains(to)) return;
            float candidate = costAtFrom + Distance(from.Location, to.Location) + extraCost;
            if (bestCost.TryGetValue(to, out float known) && known <= candidate) return;
            bestCost[to] = candidate;
            cameFrom[to] = new RouteStep(from, byLaneChange);
            frontier.Enqueue(to, KeyFor(to, candidate + Distance(to.Location, goal)));
        }
    }

    /// <summary>
    /// Priority-queue ordering: cheapest estimated total cost first, ties broken on the waypoint's
    /// unique road coordinates. Without the tie-break, two routes of identical cost would be
    /// separated by whatever order the heap happened to hold them in, and the route a vehicle got
    /// would not be reproducible.
    /// </summary>
    private readonly record struct RouteSearchKey(
        float Estimate, RoadId RoadId, SectionId SectionId, LaneId LaneId, double S)
        : IComparable<RouteSearchKey>
    {
        public int CompareTo(RouteSearchKey other)
        {
            int byEstimate = Estimate.CompareTo(other.Estimate);
            if (byEstimate != 0) return byEstimate;
            int byRoad = RoadId.CompareTo(other.RoadId);
            if (byRoad != 0) return byRoad;
            int bySection = SectionId.CompareTo(other.SectionId);
            if (bySection != 0) return bySection;
            int byLane = LaneId.CompareTo(other.LaneId);
            if (byLane != 0) return byLane;
            return S.CompareTo(other.S);
        }
    }

    private static RouteSearchKey KeyFor(SimpleWaypoint waypoint, float estimate)
    {
        var pod = waypoint.Waypoint;
        return new RouteSearchKey(estimate, pod.RoadId, pod.SectionId, pod.LaneId, pod.S);
    }

    /// <summary>
    /// Walk the predecessor links back from the destination and reverse them. The
    /// <see cref="RouteStep.ReachedByLaneChange"/> flag recorded against a node describes the edge
    /// that reached it, so on the way back it moves to the node the edge arrived at rather than the
    /// one it left.
    /// </summary>
    private static List<RouteStep> Reconstruct(
        SimpleWaypoint origin, SimpleWaypoint destination, Dictionary<SimpleWaypoint, RouteStep> cameFrom)
    {
        var reversed = new List<RouteStep>();
        SimpleWaypoint current = destination;
        bool arrivedByLaneChange = false;
        while (!ReferenceEquals(current, origin))
        {
            reversed.Add(new RouteStep(current, arrivedByLaneChange));
            RouteStep predecessor = cameFrom[current];
            current = predecessor.Waypoint;
            arrivedByLaneChange = predecessor.ReachedByLaneChange;
        }
        reversed.Add(new RouteStep(origin, ReachedByLaneChange: false));
        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// Turn a route into the breadcrumb list a vehicle is given.
    /// </summary>
    /// <remarks>
    /// Every waypoint the route passes through is recorded as covered, but the waypoint a lane
    /// change lands on is left out of the breadcrumbs. It sits directly abeam the one before it —
    /// no distance forward, a lane width across — and asking a vehicle to drive between two such
    /// points demands an instantaneous sideways step. Dropping it folds the lane change into the
    /// following breadcrumb, which is one map resolution further down the new lane, so the vehicle
    /// crosses over while still moving forward. The destination is always kept, however it was
    /// reached.
    /// </remarks>
    internal static PlannedRoute Materialize(IReadOnlyList<RouteStep> steps, Location destination)
    {
        var path = new List<Location>(steps.Count);
        var waypoints = new HashSet<SimpleWaypoint>(steps.Count);
        float length = 0.0f;

        for (int i = 0; i < steps.Count; ++i)
        {
            RouteStep step = steps[i];
            waypoints.Add(step.Waypoint);
            if (i > 0) length += Distance(steps[i - 1].Waypoint.Location, step.Waypoint.Location);
            bool isDestination = i == steps.Count - 1;
            if (step.ReachedByLaneChange && !isDestination) continue;
            path.Add(step.Waypoint.Location);
        }

        return new PlannedRoute(path, destination, length, waypoints);
    }

    private static float Distance(Location a, Location b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
