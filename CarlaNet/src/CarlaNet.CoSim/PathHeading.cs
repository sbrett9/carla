namespace CarlaNet.CoSim;

/// <summary>
/// Each body's heading from the path its front bumper actually takes, and its velocity along that path.
/// </summary>
/// <remarks>
/// <para><b>Why not the lane's tangent.</b> SUMO draws a lane as a polyline, and a junction's connectors
/// with five points: measured on the shipped worlds, the largest corner of a turning connector is a
/// median 31–35° and as much as 98°. A heading taken from the tangent under the bumper changes only at
/// those corners, all of it in the tick the bumper crosses one -- every yaw step measured on Gardnerville
/// (21,230) and on Arapahoe (49,455) fell at a vertex, 24–25° at the 99th percentile of ticks moving
/// through a junction and 53° and 178° at worst ([issue #38](https://github.com/sbrett9/carla/issues/38)).
/// And through a lane change the tangent does not turn at all, so a body slid a lane width sideways
/// pointing straight down the road.</para>
///
/// <para><b>The model: the rear axle follows the bumper.</b> A rigid vehicle's rear axle trails its
/// front along the path the front takes, so its heading <c>θ</c> -- the direction from rear axle to
/// bumper -- turns towards the direction <c>φ</c> the bumper is moving, as <c>dθ/ds = sin(φ − θ) / L</c>
/// over the forward travel <c>s</c>, <c>L</c> the distance from the bumper back to the rear axle. Over a
/// step of forward travel <c>d</c> in one direction the exact solution is
/// <c>tan((φ − θ′)/2) = tan((φ − θ)/2)·e^(−d/L)</c>, which <see cref="Follow"/> takes. The bumper's
/// direction includes its sideways movement, so a body turns into a lane change and out of it again,
/// and its yaw rate can never exceed <c>v / L</c>.</para>
///
/// <para><b>Turned only by forward travel, and held across a jump.</b> The travel <c>d</c> is the speed
/// SUMO reports times the time since the body's last pose, not the distance the bumper moved: a vehicle
/// SUMO moves sideways while standing in a queue -- the lane-change model allows 1 m/s across at a
/// standstill -- slides without turning, and a standing body holds its heading. A bumper that moved
/// more than its forward travel plus <see cref="JumpAllowanceMetres"/> made a move no vehicle drives --
/// SUMO switching a changing vehicle onto a lane that does not run parallel, or pulling a vehicle in to
/// park -- and the heading is held through it. A body's first pose, and the first after the
/// interpolation found no route between two frames, starts from the angle SUMO reported.</para>
///
/// <para><b>The velocity is the path's.</b> Where the heading followed the path, the velocity is the
/// bumper's movement since the last pose over the time since it, lateral movement included, so the
/// truth's course and speed are the motion the imagery shows; elsewhere it is SUMO's speed along the
/// heading. The bumper itself stays where SUMO put it: only the body's rotation about it is this
/// class's.</para>
///
/// <para><b>Measured</b> in world-less sessions over the recompiled scenarios (2026-10-02), Gardnerville's
/// whole run and 300 s of Arapahoe's morning peak, against the lane tangent the bridge took before:
/// moving through junctions the per-tick heading change at the 99th percentile falls from 24.1° to 3.5°
/// and from 25.0° to 4.7°, the worst from 53.2° and 177.8° to 4.9° and 19.5°, the share of junction ticks
/// turning faster than <c>v / 5 m</c> from 4.7 % and 5.2 % to 0.7 % and 0.2 %, and steps over 15° while
/// moving from 6,573 and 4,719 to 0 and 18; no standing body turns. Through a lane change at 3 m/s or
/// more the heading is 0.1° from the body's course at the median and 13.7° at the 99th percentile, where
/// the lane tangent was 3.0° and 19.5° off it. It is within 10.3° and 10.9° of SUMO's reported angle at
/// the 99th percentile while moving: SUMO's angle is the chord from the vehicle's back to its front,
/// which trails the path about as a rear axle at half the length would, and it steps by more than 15°
/// 30 and 363 times over the same runs.</para>
/// </remarks>
public sealed class PathHeading
{
    /// <summary>
    /// The rear axle's distance behind the front bumper, as a fraction of the body's measured length:
    /// 3.5 m on a 4.7 m car, whose front overhang is about 0.9 m and wheelbase 2.8 m; 0.73 on a small bus
    /// with a 1.1 m overhang and a 4 m wheelbase; about 0.75 on a rigid lorry. A rear axle that does not
    /// slide sideways makes the model exact for the bumper at this distance.
    /// </summary>
    public const double RearAxleFractionOfLength = 0.75;

    /// <summary>
    /// How much further than its forward travel a bumper may move between two poses and the move still be
    /// driven, metres: a lane change crosses about 0.06 m per 0.05 s tick, and SUMO's own jumps are metres.
    /// </summary>
    public const double JumpAllowanceMetres = 1.0;

