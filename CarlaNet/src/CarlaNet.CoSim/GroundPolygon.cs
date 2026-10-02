namespace CarlaNet.CoSim;

/// <summary>
/// A convex polygon on the ground, in the CARLA frame's metres, with its vertices in order of
/// increasing angle about its centre.
/// </summary>
/// <remarks>
/// <para>Everything a camera footprint is built from keeps a polygon convex: a disc drawn as a
/// polygon, cut by the half-planes the frustum's four sides make on the ground, and the hull of two
/// such polygons. Convexity is what makes the one question the render set asks of it -- how far is
/// this vehicle from it -- a loop over its edges rather than a geometry library.</para>
///
/// <para>Immutable, so a footprint published on the report cannot be changed by the pass that
/// replaces it.</para>
/// </remarks>
public sealed class GroundPolygon
{
    private readonly double[] _x;
    private readonly double[] _y;

    private GroundPolygon(double[] x, double[] y)
    {
        _x = x;
        _y = y;
    }

    /// <summary>A polygon with nothing in it: every point is infinitely far from it.</summary>
    public static GroundPolygon Empty { get; } = new([], []);

    /// <summary>How many vertices it has.</summary>
    public int Count => _x.Length;

    /// <summary>Whether it covers no ground at all.</summary>
    public bool IsEmpty => _x.Length == 0;

    /// <summary>One vertex.</summary>
    public (double X, double Y) this[int index] => (_x[index], _y[index]);

    /// <summary>The area it covers, square metres.</summary>
    public double Area
    {
        get
        {
            double twice = 0.0;
            for (int index = 0; index < _x.Length; index++)
            {
                int next = (index + 1) % _x.Length;
                twice += (_x[index] * _y[next]) - (_x[next] * _y[index]);
            }

            return Math.Abs(twice) / 2.0;
        }
    }

    /// <summary>The smallest axis-aligned box holding it, or zeros where it is empty.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds =>
        IsEmpty ? (0.0, 0.0, 0.0, 0.0) : (_x.Min(), _y.Min(), _x.Max(), _y.Max());

    /// <summary>
    /// A regular polygon holding a disc: its edges touch the circle, so every point of the disc is in
    /// it and none of it is more than a thousandth of the radius outside at 64 sides.
    /// </summary>
    public static GroundPolygon AroundDisc(double centreX, double centreY, double radius, int sides = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 3);
        if (!(radius > 0.0) || !double.IsFinite(radius))
        {
            return Empty;
        }

        double vertexRadius = radius / Math.Cos(Math.PI / sides);
        double[] x = new double[sides];
        double[] y = new double[sides];
        for (int index = 0; index < sides; index++)
        {
            double angle = 2.0 * Math.PI * index / sides;
            x[index] = centreX + (vertexRadius * Math.Cos(angle));
            y[index] = centreY + (vertexRadius * Math.Sin(angle));
        }

