using System.Globalization;
using CarlaNet.Types.Geom;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// A render set that follows the cameras: a vehicle is rendered while it is inside, or about to enter,
/// the ground footprint of any camera registered with the session, and the configured circle decides
/// while none is.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A fixed circle renders what is inside it, and a camera looking past its edge sees
/// the vehicles there vanish at the release radius: measured live, a free camera flown past the circle
/// saw exactly that. Rendering the whole map instead costs a body for every vehicle nothing is looking
/// at. What the imagery needs is the vehicles each camera can see, placed before they come into view
/// and taken away after they have left it.</para>
///
/// <para><b>Distances, all on the ground in the CARLA frame, from the point SUMO reports a vehicle at.</b>
/// <c>d</c> is how far that point is from the nearest footprint swept over the step the pass decides.
/// The margin <c>m</c> is the bodies' reach -- no part of a body farther than that from its reported
/// point -- plus one SUMO step of travel at the fastest plausible speed <c>v_max</c>, the distance a
/// vehicle covers between the pass that sees it outside and the one that sees it inside. Then, for a
/// vehicle travelling at <c>v</c>:</para>
/// <list type="bullet">
/// <item><b>admitted</b> where <c>d ≤ m + v × AdmitLead</c>: <see cref="AdmitLeadSeconds"/> of its own
/// travel ahead of the view, as well as the margin, so it is placed out of view;</item>
/// <item><b>kept</b> while <c>d ≤ m + v_max × AdmitLead + h</c>, <c>h</c> the circle's own hysteresis --
/// a band the widest admission threshold never reaches, so a vehicle that slows on the boundary is not
/// released and admitted again -- and for <see cref="ReleaseLagSeconds"/> after it last was, which the
/// render-set manager times;</item>
/// <item><b>subscribed</b> where <c>d ≤ m + v_max × AdmitLead + v_max × step</c> against the footprint
/// swept one step further, so that a vehicle admitted at the next pass is delivering its state at this
/// one, and unsubscribed <c>h</c> further out.</item>
/// </list>
///
/// <para><b>A moving camera's footprint is swept along its motion.</b> The render set a pass decides is
/// drawn from the last rendered frame, where the camera's pose is read, to the frame the pass read, a
/// SUMO step later, and the ground a moving camera will look at within the lead must already hold its
/// vehicles. So the camera's motion since the previous pass is carried forward over the step and the
/// lead, and the footprint is the hull of the one at the pose read and the one at the pose it is
/// carried to. A move faster than any flown camera -- a camera sent back to its start pose -- is taken
/// as a jump and carried nowhere.</para>
///
/// <para><b>Ranked under the capacity by what can be seen, then by the seed.</b> A vehicle with part of
/// its body inside a footprint ranks ahead of one only approaching; among either, one already rendered
/// ranks ahead of a newcomer, so a place is never taken from a vehicle in view to give to one arriving;
/// and within those, by its place in the scenario seed's order (<see cref="SeededOrder"/>), which is
/// fixed for the vehicle's life and says nothing about where it is. Held vehicles past their last pass
/// in view rank after all of those; the manager sheds them first.</para>
/// </remarks>
public sealed class CameraFootprintRenderSetPolicy : IRenderSetPolicy
{
    /// <summary>The admit lead unless one is given: the performance section's <c>frustum_lead_s</c>.</summary>
    public const double DefaultAdmitLeadSeconds = 3.0;

    /// <summary>The release lag unless one is given: the performance section's <c>exit_lag_s</c>.</summary>
    public const double DefaultReleaseLagSeconds = 5.0;

    /// <summary>
    /// The fewest pixels along its length a vehicle may cover and still be rendered, unless another is
    /// given: two, the least a shape can span and still be sampled at all.
    /// </summary>
    public const double DefaultMinimumPixels = 2.0;

    /// <summary>
    /// The fastest plausible vehicle, unless another is given: above the fastest lane on the sizing
    /// scenario, 39.44 m/s, and the fastest vehicle sampled on any shipped scenario, 37.59 m/s.
    /// </summary>
    public const double DefaultMaximumSpeedMetresPerSecond = 40.0;

