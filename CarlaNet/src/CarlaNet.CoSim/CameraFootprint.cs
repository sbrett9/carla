using System.Globalization;
using CarlaNet.Types.Geom;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// The ground one camera covers at one admission pass: its frustum projected onto the ground, capped
/// at the range past which a vehicle is too few pixels to be a detection target, and swept along the
/// camera's own motion over the step the pass decides.
/// </summary>
/// <remarks>
/// <para><b>The frustum on the ground is exact, not sampled.</b> The camera's field of view is a
/// four-sided pyramid with its apex at the camera, and a ground point is in view when it is on the
/// inside of all four sides. On a level plane each side is a half-plane, so the footprint is a disc
/// around the camera's nadir -- the range cap -- cut by four lines, which is convex and bounded
/// whatever the camera looks at. A view that takes in the horizon has an unbounded footprint and only
/// the cap bounds it; a view that sees no ground has an empty one.</para>
///
/// <para><b>The ground is two planes that bracket the world's surface under the view, not one.</b>
/// The world package's ground surface -- the surface every vehicle is seated on -- is sampled under
/// the footprint a plane at the height where the optical axis meets it makes, and the footprint is
/// taken again at the lowest and highest heights found and the two combined. A ray's first hit on the
/// surface lies between where it crosses those two planes, and a frustum's cross-sections at two
/// heights are the same polygon scaled about the nadir, so the combination holds everything a camera
/// over relief sees -- the true footprint over a hill is neither plane's -- without marching a ray
/// through the grid for every vehicle on every pass. Over flat ground the two planes are one, and the
/// footprint is that plane's.</para>
///
/// <para><b>The cap is measured from the image's corners.</b> A rectilinear picture spreads a ray at
/// angle θ off its axis across <c>1 / cos² θ</c> times the pixels an axial ray covers, so a vehicle
/// in a corner covers more pixels at a given range than one in the middle. The cap is the range past
/// which a body of the reference length, broadside, at the corners' scale, covers fewer than the
/// threshold's pixels: beyond it no vehicle anywhere in the picture reaches the threshold.</para>
/// </remarks>
public sealed class CameraFootprint
{
    /// <summary>How finely the range cap's circle is drawn.</summary>
    private const int DiscSides = 64;

    /// <summary>Samples along each side of the box the ground is searched over for the bracket.</summary>
    private const int BracketSamples = 9;

    /// <summary>
    /// How close to its own height a camera's lowest bracket plane may come before it is taken as
    /// the ground: a camera at or below the ground sees none of it.
    /// </summary>
    private const double LeastHeightMetres = 0.5;

    internal CameraFootprint(CameraView view,
                             double rangeCapMetres,
                             double referenceBodyMetres,
                             double minimumPixels,
                             (double Low, double High) ground,
                             double speedMetresPerSecond,
                             GroundPolygon now,
                             GroundPolygon admission)
    {
        Actor = view.Actor;
        Optics = view.Optics;
        Pose = view.Pose;
        RangeCapMetres = rangeCapMetres;
        ReferenceBodyMetres = referenceBodyMetres;
        MinimumPixels = minimumPixels;
        GroundLowMetres = ground.Low;
        GroundHighMetres = ground.High;
        SpeedMetresPerSecond = speedMetresPerSecond;
        Now = now;
        Admission = admission;
    }

    /// <summary>The camera's actor id.</summary>
    public ActorId Actor { get; }

    /// <summary>Its image size and field of view.</summary>
    public CameraOptics Optics { get; }

    /// <summary>Where it stood on the last rendered frame.</summary>
    public Transform Pose { get; }

    /// <summary>
    /// The slant range the footprint is capped at, metres: past it a body of
    /// <see cref="ReferenceBodyMetres"/> covers fewer than <see cref="MinimumPixels"/> pixels along its
    /// length anywhere in the picture.
    /// </summary>
    public double RangeCapMetres { get; }

    /// <summary>The body length the cap is computed for: the longest body the catalogue measures.</summary>
    public double ReferenceBodyMetres { get; }

    /// <summary>The fewest pixels along its length a vehicle may cover and still be rendered.</summary>
    public double MinimumPixels { get; }

    /// <summary>The lowest ground height found under the view, CARLA-local z.</summary>
    public double GroundLowMetres { get; }

    /// <summary>The highest ground height found under the view, CARLA-local z.</summary>
    public double GroundHighMetres { get; }

