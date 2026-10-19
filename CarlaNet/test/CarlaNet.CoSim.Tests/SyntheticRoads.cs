using System.Globalization;
using System.Text;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A hand-built OpenDRIVE document and the SUMO network converted beside it, made of straight roads, in
/// the shape netconvert and the world build write them: every road's lanes on its right, its reference
/// line the carriageway's left edge, the SUMO edge it carries named in its <c>sumoId</c> user data, and
/// each junction connector named after an internal edge.
/// </summary>
/// <remarks>
/// A shipped package is tens of megabytes and nothing in it is a height known in advance. The seating's
/// arithmetic needs roads whose profile at a point is predictable, so the tests make them -- through the
/// real OpenDRIVE parser and the real network reader.
/// </remarks>
internal sealed class SyntheticRoads
{
    /// <summary>The lane width every road and lane is built with.</summary>
    public const double LaneWidth = 3.35;

    private readonly StringBuilder _roads = new();
    private readonly StringBuilder _edges = new();
    private readonly StringBuilder _connections = new();

    /// <summary>
    /// A straight road and the SUMO lanes on it.
    /// </summary>
    /// <param name="id">OpenDRIVE road id.</param>
    /// <param name="sumoId">The edge it carries, written as its <c>sumoId</c>; null for a connector.</param>
    /// <param name="name">The road's name: an internal edge's id for a connector.</param>
    /// <param name="junction">The junction a connector belongs to, or -1.</param>
    /// <param name="start">Where the first lane's centre begins, SUMO frame.</param>
    /// <param name="heading">The road's heading, radians anticlockwise from east.</param>
    /// <param name="length">The road's length.</param>
    /// <param name="lanes">How many lanes, all on the right of the reference line.</param>
    /// <param name="elevation">The profile's records, as (s, a, b), linear between them.</param>
    /// <param name="sections">Where lane sections begin after the first, each with its lane count.</param>
    /// <param name="predecessor">The road a connector leaves, or 0.</param>
    /// <param name="successor">The road a connector arrives on, or 0.</param>
    /// <remarks>
    /// <paramref name="start"/> is the centre of the rightmost lane, which is SUMO's lane 0, so the
    /// reference line runs <c>lanes - 0.5</c> lane widths to its left.
    /// </remarks>
    public SyntheticRoads Road(uint id, string? sumoId, string name, int junction, (double X, double Y) start,
                               double heading, double length, int lanes,
                               IReadOnlyList<(double S, double A, double B)> elevation,
                               IReadOnlyList<(double S, int Lanes)>? sections = null,
                               uint predecessor = 0, uint successor = 0)
    {
        double leftX = -Math.Sin(heading);
        double leftY = Math.Cos(heading);
        double offset = (lanes - 0.5) * LaneWidth;
        double x = start.X + (leftX * offset);
        double y = start.Y + (leftY * offset);

        _roads.Append(Invariant($"<road name=\"{name}\" length=\"{length}\" id=\"{id}\" junction=\"{junction}\"><link>"));
        if (predecessor != 0)
        {
            _roads.Append(Invariant($"<predecessor elementType=\"road\" elementId=\"{predecessor}\" contactPoint=\"end\"/>"));
        }

        if (successor != 0)
        {
            _roads.Append(Invariant($"<successor elementType=\"road\" elementId=\"{successor}\" contactPoint=\"start\"/>"));
        }

        _roads.Append("</link><type s=\"0\" type=\"town\"/><planView>");
        _roads.Append(Invariant($"<geometry s=\"0\" x=\"{x}\" y=\"{y}\" hdg=\"{heading}\" length=\"{length}\"><line/></geometry>"));
        _roads.Append("</planView><elevationProfile>");
        foreach ((double s, double a, double b) in elevation)
        {
            _roads.Append(Invariant($"<elevation s=\"{s}\" a=\"{a}\" b=\"{b}\" c=\"0\" d=\"0\"/>"));
        }

        _roads.Append("</elevationProfile><lateralProfile/><lanes>");
        List<(double S, int Lanes)> all = [(0.0, lanes), .. sections ?? []];
        foreach ((double s, int count) in all)
        {
            _roads.Append(Invariant($"<laneSection s=\"{s}\"><center><lane id=\"0\" type=\"none\" level=\"true\"/></center><right>"));
            for (int lane = 1; lane <= count; lane++)
            {
                _roads.Append(Invariant($"<lane id=\"-{lane}\" type=\"driving\" level=\"true\"><link/><width sOffset=\"0\" a=\"{LaneWidth}\" b=\"0\" c=\"0\" d=\"0\"/></lane>"));
            }

            _roads.Append("</right></laneSection>");
        }

        _roads.Append("</lanes><objects/><signals/>");
        if (sumoId is not null)
        {
            _roads.Append(Invariant($"<userData code=\"sumoId\" value=\"{sumoId}\"/>"));
        }

        _roads.Append("</road>");
        return this;
    }

    /// <summary>A SUMO edge of straight lanes, lane 0 rightmost, starting at its lane 0's start.</summary>
    public SyntheticRoads Edge(string id, (double X, double Y) start, double heading, double length, int lanes = 1)
    {
        double leftX = -Math.Sin(heading);
        double leftY = Math.Cos(heading);
        bool inside = id.StartsWith(':');
        _edges.Append(Invariant($"<edge id=\"{id}\"{(inside ? " function=\"internal\"" : string.Empty)}>"));
        for (int index = 0; index < lanes; index++)
        {
            double x0 = start.X + (leftX * index * LaneWidth);
            double y0 = start.Y + (leftY * index * LaneWidth);
            double x1 = x0 + (Math.Cos(heading) * length);
            double y1 = y0 + (Math.Sin(heading) * length);
            _edges.Append(Invariant($"<lane id=\"{id}_{index}\" index=\"{index}\" speed=\"13.9\" length=\"{length}\" width=\"{LaneWidth}\" shape=\"{x0:0.###},{y0:0.###} {x1:0.###},{y1:0.###}\"/>"));
        }

        _edges.Append("</edge>");
        return this;
    }

    /// <summary>A SUMO connection, through an internal lane where <paramref name="via"/> names one.</summary>
    public SyntheticRoads Connection(string from, int fromLane, string to, int toLane, string via = "")
    {
        _connections.Append(Invariant($"<connection from=\"{from}\" to=\"{to}\" fromLane=\"{fromLane}\" toLane=\"{toLane}\"{(via.Length > 0 ? $" via=\"{via}\"" : string.Empty)} dir=\"s\" state=\"M\"/>"));
        return this;
    }

    /// <summary>The OpenDRIVE document.</summary>
    public string OpenDrive =>
        "<?xml version=\"1.0\" standalone=\"yes\"?><OpenDRIVE><header revMajor=\"1\" revMinor=\"4\" name=\"\" version=\"1.00\"/>"
        + _roads + "</OpenDRIVE>";

    /// <summary>The SUMO network.</summary>
    public string Network =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><net version=\"1.20\">"
        + "<location netOffset=\"0.00,0.00\" convBoundary=\"-100.00,-100.00,100.00,100.00\" origBoundary=\"-100.00,-100.00,100.00,100.00\" projParameter=\"!\"/>"
        + _edges + _connections + "</net>";

    /// <summary>The two joined.</summary>
    public RoadSurface Join() => RoadSurface.Build(OpenDrive, SumoRoadNetwork.Parse(Network));

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
