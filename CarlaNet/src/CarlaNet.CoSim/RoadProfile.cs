using CarlaNet.Map.Geom;
using CarlaNet.Map.Road.Element;
using OpenDriveRoad = CarlaNet.Map.Road.Road;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.CoSim;

/// <summary>
/// One OpenDRIVE road as the seating reads it: its reference line, sampled once so a position can be
/// projected onto it, and its elevation profile, evaluated exactly.
/// </summary>
/// <remarks>
/// <para><b>The profile is the road's height across its whole width.</b> CARLA builds a road's mesh,
/// its waypoints and its traffic-manager paths from the reference line's elevation at s and ignores
/// superelevation, so every lane of a road stands at the reference line's height at the same s. A
/// position's height on the road is therefore the profile at the s the position projects to, and the
/// lane it is in does not enter into it.</para>
///
/// <para><b>Sampled in the OpenDRIVE frame, which is SUMO's.</b> netconvert writes the OpenDRIVE and the
/// SUMO network from one projection, east and north, and CARLA negates the northing only when it builds
/// the world. A SUMO position is projected onto these samples as it is; a CARLA one has its y negated
/// first.</para>
///
/// <para><b>Built once, read per tick without allocating.</b> The reference line comes from
/// <see cref="RoadMap.GetDirectedPointIn"/> -- the port of the engine's own evaluation, lane offset
/// included -- at one-metre steps, which puts the chord of the tightest junction connector within a few
/// centimetres of its arc. The elevation records are held as the parser built them, in absolute s, so an
/// evaluation is a binary search and a cubic.</para>
/// </remarks>
internal sealed class RoadProfile
{
    /// <summary>How far apart the reference line is sampled, metres.</summary>
    private const double SampleStepMetres = 1.0;

    private readonly double[] _s;
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _elevationStarts;
    private readonly CubicPolynomial[] _elevations;

    private RoadProfile(OpenDriveRoad road, double[] s, double[] x, double[] y,
                        double[] elevationStarts, CubicPolynomial[] elevations)
    {
        Id = road.Id;
        Name = road.Name;
        Length = road.Length;
        IsJunction = road.IsJunction;
        Road = road;
        _s = s;
        _x = x;
        _y = y;
        _elevationStarts = elevationStarts;
        _elevations = elevations;
    }

    /// <summary>The road's OpenDRIVE id.</summary>
    public uint Id { get; }

    /// <summary>The road's name: a SUMO internal edge's id for a junction connector.</summary>
    public string Name { get; }

    /// <summary>The road's length along its reference line.</summary>
    public double Length { get; }

    /// <summary>Whether the road is a junction connector.</summary>
    public bool IsJunction { get; }

    /// <summary>The parsed road, for the build-time fits that need its lanes.</summary>
    public OpenDriveRoad Road { get; }

    /// <summary>
    /// The profile of one road, or <see langword="null"/> for a road with no geometry to project onto.
    /// </summary>
    public static RoadProfile? From(OpenDriveRoad road)
    {
        ArgumentNullException.ThrowIfNull(road);
        if (road.Length <= 0.0 || !road.Info.All.OfType<RoadInfoGeometry>().Any())
        {
            return null;
        }

        int segments = Math.Max(1, (int)Math.Ceiling(road.Length / SampleStepMetres));
        var s = new double[segments + 1];
        var x = new double[segments + 1];
        var y = new double[segments + 1];
        for (int index = 0; index <= segments; index++)
        {
            double at = index == segments ? road.Length : index * (road.Length / segments);
            DirectedPoint point;
            try
            {
                point = RoadMap.GetDirectedPointIn(road, at);
            }
            catch (InvalidOperationException)
            {
                // A road whose geometry does not cover its own length is not one a body can be placed
                // on; the engine could not build it either.
                return null;
            }

            s[index] = at;
            x[index] = point.Location.X;
            y[index] = point.Location.Y;
        }

        RoadInfoElevation[] records = road.Info.All.OfType<RoadInfoElevation>().ToArray();
        return new RoadProfile(road, s, x, y,
                               records.Select(record => record.Distance).ToArray(),
                               records.Select(record => record.Polynomial).ToArray());
    }