    /// <summary>The camera's height above the highest ground under its view, metres.</summary>
    public double AltitudeMetres => Pose.Location.Z - GroundHighMetres;

    /// <summary>How fast the camera moved since the pass before, metres per simulated second.</summary>
    public double SpeedMetresPerSecond { get; }

    /// <summary>The ground in view at <see cref="Pose"/>.</summary>
    public GroundPolygon Now { get; }

    /// <summary>
    /// The ground in view from <see cref="Pose"/> until the end of the step the pass decides and the
    /// admit lead after it, along the camera's motion: what a vehicle is admitted against.
    /// </summary>
    public GroundPolygon Admission { get; }

    /// <summary>
    /// The frustum of a camera at a pose, on a level ground plane, inside a disc around the camera's
    /// nadir.
    /// </summary>
    /// <param name="pose">The camera's world transform, CARLA frame.</param>
    /// <param name="optics">Its image size and field of view.</param>
    /// <param name="groundZ">The plane's height, CARLA-local z.</param>
    /// <param name="groundRadiusMetres">The disc's radius on the ground: the range cap, projected.</param>
    /// <returns>Empty where the camera is not above the plane or sees none of it.</returns>
    public static GroundPolygon Project(Transform pose, CameraOptics optics, double groundZ,
                                        double groundRadiusMetres)
    {
        double cx = pose.Location.X;
        double cy = pose.Location.Y;
        double cz = pose.Location.Z;
        if (!(cz - groundZ > 0.0))
        {
            return GroundPolygon.Empty;
        }

        (Vector forward, Vector right, Vector up) = Axes(pose.Rotation);
        double tanX = Math.Tan(optics.HorizontalFovDegrees * Math.PI / 360.0);
        double tanY = tanX * optics.ImageHeight / optics.ImageWidth;

        // The four corner rays, going round the picture.
        Span<Vector> corners =
        [
            forward - (right * tanX) + (up * tanY),
            forward + (right * tanX) + (up * tanY),
            forward + (right * tanX) - (up * tanY),
            forward - (right * tanX) - (up * tanY),
        ];

        GroundPolygon footprint = GroundPolygon.AroundDisc(cx, cy, groundRadiusMetres, DiscSides);
        for (int side = 0; side < corners.Length && !footprint.IsEmpty; side++)
        {
            // The side through two neighbouring corners, its normal turned to face the optical axis:
            // inside all four is the pyramid in front of the camera and not its mirror behind it.
            Vector normal = Vector.Cross(corners[side], corners[(side + 1) % corners.Length]);
            if (Vector.Dot(normal, forward) < 0.0)
            {
                normal = normal * -1.0;
            }

            // normal · (P - C) >= 0 for P = (x, y, groundZ).
            footprint = footprint.Keep(normal.X, normal.Y,
                                       (normal.Z * (groundZ - cz)) - (normal.X * cx) - (normal.Y * cy));
        }

        return footprint;
    }

