using System.Globalization;
using System.Text;
using CarlaNet.Map.OpenDrive;
using CarlaNet.Map.Road;
using CarlaNet.Map.Road.Element;
using CarlaNet.Map.WorldPackage;
using OpenDriveRoad = CarlaNet.Map.Road.Road;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.CoSim;

/// <summary>
/// The OpenDRIVE road network of one generated world, joined to the SUMO network it was converted
/// alongside, so a vehicle's lane and lane position name the road it is on and where along it.
/// </summary>
/// <remarks>
/// <para><b>Why the road as well as the ground.</b> The ground surface holds one height per cell, and
/// under every bridge deck it is deliberately the ground beneath: the deck carries its own road-mesh
/// collision. The road profile is the only record that knows both levels -- a deck lifted to the
/// photoreal deck and shaped ramp-deck-ramp, a road passing beneath it spanned on a chord -- and the
/// engine builds its road mesh, waypoints and paths from it. So the road a body is on says where it is
/// a structure, and there the body rides its profile: a body on the deck rides the deck and one beneath
/// it rides its own road. At grade the ground is the road a camera sees, across its whole width, and
/// the body rides the ground (<see cref="PoseConverter"/>).</para>
///
/// <para><b>How a lane finds its road.</b> netconvert writes the SUMO edge a road came from as the
/// road's <c>sumoId</c> user data, which places every normal edge with a road of its own.
/// <see cref="RedundantJunctionCollapser"/> then merges a road, its connector and the road after it into
/// one road of three lane sections, keeping only the first edge's <c>sumoId</c>; the edges it absorbed
/// are found through the lane sections' boundaries and the SUMO connections out of each edge in turn --
/// the junction was collapsed because there was only one way through it. A connector is named after an
/// internal edge, but not reliably the lane's own: netconvert names it after the first internal edge
/// whose lanes it draws, draws a split internal edge's two halves end to end on one connector, and draws
/// the lanes of several internal edges on one where their shapes run together. What does say which
/// connector carries an internal lane is the connector's own links, which name the roads it leaves and
/// arrives on: the connector whose links join the road of the lane's incoming edge to the road of its
/// outgoing edge is the lane's, and among several the one it lies on best.</para>
///
/// <para><b>How a lane position finds its s.</b> A lane's length and its road's differ -- the road's
/// reference line is the carriageway's left edge, and a curve is longer on its outside -- so the lane's
/// two ends are projected onto the road once, a lane position is carried between them in proportion,
/// and the position itself is projected onto the reference line near that estimate every tick, at the
/// lane's own offset across the road. The projection decides; the proportion only says where to look.
/// A connector whose reference line swings a lane's width sideways within a few metres -- where a lane
/// is added or dropped -- leaves its outer lanes no single foot on it, and a lane there is carried along
/// the connector in proportion alone (<see cref="RoadMappingKind.ProportionalConnector"/>). The
/// residuals of both, over every lane, are measured as the map is built (<see cref="Mapping"/>).</para>
/// </remarks>
public sealed class RoadSurface
{
    /// <summary>How far apart a lane is sampled when its fit is measured, metres.</summary>
    private const double LaneSampleStepMetres = 2.0;

    /// <summary>
    /// How far past a lane's own stretch of its road its ends may project when the lane is fitted:
    /// enough for a lane end to sit a junction's width from where its road's section begins.
    /// </summary>
    private const double FitMarginMetres = 10.0;

    /// <summary>
    /// How far either side of the proportional estimate a position is searched for along its road,
    /// metres: more than four times the 99th percentile of the along-track residual measured on the
    /// shipped networks. A lane whose own fit strays further is searched further
    /// (<see cref="SlackMarginMetres"/>).
    /// </summary>
    private const double SearchSlackMetres = 12.0;

    /// <summary>
    /// How much further than its own worst measured along-track residual a lane is searched, metres:
    /// a body's origin sits a few metres behind the bumper the lane position is of.
    /// </summary>
    private const double SlackMarginMetres = 4.0;

    /// <summary>The step ahead along the heading over which the slope's sign and scale are read, metres.</summary>
    private const double HeadingStepMetres = 1.0;

    /// <summary>
    /// How far any sample of a lane may lie off the road it is fitted to -- past one of its ends, or
    /// outside its paved width -- and the lane still be on that road, metres.
    /// </summary>
    private const double OnTheRoadToleranceMetres = 3.0;

    /// <summary>
    /// How far one sample of a lane may project behind the one before it along the road, metres, before
    /// the lane is taken to have no well-defined projection onto the road. Two metres apart, consecutive
    /// samples of a lane that does project advance by about that much.
    /// </summary>
    private const double BackwardsToleranceMetres = 0.5;

    /// <summary>
    /// How much of the stretch of connector its links say it covers a lane's projection must cover. A lane
    /// runs from the road before a connector to the road after it, so a projection that crowds the lane
    /// into a fraction of the connector -- a turnaround around a reference line of almost no radius, or
    /// the outer lanes of one swinging sideways -- is not the lane's position along it.
    /// </summary>
    private const double MinimumCoverage = 0.5;

    /// <summary>
    /// How far a position may sit from its lane's mean offset across the road and still be on that road,
    /// metres. A lane change puts a body at most half a lane width from the lane it is carried on, and a
    /// lane's own offset varies along a connector by up to a few metres where the connector's drawn shape
    /// and the lane's part company; a position further than this is somewhere the road's profile does not
    /// describe.
    /// </summary>
    public const double OffTheRoadMetres = 5.0;

    /// <summary>
    /// How far into the carriageway sections either side of a merge's absorbed connector its ground weight
    /// is carried from, metres.
    /// </summary>
    private const double MergedConnectorMarginMetres = 2.0;

    /// <summary>
    /// The byte-order mark netconvert writes, which survives a decode that does not strip it and makes
    /// the parser reject the document at its first character.
    /// </summary>
    private const char ByteOrderMark = (char)0xFEFF;