    /// <summary>
    /// The road's height above the georeference origin at <paramref name="s"/>, and its slope along +s.
    /// </summary>
    /// <remarks>
    /// Inside the road the record in force at s is evaluated, exactly as the engine does. Off either end
    /// the profile is carried on straight from the end at the end's own slope, which is what a body whose
    /// origin has not quite reached the road -- or has just left it -- stands on to within the change of
    /// grade across the joint: roads meet at one height, reconciled when the world was built. A road the
    /// file gives no elevation is at zero, as the engine builds it.
    /// </remarks>
    public (double Z, double Slope) Elevation(double s)
    {
        if (_elevations.Length == 0)
        {
            return (0.0, 0.0);
        }

        double inside = Math.Clamp(s, 0.0, Length);
        CubicPolynomial record = _elevations[RecordAt(inside)];
        double z = record.Evaluate(inside);
        double slope = record.Tangent(inside);
        return (z + (slope * (s - inside)), slope);
    }

    /// <summary>
    /// Project a position onto the reference line between two values of s.
    /// </summary>
    /// <param name="x">Easting in the OpenDRIVE frame, which is SUMO's.</param>
    /// <param name="y">Northing in the same frame.</param>
    /// <param name="fromS">The lowest s to search from; clamped to the road.</param>
    /// <param name="toS">The highest s to search to; clamped to the road.</param>
    /// <param name="expectedLateral">
    /// The offset across the road the position is expected at -- its lane's -- or
    /// <see cref="double.NaN"/> to take the plainly nearest point.
    /// </param>
    /// <remarks>
    /// <para>The point of the sampled polyline inside the window nearest the position, measured against
    /// the line the expected offset runs along: the reference line shifted across by that offset, whose
    /// normals are the reference line's own. That is what tells apart two stretches of one road lying side
    /// by side, which the plain distance cannot. A merge that collapsed a dead end runs a road out along
    /// a street, round its turning connector and back along the same centre line, so a point on the way
    /// out is exactly as near the way back -- a lane width to the other side of the line.</para>
    ///
    /// <para>On the road's first and last segment the projection is allowed to run off the end, so a
    /// position beyond the road's start answers with a negative s and one beyond its end with an s past
    /// its length -- which is how a caller learns that the position belongs to the road before or after
    /// this one.</para>
    /// </remarks>
    public RoadProjection Project(double x, double y, double fromS, double toS,
                                  double expectedLateral = double.NaN)
    {
        int last = _s.Length - 2;
        int first = Math.Clamp(SegmentAt(Math.Max(fromS, 0.0)), 0, last);
        int final = Math.Clamp(SegmentAt(Math.Min(toS, Length)), first, last);
        bool offset = !double.IsNaN(expectedLateral);

        RoadProjection best = new(double.NaN, 0.0, double.PositiveInfinity);
        double bestScore = double.PositiveInfinity;
        for (int segment = first; segment <= final; segment++)
        {
            double ax = _x[segment];
            double ay = _y[segment];
            double dx = _x[segment + 1] - ax;
            double dy = _y[segment + 1] - ay;
            double lengthSquared = (dx * dx) + (dy * dy);
            if (lengthSquared <= 0.0)
            {
                continue;
            }

            double length = Math.Sqrt(lengthSquared);
            double free = (((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared;
            double lower = segment == 0 ? double.NegativeInfinity : 0.0;
            double upper = segment == last ? double.PositiveInfinity : 1.0;
            double u = Math.Clamp(free, lower, upper);

            // Positive to the left of the direction of increasing s, as OpenDRIVE measures t.
            double lateral = ((dx * (y - ay)) - (dy * (x - ax))) / length;
            double along = (free - u) * length;
            double across = offset ? lateral - expectedLateral : lateral;
            double score = (along * along) + (across * across);
            if (score < bestScore)
            {
                bestScore = score;
                best = new RoadProjection(_s[segment] + (u * (_s[segment + 1] - _s[segment])),
                                          lateral, (along * along) + (lateral * lateral));
            }
        }

        return best;
    }

    /// <summary>The index of the elevation record in force at s, inside the road.</summary>
    private int RecordAt(double s)
    {
        // The last record starting at or before s, as RoadElementSet answers it; the first where s lies
        // before every record, which a built world never has.
        int low = 0;
        int high = _elevationStarts.Length;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (_elevationStarts[middle] <= s)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return Math.Max(0, low - 1);
    }

    /// <summary>The index of the sampled segment that contains s.</summary>
    private int SegmentAt(double s)
    {
        int low = 0;
        int high = _s.Length - 1;
        while (low + 1 < high)
        {
            int middle = (low + high) >> 1;
            if (_s[middle] <= s)
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

/// <summary>Where a position projects onto a road's reference line.</summary>
/// <param name="S">Distance along the reference line; negative before the road, past its length after it.</param>
/// <param name="Lateral">Signed offset from the reference line, positive to the left of increasing s.</param>
/// <param name="DistanceSquared">Squared distance to the projected point, infinite where nothing was searched.</param>
internal readonly record struct RoadProjection(double S, double Lateral, double DistanceSquared);