    /// <summary>
    /// The frustum of a camera at a pose on the world's ground surface, bracketed by the lowest and
    /// highest heights of that surface under the view, and capped at a slant range.
    /// </summary>
    /// <param name="pose">The camera's world transform, CARLA frame.</param>
    /// <param name="optics">Its image size and field of view.</param>
    /// <param name="rangeCapMetres">The slant range the view is capped at.</param>
    /// <param name="groundHeight">The surface's CARLA-local z under a position, or null off the grid.</param>
    /// <param name="bracket">The lowest and highest ground heights taken.</param>
    public static GroundPolygon OnTheGround(Transform pose,
                                            CameraOptics optics,
                                            double rangeCapMetres,
                                            Func<double, double, double?> groundHeight,
                                            out (double Low, double High) bracket)
    {
        ArgumentNullException.ThrowIfNull(groundHeight);
        double cx = pose.Location.X;
        double cy = pose.Location.Y;
        double cz = pose.Location.Z;

        // Where the optical axis meets the ground is the view's own ground; one that meets none, or
        // meets it off the grid, takes the ground under the camera, and a camera off the grid takes
        // the origin's.
        double nominal = groundHeight(cx, cy) ?? 0.0;
        (Vector forward, _, _) = Axes(pose.Rotation);
        if (forward.Z < -1e-9)
        {
            double t = (nominal - cz) / forward.Z;
            if (t > 0.0 && groundHeight(cx + (t * forward.X), cy + (t * forward.Y)) is { } axial)
            {
                nominal = axial;
            }
        }

        nominal = Math.Min(nominal, cz - LeastHeightMetres);
        GroundPolygon first = Project(pose, optics, nominal, GroundRadius(rangeCapMetres, cz - nominal));
        double low = nominal;
        double high = nominal;
        void Take(double x, double y)
        {
            if (groundHeight(x, y) is { } height)
            {
                low = Math.Min(low, height);
                high = Math.Max(high, height);
            }
        }

        Take(cx, cy);
        for (int index = 0; index < first.Count; index++)
        {
            Take(first[index].X, first[index].Y);
        }

        (double minX, double minY, double maxX, double maxY) = first.Bounds;
        for (int column = 0; column < BracketSamples && !first.IsEmpty; column++)
        {
            for (int row = 0; row < BracketSamples; row++)
            {
                double x = minX + ((maxX - minX) * column / (BracketSamples - 1));
                double y = minY + ((maxY - minY) * row / (BracketSamples - 1));
                if (first.DistanceTo(x, y) == 0.0)
                {
                    Take(x, y);
                }
            }
        }

        // Nothing the camera stands at or below is ground it looks down on.
        high = Math.Min(high, cz - LeastHeightMetres);
        low = Math.Min(low, high);
        bracket = (low, high);
        if (high - low < 1e-6)
        {
            return high == nominal
                ? first
                : Project(pose, optics, high, GroundRadius(rangeCapMetres, cz - high));
        }

        // Both planes take the disc of the nearer one, which is the larger: a surface point between
        // the planes is no farther out than the nearer plane's disc allows.
        double radius = GroundRadius(rangeCapMetres, cz - high);
        return GroundPolygon.Hull(Project(pose, optics, low, radius), Project(pose, optics, high, radius));
    }

    /// <summary>How far out on the ground a slant range reaches from a camera this far above it.</summary>
    public static double GroundRadius(double slantRangeMetres, double heightMetres) =>
        slantRangeMetres > heightMetres
            ? Math.Sqrt((slantRangeMetres * slantRangeMetres) - (heightMetres * heightMetres))
            : 0.0;

    /// <summary>The footprint in the report's words.</summary>
    public override string ToString() =>
        FormattableString.Invariant($"camera {Actor}: {Optics}; capped at {RangeCapMetres:0} m, where a ")
        + FormattableString.Invariant($"{ReferenceBodyMetres:0.##} m body covers {MinimumPixels:0.##} px; ")
        + FormattableString.Invariant($"{AltitudeMetres:0} m above the ground; {Now.Area / 1e6:0.###} sq km in view")
        + (SpeedMetresPerSecond > 0.0
            ? FormattableString.Invariant($", moving at {SpeedMetresPerSecond:0.#} m/s")
            : string.Empty);

    /// <summary>
    /// A camera's forward, right and up axes, as CARLA turns a rotation into axes
    /// (<c>Math::GetForwardVector</c>, <c>GetRightVector</c>, <c>GetUpVector</c> in
    /// <c>LibCarla/source/carla/geom/Math.cpp</c>), so the picture's right and up are the engine's.
    /// </summary>
    internal static (Vector Forward, Vector Right, Vector Up) Axes(Rotation rotation)
    {
        double pitch = rotation.Pitch * Math.PI / 180.0;
        double yaw = rotation.Yaw * Math.PI / 180.0;
        double roll = rotation.Roll * Math.PI / 180.0;
        double cp = Math.Cos(pitch);
        double sp = Math.Sin(pitch);
        double cy = Math.Cos(yaw);
        double sy = Math.Sin(yaw);
        double cr = Math.Cos(roll);
        double sr = Math.Sin(roll);
        return (new Vector(cy * cp, sy * cp, sp),
                new Vector((cy * sp * sr) - (sy * cr), (sy * sp * sr) + (cy * cr), -cp * sr),
                new Vector((-cy * sp * cr) - (sy * sr), (-sy * sp * cr) + (cy * sr), cp * cr));
    }

    /// <summary>A direction in the CARLA frame, in doubles.</summary>
    internal readonly record struct Vector(double X, double Y, double Z)
    {
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vector operator *(Vector a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);

        public static double Dot(Vector a, Vector b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

        public static Vector Cross(Vector a, Vector b) =>
            new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));
    }
}