        return new GroundPolygon(x, y);
    }

    /// <summary>
    /// The part of the polygon where <c>a·x + b·y + c ≥ 0</c>.
    /// </summary>
    /// <remarks>
    /// One pass of Sutherland and Hodgman's clip against a single line, which keeps a convex polygon
    /// convex and its vertices in order. A line with no slope (<c>a = b = 0</c>) keeps all of it or none,
    /// by the sign of <c>c</c>: that is a frustum side lying parallel to the ground.
    /// </remarks>
    public GroundPolygon Keep(double a, double b, double c)
    {
        if (IsEmpty)
        {
            return this;
        }

        List<double> x = new(_x.Length + 2);
        List<double> y = new(_y.Length + 2);
        for (int index = 0; index < _x.Length; index++)
        {
            int next = (index + 1) % _x.Length;
            double here = (a * _x[index]) + (b * _y[index]) + c;
            double there = (a * _x[next]) + (b * _y[next]) + c;
            if (here >= 0.0)
            {
                x.Add(_x[index]);
                y.Add(_y[index]);
            }

            if ((here >= 0.0) != (there >= 0.0))
            {
                double t = here / (here - there);
                x.Add(_x[index] + (t * (_x[next] - _x[index])));
                y.Add(_y[index] + (t * (_y[next] - _y[index])));
            }
        }

        return x.Count < 3 ? Empty : new GroundPolygon([.. x], [.. y]);
    }

    /// <summary>
    /// The convex hull of every vertex of the polygons given: the smallest convex polygon holding all
    /// of them.
    /// </summary>
    /// <remarks>
    /// Andrew's monotone chain. The hull of a footprint at one pose and the same footprint moved along
    /// a straight line is exactly the ground the footprint sweeps on the way, which is what a moving
    /// camera's render set has to cover.
    /// </remarks>
    public static GroundPolygon Hull(params GroundPolygon[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        List<(double X, double Y)> points = [];
        foreach (GroundPolygon part in parts)
        {
            for (int index = 0; index < part.Count; index++)
            {
                points.Add(part[index]);
            }
        }

        if (points.Count < 3)
        {
            return Empty;
        }

        points.Sort(static (left, right) =>
        {
            int byX = left.X.CompareTo(right.X);
            return byX != 0 ? byX : left.Y.CompareTo(right.Y);
        });

        var hull = new (double X, double Y)[points.Count * 2];
        int size = 0;
        foreach ((double X, double Y) point in points)
        {
            while (size >= 2 && Cross(hull[size - 2], hull[size - 1], point) <= 0.0)
            {
                size--;
            }

            hull[size++] = point;
        }

        int lower = size + 1;
        for (int index = points.Count - 2; index >= 0; index--)
        {
            while (size >= lower && Cross(hull[size - 2], hull[size - 1], points[index]) <= 0.0)
            {
                size--;
            }

            hull[size++] = points[index];
        }

        // The chain ends where it began.
        size--;
        if (size < 3)
        {
            return Empty;
        }

        double[] x = new double[size];
        double[] y = new double[size];
        for (int index = 0; index < size; index++)
        {
            (x[index], y[index]) = hull[index];
        }

        return new GroundPolygon(x, y);
    }

    /// <summary>
    /// How far a point is from the polygon, metres: zero on or inside it, and positive infinity where
    /// the polygon is empty.
    /// </summary>
    public double DistanceTo(double x, double y)
    {
        if (IsEmpty)
        {
            return double.PositiveInfinity;
        }

        bool inside = true;
        double nearest = double.PositiveInfinity;
        double signedArea = SignedArea();
        for (int index = 0; index < _x.Length; index++)
        {
            int next = (index + 1) % _x.Length;
            double edgeX = _x[next] - _x[index];
            double edgeY = _y[next] - _y[index];
            double toX = x - _x[index];
            double toY = y - _y[index];

            // Outside any one edge is outside a convex polygon; which side is outside follows the
            // polygon's winding, which the clip preserves from the disc it started as.
            if (((edgeX * toY) - (edgeY * toX)) * signedArea < 0.0)
            {
                inside = false;
            }

            double lengthSquared = (edgeX * edgeX) + (edgeY * edgeY);
            double t = lengthSquared > 0.0
                ? Math.Clamp(((toX * edgeX) + (toY * edgeY)) / lengthSquared, 0.0, 1.0)
                : 0.0;
            double dx = toX - (t * edgeX);
            double dy = toY - (t * edgeY);
            nearest = Math.Min(nearest, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        return inside ? 0.0 : nearest;
    }

    private double SignedArea()
    {
        double twice = 0.0;
        for (int index = 0; index < _x.Length; index++)
        {
            int next = (index + 1) % _x.Length;
            twice += (_x[index] * _y[next]) - (_x[next] * _y[index]);
        }

        return twice;
    }

    private static double Cross((double X, double Y) origin, (double X, double Y) a, (double X, double Y) b) =>
        ((a.X - origin.X) * (b.Y - origin.Y)) - ((a.Y - origin.Y) * (b.X - origin.X));
}
