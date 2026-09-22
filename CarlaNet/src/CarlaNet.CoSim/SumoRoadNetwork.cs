using System.Globalization;
using System.Xml;
using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim;

/// <summary>
/// The lane shapes and the connections between them, read out of the SUMO network the world was
/// built from.
/// </summary>
/// <remarks>
/// <para><b>Read from the world package, not from a fresh netconvert run.</b> The network in the
/// package came out of the same invocation as the world's OpenDRIVE, and it cannot be reproduced:
/// asking netconvert for OpenDRIVE output flips the rectangular lane cut, which feeds junction shape
/// computation, so a second run with identical flags gives a different graph. A bridge that read a
/// regenerated network would interpolate along lanes the rendered world does not have.</para>
///
/// <para>Only what an interpolation needs is kept: lane shapes, lane lengths, and which lane leads
/// to which through which junction connector. Traffic-light programs, right-of-way rows, edge types
/// and everything else is skipped while parsing.</para>
/// </remarks>
public sealed class SumoRoadNetwork
{
    private readonly Dictionary<string, SumoLane> _lanes;
    private readonly Dictionary<string, List<LaneLink>> _successors;
    private readonly Dictionary<string, List<SumoLane>> _lanesByEdge = [];

    private SumoRoadNetwork(Dictionary<string, SumoLane> lanes,
                            Dictionary<string, List<LaneLink>> successors,
                            (double MinX, double MinY, double MaxX, double MaxY) convBoundary,
                            (double X, double Y) netOffset,
                            string projection)
    {
        _lanes = lanes;
        _successors = successors;
        foreach (SumoLane lane in lanes.Values)
        {
            if (!_lanesByEdge.TryGetValue(lane.EdgeId, out List<SumoLane>? siblings))
            {
                siblings = [];
                _lanesByEdge[lane.EdgeId] = siblings;
            }

            siblings.Add(lane);
        }

        foreach (List<SumoLane> siblings in _lanesByEdge.Values)
        {
            siblings.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        }

        ConvBoundary = convBoundary;
        NetOffset = netOffset;
        Projection = projection;
    }

    /// <summary>The network's own extent in its projected frame.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) ConvBoundary { get; }

    /// <summary>
    /// The offset netconvert applied when it normalised coordinates. A world and a scenario that
    /// share an origin have this at zero, which is what makes the frame conversion a sign and
    /// nothing else.
    /// </summary>
    public (double X, double Y) NetOffset { get; }

    /// <summary>The projection string netconvert was given.</summary>
    public string Projection { get; }

    /// <summary>How many lanes the network has, internal ones included.</summary>
    public int LaneCount => _lanes.Count;

    /// <summary>Read the network a world package carries.</summary>
    public static SumoRoadNetwork FromWorldPackage(string packagePath) =>
        Parse(WorldPackage.ReadNetwork(packagePath));

    /// <summary>Read a network from a file.</summary>
    public static SumoRoadNetwork Load(string netXmlPath) =>
        Parse(File.ReadAllText(netXmlPath));