    /// <summary>
    /// A camera that moved faster than this between two passes jumped rather than flew, and its motion
    /// is not carried forward.
    /// </summary>
    private const double CameraJumpMetresPerSecond = 1000.0;

    private readonly RegionRenderSetPolicy _fallback;
    private readonly double _lead;
    private readonly double _lag;
    private readonly double _minimumPixels;
    private readonly double _maximumSpeed;
    private readonly Dictionary<ActorId, (Transform Pose, double At)> _lastSeen = [];
    private readonly HashSet<ActorId> _seenThisPass = [];
    private readonly List<ActorId> _gone = [];
    private IReadOnlyList<CameraFootprint> _footprints = [];
    private double _bodyReach;
    private double _stepTravel;
    private long _seed;

    /// <param name="whereNoCameraIsRegistered">
    /// The circle that decides while no camera is registered, whose capacity is the render set's and
    /// whose hysteresis is the band a rendered vehicle is kept inside.
    /// </param>
    /// <param name="admitLeadSeconds">Simulated seconds of its own travel ahead of a footprint a vehicle is admitted.</param>
    /// <param name="releaseLagSeconds">Simulated seconds a vehicle is held after it last passed.</param>
    /// <param name="minimumPixels">
    /// The fewest pixels along its length the catalogue's longest body may cover at a camera's range cap.
    /// </param>
    /// <param name="maximumSpeedMetresPerSecond">The fastest plausible vehicle.</param>
    public CameraFootprintRenderSetPolicy(RegionRenderSetPolicy whereNoCameraIsRegistered,
                                          double admitLeadSeconds = DefaultAdmitLeadSeconds,
                                          double releaseLagSeconds = DefaultReleaseLagSeconds,
                                          double minimumPixels = DefaultMinimumPixels,
                                          double maximumSpeedMetresPerSecond = DefaultMaximumSpeedMetresPerSecond)
    {
        ArgumentNullException.ThrowIfNull(whereNoCameraIsRegistered);
        RequireFinite(admitLeadSeconds, nameof(admitLeadSeconds), allowZero: true);
        RequireFinite(releaseLagSeconds, nameof(releaseLagSeconds), allowZero: true);
        RequireFinite(minimumPixels, nameof(minimumPixels), allowZero: false);
        RequireFinite(maximumSpeedMetresPerSecond, nameof(maximumSpeedMetresPerSecond), allowZero: false);
        _fallback = whereNoCameraIsRegistered;
        _lead = admitLeadSeconds;
        _lag = releaseLagSeconds;
        _minimumPixels = minimumPixels;
        _maximumSpeed = maximumSpeedMetresPerSecond;
    }

    /// <inheritdoc/>
    public int Capacity => _fallback.Capacity;

    /// <inheritdoc/>
    public double AdmitLeadSeconds => ActiveRule == RenderSetRule.Cameras ? _lead : _fallback.AdmitLeadSeconds;

    /// <inheritdoc/>
    public double ReleaseLagSeconds => ActiveRule == RenderSetRule.Cameras ? _lag : _fallback.ReleaseLagSeconds;

    /// <inheritdoc/>
    public RenderSetRule ActiveRule { get; private set; } = RenderSetRule.Circle;

    /// <inheritdoc/>
    public IReadOnlyList<CameraFootprint> Footprints => _footprints;

    /// <summary>The circle that decides while no camera is registered.</summary>
    public RegionRenderSetPolicy Fallback => _fallback;

    /// <summary>The admit lead the cameras' rule applies, whichever rule is in force.</summary>
    public double CameraAdmitLeadSeconds => _lead;

    /// <summary>The release lag the cameras' rule applies, whichever rule is in force.</summary>
    public double CameraReleaseLagSeconds => _lag;

    /// <summary>The fewest pixels along its length the longest body covers at a range cap.</summary>
    public double MinimumPixels => _minimumPixels;

    /// <summary>The fastest plausible vehicle, metres per second.</summary>
    public double MaximumSpeedMetresPerSecond => _maximumSpeed;