    private readonly Dictionary<string, LaneSpan> _spans;
    private readonly Dictionary<string, LaneSpan[]> _predecessors;
    private readonly Dictionary<string, LaneSpan[]> _successors;

    private RoadSurface(Dictionary<string, LaneSpan> spans,
                        Dictionary<string, LaneSpan[]> predecessors,
                        Dictionary<string, LaneSpan[]> successors,
                        RoadMappingSummary mapping,
                        IReadOnlyDictionary<uint, RoadProfile> roads)
    {
        _spans = spans;
        _predecessors = predecessors;
        _successors = successors;
        Mapping = mapping;
        Roads = roads;
    }

    /// <summary>How much of the SUMO network was joined to a road, and how closely.</summary>
    public RoadMappingSummary Mapping { get; }

    /// <summary>Every road with geometry to project onto, by OpenDRIVE id.</summary>
    internal IReadOnlyDictionary<uint, RoadProfile> Roads { get; }

    /// <summary>Read and join a world package's OpenDRIVE and SUMO network.</summary>
    public static RoadSurface FromWorldPackage(string packagePath, SumoRoadNetwork network)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        return Build(WorldPackage.ReadOpenDrive(packagePath), network);
    }

    /// <summary>
    /// Join an OpenDRIVE document to the SUMO network converted beside it. A document with no roads --
    /// or one that does not parse -- joins nothing, and every vehicle is then seated on the ground.
    /// </summary>
    public static RoadSurface Build(string openDriveXml, SumoRoadNetwork network)
    {
        ArgumentNullException.ThrowIfNull(openDriveXml);
        ArgumentNullException.ThrowIfNull(network);
        return new Builder(OpenDriveParser.Load(openDriveXml.TrimStart(ByteOrderMark)), network).Build();
    }

    /// <summary>
    /// The road a lane was joined to and the s its two ends project to, or false for a lane joined to
    /// none.
    /// </summary>
    public bool TryGetLaneRoad(string laneId, out uint roadId, out double startS, out double endS)
    {
        if (_spans.TryGetValue(laneId, out LaneSpan? span))
        {
            (roadId, startS, endS) = (span.Road.Id, span.StartS, span.EndS);
            return true;
        }

        (roadId, startS, endS) = (0u, 0.0, 0.0);
        return false;
    }

    /// <summary>
    /// The road profile under a position on a lane: its height, and its slope along a heading.
    /// </summary>
    /// <param name="laneId">The lane the front bumper is on; empty where SUMO reports none.</param>
    /// <param name="lanePositionMetres">How far along that lane the front bumper is.</param>
    /// <param name="x">Easting of the point to seat -- the body's origin -- in the SUMO frame.</param>
    /// <param name="y">Northing of the same point.</param>
    /// <param name="forwardX">The heading's easting component, a unit vector in the SUMO frame.</param>
    /// <param name="forwardY">The heading's northing component.</param>
    /// <param name="behindBumperMetres">How far behind the front bumper the point is, along the heading.</param>
    /// <param name="seat">
    /// The road, the s the point projects to, and the profile there; how the seat is shared with the
    /// ground is left for the caller.
    /// </param>
    /// <param name="reason">Why there is no road under the point, where there is none.</param>
    /// <remarks>
    /// <para>The origin of a body is behind its bumper, so for a moment after the bumper crosses onto a
    /// new road the origin is still on the one before. Where the projection onto the lane's road falls
    /// off that road's start, the roads of the lanes leading into the lane are tried, and the one the
    /// point lies on best is taken -- inside its length, at its lane's offset across it; joined roads
    /// meet at one height, so those that overlap at a junction's mouth agree there. A road shorter than
    /// its lane -- a connector netconvert drew a few centimetres long -- can leave a point off its far end
    /// as well, and the roads of the lanes it leads to are tried the same way. A point off a road no lane
    /// leads into, or out of, is carried on from the road's end at its slope.</para>
    ///
    /// <para>The slope is the profile's slope at s times how far s advances per metre travelled along
    /// the heading, read by projecting a point a metre ahead: plus one for a body driving along +s on the
    /// reference line, minus one against it, and more than one on the inside of a curve.</para>
    /// </remarks>
    internal bool TrySeat(string laneId, double lanePositionMetres, double x, double y,
                          double forwardX, double forwardY, double behindBumperMetres,
                          out RoadPlace seat, out GroundSeatReason reason)
    {
        seat = default;
        if (laneId.Length == 0)
        {
            reason = GroundSeatReason.NoLane;
            return false;
        }

        if (!_spans.TryGetValue(laneId, out LaneSpan? span))
        {
            reason = GroundSeatReason.NoRoad;
            return false;
        }

        double expected = span.SAt(lanePositionMetres) - (span.Scale * behindBumperMetres);
        RoadProjection at = span.Locate(x, y, expected);
        LaneSpan used = span;
        double offStart = span.Direction > 0.0 ? -at.S : at.S - span.Road.Length;
        double offEnd = span.Direction > 0.0 ? at.S - span.Road.Length : -at.S;
        LaneSpan[]? neighbours = null;
        bool before = offStart > 0.0 && _predecessors.TryGetValue(laneId, out neighbours);
        if (before || (offEnd > 0.0 && _successors.TryGetValue(laneId, out neighbours)))
        {
            double off = before ? offStart : offEnd;
            double bestResidual = used.Residual(at);
            foreach (LaneSpan neighbour in neighbours!)
            {
                double guess = before
                    ? neighbour.EndS - (neighbour.Direction * off)
                    : neighbour.StartS + (neighbour.Direction * off);
                RoadProjection there = neighbour.Locate(x, y, guess);
                double residual = neighbour.Residual(there);
                if (residual < bestResidual)
                {
                    bestResidual = residual;
                    used = neighbour;
                    at = there;
                }
            }
        }

        if (Math.Abs(at.Lateral - used.MeanLateral) > OffTheRoadMetres)
        {
            reason = GroundSeatReason.OffTheRoad;
            return false;
        }

        (double z, double slope) = used.Road.Elevation(at.S);
        double alongPerMetre = used.Scale;
        if (used.Projected)
        {
            RoadProjection ahead = used.Road.Project(x + (forwardX * HeadingStepMetres),
                                                     y + (forwardY * HeadingStepMetres),
                                                     at.S - (2.0 * HeadingStepMetres),
                                                     at.S + (2.0 * HeadingStepMetres),
                                                     at.Lateral);
            alongPerMetre = (ahead.S - at.S) / HeadingStepMetres;
        }

        seat = new RoadPlace(used.Road, at.S, z, slope, alongPerMetre);
        reason = GroundSeatReason.None;
        return true;
    }

    /// <summary>One lane's stretch of the road it was joined to.</summary>
    private sealed class LaneSpan(RoadProfile road, double startS, double endS, double declaredLength,
                                  double meanLateral, double slack, bool projected = true)
    {
        public RoadProfile Road { get; } = road;

        /// <summary>
        /// Whether a position on the lane is projected onto the road, or only carried along it in
        /// proportion: the lane lies on a connector whose drawn shape it cannot be projected onto.
        /// </summary>
        public bool Projected { get; } = projected;

        /// <summary>How far either side of the proportional estimate a position on the lane is searched for.</summary>
        public double Slack { get; } = slack;

        /// <summary>Where the lane's start projects onto the road.</summary>
        public double StartS { get; } = startS;

        /// <summary>Where the lane's end projects onto the road.</summary>
        public double EndS { get; } = endS;

        /// <summary>The lane's mean offset across the road from its reference line.</summary>
        public double MeanLateral { get; } = meanLateral;

        /// <summary>Plus one where the lane runs along +s, minus one against it.</summary>
        public double Direction { get; } = endS >= startS ? 1.0 : -1.0;

        /// <summary>How far s moves per metre of lane position.</summary>
        public double Scale { get; } =
            declaredLength > 0.0 ? (endS - startS) / declaredLength : (endS >= startS ? 1.0 : -1.0);

        /// <summary>The s a lane position is carried to in proportion.</summary>
        public double SAt(double lanePositionMetres) => StartS + (lanePositionMetres * Scale);

        /// <summary>
        /// Where a position near an estimated s lies on the road: projected at the lane's offset across it
        /// within the lane's search slack, or, for a lane carried in proportion, the estimate itself.
        /// </summary>
        public RoadProjection Locate(double x, double y, double estimate) =>
            Projected
                ? Road.Project(x, y, estimate - Slack, estimate + Slack, MeanLateral)
                : new RoadProjection(estimate, MeanLateral, 0.0);

        /// <summary>
        /// How far a projection lies from this lane on its road: off the road's ends along it, and off
        /// the lane's offset across it.
        /// </summary>
        public double Residual(in RoadProjection projection)
        {
            double off = Math.Max(0.0, Math.Max(-projection.S, projection.S - Road.Length));
            double across = projection.Lateral - MeanLateral;
            return Math.Sqrt((off * off) + (across * across));
        }
    }

    /// <summary>The join, made once.</summary>
    private sealed class Builder
    {
        private readonly RoadMap? _map;
        private readonly SumoRoadNetwork _network;
        private readonly Dictionary<uint, RoadProfile> _roads = [];
        private readonly Dictionary<string, LaneSpan> _spans = [];
        private readonly Dictionary<string, RoadMappingKind> _kinds = [];
        private readonly Dictionary<string, List<string>> _predecessorLanes = [];
        private readonly Dictionary<string, string> _firstHalfOf = [];
        private readonly Dictionary<string, string> _secondHalfOf = [];
        private readonly List<double> _along = [];
        private readonly List<double> _lateral = [];

        public Builder(RoadMap? map, SumoRoadNetwork network)
        {
            _map = map;
            _network = network;
        }

        public RoadSurface Build()
        {
            Dictionary<string, RoadProfile> bySumoId = [];
            Dictionary<string, List<RoadProfile>> byName = [];
            Dictionary<string, List<RoadProfile>> byJunction = [];
            foreach (OpenDriveRoad road in _map?.Roads.Values ?? [])
            {
                if (RoadProfile.From(road) is not { } profile)
                {
                    continue;
                }

                _roads[road.Id] = profile;
                if (!road.IsJunction && road.UserData.TryGetValue("sumoId", out string? edge)
                    && edge.Length > 0)
                {
                    bySumoId.TryAdd(edge, profile);
                }
                else if (road.IsJunction && road.Name.Length > 0)
                {
                    Index(byName, road.Name, profile);
                    if (JunctionOf(road.Name) is { } junction)
                    {
                        Index(byJunction, junction, profile);
                    }
                }
            }

            // Every road that carries its edge, and the edges a merge absorbed into it.
            foreach ((string edge, RoadProfile road) in bySumoId.OrderBy(entry => entry.Value.Id))
            {
                JoinMergedRoad(edge, road);
            }

            List<SumoLane> internalLanes = [.. _network.Lanes.Where(lane => lane.IsInternal)
                                                          .OrderBy(lane => lane.Id, StringComparer.Ordinal)];
            IndexLanes(internalLanes);

            // Every internal lane on the connector whose own links name the roads the lane's traffic comes
            // from and goes to. A connector's links are what the engine routes through, so they say which
            // connector a lane is carried by even where its name does not -- netconvert names a connector
            // after the first internal edge whose lanes it draws -- and where its drawn shape cannot be
            // projected onto.
            Dictionary<(uint From, uint To), List<RoadProfile>> between = [];
            foreach (RoadProfile road in _roads.Values.Where(road => road.IsJunction).OrderBy(road => road.Id))
            {
                (uint, uint) key = (road.Road.PredecessorRoadId, road.Road.SuccessorRoadId);
                if (!between.TryGetValue(key, out List<RoadProfile>? connectors))
                {
                    connectors = [];
                    between[key] = connectors;
                }

                connectors.Add(road);
            }

            foreach (SumoLane lane in internalLanes)
            {
                if (!_spans.ContainsKey(lane.Id)
                    && RoadOfEdge(IncomingEdge(lane.Id, 0)) is { } from
                    && RoadOfEdge(OutgoingEdge(lane.Id, 0)) is { } to
                    && between.TryGetValue((from, to), out List<RoadProfile>? connectors))
                {
                    JoinConnector(lane, connectors);
                }
            }

            // What the links do not place -- a connector whose links the build left pointing elsewhere --
            // by the connector's name, and then by the other connectors of its junction, on their shapes
            // alone.
            foreach (SumoLane lane in internalLanes)
            {
                if (!_spans.ContainsKey(lane.Id) && byName.TryGetValue(lane.EdgeId, out List<RoadProfile>? named))
                {
                    JoinBestOf(lane, named, PortionOf(lane), RoadMappingKind.Connector);
                }
            }

            foreach (SumoLane lane in internalLanes)
            {
                if (!_spans.ContainsKey(lane.Id) && JunctionOf(lane.EdgeId) is { } junction
                    && byJunction.TryGetValue(junction, out List<RoadProfile>? siblings))
                {
                    JoinBestOf(lane, siblings, PortionOf(lane), RoadMappingKind.JunctionConnector);
                }
            }

            foreach (RoadProfile connector in _roads.Values.Where(road => road.IsJunction))
            {
                CarryAcross(connector);
            }

            (Dictionary<string, LaneSpan[]> predecessors, Dictionary<string, LaneSpan[]> successors) = Neighbours();
            return new RoadSurface(_spans, predecessors, successors, Summarise(), _roads);

            static void Index(Dictionary<string, List<RoadProfile>> index, string key, RoadProfile road)
            {
                if (!index.TryGetValue(key, out List<RoadProfile>? roads))
                {
                    roads = [];
                    index[key] = roads;
                }

                roads.Add(road);
            }
        }

        /// <summary>
        /// The SUMO junction an internal edge belongs to: its id between the leading colon and the last
        /// underscore, which is how SUMO names an internal edge and netconvert the connector drawn from it.
        /// </summary>
        private static string? JunctionOf(string internalEdge)
        {
            int underscore = internalEdge.LastIndexOf('_');
            return internalEdge.Length > 1 && internalEdge[0] == ':' && underscore > 1
                ? internalEdge[1..underscore]
                : null;
        }

        /// <summary>
        /// Index the internal lanes' halves and every lane's predecessors, which the walk to the edges an
        /// internal lane joins reads.
        /// </summary>
        private void IndexLanes(List<SumoLane> internalLanes)
        {
            foreach (SumoLane lane in _network.Lanes)
            {
                foreach (LaneLink link in _network.SuccessorsOf(lane.Id))
                {
                    string next = _network.TryGetLane(link.NextLaneId, out _) ? link.NextLaneId : link.ToLaneId;
                    if (!_predecessorLanes.TryGetValue(next, out List<string>? before))
                    {
                        before = [];
                        _predecessorLanes[next] = before;
                    }

                    if (!before.Contains(lane.Id))
                    {
                        before.Add(lane.Id);
                    }

                    if (lane.IsInternal && link.ViaLaneId.Length > 0)
                    {
                        _firstHalfOf.TryAdd(link.ViaLaneId, lane.Id);
                        _secondHalfOf.TryAdd(lane.Id, link.ViaLaneId);
                    }
                }
            }
        }

        /// <summary>The normal edge an internal lane's traffic comes from, through any first half.</summary>
        private string? IncomingEdge(string laneId, int depth)
        {
            if (depth > 4 || !_predecessorLanes.TryGetValue(laneId, out List<string>? before))
            {
                return null;
            }

            foreach (string previous in before)
            {
                if (_network.TryGetLane(previous, out SumoLane lane) && !lane.IsInternal)
                {
                    return lane.EdgeId;
                }
            }

            return before.Select(previous => IncomingEdge(previous, depth + 1)).FirstOrDefault(edge => edge is not null);
        }

        /// <summary>The normal edge an internal lane's traffic goes to, through any second half.</summary>
        private string? OutgoingEdge(string laneId, int depth)
        {
            if (depth > 4)
            {
                return null;
            }

            List<string> onward = [];
            foreach (LaneLink link in _network.SuccessorsOf(laneId))
            {
                string next = _network.TryGetLane(link.NextLaneId, out _) ? link.NextLaneId : link.ToLaneId;
                if (_network.TryGetLane(next, out SumoLane lane) && !lane.IsInternal)
                {
                    return lane.EdgeId;
                }

                onward.Add(next);
            }

            return onward.Select(next => OutgoingEdge(next, depth + 1)).FirstOrDefault(edge => edge is not null);
        }

        /// <summary>The road a normal edge's lanes were joined to, or null for an edge joined to none.</summary>
        private uint? RoadOfEdge(string? edge)
        {
            if (edge is null)
            {
                return null;
            }

            foreach (SumoLane lane in _network.LanesOfEdge(edge))
            {
                if (_spans.TryGetValue(lane.Id, out LaneSpan? span))
                {
                    return span.Road.Id;
                }
            }

            return null;
        }

        /// <summary>
        /// Which part of its connector an internal lane covers, as fractions of the connector's length: the
        /// whole of it, or -- for a split internal edge, whose halves one connector draws end to end -- the
        /// first or second part, in proportion to the halves' lengths.
        /// </summary>
        private (double From, double To) PortionOf(SumoLane lane)
        {
            if (_secondHalfOf.TryGetValue(lane.Id, out string? second)
                && _network.TryGetLane(second, out SumoLane after))
            {
                return (0.0, Share(lane, after));
            }

            if (_firstHalfOf.TryGetValue(lane.Id, out string? first)
                && _network.TryGetLane(first, out SumoLane before))
            {
                return (Share(before, lane), 1.0);
            }

            return (0.0, 1.0);

            static double Share(SumoLane head, SumoLane tail)
            {
                double total = head.DeclaredLengthMetres + tail.DeclaredLengthMetres;
                return total > 0.0 ? head.DeclaredLengthMetres / total : 0.5;
            }
        }

        /// <summary>
        /// Join an internal lane to the connector its links name: the one whose drawn shape it lies on best,
        /// or, where it cannot be projected onto any of them, the one named after its edge, carried along it
        /// in proportion.
        /// </summary>
        private void JoinConnector(SumoLane lane, List<RoadProfile> connectors)
        {
            (double from, double to) = PortionOf(lane);
            if (JoinBestOf(lane, connectors, (from, to), RoadMappingKind.Connector, MinimumCoverage))
            {
                return;
            }

            RoadProfile road = connectors.FirstOrDefault(each => each.Name == lane.EdgeId) ?? connectors[0];
            KeepProportional(lane, road, from * road.Length, to * road.Length,
                             RoadMappingKind.ProportionalConnector);
        }

        /// <summary>Join a lane to a stretch of road it is carried along in proportion, not projected onto.</summary>
        private void KeepProportional(SumoLane lane, RoadProfile road, double fromS, double toS,
                                      RoadMappingKind kind)
        {
            _spans[lane.Id] = new LaneSpan(road, fromS, toS, lane.DeclaredLengthMetres, 0.0, 0.0, projected: false);
            _kinds[lane.Id] = kind;
        }

        /// <summary>
        /// Record the carriageways a junction connector's links join, and the end of each it meets, so its
        /// ground weight can be made to meet theirs (<see cref="CarriedWeight"/>).
        /// </summary>
        /// <remarks>
        /// netconvert draws a connector's reference line along the left edge of the connection it was
        /// drawn from, which need not be where the road it joins draws its own: measured on Arapahoe, the
        /// two stand a lane width or more apart at one joint in ten, and as far as 20 m. Under one the
        /// ground can be a kerb or a raised median the other clears, so the two departures read at one
        /// joint can differ by a metre, and weighed by them alone a body's seat stepped there by up to half
        /// a metre. A link to no carriageway -- none, or another connector -- leaves the connector's own
        /// weight at that end.
        /// </remarks>
        private void CarryAcross(RoadProfile connector)
        {
            (RoadProfile before, double beforeS) = JoinedAt(connector, connector.Road.PredecessorRoadId,
                                                            connector.Road.PredecessorContactPoint, 0.0);
            (RoadProfile after, double afterS) = JoinedAt(connector, connector.Road.SuccessorRoadId,
                                                          connector.Road.SuccessorContactPoint, connector.Length);
            if (!ReferenceEquals(before, connector) || !ReferenceEquals(after, connector))
            {
                connector.CarryWeight(new CarriedWeight(0.0, connector.Length, before, beforeS, after, afterS));
            }
        }

        /// <summary>
        /// The carriageway a connector's link names and the end of it the link's contact point names --
        /// or, where it names none, the end nearer the connector's own end at <paramref name="atS"/>; the
        /// connector itself, at that end, where the link names no carriageway.
        /// </summary>
        private (RoadProfile Road, double S) JoinedAt(RoadProfile connector, uint linkedRoadId,
                                                      string contactPoint, double atS)
        {
            if (!_roads.TryGetValue(linkedRoadId, out RoadProfile? linked) || linked.IsJunction)
            {
                return (connector, atS);
            }

            if (contactPoint is "start" or "end")
            {
                return (linked, contactPoint == "start" ? 0.0 : linked.Length);
            }

            (double x, double y) = connector.ReferencePoint(atS);
            (double startX, double startY) = linked.ReferencePoint(0.0);
            (double endX, double endY) = linked.ReferencePoint(linked.Length);
            double toStart = ((startX - x) * (startX - x)) + ((startY - y) * (startY - y));
            double toEnd = ((endX - x) * (endX - x)) + ((endY - y) * (endY - y));
            return (linked, toStart <= toEnd ? 0.0 : linked.Length);
        }

        /// <summary>
        /// Join the edge a road carries to its first lane section, and walk the sections a merge
        /// appended -- a connector, then the next edge, in turn -- along the SUMO connections.
        /// </summary>
        private void JoinMergedRoad(string edge, RoadProfile road)
        {
            List<LaneSection> sections = [.. road.Road.LaneSections.OrderBy(section => section.S)];
            double SectionEnd(int index) => index + 1 < sections.Count ? sections[index + 1].S : road.Length;
            double SectionStart(int index) => sections.Count == 0 ? 0.0 : sections[index].S;

            JoinEdge(edge, road, SectionStart(0), SectionEnd(0), RoadMappingKind.EdgeRoad);
            string current = edge;
            for (int index = 1; index + 1 < sections.Count; index += 2)
            {
                if (!FollowThrough(current, road, SectionStart(index + 1), SectionEnd(index + 1),
                                   out string? connector, out string next))
                {
                    break;
                }

                if (connector is not null)
                {
                    JoinEdge(connector, road, SectionStart(index), SectionEnd(index),
                             RoadMappingKind.MergedConnector);
                }

                // The absorbed connector is drawn as its junction drew it, and weighed as one: across it,
                // from the carriageway section before to the one after, read a little way into each --
                // round a collapsed dead end's turnaround the reference line is still swinging back
                // across the street for the first half-metre of the way back (measured on Bahonar).
                double from = Math.Max(0.0, SectionStart(index) - MergedConnectorMarginMetres);
                double to = Math.Min(road.Length, SectionEnd(index) + MergedConnectorMarginMetres);
                road.CarryWeight(new CarriedWeight(from, to, road, from, road, to));

                JoinEdge(next, road, SectionStart(index + 1), SectionEnd(index + 1),
                         RoadMappingKind.MergedEdge);
                current = next;
            }
        }

        /// <summary>
        /// The internal edge and the edge after it that the SUMO connections out of an edge lead to,
        /// where the next section of the merged road begins.
        /// </summary>
        /// <remarks>
        /// A merge only ever collapsed a junction with one way through it, so the edge's connections
        /// lead to one edge. Should they lead to more, the one whose lanes begin on the next section is
        /// taken.
        /// </remarks>
        private bool FollowThrough(string edge, RoadProfile road, double nextStart, double nextEnd,
                                   out string? connector, out string next)
        {
            Dictionary<string, string?> reached = [];
            foreach (SumoLane lane in _network.LanesOfEdge(edge))
            {
                foreach (LaneLink link in _network.SuccessorsOf(lane.Id))
                {
                    if (!_network.TryGetLane(link.ToLaneId, out SumoLane to) || to.IsInternal)
                    {
                        continue;
                    }

                    string? via = link.ViaLaneId.Length > 0 && _network.TryGetLane(link.ViaLaneId, out SumoLane viaLane)
                        ? viaLane.EdgeId
                        : null;
                    if (!reached.TryGetValue(to.EdgeId, out string? known) || known is null)
                    {
                        reached[to.EdgeId] = via;
                    }
                }
            }

            connector = null;
            next = string.Empty;
            double nearest = double.PositiveInfinity;
            foreach ((string candidate, string? via) in reached)
            {
                IReadOnlyList<SumoLane> lanes = _network.LanesOfEdge(candidate);
                if (lanes.Count == 0)
                {
                    continue;
                }

                (double x, double y, _, _) = lanes[0].PointAt(0.0);
                RoadProjection start = road.Project(x, y, nextStart - FitMarginMetres, nextEnd);
                double distance = Math.Sqrt(start.DistanceSquared) + Math.Abs(start.S - nextStart);
                if (distance < nearest)
                {
                    nearest = distance;
                    next = candidate;
                    connector = via;
                }
            }

            return next.Length > 0;
        }

        /// <summary>
        /// Join an edge's lanes to a stretch of a road. A merged connector's lanes the stretch cannot be
        /// projected onto -- the turnaround of a collapsed dead end, around a reference line of almost no
        /// radius -- are carried along it in proportion: the merge says the lane is that section.
        /// </summary>
        private void JoinEdge(string edge, RoadProfile road, double fromS, double toS, RoadMappingKind kind)
        {
            foreach (SumoLane lane in _network.LanesOfEdge(edge))
            {
                if (_spans.ContainsKey(lane.Id))
                {
                    continue;
                }

                if (Fit(lane, road, fromS, toS) is { } fit
                    && (kind != RoadMappingKind.MergedConnector
                        || Math.Abs(fit.Span.EndS - fit.Span.StartS) >= MinimumCoverage * (toS - fromS)))
                {
                    Keep(lane, fit, kind);
                }
                else if (kind == RoadMappingKind.MergedConnector)
                {
                    KeepProportional(lane, road, fromS, toS, kind);
                }
            }
        }

        /// <summary>
        /// Join an internal lane to whichever of several connectors it lies on best, over the portion of
        /// it the lane covers; false where it lies on none of them.
        /// </summary>
        private bool JoinBestOf(SumoLane lane, List<RoadProfile> candidates, (double From, double To) portion,
                                RoadMappingKind kind, double minimumCoverage = 0.0)
        {
            LaneFit? best = null;
            foreach (RoadProfile road in candidates)
            {
                double fromS = portion.From > 0.0 ? (portion.From * road.Length) - FitMarginMetres : 0.0;
                double toS = portion.To < 1.0 ? (portion.To * road.Length) + FitMarginMetres : road.Length;
                double expected = (portion.To - portion.From) * road.Length;
                if (Fit(lane, road, Math.Max(0.0, fromS), Math.Min(road.Length, toS)) is { } fit
                    && Math.Abs(fit.Span.EndS - fit.Span.StartS) >= minimumCoverage * expected
                    && (best is null || fit.RmsLateralResidual < best.RmsLateralResidual))
                {
                    best = fit;
                }
            }

            if (best is null)
            {
                return false;
            }

            Keep(lane, best, kind == RoadMappingKind.Connector && _firstHalfOf.ContainsKey(lane.Id)
                ? RoadMappingKind.SecondHalf
                : kind);
            return true;
        }

        private void Keep(SumoLane lane, LaneFit fit, RoadMappingKind kind)
        {
            _spans[lane.Id] = fit.Span;
            _kinds[lane.Id] = kind;
            _along.AddRange(fit.Along);
            _lateral.AddRange(fit.Lateral);
        }

        /// <summary>
        /// Fit a lane onto a stretch of a road: where its ends project, its mean offset across the road,
        /// and how far each sample of it lies from where the per-tick search would find it and from the
        /// nearest lane centre of the road.
        /// </summary>
        /// <remarks>
        /// Two passes. The first projects the lane's ends and samples onto the stretch alone -- a merged
        /// road's own section, inside which nothing lies beside the lane but its own carriageway -- for the
        /// s range and the lane's offset across the road. The second searches each sample exactly as a tick
        /// does, near its proportional estimate and at that offset, so the residuals measured are the
        /// ones the seating will have.
        /// </remarks>
        private static LaneFit? Fit(SumoLane lane, RoadProfile road, double fromS, double toS)
        {
            double declared = lane.DeclaredLengthMetres;
            (double startX, double startY, _, _) = lane.PointAt(0.0);
            (double endX, double endY, _, _) = lane.PointAt(declared);
            RoadProjection start = road.Project(startX, startY, fromS, toS);
            RoadProjection end = road.Project(endX, endY, fromS, toS);
            if (double.IsNaN(start.S) || double.IsNaN(end.S))
            {
                return null;
            }

            double startS = Math.Clamp(start.S, 0.0, road.Length);
            double endS = Math.Clamp(end.S, 0.0, road.Length);
            if (Math.Abs(endS - startS) < 1e-6 && declared > 0.5)
            {
                return null;
            }

            int samples = Math.Max(1, (int)Math.Ceiling(declared / LaneSampleStepMetres));
            var offsets = new double[samples + 1];
            for (int index = 0; index <= samples; index++)
            {
                (double x, double y, _, _) = lane.PointAt(declared * index / samples);
                offsets[index] = road.Project(x, y, fromS, toS).Lateral;
            }

            double[] ordered = [.. offsets.OrderBy(value => value)];
            double typical = ordered[ordered.Length / 2];

            // The lane's own stretch, inside which its width is the road's: a lane that ends where its road
            // narrows -- the lanes of an edge merging at a junction -- has a last sample a few centimetres
            // into the narrower section.
            double stretchFrom = Math.Max(0.0, Math.Min(fromS, toS));
            double stretchTo = Math.Max(stretchFrom, Math.Min(road.Length, Math.Max(fromS, toS)) - 1e-6);
            double wide = SearchSlackMetres + declared;
            var along = new double[samples + 1];
            var lateral = new double[samples + 1];
            double offsetTotal = 0.0;
            double squared = 0.0;
            double worstAlong = 0.0;
            double direction = endS >= startS ? 1.0 : -1.0;
            double previous = double.NaN;
            for (int index = 0; index <= samples; index++)
            {
                double position = declared * index / samples;
                (double x, double y, _, _) = lane.PointAt(position);
                double estimate = startS + ((endS - startS) * (declared > 0.0 ? position / declared : 0.0));
                RoadProjection there = road.Project(x, y, estimate - wide, estimate + wide, typical);

                // A lane further across the road than its reference line's radius of curvature -- the
                // outer lanes of a connector whose reference line swings a lane's width sideways within a
                // few metres -- has no single foot on it, and its projections run backwards along the road.
                if (!double.IsNaN(previous) && declared >= LaneSampleStepMetres
                    && (there.S - previous) * direction < -BackwardsToleranceMetres)
                {
                    return null;
                }

                previous = there.S;
                double s = Math.Clamp(there.S, 0.0, road.Length);
                (double nearest, double outside) = AcrossTheRoad(road.Road, Math.Clamp(s, stretchFrom, stretchTo),
                                                                 there.Lateral);

                // Off the road altogether -- past one of its ends, or beside its paved width -- by more
                // than a lane's own misfit: a connector netconvert drew a few centimetres long under a
                // turn of several metres, or the connector of another turn of the same name.
                double off = there.S - s;
                if (Math.Sqrt((off * off) + (outside * outside)) > OnTheRoadToleranceMetres)
                {
                    return null;
                }

                along[index] = there.S - estimate;
                worstAlong = Math.Max(worstAlong, Math.Abs(along[index]));
                lateral[index] = nearest;
                offsetTotal += there.Lateral;
                squared += nearest * nearest;
            }

            // Searched per tick as widely as this lane needs: the common slack, or more for a lane whose
            // proportional estimate strays further -- one drawn over a connector of another length.
            var span = new LaneSpan(road, startS, endS, declared, offsetTotal / (samples + 1),
                                    Math.Max(SearchSlackMetres, worstAlong + SlackMarginMetres));
            return new LaneFit(span, along, lateral, Math.Sqrt(squared / (samples + 1)));
        }

        /// <summary>
        /// Where an offset across a road lies against the road's lanes at s: how far from the centre of
        /// the nearest of them, and how far outside the road's paved width altogether.
        /// </summary>
        private static (double NearestCentre, double Outside) AcrossTheRoad(OpenDriveRoad road, double s,
                                                                            double lateral)
        {
            LaneSection? section = null;
            foreach (LaneSection candidate in road.LaneSections.OrderBy(each => each.S))
            {
                if (section is null || candidate.S <= s + 1e-9)
                {
                    section = candidate;
                }
            }

            if (section is null)
            {
                return (double.PositiveInfinity, double.PositiveInfinity);
            }

            double nearest = double.PositiveInfinity;
            double right = 0.0;
            double left = 0.0;
            foreach (int side in new[] { -1, 1 })
            {
                double edge = 0.0;
                for (int id = side; section.Lanes.TryGetValue(id, out Lane? lane); id += side)
                {
                    double width = lane.GetInfoAt<RoadInfoLaneWidth>(s)?.Polynomial.Evaluate(s) ?? 0.0;
                    double centre = side * (edge + (width / 2.0));
                    if (width > 0.0)
                    {
                        nearest = Math.Min(nearest, Math.Abs(lateral - centre));
                    }

                    edge += width;
                }

                if (side < 0)
                {
                    right = edge;
                }
                else
                {
                    left = edge;
                }
            }

            return (nearest, Math.Max(0.0, Math.Max(-right - lateral, lateral - left)));
        }

        /// <summary>
        /// The roads of the lanes leading into each lane, and of the lanes it leads to, one span per
        /// road.
        /// </summary>
        private (Dictionary<string, LaneSpan[]> Leading, Dictionary<string, LaneSpan[]> Following) Neighbours()
        {
            Dictionary<string, List<LaneSpan>> leading = [];
            Dictionary<string, List<LaneSpan>> following = [];
            foreach (SumoLane lane in _network.Lanes)
            {
                _spans.TryGetValue(lane.Id, out LaneSpan? from);
                foreach (LaneLink link in _network.SuccessorsOf(lane.Id))
                {
                    string next = _network.TryGetLane(link.NextLaneId, out _) ? link.NextLaneId : link.ToLaneId;
                    if (from is not null)
                    {
                        Add(leading, next, from);
                    }

                    if (_spans.TryGetValue(next, out LaneSpan? to))
                    {
                        Add(following, lane.Id, to);
                    }
                }
            }

            return (leading.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray()),
                    following.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray()));

            static void Add(Dictionary<string, List<LaneSpan>> index, string laneId, LaneSpan span)
            {
                if (!index.TryGetValue(laneId, out List<LaneSpan>? spans))
                {
                    spans = [];
                    index[laneId] = spans;
                }

                if (!spans.Any(each => each.Road == span.Road))
                {
                    spans.Add(span);
                }
            }
        }

        private RoadMappingSummary Summarise()
        {
            int normal = 0;
            int internalLanes = 0;
            foreach (SumoLane lane in _network.Lanes)
            {
                if (lane.IsInternal)
                {
                    internalLanes++;
                }
                else
                {
                    normal++;
                }
            }

            Dictionary<RoadMappingKind, int> byKind = [];
            foreach (RoadMappingKind kind in _kinds.Values)
            {
                byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            }

            List<string> unmapped = [.. _network.Lanes.Where(lane => !_spans.ContainsKey(lane.Id))
                                                     .Select(lane => lane.Id)
                                                     .OrderBy(id => id, StringComparer.Ordinal)];
            return new RoadMappingSummary(
                _roads.Count, normal, internalLanes, byKind, unmapped,
                ResidualStatistics.Of(_along), ResidualStatistics.Of(_lateral));
        }
    }

    private sealed record LaneFit(LaneSpan Span, double[] Along, double[] Lateral, double RmsLateralResidual);
}

