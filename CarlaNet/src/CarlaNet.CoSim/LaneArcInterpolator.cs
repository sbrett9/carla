namespace CarlaNet.CoSim;

/// <summary>
/// Produces a vehicle's state at any instant between two SUMO frames, along the lanes it is
/// actually on.
/// </summary>
/// <remarks>
/// <para><b>Why not the straight line between the two reported points.</b> A junction connector on a
/// real network has a median length of about eight metres and turns through whatever the junction
/// turns through. A vehicle at traffic speed crosses one in a fraction of a SUMO step, so two
/// consecutive samples sit on opposite sides of the turn, and the chord between them passes several
/// lane widths inside the corner. A vehicle rendered on that chord drives across the pavement, and
/// every frame of it is imagery a detector would be trained on.</para>
///
/// <para>The buffered pair of frames is what makes this possible: SUMO runs one step ahead of the
/// rendered clock, so both ends of every interpolation are known rather than extrapolated. The cost
/// is a constant, known step of simulated latency, which nothing in a stored capture can notice.</para>
/// </remarks>
public sealed class LaneArcInterpolator
{
    /// <summary>
    /// How much further than the faster of the two frames could have travelled counts as a
    /// discontinuity.
    /// </summary>
    private const double DistanceAllowance = 1.5;

    /// <summary>
    /// The along-route distance below which a step is never called discontinuous however slow the
    /// vehicle is, in metres. A vehicle stopped at both ends reports zero speed, which would make
    /// any movement at all exceed a speed-derived limit of zero.
    /// </summary>
    private const double StationaryAllowanceMetres = 1.0;

    private readonly SumoRoadNetwork _network;

    public LaneArcInterpolator(SumoRoadNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        _network = network;
    }

    /// <summary>
    /// The state at a fraction of the way from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <param name="from">The earlier SUMO frame.</param>
    /// <param name="to">The later one, which the bridge already holds.</param>
    /// <param name="fraction">Where between them, from zero at the earlier frame.</param>
    /// <param name="stepSeconds">The SUMO step the two frames are apart.</param>
    public InterpolatedState Interpolate(in CoSimVehicleFrame from,
                                         in CoSimVehicleFrame to,
                                         double fraction,
                                         double stepSeconds)
    {
        double speed = from.SpeedMetresPerSecond
                       + ((to.SpeedMetresPerSecond - from.SpeedMetresPerSecond) * fraction);

        if (!_network.TryGetLane(from.LaneId, out SumoLane fromLane)
            || !_network.TryGetLane(to.LaneId, out SumoLane toLane))
        {
            return Reported(to, speed, LaneInterpolationCase.Discontinuous);
        }

        if (from.LaneId == to.LaneId)
        {
            double covered = DistanceFraction(from.SpeedMetresPerSecond, to.SpeedMetresPerSecond,
                                              fraction);
            double along = from.LanePositionMetres
                           + ((to.LanePositionMetres - from.LanePositionMetres) * covered);
            return Evaluate(fromLane, along, speed, LaneInterpolationCase.SameLane);
        }

        if (from.EdgeId == to.EdgeId)
        {
            return AcrossLanes(fromLane, toLane, from, to, fraction, speed);
        }

        return AlongRoute(fromLane, toLane, from, to, fraction, speed, stepSeconds);
    }

    /// <summary>
    /// The along-route distance between two frames, or <see langword="null"/> where no route of a
    /// few connections joins them.
    /// </summary>
    /// <remarks>
    /// Exposed because it is the quantity the discontinuity test is made on, and a run that wants to
    /// report how far its vehicles travelled per step needs the same number.
    /// </remarks>
    public double? RouteDistance(in CoSimVehicleFrame from, in CoSimVehicleFrame to)
    {
        if (!_network.TryGetLane(from.LaneId, out SumoLane fromLane)
            || !_network.TryGetLane(to.LaneId, out SumoLane _))
        {
            return null;
        }

        if (from.LaneId == to.LaneId)
        {
            return to.LanePositionMetres - from.LanePositionMetres;
        }

        IReadOnlyList<SumoLane>? path = FindRoute(from, to, out _);
        if (path is null)
        {
            return null;
        }

        double distance = fromLane.DeclaredLengthMetres - from.LanePositionMetres;
        for (int index = 0; index < path.Count - 1; index++)
        {
            distance += path[index].DeclaredLengthMetres;
        }

        return distance + to.LanePositionMetres;
    }

