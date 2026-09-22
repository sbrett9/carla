namespace CarlaNet.CoSim;

/// <summary>
/// A render set drawn as a disc around a fixed point on the network, with distinct radii for
/// subscribing, admitting and releasing.
/// </summary>
/// <remarks>
/// <para>The simplest policy that has the three properties the interface asks for, and the one a
/// capture that points its cameras at one place actually needs. A camera-footprint policy replaces
/// it without the bridge changing: the bridge only ever asks the two predicates and the rank.</para>
///
/// <para><b>Three radii, in order.</b> A vehicle is subscribed further out than it is rendered, so
/// its state is already arriving when the render decision is made; it is released further out than
/// it is admitted, so a vehicle sitting on the boundary does not flicker. Release before
/// unsubscribe, for the same reason one step further out.</para>
/// </remarks>
public sealed class RegionRenderSetPolicy : IRenderSetPolicy
{
    private readonly double _centreX;
    private readonly double _centreY;
    private readonly double _admitRadius;
    private readonly double _releaseRadius;
    private readonly double _subscribeRadius;
    private readonly double _unsubscribeRadius;

    /// <param name="centreX">Region centre, SUMO easting in metres.</param>
    /// <param name="centreY">Region centre, SUMO northing in metres.</param>
    /// <param name="admitRadiusMetres">Inside this, a subscribed vehicle is admitted to the render set.</param>
    /// <param name="hysteresisMetres">
    /// How much further out than it was admitted a vehicle is released, and how much further out
    /// again it is unsubscribed. Zero is refused: a policy with no hysteresis oscillates.
    /// </param>
    /// <param name="capacity">How many vehicles may be rendered at once.</param>
    public RegionRenderSetPolicy(double centreX,
                                 double centreY,
                                 double admitRadiusMetres,
                                 double hysteresisMetres,
                                 int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(admitRadiusMetres);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hysteresisMetres);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _centreX = centreX;
        _centreY = centreY;
        _admitRadius = admitRadiusMetres;
        _releaseRadius = admitRadiusMetres + hysteresisMetres;
        _subscribeRadius = _releaseRadius + hysteresisMetres;
        _unsubscribeRadius = _subscribeRadius + hysteresisMetres;
        Capacity = capacity;
    }

    /// <inheritdoc/>
    public int Capacity { get; }

    /// <summary>Region centre, for a report that has to say where the render set was.</summary>
    public (double X, double Y) Centre => (_centreX, _centreY);

    /// <summary>The four radii, outermost last.</summary>
    public (double Admit, double Release, double Subscribe, double Unsubscribe) Radii =>
        (_admitRadius, _releaseRadius, _subscribeRadius, _unsubscribeRadius);

    /// <inheritdoc/>
    public bool ShouldSubscribe(double x, double y, bool alreadySubscribed) =>
        SquaredDistance(x, y) <= Square(alreadySubscribed ? _unsubscribeRadius : _subscribeRadius);

    /// <inheritdoc/>
    public bool ShouldRender(in CoSimVehicleFrame frame, bool alreadyRendered) =>
        SquaredDistance(frame.X, frame.Y) <= Square(alreadyRendered ? _releaseRadius : _admitRadius);

    /// <inheritdoc/>
    /// <remarks>
    /// Nearest to the centre first. A pure function of the vehicle's position, so two runs of one
    /// seed admit the same set in the same order; the caller breaks a tie on the vehicle id, which
    /// SUMO assigns deterministically from the same seed.
    /// </remarks>
    public double Rank(in CoSimVehicleFrame frame) => SquaredDistance(frame.X, frame.Y);

    private double SquaredDistance(double x, double y)
    {
        double dx = x - _centreX;
        double dy = y - _centreY;
        return (dx * dx) + (dy * dy);
    }

    private static double Square(double value) => value * value;
}