    /// <inheritdoc/>
    public string Description =>
        "cameras: inside or approaching a registered camera's ground footprint, capped where the catalogue's "
        + FormattableString.Invariant($"longest body covers {_minimumPixels:0.##} px; admitted {_lead:0.##} s of ")
        + FormattableString.Invariant($"travel ahead, released {_lag:0.##} s after leaving; a margin of the ")
        + FormattableString.Invariant($"bodies' reach and a step at {_maximumSpeed:0.#} m/s; ranked under the ")
        + "capacity by what is in view, then by the seed. While no camera is registered, the "
        + _fallback.Description;

    /// <inheritdoc/>
    /// <remarks>
    /// Every camera the pass names is projected, capped and swept here, once; the predicates then only
    /// measure distances. A camera the previous pass named and this one does not has its motion
    /// forgotten, so a camera registered again later is carried nowhere until it has been seen twice.
    /// </remarks>
    public void BeginPass(RenderSetPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        _bodyReach = Math.Max(0.0, pass.BodyReachMetres);
        _stepTravel = _maximumSpeed * Math.Max(0.0, pass.SumoStepSeconds);
        _seed = pass.Seed;

        _seenThisPass.Clear();
        List<CameraFootprint> footprints = new(pass.Cameras.Count);
        foreach (CameraView view in pass.Cameras)
        {
            if (!_seenThisPass.Add(view.Actor))
            {
                continue;
            }

            double cap = view.Optics.RangeAtWhichABodyCovers(pass.LongestBodyMetres, _minimumPixels);
            Motion motion = MotionOf(view, pass.SimulatedTimeSeconds);
            _lastSeen[view.Actor] = (view.Pose, pass.SimulatedTimeSeconds);

            GroundPolygon now = CameraFootprint.OnTheGround(view.Pose, view.Optics, cap, pass.GroundHeight,
                                                            out (double Low, double High) bracket);
            GroundPolygon admission = now;
            GroundPolygon subscription = now;
            if (motion.Speed > 0.0 || motion.Turning)
            {
                double admitted = pass.SumoStepSeconds + _lead;
                admission = GroundPolygon.Hull(
                    now, CameraFootprint.OnTheGround(motion.Carry(view.Pose, admitted), view.Optics, cap,
                                                     pass.GroundHeight, out _));
                subscription = GroundPolygon.Hull(
                    now, CameraFootprint.OnTheGround(motion.Carry(view.Pose, admitted + pass.SumoStepSeconds),
                                                     view.Optics, cap, pass.GroundHeight, out _));
            }

            footprints.Add(new CameraFootprint(view, cap, pass.LongestBodyMetres, _minimumPixels, bracket,
                                               motion.Speed, now, admission, subscription));
        }

        _gone.Clear();
        foreach (ActorId actor in _lastSeen.Keys)
        {
            if (!_seenThisPass.Contains(actor))
            {
                _gone.Add(actor);
            }
        }

        foreach (ActorId actor in _gone)
        {
            _lastSeen.Remove(actor);
        }

        _footprints = footprints;
        ActiveRule = footprints.Count > 0 ? RenderSetRule.Cameras : RenderSetRule.Circle;
    }

    /// <inheritdoc/>
    public bool ShouldSubscribe(double x, double y, bool alreadySubscribed)
    {
        if (ActiveRule == RenderSetRule.Circle)
        {
            return _fallback.ShouldSubscribe(x, y, alreadySubscribed);
        }

        double threshold = Margin + (_maximumSpeed * _lead) + _stepTravel
                           + (alreadySubscribed ? _fallback.HysteresisMetres : 0.0);
        return Nearest(x, -y, static footprint => footprint.Subscription) <= threshold;
    }