    /// <summary>Read a network from its XML.</summary>
    public static SumoRoadNetwork Parse(string netXml)
    {
        ArgumentNullException.ThrowIfNull(netXml);

        Dictionary<string, SumoLane> lanes = [];
        Dictionary<string, List<LaneLink>> successors = [];
        (double, double, double, double) boundary = default;
        (double, double) offset = default;
        string projection = string.Empty;

        using var reader = XmlReader.Create(new StringReader(netXml),
                                            new XmlReaderSettings { IgnoreComments = true });
        string edgeId = string.Empty;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.Name)
            {
                case "location":
                    boundary = ReadBoundary(reader.GetAttribute("convBoundary"));
                    (double x, double y, _, _) = ReadBoundary(reader.GetAttribute("netOffset") + ",0,0");
                    offset = (x, y);
                    projection = reader.GetAttribute("projParameter") ?? string.Empty;
                    break;

                case "edge":
                    edgeId = reader.GetAttribute("id") ?? string.Empty;
                    break;

                case "lane":
                    SumoLane? lane = ReadLane(reader, edgeId);
                    if (lane is not null)
                    {
                        lanes[lane.Id] = lane;
                    }

                    break;

                case "connection":
                    ReadConnection(reader, successors);
                    break;
            }
        }

        return new SumoRoadNetwork(lanes, successors, boundary, offset, projection);
    }

    /// <summary>One lane by its id.</summary>
    public bool TryGetLane(string laneId, out SumoLane lane) => _lanes.TryGetValue(laneId, out lane!);

    /// <summary>Every lane of one edge, ordered by index.</summary>
    /// <remarks>
    /// Lanes of one edge run alongside each other for its whole length, so a distance along one is
    /// the same distance along its siblings. That is what makes a lane change expressible as a
    /// sideways blend between two points at one along-lane distance.
    /// </remarks>
    public IReadOnlyList<SumoLane> LanesOfEdge(string edgeId) =>
        _lanesByEdge.TryGetValue(edgeId, out List<SumoLane>? lanes) ? lanes : [];

    /// <summary>Where a lane leads, and through which junction connector.</summary>
    public IReadOnlyList<LaneLink> SuccessorsOf(string laneId) =>
        _successors.TryGetValue(laneId, out List<LaneLink>? links) ? links : [];

    /// <summary>
    /// The lanes a vehicle passes through to get from one lane to another, the first exclusive and
    /// the last inclusive, or <see langword="null"/> where no route of at most
    /// <paramref name="maximumHops"/> connections joins them.
    /// </summary>
    /// <remarks>
    /// Breadth-first, so the route found is the one with the fewest connections, which for a road
    /// network is the one a vehicle took. The hop limit exists because a search that does not find a
    /// route has to stop somewhere and because a vehicle that crossed more than a handful of lanes
    /// in one SUMO step did not drive there.
    /// </remarks>
    public IReadOnlyList<SumoLane>? FindPath(string fromLaneId, string toLaneId, int maximumHops = 6)
    {
        if (fromLaneId == toLaneId)
        {
            return [];
        }

        Queue<string> frontier = new();
        Dictionary<string, string> cameFrom = [];
        frontier.Enqueue(fromLaneId);
        cameFrom[fromLaneId] = string.Empty;

        for (int hop = 0; hop < maximumHops && frontier.Count > 0; hop++)
        {
            for (int width = frontier.Count; width > 0; width--)
            {
                string current = frontier.Dequeue();
                foreach (LaneLink link in SuccessorsOf(current))
                {
                    // The connector where there is one, so a route through a junction carries the
                    // turn's own length and shape. A connector the network describes with a single
                    // shape point is not a lane this can evaluate along, and the link's far lane
                    // stands in for it rather than breaking the route.
                    string next = _lanes.ContainsKey(link.NextLaneId) ? link.NextLaneId : link.ToLaneId;
                    if (!cameFrom.TryAdd(next, current))
                    {
                        continue;
                    }

                    if (next == toLaneId)
                    {
                        return Rebuild(cameFrom, fromLaneId, toLaneId);
                    }

                    frontier.Enqueue(next);
                }
            }
        }

        return null;
    }

    private IReadOnlyList<SumoLane> Rebuild(Dictionary<string, string> cameFrom,
                                            string fromLaneId,
                                            string toLaneId)
    {
        List<SumoLane> path = [];
        for (string at = toLaneId; at != fromLaneId; at = cameFrom[at])
        {
            if (_lanes.TryGetValue(at, out SumoLane? lane))
            {
                path.Add(lane);
            }
        }

        path.Reverse();
        return path;
    }

    private static SumoLane? ReadLane(XmlReader reader, string edgeId)
    {
        string? id = reader.GetAttribute("id");
        string? shape = reader.GetAttribute("shape");
        if (id is null || shape is null)
        {
            return null;
        }

        List<(double X, double Y)> points = [];
        foreach (Range part in shape.AsSpan().Split(' '))
        {
            ReadOnlySpan<char> point = shape.AsSpan()[part];
            int comma = point.IndexOf(',');
            if (comma < 0)
            {
                continue;
            }

            points.Add((Number(point[..comma]), Number(point[(comma + 1)..])));
        }

        if (points.Count < 2)
        {
            // A lane whose shape is a single point cannot be evaluated along, and SUMO writes one
            // for a connector of zero length. It is skipped rather than kept as a degenerate lane,
            // so a lookup for it misses rather than returning something that cannot be interpolated.
            return null;
        }

        return new SumoLane(
            id,
            edgeId,
            int.TryParse(reader.GetAttribute("index"), out int index) ? index : 0,
            Number(reader.GetAttribute("length")),
            Number(reader.GetAttribute("width")),
            points);
    }

    private static void ReadConnection(XmlReader reader, Dictionary<string, List<LaneLink>> successors)
    {
        string? from = reader.GetAttribute("from");
        string? to = reader.GetAttribute("to");
        if (from is null || to is null)
        {
            return;
        }

        string fromLane = from + "_" + (reader.GetAttribute("fromLane") ?? "0");
        string toLane = to + "_" + (reader.GetAttribute("toLane") ?? "0");
        string via = reader.GetAttribute("via") ?? string.Empty;

        if (!successors.TryGetValue(fromLane, out List<LaneLink>? links))
        {
            links = [];
            successors[fromLane] = links;
        }

        links.Add(new LaneLink(via, toLane));
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) ReadBoundary(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        string[] parts = text.Split(',');
        return parts.Length < 4
            ? default
            : (Number(parts[0]), Number(parts[1]), Number(parts[2]), Number(parts[3]));
    }

    private static double Number(ReadOnlySpan<char> text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0.0;

    private static double Number(string? text) => text is null ? 0.0 : Number(text.AsSpan());
}
