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
        => Plan(_localMap.GetWaypoint(origin), destination);

    /// <summary>
    /// Plan from a waypoint the caller has already resolved, rather than from a position to be
    /// snapped to one.
    /// </summary>
    /// <remarks>
    /// Replanning uses this. A vehicle's position and the head of its horizon buffer are two
    /// different waypoints — the buffer head is wherever the traffic manager last had the vehicle on
    /// the graph, which it tolerates being up to <c>MAX_START_DISTANCE</c> away before re-seeding
    /// it. Planning from the position while judging adherence against the head means the route
    /// routinely fails to contain the very node it is judged by, and the vehicle is declared off its
    /// route on the tick after it was put on one. Anchoring the route to the head makes the two
    /// agree by construction.
    /// </remarks>
    public PlannedRoute? Plan(SimpleWaypoint origin, Location destination)
    {
        SimpleWaypoint destinationWaypoint = _localMap.GetWaypoint(destination);
        int maxExpansions = _localMap.GetDenseTopology().Count + ExpansionHeadroom;

        List<RouteStep>? steps = Search(origin, destinationWaypoint, maxExpansions);
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
    ///
    /// The search state is a waypoint AND whether it was reached by changing lane, because a lane
    /// change is only offered from a node the route drove to. Without that, crossing a wide
    /// carriageway is planned as a run of lane changes at one point along the road — five lanes of a
    /// motorway traversed at the same s, no distance travelled — and a vehicle cannot follow it: the
    /// waypoints either side of that run are metres apart sideways and not at all forward, so the
    /// breadcrumb never comes within the radius at which the horizon walk consumes one. The vehicle
    /// carries on down the lane the route abandoned and is off its route for good. Requiring a
    /// driven step between changes turns the same crossing into a staircase the vehicle can follow.
    /// </remarks>
    internal static List<RouteStep>? Search(
        SimpleWaypoint origin, SimpleWaypoint destination, int maxExpansions)
    {
        if (ReferenceEquals(origin, destination))
            return new List<RouteStep> { new(origin, ReachedByLaneChange: false) };

        Location goal = destination.Location;
        var start = new SearchState(origin, ReachedByLaneChange: false);
        var bestCost = new Dictionary<SearchState, float> { [start] = 0.0f };
        var cameFrom = new Dictionary<SearchState, SearchState>();
        var settled = new HashSet<SearchState>();
        var frontier = new PriorityQueue<SearchState, RouteSearchKey>();
        frontier.Enqueue(start, KeyFor(start, Distance(origin.Location, goal)));

        int expansions = 0;
        while (frontier.TryDequeue(out SearchState current, out _))
        {
            // A stale queue entry left behind when a cheaper route to this state was found later.
            if (!settled.Add(current)) continue;

            if (ReferenceEquals(current.Waypoint, destination))
                return Reconstruct(start, current, cameFrom);

            if (++expansions > maxExpansions) return null;

            float costHere = bestCost[current];

            IReadOnlyList<SimpleWaypoint> nexts = current.Waypoint.GetNextWaypoint();
            for (int i = 0; i < nexts.Count; ++i)
                Relax(current, new SearchState(nexts[i], false), costHere, extraCost: 0.0f);

            // Only from a node the route drove to, so changes cannot stack up over a few metres.
            // Lane-change neighbours are linked only between lanes running the same way (InMemoryMap
            // requires a matching lane-id sign), so following one can never plan a wrong-way route.
            if (current.ReachedByLaneChange) continue;

            RelaxLaneChange(current, current.Waypoint.GetLeftWaypoint(), costHere);
            RelaxLaneChange(current, current.Waypoint.GetRightWaypoint(), costHere);
        }

        return null;

        // A lane change carries the vehicle FORWARD as it carries it across, so the edge lands one
        // waypoint along the adjacent lane rather than directly abeam. An edge to the waypoint
        // abeam describes moving sideways without moving at all, which is not a manoeuvre a vehicle
        // can perform and not a step the horizon walk can follow: the waypoints either side of it
        // are metres apart across the road and not at all along it.
        //
        // Declined where the adjacent lane is inside a junction or forks, because which way it
        // forks is not yet decided and the junction machinery downstream reads the horizon buffer
        // expecting an unbroken run of road.
        void RelaxLaneChange(SearchState from, SimpleWaypoint? neighbour, float costAtFrom)
        {
            if (neighbour is null || neighbour.CheckJunction()) return;
            IReadOnlyList<SimpleWaypoint> onward = neighbour.GetNextWaypoint();
            if (onward.Count != 1) return;
            SimpleWaypoint target = onward[0];
            if (target.CheckJunction()) return;
            Relax(from, new SearchState(target, true), costAtFrom, LaneChangeCostMetres);
        }

        void Relax(SearchState from, SearchState to, float costAtFrom, float extraCost)
        {
            if (settled.Contains(to)) return;
            float candidate = costAtFrom
                            + Distance(from.Waypoint.Location, to.Waypoint.Location)
                            + extraCost;
            if (bestCost.TryGetValue(to, out float known) && known <= candidate) return;
            bestCost[to] = candidate;
            cameFrom[to] = from;
            frontier.Enqueue(to, KeyFor(to, candidate + Distance(to.Waypoint.Location, goal)));
        }
    }

    /// <summary>
    /// A node of the search: a waypoint together with how the route arrived at it. The same waypoint
    /// reached by driving and reached by changing lane are different states, because only the former
    /// may change lane again.
    /// </summary>
    private readonly record struct SearchState(SimpleWaypoint Waypoint, bool ReachedByLaneChange);

    /// <summary>
    /// Priority-queue ordering: cheapest estimated total cost first, ties broken on the waypoint's
    /// unique road coordinates. Without the tie-break, two routes of identical cost would be
    /// separated by whatever order the heap happened to hold them in, and the route a vehicle got
    /// would not be reproducible.
    /// </summary>
    private readonly record struct RouteSearchKey(
        float Estimate, RoadId RoadId, SectionId SectionId, LaneId LaneId, double S, bool ByLaneChange)
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
            int byS = S.CompareTo(other.S);
            if (byS != 0) return byS;
            return ByLaneChange.CompareTo(other.ByLaneChange);
        }
    }

    private static RouteSearchKey KeyFor(SearchState state, float estimate)
    {
        var pod = state.Waypoint.Waypoint;
        return new RouteSearchKey(
            estimate, pod.RoadId, pod.SectionId, pod.LaneId, pod.S, state.ReachedByLaneChange);
    }

    /// <summary>
    /// Walk the predecessor links back from the destination and reverse them. Each state already
    /// carries how the route arrived at it, so nothing has to be shifted along the way.
    /// </summary>
    private static List<RouteStep> Reconstruct(
        SearchState origin, SearchState destination, Dictionary<SearchState, SearchState> cameFrom)
    {
        var reversed = new List<RouteStep>();
        SearchState current = destination;
        while (current != origin)
        {
            reversed.Add(new RouteStep(current.Waypoint, current.ReachedByLaneChange));
            current = cameFrom[current];
        }
        reversed.Add(new RouteStep(origin.Waypoint, ReachedByLaneChange: false));
        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// Turn a route into the breadcrumb list a vehicle is given.
    /// </summary>
    /// <remarks>
    /// Every waypoint the route passes through is emitted, in order. Nothing is left out: each step
    /// — including a lane change, which lands one waypoint along the adjacent lane — is somewhere
    /// the vehicle actually goes, so the breadcrumbs stay a continuous run down the road.
    ///
    /// This matters more than it looks. The horizon walk appends a breadcrumb to the vehicle's
    /// buffer as it consumes it, and everything downstream of that buffer — junction entry and exit
    /// detection, the look-ahead that picks a steering target by index, the collision look-ahead
    /// that reuses that index — reads it as an unbroken run of road at one map resolution per step.
    /// Emitting a route that skips a waypoint puts a gap in that run, and those readers have no way
    /// to notice.
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
