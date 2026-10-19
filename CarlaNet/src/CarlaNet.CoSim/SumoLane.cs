namespace CarlaNet.CoSim;

/// <summary>
/// One lane of a SUMO network: its shape, and how to find a point along it.
/// </summary>
/// <remarks>
/// <para>The shape is the reason this class exists. A vehicle's position between two SUMO steps has
/// to be found <b>along the lane</b> rather than on the straight line between the two reported
/// points: a junction connector is a few metres long and turns through a right angle, and at
/// traffic speed two consecutive samples routinely sit on opposite sides of it.</para>
///
/// <para>A lane's declared length and the length of its own polyline are not required to agree --
/// a network can set one directly -- and TraCI reports a vehicle's lane position against the
/// declared one. The two are held separately and a position is mapped between them, so a network
/// where they differ is evaluated at the right place rather than off the end of the shape.</para>
/// </remarks>
public sealed class SumoLane
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _cumulative;

    internal SumoLane(string id, string edgeId, int index, double declaredLength, double width,
                      IReadOnlyList<(double X, double Y)> shape)
    {
        if (shape.Count < 2)
        {
            throw new ArgumentException($"lane '{id}' has {shape.Count} shape points", nameof(shape));
        }

        Id = id;
        EdgeId = edgeId;
        Index = index;
        DeclaredLengthMetres = declaredLength;
        WidthMetres = width;

        _x = new double[shape.Count];
        _y = new double[shape.Count];
        _cumulative = new double[shape.Count];
        for (int point = 0; point < shape.Count; point++)
        {
            _x[point] = shape[point].X;
            _y[point] = shape[point].Y;
            if (point > 0)
            {
                double dx = _x[point] - _x[point - 1];
                double dy = _y[point] - _y[point - 1];
                _cumulative[point] = _cumulative[point - 1] + Math.Sqrt((dx * dx) + (dy * dy));
            }
        }

        ShapeLengthMetres = _cumulative[^1];
    }

    /// <summary>The lane id, as <c>&lt;edge&gt;_&lt;index&gt;</c>.</summary>
    public string Id { get; }

    /// <summary>The edge the lane belongs to; one inside a junction has a leading colon.</summary>
    public string EdgeId { get; }

    /// <summary>The lane's index within its edge, counting from the right.</summary>
    public int Index { get; }

    /// <summary>What the network declares the lane's length to be, which lane positions are against.</summary>
    public double DeclaredLengthMetres { get; }

    /// <summary>The length of the lane's own polyline.</summary>
    public double ShapeLengthMetres { get; }

    /// <summary>The lane's width.</summary>
    public double WidthMetres { get; }

    /// <summary>Whether the lane is inside a junction, which SUMO marks by the edge's name.</summary>
    public bool IsInternal => EdgeId.Length > 0 && EdgeId[0] == ':';

    /// <summary>How many points the lane's polyline has.</summary>
    public int ShapePointCount => _x.Length;

    /// <summary>One point of the lane's polyline.</summary>
    public (double X, double Y) ShapePoint(int index) => (_x[index], _y[index]);

    /// <summary>
    /// The point and forward direction at a lane position, clamped to the lane's two ends.
    /// </summary>
    /// <param name="lanePositionMetres">
    /// Distance from the lane's start, against the <see cref="DeclaredLengthMetres"/> a TraCI lane
    /// position is reported in.
    /// </param>
    public (double X, double Y, double DirectionX, double DirectionY) PointAt(double lanePositionMetres)
    {
        double along = Math.Clamp(ToShapeDistance(lanePositionMetres), 0.0, ShapeLengthMetres);
        int segment = FindSegment(along);
        double start = _cumulative[segment];
        double span = _cumulative[segment + 1] - start;
        double fraction = span > 0.0 ? (along - start) / span : 0.0;

        double dx = _x[segment + 1] - _x[segment];
        double dy = _y[segment + 1] - _y[segment];
        double magnitude = Math.Sqrt((dx * dx) + (dy * dy));
        if (magnitude <= 0.0)
        {
            magnitude = 1.0;
            dx = 1.0;
            dy = 0.0;
        }

        return (_x[segment] + (dx * fraction), _y[segment] + (dy * fraction),
                dx / magnitude, dy / magnitude);
    }

    /// <summary>
    /// A distance along the declared length, expressed along the polyline the shape actually draws.
    /// </summary>
    public double ToShapeDistance(double lanePositionMetres) =>
        DeclaredLengthMetres > 0.0
            ? lanePositionMetres * (ShapeLengthMetres / DeclaredLengthMetres)
            : lanePositionMetres;

    private int FindSegment(double along)
    {
        int low = 0;
        int high = _cumulative.Length - 1;
        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            if (_cumulative[middle] <= along)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