    private readonly Dictionary<string, Track> _tracks = [];

    /// <summary>The rear axle's distance behind the front bumper for a measured body, metres.</summary>
    public static double RearAxleMetres(in VehicleExtent extent) => RearAxleFractionOfLength * extent.LengthMetres;

    /// <summary>
    /// Advance one body's heading to its pose at a tick, and say what velocity the path gives it.
    /// </summary>
    /// <param name="vehicleId">The SUMO vehicle.</param>
    /// <param name="tickIndex">The tick the pose is for.</param>
    /// <param name="x">Projected easting of the bumper at this tick, metres.</param>
    /// <param name="y">Projected northing of the bumper, metres.</param>
    /// <param name="speedMetresPerSecond">SUMO's speed at this tick.</param>
    /// <param name="secondsPerTick">The world's fixed delta.</param>
    /// <param name="rearAxleMetres">The body's rear axle distance (<see cref="RearAxleMetres"/>).</param>
    /// <param name="sumoAngleDegrees">SUMO's reported angle at this tick, which a heading starts from.</param>
    /// <param name="restart">Start again from SUMO's angle: the interpolation found no route here.</param>
    public Step Advance(string vehicleId, long tickIndex, double x, double y, double speedMetresPerSecond,
                        double secondsPerTick, double rearAxleMetres, double sumoAngleDegrees, bool restart)
    {
        ArgumentNullException.ThrowIfNull(vehicleId);
        if (restart || !_tracks.TryGetValue(vehicleId, out Track last) || tickIndex <= last.TickIndex)
        {
            double started = Normalised(sumoAngleDegrees);
            _tracks[vehicleId] = new Track(tickIndex, x, y, speedMetresPerSecond, started);
            return new Step(started, null, Held: false);
        }

        double seconds = (tickIndex - last.TickIndex) * secondsPerTick;
        double forward = 0.5 * (speedMetresPerSecond + last.SpeedMetresPerSecond) * seconds;
        double dx = x - last.X;
        double dy = y - last.Y;
        double moved = Math.Sqrt((dx * dx) + (dy * dy));

        double heading = last.HeadingDegrees;
        (double X, double Y)? velocity = null;
        bool held = moved > forward + JumpAllowanceMetres;
        if (!held && seconds > 0.0)
        {
            velocity = (dx / seconds, dy / seconds);
            if (forward > 0.0 && moved > 0.0)
            {
                heading = Follow(heading, Normalised(Math.Atan2(dx, dy) * (180.0 / Math.PI)), forward,
                                 rearAxleMetres);
            }
        }

        _tracks[vehicleId] = new Track(tickIndex, x, y, speedMetresPerSecond, heading);
        return new Step(heading, velocity, held);
    }

    /// <summary>Forget a body's track: the vehicle left the render set, or SUMO removed it.</summary>
    public void Forget(string vehicleId) => _tracks.Remove(vehicleId);

    /// <summary>
    /// The heading after <paramref name="forwardMetres"/> of forward travel with the bumper moving in
    /// <paramref name="pathDegrees"/>: the exact solution of <c>dθ/ds = sin(φ − θ) / L</c> over the step.
    /// </summary>
    /// <param name="headingDegrees">The heading before the step, degrees clockwise from north.</param>
    /// <param name="pathDegrees">The direction the bumper moved, degrees clockwise from north.</param>
    /// <param name="forwardMetres">Forward travel over the step.</param>
    /// <param name="rearAxleMetres">The rear axle's distance behind the bumper.</param>
    public static double Follow(double headingDegrees, double pathDegrees, double forwardMetres,
                                double rearAxleMetres)
    {
        double behind = Math.IEEERemainder(pathDegrees - headingDegrees, 360.0) * (Math.PI / 180.0);
        double half = Math.Atan(Math.Tan(behind / 2.0) * Math.Exp(-forwardMetres / rearAxleMetres));
        return Normalised(pathDegrees - (2.0 * half * (180.0 / Math.PI)));
    }

    private static double Normalised(double degrees)
    {
        double wrapped = degrees % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }

    /// <summary>A body's heading at a tick, and the velocity its path gives it.</summary>
    /// <param name="HeadingDegrees">Degrees clockwise from north.</param>
    /// <param name="Velocity">
    /// The bumper's movement since the body's last pose over the time since it, in SUMO's frame (east,
    /// north), metres per second; null on a first pose and across a jump, where SUMO's speed along the
    /// heading stands in.
    /// </param>
    /// <param name="Held">Whether the heading was held across a move no vehicle drives.</param>
    public readonly record struct Step(double HeadingDegrees, (double X, double Y)? Velocity, bool Held);

    private readonly record struct Track(long TickIndex, double X, double Y, double SpeedMetresPerSecond,
                                         double HeadingDegrees);
}