/// <summary>Which of the world build's records joined a SUMO lane to its road.</summary>
public enum RoadMappingKind
{
    /// <summary>A normal edge, on the road whose <c>sumoId</c> names it.</summary>
    EdgeRoad,

    /// <summary>A normal edge a merge appended to the road of the edge before it.</summary>
    MergedEdge,

    /// <summary>An internal edge, on the junction connector named after it.</summary>
    Connector,

    /// <summary>An internal edge a merge appended between two edges' sections.</summary>
    MergedConnector,

    /// <summary>The second half of a split internal edge, on its first half's connector.</summary>
    SecondHalf,

    /// <summary>
    /// An internal edge on another connector of its junction, by shape alone: netconvert draws the lanes
    /// of several internal edges on one connector, named after the first of them.
    /// </summary>
    JunctionConnector,

    /// <summary>
    /// An internal edge on the connector its links name, carried along it in proportion rather than
    /// projected: a connector whose reference line swings across several lanes within a few metres, where
    /// a lane far from it has no well-defined projection.
    /// </summary>
    ProportionalConnector,
}

/// <summary>A distribution of residuals, in metres.</summary>
/// <param name="Count">How many samples.</param>
/// <param name="MeanAbsolute">The mean magnitude.</param>
/// <param name="P99Absolute">The 99th percentile of the magnitude.</param>
/// <param name="WorstAbsolute">The largest magnitude.</param>
public readonly record struct ResidualStatistics(int Count, double MeanAbsolute, double P99Absolute, double WorstAbsolute)
{
    /// <summary>The statistics of a set of samples.</summary>
    public static ResidualStatistics Of(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0)
        {
            return default;
        }

        double[] magnitudes = [.. samples.Select(Math.Abs).Where(double.IsFinite).OrderBy(value => value)];
        if (magnitudes.Length == 0)
        {
            return new ResidualStatistics(samples.Count, double.NaN, double.NaN, double.NaN);
        }

        return new ResidualStatistics(
            magnitudes.Length, magnitudes.Average(),
            magnitudes[Math.Min(magnitudes.Length - 1, (int)Math.Ceiling(0.99 * magnitudes.Length) - 1)],
            magnitudes[^1]);
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"mean {MeanAbsolute:0.000} m, p99 {P99Absolute:0.000} m, worst {WorstAbsolute:0.000} m over {Count} samples");
}