    /// <summary>
    /// The lanes from one frame to the next, and the lane the route actually arrives on.
    /// </summary>
    /// <remarks>
    /// <para>A vehicle that leaves a junction and changes lane inside one SUMO step ends up on a
    /// lane the connector does not feed, so there is no route to it at all. There is a route to its
    /// sibling, and the difference between the two is a sideways move -- which is a lane change, not
    /// a discontinuity. Measured on the shipped Arapahoe scenario at a one-second step, 38 of the
    /// 100 steps sampled contained one; calling them discontinuities releases and re-admits a
    /// vehicle that did nothing but change lane.</para>
    ///
    /// <para>The sibling nearest the reported lane is preferred, because a connector feeding the
    /// lane next door is a one-lane move and one feeding the far side of the carriageway is not.</para>
    /// </remarks>
    private IReadOnlyList<SumoLane>? FindRoute(in CoSimVehicleFrame from,
                                               in CoSimVehicleFrame to,
                                               out SumoLane? arrival)
    {
        arrival = null;
        IReadOnlyList<SumoLane>? direct = _network.FindPath(from.LaneId, to.LaneId);
        if (direct is { Count: > 0 })
        {
            return direct;
        }

        IReadOnlyList<SumoLane> siblings = _network.LanesOfEdge(to.EdgeId);
        int wanted = to.LaneId.LastIndexOf('_') is var underscore and >= 0
                     && int.TryParse(to.LaneId.AsSpan(underscore + 1), out int index)
            ? index
            : 0;

        foreach (SumoLane sibling in siblings.OrderBy(lane => Math.Abs(lane.Index - wanted)))
        {
            if (sibling.Id == to.LaneId)
            {
                continue;
            }

            IReadOnlyList<SumoLane>? route = _network.FindPath(from.LaneId, sibling.Id);
            if (route is { Count: > 0 })
            {
                arrival = sibling;
                return route;
            }
        }

        return null;
    }

    private InterpolatedState AcrossLanes(SumoLane fromLane,
                                          SumoLane toLane,
                                          in CoSimVehicleFrame from,
                                          in CoSimVehicleFrame to,
                                          double fraction,
                                          double speed)
    {
        // Two lanes of one edge run alongside each other, so a lane position means the same distance
        // on both. The vehicle advances along each of them and the two points are blended sideways.
        double covered = DistanceFraction(from.SpeedMetresPerSecond, to.SpeedMetresPerSecond,
                                          fraction);
        double along = from.LanePositionMetres
                       + ((to.LanePositionMetres - from.LanePositionMetres) * covered);
        (double fromX, double fromY, double fromDirectionX, double fromDirectionY) =
            fromLane.PointAt(along);
        (double toX, double toY, double toDirectionX, double toDirectionY) = toLane.PointAt(along);

        // Smoothstep rather than a straight blend, so the sideways movement starts and ends at rest
        // instead of beginning and ending with a corner in the path.
        double blend = fraction * fraction * (3.0 - (2.0 * fraction));
        double x = fromX + ((toX - fromX) * blend);
        double y = fromY + ((toY - fromY) * blend);
        double directionX = fromDirectionX + ((toDirectionX - fromDirectionX) * blend);
        double directionY = fromDirectionY + ((toDirectionY - fromDirectionY) * blend);

        return new InterpolatedState(x, y, Heading(directionX, directionY), speed,
                                     LaneInterpolationCase.LaneChange);
    }

