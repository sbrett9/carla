using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// A render set drawn as a disc around a fixed point on the network, admitted inside one radius and
/// released beyond a wider one.
/// </summary>
/// <remarks>
/// <para>An optional performance control, off unless chosen: the default draws every vehicle SUMO has
/// (<see cref="EveryVehicleRenderSetPolicy"/>). A circle suits a capture that points its cameras at one
/// place; a vehicle outside it is simulated by SUMO and has no body in CARLA and no truth record. The
/// camera-footprint policy (<see cref="CameraFootprintRenderSetPolicy"/>) can fall back to it while no
/// camera is registered.</para>
///
/// <para><b>Two radii, in order.</b> A vehicle is released further out than it is admitted, so one
/// sitting on the boundary does not flicker. The circle has no cameras, no lead and no lag: a vehicle
/// is released at the first pass it is beyond the release radius.</para>
///
/// <para><b>Under a capacity</b>, also optional, the vehicles nearest the centre are drawn first,
/// whether drawn already or not, so a newcomer nearer the centre takes the place of the farthest
/// vehicle drawn, which is released for the capacity.</para>
/// </remarks>
public sealed class RegionRenderSetPolicy : IRenderSetPolicy
{
    private readonly double _centreX;
    private readonly double _centreY;
    private readonly double _admitRadius;
    private readonly double _releaseRadius;

    /// <param name="centreX">Region centre, SUMO easting in metres.</param>
    /// <param name="centreY">Region centre, SUMO northing in metres.</param>
    /// <param name="admitRadiusMetres">Inside this, a vehicle is admitted to the render set.</param>
    /// <param name="hysteresisMetres">
    /// How much further out than it was admitted a vehicle is released. Zero is refused: a policy with
    /// no hysteresis oscillates.
    /// </param>
    /// <param name="capacity">How many vehicles may be rendered at once, or null for no limit on the count.</param>
    public RegionRenderSetPolicy(double centreX,
                                 double centreY,
                                 double admitRadiusMetres,
                                 double hysteresisMetres,
                                 int? capacity = null)
    {
        if (!double.IsFinite(centreX) || !double.IsFinite(centreY))
        {
            throw new ArgumentOutOfRangeException(nameof(centreX), "the center must be a finite point");
        }

        if (!double.IsFinite(admitRadiusMetres))
        {
            throw new ArgumentOutOfRangeException(nameof(admitRadiusMetres), admitRadiusMetres, "must be finite");
        }

        if (!double.IsFinite(hysteresisMetres))
        {
            throw new ArgumentOutOfRangeException(nameof(hysteresisMetres), hysteresisMetres, "must be finite");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(admitRadiusMetres);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hysteresisMetres);
        if (capacity is { } count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count, nameof(capacity));
        }

        _centreX = centreX;
        _centreY = centreY;
        _admitRadius = admitRadiusMetres;
        _releaseRadius = admitRadiusMetres + hysteresisMetres;
        Capacity = capacity;
    }

    /// <inheritdoc/>
    public int? Capacity { get; }

    /// <inheritdoc/>
    public bool Limits => true;

    /// <inheritdoc/>
    public double AdmitLeadSeconds => 0.0;

    /// <inheritdoc/>
    public double ReleaseLagSeconds => 0.0;

    /// <inheritdoc/>
    public RenderSetRule ActiveRule => RenderSetRule.Circle;

    /// <inheritdoc/>
    public IReadOnlyList<CameraFootprint> Footprints => [];

    /// <summary>Region centre, for a report that has to say where the render set was.</summary>
    public (double X, double Y) Centre => (_centreX, _centreY);

    /// <summary>The two radii, the wider last.</summary>
    public (double Admit, double Release) Radii => (_admitRadius, _releaseRadius);

    /// <summary>How much further out than it was admitted a vehicle is released.</summary>
    public double HysteresisMetres => _releaseRadius - _admitRadius;

    /// <inheritdoc/>
    public string Description =>
        FormattableString.Invariant(
            $"circle of {_admitRadius:0.#} m around ({_centreX:0.#}, {_centreY:0.#}) in SUMO meters, released beyond {_releaseRadius:0.#} m")
        + (Capacity is { } capacity
            ? ", up to " + capacity.ToString(CultureInfo.InvariantCulture) + " at once, nearest the center first"
            : string.Empty);

    /// <inheritdoc/>
    /// <remarks>Nothing to take: the circle is fixed for the session.</remarks>
    public void BeginPass(RenderSetPass pass) => ArgumentNullException.ThrowIfNull(pass);

    /// <inheritdoc/>
    public bool ShouldRender(in CoSimVehicleFrame frame, bool alreadyRendered) =>
        SquaredDistance(frame.X, frame.Y) <= Square(alreadyRendered ? _releaseRadius : _admitRadius);

    /// <inheritdoc/>
    /// <remarks>
    /// Nearest to the centre first, whether rendered or not. A pure function of the vehicle's position,
    /// so two runs of one seed admit the same set in the same order; the caller breaks a tie on the
    /// vehicle id, which SUMO assigns deterministically from the same seed.
    /// </remarks>
    public double Rank(in CoSimVehicleFrame frame, bool alreadyRendered) => SquaredDistance(frame.X, frame.Y);

    private double SquaredDistance(double x, double y)
    {
        double dx = x - _centreX;
        double dy = y - _centreY;
        return (dx * dx) + (dy * dy);
    }

    private static double Square(double value) => value * value;
}