/// <summary>How a SUMO network was joined to its OpenDRIVE roads.</summary>
/// <param name="Roads">OpenDRIVE roads with geometry to project onto.</param>
/// <param name="NormalLanes">SUMO lanes on normal edges.</param>
/// <param name="InternalLanes">SUMO lanes inside junctions.</param>
/// <param name="LanesByKind">Lanes joined to a road, by the record that joined them.</param>
/// <param name="UnmappedLanes">Lanes joined to no road, by id.</param>
/// <param name="AlongTrack">
/// How far each lane sample projects along its road from where its lane position carries it in
/// proportion: what the per-tick projection corrects.
/// </param>
/// <param name="Lateral">
/// How far each lane sample lies across its road from the centre of the nearest of the road's lanes.
/// </param>
public sealed record RoadMappingSummary(
    int Roads,
    int NormalLanes,
    int InternalLanes,
    IReadOnlyDictionary<RoadMappingKind, int> LanesByKind,
    IReadOnlyList<string> UnmappedLanes,
    ResidualStatistics AlongTrack,
    ResidualStatistics Lateral)
{
    /// <summary>Every lane the network describes with a shape.</summary>
    public int Lanes => NormalLanes + InternalLanes;

    /// <summary>Lanes joined to a road.</summary>
    public int MappedLanes => LanesByKind.Values.Sum();

    /// <inheritdoc/>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
                    $"{MappedLanes} of {Lanes} SUMO lanes on {Roads} OpenDRIVE roads");
        if (LanesByKind.Count > 0)
        {
            text.Append(" (")
                .Append(string.Join(", ", LanesByKind.OrderBy(entry => entry.Key)
                                                     .Select(entry => $"{entry.Key} {entry.Value}")))
                .Append(')');
        }

        text.Append(CultureInfo.InvariantCulture, $"; along-track {AlongTrack}; lateral {Lateral}");
        return text.ToString();
    }
}