    private InterpolatedState AlongRoute(SumoLane fromLane,
                                         SumoLane toLane,
                                         in CoSimVehicleFrame from,
                                         in CoSimVehicleFrame to,
                                         double fraction,
                                         double speed,
                                         double stepSeconds)
    {
        IReadOnlyList<SumoLane>? path = FindRoute(from, to, out SumoLane? arrival);
        if (path is null)
        {
            return Reported(to, speed, LaneInterpolationCase.Discontinuous);
        }

        double distance = fromLane.DeclaredLengthMetres - from.LanePositionMetres;
        for (int index = 0; index < path.Count - 1; index++)
        {
            distance += path[index].DeclaredLengthMetres;
        }

        distance += to.LanePositionMetres;

        double reachable = Math.Max(from.SpeedMetresPerSecond, to.SpeedMetresPerSecond)
                           * stepSeconds * DistanceAllowance;
        if (distance > Math.Max(reachable, StationaryAllowanceMetres))
        {
            return Reported(to, speed, LaneInterpolationCase.Discontinuous);
        }

        LaneInterpolationCase which = arrival is null
            ? LaneInterpolationCase.CrossedEdges
            : LaneInterpolationCase.CrossedEdgesWithLaneChange;

        // Walk the concatenated shape: the tail of the lane the vehicle started on, then each lane
        // of the route in turn, then the head of the one it arrives on.
        double travelled = distance * DistanceFraction(from.SpeedMetresPerSecond,
                                                       to.SpeedMetresPerSecond, fraction);
        double remainingOnFirst = fromLane.DeclaredLengthMetres - from.LanePositionMetres;
        if (travelled <= remainingOnFirst)
        {
            return Evaluate(fromLane, from.LanePositionMetres + travelled, speed, which);
        }

        travelled -= remainingOnFirst;
        for (int index = 0; index < path.Count - 1; index++)
        {
            if (travelled <= path[index].DeclaredLengthMetres)
            {
                return Evaluate(path[index], travelled, speed, which);
            }

            travelled -= path[index].DeclaredLengthMetres;
        }

        // On the final lane of the route. Where that is not the lane the vehicle reported, the
        // difference between the two is the lane change, blended sideways over the step exactly as
        // one within an edge is.
        SumoLane last = arrival ?? toLane;
        if (arrival is null)
        {
            return Evaluate(last, travelled, speed, which);
        }

        (double alongX, double alongY, double alongDirectionX, double alongDirectionY) =
            last.PointAt(travelled);
        (double sideX, double sideY, double sideDirectionX, double sideDirectionY) =
            toLane.PointAt(travelled);
        double blend = fraction * fraction * (3.0 - (2.0 * fraction));
        return new InterpolatedState(
            alongX + ((sideX - alongX) * blend),
            alongY + ((sideY - alongY) * blend),
            Heading(alongDirectionX + ((sideDirectionX - alongDirectionX) * blend),
                    alongDirectionY + ((sideDirectionY - alongDirectionY) * blend)),
            speed,
            which);
    }

    /// <summary>
    /// How much of a step's distance has been covered by a given fraction of its time, given the
    /// speed at each end.
    /// </summary>
    /// <remarks>
    /// <para>Distance is not linear in time across a step during which the vehicle's speed changed,
    /// and the error from pretending it is lands entirely along the vehicle's own track. Measured on
    /// a recorded run of the fixture scenario, sampled at the world's tick rate and then subsampled
    /// to one-second SUMO steps: a linear-in-time distance is out by up to <b>0.563 m</b> at the
    /// worst instant, which is a quarter of a car, and the worst instants are where a vehicle brakes
    /// for a junction. The bound is the step's acceleration times the square of its length over
    /// eight, and SUMO's default deceleration of 4.5 m/s squared over a one-second step gives
    /// exactly the figure measured.</para>
    ///
    /// <para>Integrating a linear ramp of speed instead costs three multiplications and removes it:
    /// the same measurement falls to a few centimetres. The step length cancels, so this is a
    /// fraction of the distance actually travelled rather than a distance in its own right, which is
    /// what keeps it right for a step SUMO's own discretisation did not cover at the average of the
    /// two speeds.</para>
    /// </remarks>
    private static double DistanceFraction(double fromSpeed, double toSpeed, double fraction)
    {
        double mean = (fromSpeed + toSpeed) * 0.5;
        if (mean <= 0.0)
        {
            return fraction;
        }

        return ((fromSpeed * fraction) + ((toSpeed - fromSpeed) * fraction * fraction * 0.5)) / mean;
    }

    private static InterpolatedState Evaluate(SumoLane lane,
                                              double lanePosition,
                                              double speed,
                                              LaneInterpolationCase which)
    {
        (double x, double y, double directionX, double directionY) = lane.PointAt(lanePosition);
        return new InterpolatedState(x, y, Heading(directionX, directionY), speed, which);
    }

    private static InterpolatedState Reported(in CoSimVehicleFrame frame,
                                              double speed,
                                              LaneInterpolationCase which) =>
        new(frame.X, frame.Y, frame.HeadingDegrees, speed, which);

    /// <summary>A direction in the projected frame, as degrees clockwise from north.</summary>
    private static double Heading(double directionX, double directionY)
    {
        double degrees = Math.Atan2(directionX, directionY) * (180.0 / Math.PI);
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }
}