    /// <inheritdoc/>
    public bool ShouldRender(in CoSimVehicleFrame frame, bool alreadyRendered)
    {
        if (ActiveRule == RenderSetRule.Circle)
        {
            return _fallback.ShouldRender(frame, alreadyRendered);
        }

        double distance = Nearest(frame.X, -frame.Y, static footprint => footprint.Admission);
        double threshold = alreadyRendered
            ? Margin + (_maximumSpeed * _lead) + _fallback.HysteresisMetres
            : Margin + (Math.Clamp(frame.SpeedMetresPerSecond, 0.0, _maximumSpeed) * _lead);
        return distance <= threshold;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Four apart per tier and two apart per incumbency, with the seed's place in [0, 1) below both, so
    /// the tiers never interleave.
    /// </remarks>
    public double Rank(in CoSimVehicleFrame frame, bool alreadyRendered)
    {
        if (ActiveRule == RenderSetRule.Circle)
        {
            return _fallback.Rank(frame, alreadyRendered);
        }

        bool inView = Nearest(frame.X, -frame.Y, static footprint => footprint.Admission) <= _bodyReach;
        return (inView ? 0.0 : 4.0) + (alreadyRendered ? 0.0 : 2.0) + SeededOrder.Of(_seed, frame.Id);
    }

    /// <summary>The bodies' reach and one step of travel at the fastest plausible speed.</summary>
    private double Margin => _bodyReach + _stepTravel;

    /// <summary>How far a CARLA-frame point is from the nearest camera's footprint of the kind asked for.</summary>
    private double Nearest(double carlaX, double carlaY, Func<CameraFootprint, GroundPolygon> which)
    {
        double nearest = double.PositiveInfinity;
        foreach (CameraFootprint footprint in _footprints)
        {
            nearest = Math.Min(nearest, which(footprint).DistanceTo(carlaX, carlaY));
            if (nearest == 0.0)
            {
                break;
            }
        }

        return nearest;
    }

    /// <summary>
    /// How a camera moved since the pass that last saw it, per simulated second, or no motion where no
    /// pass has, or where it moved too far to have flown.
    /// </summary>
    private Motion MotionOf(CameraView view, double now)
    {
        if (!_lastSeen.TryGetValue(view.Actor, out (Transform Pose, double At) last) || now - last.At <= 0.0)
        {
            return default;
        }

        double seconds = now - last.At;
        Location to = view.Pose.Location;
        Location from = last.Pose.Location;
        double vx = (to.X - from.X) / seconds;
        double vy = (to.Y - from.Y) / seconds;
        double vz = (to.Z - from.Z) / seconds;
        double speed = Math.Sqrt((vx * vx) + (vy * vy) + (vz * vz));
        if (speed > CameraJumpMetresPerSecond)
        {
            return default;
        }

        return new Motion(vx, vy, vz,
                          Arc(view.Pose.Rotation.Pitch, last.Pose.Rotation.Pitch) / seconds,
                          Arc(view.Pose.Rotation.Yaw, last.Pose.Rotation.Yaw) / seconds,
                          Arc(view.Pose.Rotation.Roll, last.Pose.Rotation.Roll) / seconds,
                          speed);
    }

    /// <summary>The shortest turn from one angle to another, degrees.</summary>
    private static double Arc(double to, double from) => Math.IEEERemainder(to - from, 360.0);

    private static void RequireFinite(double value, string name, bool allowZero)
    {
        if (!double.IsFinite(value) || value < 0.0 || (!allowZero && value == 0.0))
        {
            throw new ArgumentOutOfRangeException(name, value,
                                                  allowZero ? "must be a finite number, zero or more"
                                                            : "must be a finite number above zero");
        }
    }

    /// <summary>A camera's motion, metres and degrees per simulated second.</summary>
    private readonly record struct Motion(double Vx, double Vy, double Vz,
                                          double PitchRate, double YawRate, double RollRate, double Speed)
    {
        public bool Turning => PitchRate != 0.0 || YawRate != 0.0 || RollRate != 0.0;

        /// <summary>Where the camera is after the motion is carried on for some seconds.</summary>
        public Transform Carry(Transform pose, double seconds) =>
            new(new Location((float)(pose.Location.X + (Vx * seconds)),
                             (float)(pose.Location.Y + (Vy * seconds)),
                             (float)(pose.Location.Z + (Vz * seconds))),
                new Rotation((float)(pose.Rotation.Pitch + (PitchRate * seconds)),
                             (float)(pose.Rotation.Yaw + (YawRate * seconds)),
                             (float)(pose.Rotation.Roll + (RollRate * seconds))));
    }
}
