// Source: carla/rpc/OrbitParameters.h
// OrbitParameters MSGPACK_DEFINE_ARRAY(centre_x_m, centre_y_m, centre_z_m, radius_m, altitude_m, period_s,
//                                      clockwise, start_angle_rad, pitch_overridden, pitch_deg, enabled)
namespace CarlaNet.Types.Rpc.Orbit;

/// <summary>
/// How the server flies an actor round a circle (<c>set_orbit</c>): the orbit mover it puts on the actor
/// advances the angle on the simulation clock each tick and sets the actor on the circle with its
/// boresight on the centre, so a client sends these once and no pose after.
/// </summary>
/// <remarks>
/// Every length is in CARLA metres, in CARLA's frame. The pose at an angle <c>a</c> is the client's
/// rule (<c>OrbitSensorController.orbit_transform</c>): position <c>(centre_x + radius cos a, centre_y +
/// radius sin a, centre_z + altitude)</c>, yaw towards the centre, pitch the depression to it, roll zero.
/// With the angle increasing the actor goes from the centre's east through its south, which is clockwise
/// seen from above in a world where -y is north.
/// </remarks>
/// <param name="CentreXMetres">The centre's x, CARLA metres.</param>
/// <param name="CentreYMetres">The centre's y, CARLA metres.</param>
/// <param name="CentreZMetres">The centre's height, CARLA metres; the altitude is measured from it.</param>
/// <param name="RadiusMetres">Positive.</param>
/// <param name="AltitudeMetres">Above the centre's height.</param>
/// <param name="PeriodSeconds">Simulated seconds per revolution; positive.</param>
/// <param name="Clockwise">The angle increases (east through south seen from above); false runs it the other way.</param>
/// <param name="StartAngleRadians">Where the orbit begins; zero is east of the centre.</param>
/// <param name="PitchOverridden">Hold the pitch at <paramref name="PitchDegrees"/> instead of the depression to the centre.</param>
/// <param name="PitchDegrees">The pitch held when <paramref name="PitchOverridden"/>.</param>
/// <param name="Enabled">
/// Start moving at once. False configures the mover and leaves the actor where it is until
/// <c>set_orbit_enabled</c>; a client that holds an opening pose through a pre-roll sends that.
/// </param>
[MessagePackObject]
public record struct OrbitParameters(
    [property: Key(0)] double CentreXMetres,
    [property: Key(1)] double CentreYMetres,
    [property: Key(2)] double CentreZMetres,
    [property: Key(3)] double RadiusMetres,
    [property: Key(4)] double AltitudeMetres,
    [property: Key(5)] double PeriodSeconds,
    [property: Key(6)] bool Clockwise,
    [property: Key(7)] double StartAngleRadians,
    [property: Key(8)] bool PitchOverridden,
    [property: Key(9)] double PitchDegrees,
    [property: Key(10)] bool Enabled)
{
    /// <summary>A circle with no pitch override, clockwise, from angle zero, moving at once.</summary>
    public OrbitParameters(
        double centreXMetres, double centreYMetres, double centreZMetres,
        double radiusMetres, double altitudeMetres, double periodSeconds)
        : this(centreXMetres, centreYMetres, centreZMetres, radiusMetres, altitudeMetres, periodSeconds,
               Clockwise: true, StartAngleRadians: 0.0, PitchOverridden: false, PitchDegrees: 0.0, Enabled: true)
    {
    }

    /// <summary>The angular rate the mover advances at, radians per simulated second, signed by direction.</summary>
    [IgnoreMember]
    public double AngularRateRadiansPerSecond =>
        (Clockwise ? 1.0 : -1.0) * (2.0 * Math.PI / PeriodSeconds);

    /// <summary>
    /// The angle the mover holds after <paramref name="elapsedSeconds"/> of simulated time moving from the
    /// start angle, in [0, 2 pi): what a client predicts without asking the server.
    /// </summary>
    public double AngleAfter(double elapsedSeconds)
    {
        double angle = (StartAngleRadians + AngularRateRadiansPerSecond * elapsedSeconds) % (2.0 * Math.PI);
        return angle < 0.0 ? angle + 2.0 * Math.PI : angle;
    }

    /// <summary>
    /// The pose on the circle at an angle, the rule the server flies: on the circle at the centre's height
    /// plus the altitude, yaw to the centre, pitch the depression to it unless overridden, roll zero.
    /// </summary>
    public Transform PoseAt(double angleRadians)
    {
        double x = CentreXMetres + RadiusMetres * Math.Cos(angleRadians);
        double y = CentreYMetres + RadiusMetres * Math.Sin(angleRadians);
        double z = CentreZMetres + AltitudeMetres;
        double dx = CentreXMetres - x;
        double dy = CentreYMetres - y;
        double dz = CentreZMetres - z;
        double horizontal = Math.Sqrt(dx * dx + dy * dy);
        double pitch = PitchOverridden ? PitchDegrees : Math.Atan2(dz, horizontal) * 180.0 / Math.PI;
        double yaw = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        return new Transform(new Location((float)x, (float)y, (float)z),
                             new Rotation((float)pitch, (float)yaw, 0.0f));
    }

    /// <summary>Why the server would refuse this circle, or null where it would take it.</summary>
    public string? Problem()
    {
        if (!double.IsFinite(CentreXMetres) || !double.IsFinite(CentreYMetres) || !double.IsFinite(CentreZMetres))
        {
            return "The center is three finite numbers of meters.";
        }

        if (!double.IsFinite(RadiusMetres) || RadiusMetres <= 0.0)
        {
            return "The radius is a positive number of meters.";
        }

        if (!double.IsFinite(AltitudeMetres))
        {
            return "The altitude is a finite number of meters above the center.";
        }

        if (!double.IsFinite(PeriodSeconds) || PeriodSeconds <= 0.0)
        {
            return "The period is a positive number of simulated seconds per revolution.";
        }

        if (!double.IsFinite(StartAngleRadians))
        {
            return "The start angle is a finite number of radians.";
        }

        if (PitchOverridden && !double.IsFinite(PitchDegrees))
        {
            return "A pitch override is a finite number of degrees.";
        }

        return null;
    }
}
