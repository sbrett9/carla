using System.Text;
using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// What a run established: how much of the conversion ran, how far it disagreed with SUMO,
/// and what it refused.
/// </summary>
/// <remarks>
/// <para>The point of running the conversion with nothing applied is that it becomes checkable
/// before anything moves. A pose wrong by half a car length shows up here as a residual; applied, it
/// shows up as plausible imagery with every box in the wrong place, which no amount of looking at
/// the imagery will reveal.</para>
///
/// <para>Two residuals, and they measure different things. <b>The bumper residual</b> takes the
/// computed pose back through the rotation to the point SUMO reported and is arithmetic checking
/// itself; anything but a rounding error there is a defect in the conversion. <b>The lane-geometry
/// residual</b> compares the pose the lane's own polyline gives at a SUMO frame's reported lane
/// position against the position SUMO reported for that frame, and measures whether the network the
/// bridge read is the network SUMO is driving on.</para>
/// </remarks>
public sealed class CoSimRunReport
{
    private readonly Dictionary<LaneInterpolationCase, long> _cases = [];
    private readonly Dictionary<UnrenderableReason, long> _refusedTypes = [];
    private readonly Dictionary<RenderSetReleaseReason, long> _releases = [];
    private readonly Dictionary<GroundSeatReason, long> _groundSeats = [];

    /// <summary>The clock the session resolved.</summary>
    public required CoSimClock Clock { get; init; }

    /// <summary>Which scenario and which world it ran.</summary>
    public required string ScenarioPath { get; init; }

    /// <summary>The world package the ground surface and the road network came from.</summary>
    public required string WorldPackagePath { get; init; }

    /// <summary>The catalogue's declared digest, so a run can be tied to the measurements it used.</summary>
    public required string CatalogueDigest { get; init; }

    /// <summary>
    /// The SUMO the run launched -- its root, the binary (<c>sumo</c>, or <c>sumo-gui</c> in its
    /// place), that binary's release and the rule that found the installation -- and how that release
    /// stood against the converter the world package records.
    /// </summary>
    /// <remarks>
    /// A run's traffic is one SUMO release's behaviour on one netconvert's network, and nothing in
    /// the imagery or the truth says which. An accepted mismatch and a world that records no converter
    /// both run, and both are named here rather than left for a log nobody kept.
    /// </remarks>
    public required SumoReleaseCheck Sumo { get; init; }

    /// <summary>
    /// The scenario's compile lock: whether one sat beside the configuration, and where it did, the
    /// scenario it binds, the SUMO release that routed its demand and the world it was compiled for.
    /// </summary>
    /// <remarks>
    /// Checked before SUMO was started, so a run that has a report ran the files, the catalogue and
    /// the epoch its lock binds. A scenario with no lock ran as an uncompiled one, and this says so:
    /// its traffic is described by nothing but its own files.
    /// </remarks>
    public required ScenarioLockCheck CompileLock { get; init; }

    /// <summary>
    /// Whether SUMO could teleport a blocked vehicle in this run -- its <c>time-to-teleport</c>, as
    /// declared or by SUMO's default -- and, where it could, that the run accepted it explicitly.
    /// </summary>
    public required TeleportingCheck Teleporting { get; init; }

    /// <summary>
    /// That a route SUMO cannot follow stops the run: the configuration does not tell SUMO to carry on
    /// past one, which it would do silently.
    /// </summary>
    public required RouteErrorCheck RouteErrors { get; init; }

    /// <summary>
    /// What SUMO does about a collision in this run -- whether it registers one, and what it does to the
    /// two vehicles -- as the configuration sets it or SUMO's default leaves it.
    /// </summary>
    public required SumoCollisionHandling CollisionHandling { get; init; }

    /// <summary>
    /// Which rendering layers the session wrote before its first tick, and what it wrote them to.
    /// </summary>
    /// <remarks>
    /// What was in frame is a property of the corpus and is not recoverable from the imagery: a
    /// capture with no road mesh in it and a capture of a world that has no road mesh look the same.
    /// Empty where the session drove no world, which is a run that rendered nothing at all.
    /// </remarks>
    public IReadOnlyDictionary<string, bool> LayerVisibility { get; init; } =
        new Dictionary<string, bool>();

    /// <summary>
    /// The SUMO step length the run was forced to, where an operator overrode the scenario's own.
    /// </summary>
    /// <remarks>
    /// Recorded because it changes the behaviour being captured, not just the rendering of it:
    /// measured on the shipped port scenario, moving from a one-second step to a tenth of a second
    /// left the demand identical and cut mean time loss per vehicle by 62%.
    /// </remarks>
    public double? SumoStepOverrideSeconds { get; init; }

    /// <summary>
    /// The pace the run was declared to hold against the wall clock, and the pace it held: over each
    /// fixed window of wall clock, over the whole run, and for the worst window.
    /// </summary>
    /// <remarks>
    /// Live while the run goes, so a harness can print it between advances. Published whether or not
    /// the run was paced: a run that did not hold its declared rate is a fact about the data -- its
    /// frames are unevenly spaced in wall clock -- and an unpaced run's figure is how fast it went.
    /// </remarks>
    public required RealTimePacer Pacing { get; init; }

    /// <summary>What simulated second zero meant in civil time, as the run declared it.</summary>
    public SolarEpoch? Epoch { get; init; }

    /// <summary>The simulated instant of the first frame the session rendered.</summary>
    public double FirstRenderedSeconds { get; internal set; }

    /// <summary>
    /// The simulated instant the capture window opened. Frames rendered before it are the prewarm's;
    /// a sun frozen at the window's start is pinned here.
    /// </summary>
    public double WindowOpensAtSeconds { get; internal set; }

    /// <summary>What the run declared its sun would do.</summary>
    /// <remarks>
    /// Recorded because a frozen run and a run nobody configured write identical sun blocks: the
    /// declaration is what tells them apart.
    /// </remarks>
    public IlluminationPolicy? Illumination { get; init; }

    /// <summary>
    /// The sun the run found the world holding, what it bound it to, and what the world reported
    /// back. Null where the run bound no sun.
    /// </summary>
    public SolarLease? Sun { get; internal set; }

    /// <summary>
    /// Every comparison of the world's sun against the declared one: at window open and on every
    /// tick. Null where the run bound no sun, which is an audit that was skipped rather than passed.
    /// </summary>
    public SolarAudit? SunAudit { get; internal set; }

    /// <summary>The largest angle between the world's sun and the declared one, degrees.</summary>
    public double? WorstSolarResidualDegrees => SunAudit?.WorstAngle?.AngleResidualDegrees;

    /// <summary>The largest distance between the world's sun clock and the declared instant, seconds.</summary>
    public double? WorstSolarClockResidualSeconds => SunAudit?.WorstClock?.ClockResidualSeconds;

    /// <summary>The largest difference in refraction-corrected elevation, degrees, where it was carried.</summary>
    public double? WorstSolarCorrectedResidualDegrees => SunAudit?.WorstCorrected?.CorrectedResidualDegrees;

    /// <summary>
    /// How the run stopped, where it stopped because SUMO, the CARLA server or the world's sun failed
    /// part-way -- the stage, which side, and the last frame whose truth still holds. Null for a run
    /// that has not stopped that way.
    /// </summary>
    public CoSimRunStop? Stopped { get; internal set; }

    /// <summary>
    /// Collisions SUMO registered while the session read it, each counted once however many steps it
    /// lasted.
    /// </summary>
    public long Collisions { get; internal set; }

    /// <summary>The first few collisions, as closed spans.</summary>
    public IReadOnlyList<CollisionSpan> CollisionSamples => _collisions;

    /// <summary>
    /// Vehicles SUMO was trying to insert and gave up on, which it does without a word.
    /// </summary>
    public long VehiclesNotInserted { get; internal set; }

    /// <summary>The first few of those.</summary>
    public IReadOnlyList<VehicleNotInserted> VehiclesNotInsertedSamples => _notInserted;

    /// <summary>
    /// Vehicles whose departure time had come and that SUMO had still not inserted, at the last SUMO
    /// frame the session read.
    /// </summary>
    public int VehiclesAwaitingInsertion { get; internal set; }

    /// <summary>
    /// Render-set releases, by why each vehicle stopped holding a place -- SUMO listing it as arrived,
    /// it vanishing without being listed, or the session ending.
    /// </summary>
    public IReadOnlyDictionary<RenderSetReleaseReason, long> Releases => _releases;

    /// <summary>
    /// The seed the scenario's configuration runs SUMO under, or SUMO's own default where it declares
    /// none.
    /// </summary>
    /// <remarks>
    /// Read from the configuration SUMO was started on, the way SUMO reads it, so the report names the
    /// seed its traffic was simulated under: a run configuration's recorded seed is the same number,
    /// compiled into the same file.
    /// </remarks>
    public long SumoSeed { get; init; }

    /// <summary>How many warnings SUMO wrote to its console, the fast-forward's included.</summary>
    /// <remarks>
    /// SUMO says on its console, and nowhere a client can ask, when it reroutes a vehicle it could not
    /// route, teleports one, registers a collision or stops one in an emergency. Counted and sampled
    /// rather than interpreted. Read live; a warning arrives a moment after the step that caused it.
    /// </remarks>
    public long SumoWarnings => Console?.WarningCount ?? 0;

    /// <summary>The first few warnings SUMO wrote, as it wrote them.</summary>
    public IReadOnlyList<string> SumoWarningSamples => Console?.WarningSamples ?? [];

    /// <summary>SUMO's console, which the warnings are read from.</summary>
    internal SumoConsoleTail? Console { get; init; }

    /// <summary>Whether the session drove each body's lamps.</summary>
    public bool VehicleLampsDriven { get; init; }

    /// <summary>
    /// The rule the headlights followed, and what it did, where the session drove lamps and bound the
    /// sun; null where headlights were not driven, so every body's headlights stayed off.
    /// </summary>
    public HeadlightRule? Headlights { get; internal set; }

    /// <summary>
    /// Lamp commands written: one per body lent, one per change of a vehicle's lamps, one per body
    /// given back and darkened.
    /// </summary>
    public long LampCommandsWritten { get; internal set; }

    /// <summary>World ticks the session ran.</summary>
    public long Ticks { get; internal set; }

    /// <summary>SUMO steps it took.</summary>
    public long SumoSteps { get; internal set; }

    /// <summary>Poses computed and not applied.</summary>
    public long PosesComputed { get; internal set; }

    /// <summary>Poses whose height rested on the bounding box rather than on a settled measurement.</summary>
    public long PosesOnAnApproximatedSeatHeight { get; internal set; }

    /// <summary>Vehicle-ticks where the ground surface had no height under the vehicle.</summary>
    public long PosesRefusedForMissingGround { get; internal set; }

    /// <summary>
    /// How the session joined the SUMO network to the world's OpenDRIVE roads: lanes joined, by the
    /// record that joined them, the lanes left unjoined, and how closely the lanes lie on their roads.
    /// Null for a report built without one.
    /// </summary>
    public RoadMappingSummary? RoadMapping { get; init; }

    /// <summary>
    /// Poses of vehicles on a road: seated on the ground where the road is at grade, on the road's profile
    /// where it is a structure, blended between.
    /// </summary>
    public long PosesSeatedOnTheRoad { get; internal set; }

    /// <summary>Poses on a road at grade, seated on the ground surface exactly.</summary>
    public long PosesAtGrade { get; internal set; }

    /// <summary>
    /// Poses seated on the ground surface instead, by why: the vehicle was on no lane, its lane was on
    /// no road, or it stood further from its road than a vehicle on it can.
    /// </summary>
    public IReadOnlyDictionary<GroundSeatReason, long> PosesSeatedOnTheGround => _groundSeats;

    /// <summary>
    /// Poses on a road that departs from the ground by enough to be a structure -- a deck, or a road
    /// spanning the ground beneath one -- seated on the road's profile with no roll.
    /// </summary>
    public long PosesOnAStructure { get; internal set; }

    /// <summary>Poses on a road between at grade and a structure, whose seat was blended.</summary>
    public long PosesOnAnApproach { get; internal set; }

    /// <summary>
    /// The furthest any road a body was on stood from the ground surface at its reference line, metres,
    /// signed: positive for a deck above the ground.
    /// </summary>
    public double WorstRoadDepartureFromGroundMetres { get; private set; }

    /// <summary>Vehicle-ticks skipped because the vehicle's type has no measured body.</summary>
    public long VehicleTicksWithNoMeasuredBody { get; internal set; }

    /// <summary>
    /// Admissions to the render set since the session started, as of the last pass: one for each
    /// vehicle SUMO had while the session read it.
    /// </summary>
    public long Admissions { get; internal set; }

    /// <summary>
    /// The render set's most recent admission pass -- the population, and the vehicles admitted and
    /// released -- replaced once per SUMO step as the pass is made. Null only before the session's
    /// first pass, which it makes while starting.
    /// </summary>
    /// <remarks>
    /// Live, so a monitor reads the population between advances rather than waiting for the end of the
    /// run; <see cref="Admissions"/> is the same pass's running total.
    /// </remarks>
    public AdmissionPass? LastAdmissionPass { get; internal set; }

    /// <summary>
    /// CARLA actors the session owns, spawned once each and never during a tick: as many of each
    /// blueprint as the scene ever held vehicles of it at once.
    /// </summary>
    public long BodiesSpawned { get; internal set; }

    /// <summary>
    /// Changes to the render set named to the server: one round trip on each tick whose lending
    /// changed -- a body lent or given back -- and none on any other.
    /// </summary>
    /// <remarks>
    /// The server carries what it was told on every world-observer snapshot, so the truth telemetry
    /// of every process lists only the bodies a frame drew, each named by its SUMO vehicle. Read
    /// against <see cref="Ticks"/>: at most one per tick, and in a steady scene far fewer.
    /// </remarks>
    public long RenderSetUpdates { get; internal set; }

    /// <summary>
    /// Bodies named to the server that it did not find, summed over the changes: a body the server no
    /// longer had, whose naming therefore reached no snapshot. Zero in a healthy run.
    /// </summary>
    public long RenderSetBodiesNotFound { get; internal set; }

    /// <summary>
    /// Why the server refused the render set, in its words, or <see langword="null"/> where it took
    /// every change or none was sent. A server built before it carried a render set refuses the first
    /// change, and the session names nothing more: its own recorded truth is cut to the render set in
    /// process either way, and only the truth other processes read lists every vehicle actor again.
    /// </summary>
    public string? RenderSetRefused { get; internal set; }

    /// <summary>
    /// Round trips spent writing poses: one per world tick that had a pose to write, never more.
    /// </summary>
    /// <remarks>
    /// The number to read against <see cref="Ticks"/>. Anything above one per tick means a write
    /// path grew a tail, which is the cost this mode was designed to not have.
    /// </remarks>
    public long Batches { get; internal set; }

    /// <summary>Commands written across every batch.</summary>
    public long CommandsWritten { get; internal set; }

    /// <summary>Commands the server answered with an error.</summary>
    public long BatchFailures { get; internal set; }

    /// <summary>Commanded-against-applied comparisons taken, one per rendered vehicle per tick.</summary>
    public long DivergenceSamples { get; private set; }

    /// <summary>
    /// Vehicle-ticks whose pose was written and whose body the world reported nothing for.
    /// </summary>
    /// <remarks>
    /// Distinct from a divergence of zero and from one of anything else. A body nothing reports is a
    /// body whose pose nobody has checked, and a run in which that is most of them has measured
    /// nothing while appearing to measure everything.
    /// </remarks>
    public long VehicleTicksWithNoReadBack { get; internal set; }

    /// <summary>The largest separation between a commanded position and the applied one, metres.</summary>
    public double WorstPositionDivergenceMetres { get; private set; }

    /// <summary>The mean of the same, metres.</summary>
    public double MeanPositionDivergenceMetres =>
        DivergenceSamples == 0 ? 0.0 : _positionDivergenceTotal / DivergenceSamples;

    /// <summary>The largest shortest-arc separation in yaw, degrees.</summary>
    public double WorstYawDivergenceDegrees { get; private set; }

    /// <summary>The largest shortest-arc separation in pitch, degrees.</summary>
    public double WorstPitchDivergenceDegrees { get; private set; }

    /// <summary>The largest shortest-arc separation in roll, degrees.</summary>
    public double WorstRollDivergenceDegrees { get; private set; }

    /// <summary>The vehicle and tick the largest position separation was measured on.</summary>
    /// <remarks>
    /// The worst figure on its own says how bad the run is; the vehicle and the instant say where to
    /// look, which is the next question every time.
    /// </remarks>
    public PoseDivergence? WorstDivergence { get; private set; }

    /// <summary>
    /// The largest difference between a commanded velocity and the one the world reported for the
    /// body, metres per second.
    /// </summary>
    /// <remarks>
    /// The reported velocity is the one the truth telemetry reads, so this bounds how far the truth
    /// record's speed is from SUMO's. Read it against <see cref="MeanCommandedSpeedMetresPerSecond"/>:
    /// a bridge that sends no velocity, or a server that cannot report one for a physics-disabled
    /// vehicle, shows a mean gap equal to the mean commanded speed.
    /// </remarks>
    public double WorstVelocityDivergenceMetresPerSecond { get; private set; }

    /// <summary>The mean of the same, metres per second.</summary>
    public double MeanVelocityDivergenceMetresPerSecond =>
        DivergenceSamples == 0 ? 0.0 : _velocityDivergenceTotal / DivergenceSamples;

    /// <summary>
    /// The mean length of the commanded velocities the velocity gap is measured against, metres per
    /// second: what the mean gap would be if no velocity reached any body.
    /// </summary>
    public double MeanCommandedSpeedMetresPerSecond =>
        DivergenceSamples == 0 ? 0.0 : _commandedSpeedTotal / DivergenceSamples;

    /// <summary>The vehicle and tick the largest velocity difference was measured on.</summary>
    public PoseDivergence? WorstVelocityDivergence { get; private set; }

    /// <summary>The first few command failures, as the server described them.</summary>
    /// <remarks>
    /// A count says how much of the imagery is wrong; the messages say what about it. Kept to a
    /// handful because a batch that starts failing usually fails the same way every tick.
    /// </remarks>
    public IReadOnlyList<string> BatchFailureSamples => _batchFailures;

    /// <summary>The largest and mean bumper round-trip residual, in metres.</summary>
    public double WorstBumperResidualMetres { get; internal set; }

    /// <summary>The largest lane-geometry residual at a SUMO frame, in metres.</summary>
    public double WorstLaneGeometryResidualMetres { get; internal set; }

    /// <summary>The mean lane-geometry residual at a SUMO frame, in metres.</summary>
    public double MeanLaneGeometryResidualMetres =>
        LaneGeometrySamples == 0 ? 0.0 : _laneGeometryTotal / LaneGeometrySamples;

    /// <summary>How many frames the lane-geometry residual was taken on.</summary>
    public long LaneGeometrySamples { get; internal set; }

    /// <summary>Wall-clock seconds spent in the bridge's own per-tick work, excluding the world tick.</summary>
    public double BridgeSecondsOnTicks { get; internal set; }

    /// <summary>Wall-clock seconds spent stepping SUMO and reading its answer.</summary>
    public double SumoSecondsOnSteps { get; internal set; }

    /// <summary>How each sub-step pose was produced.</summary>
    public IReadOnlyDictionary<LaneInterpolationCase, long> InterpolationCases => _cases;

    /// <summary>Vehicle types the catalogue would not supply a body for, by reason.</summary>
    public IReadOnlyDictionary<UnrenderableReason, long> RefusedVehicleTypes => _refusedTypes;

    /// <summary>
    /// The first few discontinuities, described: which lanes, and how far apart along the route.
    /// </summary>
    /// <remarks>
    /// Kept to a handful rather than all of them. Every one is a vehicle a driving session would
    /// release and re-admit rather than slide across the gap, so a run that produces a great many is
    /// producing tracks that start and stop for reasons nothing downstream can see -- and the first
    /// question about them is always which lanes they were between, which a count cannot answer.
    /// </remarks>
    public IReadOnlyList<string> DiscontinuitySamples => _discontinuities.Samples;

    private const int DiscontinuitySampleLimit = 20;
    private const int BatchFailureSampleLimit = 10;
    private const int CollisionSampleLimit = 10;
    private const int NotInsertedSampleLimit = 10;

    private readonly List<string> _batchFailures = [];
    private readonly DiscontinuitySampler _discontinuities = new(DiscontinuitySampleLimit);
    private readonly List<CollisionSpan> _collisions = [];
    private readonly List<VehicleNotInserted> _notInserted = [];
    private double _positionDivergenceTotal;
    private double _velocityDivergenceTotal;
    private double _commandedSpeedTotal;
    private double _laneGeometryTotal;

    internal void SampleDiscontinuity(in CoSimVehicleFrame from,
                                      in CoSimVehicleFrame to,
                                      double? routeDistanceMetres) =>
        _discontinuities.Sample(from, to, routeDistanceMetres);

    internal void AddDivergence(in PoseDivergence divergence)
    {
        DivergenceSamples++;
        _positionDivergenceTotal += divergence.PositionMetres;
        if (divergence.PositionMetres > WorstPositionDivergenceMetres || WorstDivergence is null)
        {
            WorstPositionDivergenceMetres = divergence.PositionMetres;
            WorstDivergence = divergence;
        }

        WorstYawDivergenceDegrees = Math.Max(WorstYawDivergenceDegrees, divergence.YawDegrees);
        WorstPitchDivergenceDegrees = Math.Max(WorstPitchDivergenceDegrees, divergence.PitchDegrees);
        WorstRollDivergenceDegrees = Math.Max(WorstRollDivergenceDegrees, divergence.RollDegrees);

        _velocityDivergenceTotal += divergence.VelocityMetresPerSecond;
        _commandedSpeedTotal += divergence.CommandedSpeedMetresPerSecond;
        if (divergence.VelocityMetresPerSecond > WorstVelocityDivergenceMetresPerSecond
            || WorstVelocityDivergence is null)
        {
            WorstVelocityDivergenceMetresPerSecond = divergence.VelocityMetresPerSecond;
            WorstVelocityDivergence = divergence;
        }
    }

    internal void SampleBatchFailure(string? message)
    {
        BatchFailures++;
        if (_batchFailures.Count < BatchFailureSampleLimit && message is { Length: > 0 })
        {
            _batchFailures.Add(message);
        }
    }

    internal void CountCase(LaneInterpolationCase which) =>
        _cases[which] = _cases.GetValueOrDefault(which) + 1;

    /// <summary>Count where a pose's height came from.</summary>
    internal void CountSeat(in VehiclePose pose)
    {
        if (pose.Road is not { } road)
        {
            _groundSeats[pose.GroundReason] = _groundSeats.GetValueOrDefault(pose.GroundReason) + 1;
            return;
        }

        PosesSeatedOnTheRoad++;
        if (road.GroundWeight <= 0.0)
        {
            PosesOnAStructure++;
        }
        else if (road.GroundWeight < 1.0)
        {
            PosesOnAnApproach++;
        }
        else
        {
            PosesAtGrade++;
        }

        if (Math.Abs(road.DepartureFromGroundMetres) > Math.Abs(WorstRoadDepartureFromGroundMetres))
        {
            WorstRoadDepartureFromGroundMetres = road.DepartureFromGroundMetres;
        }
    }

    internal void CountRelease(RenderSetReleaseReason reason) =>
        _releases[reason] = _releases.GetValueOrDefault(reason) + 1;

    internal void SampleCollision(in CollisionSpan span)
    {
        if (_collisions.Count < CollisionSampleLimit)
        {
            _collisions.Add(span);
        }
    }

    internal void AddNotInserted(in VehicleNotInserted vehicle)
    {
        VehiclesNotInserted++;
        if (_notInserted.Count < NotInsertedSampleLimit)
        {
            _notInserted.Add(vehicle);
        }
    }

    internal void CountRefusedType(UnrenderableReason reason) =>
        _refusedTypes[reason] = _refusedTypes.GetValueOrDefault(reason) + 1;

    internal void AddLaneGeometryResidual(double metres)
    {
        LaneGeometrySamples++;
        _laneGeometryTotal += metres;
        WorstLaneGeometryResidualMetres = Math.Max(WorstLaneGeometryResidualMetres, metres);
    }

    /// <summary>
    /// The illumination lines: what was declared, what the world was found holding, what was bound,
    /// and how far the world's sun ever sat from the declaration.
    /// </summary>
    private void AppendIllumination(StringBuilder text)
    {
        if (Epoch is { } epoch)
        {
            text.AppendLine($"epoch              {epoch}; digest {epoch.Digest[..12]}");
        }

        double prewarm = WindowOpensAtSeconds - FirstRenderedSeconds;
        text.AppendLine($"window             opens at t={Seconds(WindowOpensAtSeconds)} s"
                        + (Epoch is { } civil
                            ? $" ({SolarEpoch.FormatCivil(civil.CivilInstantAt(WindowOpensAtSeconds))})"
                            : string.Empty)
                        + (prewarm > 1e-6
                            ? $"; rendered from t={Seconds(FirstRenderedSeconds)} s, {Seconds(prewarm)} s of prewarm first"
                            : "; its first frame is the first rendered"));

        if (Illumination is not { } policy)
        {
            return;
        }

        text.AppendLine($"illumination       {policy}; "
                        + (policy.HonoursTheEpoch && Sun is { NoSun: false }
                            ? "each frame lit by its declared civil instant"
                            : "the frame's civil instant is not the sun it is lit by"));
        if (Sun is not { } sun)
        {
            text.AppendLine("sun                not bound, so not audited");
            return;
        }

        if (sun.NoSun)
        {
            text.AppendLine("sun                the world has none; not audited");
            return;
        }

        if (sun.SunGoneWhenGivenBack)
        {
            text.AppendLine("  given back       the world had no sun left at the end, so nothing held the session's writes");
        }

        text.AppendLine($"  found holding    {sun.AsFound}");
        if (SunAudit?.AtWindowOpen is { } opened)
        {
            SunPosition declared = opened.Modelled;
            text.AppendLine($"  bound            {SolarEpoch.FormatCivil(sun.Declared.WindowOpenCivil)}, sun "
                            + $"{sun.Declared.SunAtWindowOpen:yyyy-MM-dd HH:mm:ss} at "
                            + $"UTC{SolarEpoch.FormatOffset(sun.Declared.Epoch.UtcOffset)}: declared elevation "
                            + $"{DeclaredSunElevation.Of(declared):0.####} deg {DeclaredSunElevation.Name} "
                            + $"(geometric {declared.ElevationDegrees:0.####}, corrected "
                            + $"{declared.CorrectedElevationDegrees:0.####}), azimuth "
                            + $"{declared.AzimuthDegrees:0.####}");
        }

        if (policy.Advances)
        {
            text.AppendLine($"  written          for {sun.FrameWrites} frames, each a millisecond past the "
                            + "whole second nearest its declared instant; the engine's own advance off");
        }

        if (SunAudit is { } audit)
        {
            text.AppendLine($"  audit            {audit.AuditedTicks} ticks, corrected elevation on "
                            + $"{audit.TicksWithCorrectedElevation} of them and at window open; "
                            + $"tolerance {audit.ToleranceSeconds:0.###} s, {audit.ToleranceDegrees:0.####} deg");
            if (audit.WorstAngle is { } angle)
            {
                text.AppendLine($"  worst direction  {angle.AngleResidualDegrees:0.######} deg at {angle.Where}");
            }

            if (audit.WorstClock is { } clock)
            {
                text.AppendLine($"  worst clock      {clock.ClockResidualSeconds:+0.######;-0.######;0} s at {clock.Where}");
            }

            if (audit.WorstCorrected is { CorrectedResidualDegrees: { } corrected } refracted)
            {
                text.AppendLine($"  worst corrected  {corrected:+0.######;-0.######;0} deg at {refracted.Where}");
            }

            if (audit.Failure is { } failure)
            {
                text.AppendLine($"  FAILED           at {failure.Where}");
            }
        }
    }

    /// <summary>
    /// The pacing lines: what was declared, what the whole run achieved, the windows, and for a paced
    /// run how far behind its schedule it went.
    /// </summary>
    private void AppendPacing(StringBuilder text)
    {
        text.AppendLine($"pacing             {Pacing}");
        if (Pacing.CompletedWindows > 0)
        {
            text.AppendLine($"  windows          {Pacing.CompletedWindows} of {Pacing.WindowSeconds:0.###} s; "
                            + $"last {Pacing.LastWindowFactor:0.####} x, worst "
                            + $"{Pacing.WorstWindowFactor:0.####} x closing at "
                            + $"{Pacing.WorstWindowClosedAtSeconds:0.00} s");
        }

        if (Pacing.Paced && Pacing.Cues > 1)
        {
            text.AppendLine($"  behind schedule  {Pacing.BehindScheduleSeconds:0.000} s at the last tick, worst "
                            + $"{Pacing.WorstBehindScheduleSeconds:0.000} s"
                            + (Pacing.WorstBehindScheduleAtSeconds is { } at ? $" at {at:0.00} s" : string.Empty));
        }
    }

    private static string Seconds(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The bridge's own cost per world tick, in milliseconds.</summary>
    public double BridgeMillisecondsPerTick =>
        Ticks == 0 ? 0.0 : BridgeSecondsOnTicks * 1000.0 / Ticks;

    /// <summary>SUMO's cost per step, in milliseconds.</summary>
    public double SumoMillisecondsPerStep =>
        SumoSteps == 0 ? 0.0 : SumoSecondsOnSteps * 1000.0 / SumoSteps;

    /// <inheritdoc/>
    public override string ToString()
    {
        var text = new StringBuilder();
        if (Stopped is { } stopped)
        {
            text.AppendLine($"STOPPED            {stopped}");
        }

        text.AppendLine($"scenario           {ScenarioPath}");
        text.AppendLine($"world              {WorldPackagePath}");
        text.AppendLine($"catalogue          {CatalogueDigest}");
        text.AppendLine($"sumo               {Sumo.Installation}");
        if (Sumo.Binary is { } launched)
        {
            text.AppendLine($"  launched         {launched}");
        }

        text.AppendLine($"  world converter  {Sumo.Verdict}");
        text.AppendLine($"  seed             {SumoSeed}");
        text.AppendLine($"compile lock       {CompileLock}");
        if (CompileLock.Compiled)
        {
            text.AppendLine($"  routed by        {CompileLock.RoutedByText}");
            text.AppendLine($"  compiled for     {CompileLock.WorldText}");
        }

        text.AppendLine($"teleporting        {Teleporting}");
        text.AppendLine($"route errors       {RouteErrors}");
        text.AppendLine($"collisions         {CollisionHandling}");
        text.AppendLine($"clock              {Clock}");
        if (SumoStepOverrideSeconds is { } forced)
        {
            text.AppendLine($"step override      {forced:0.###} s (behaviour-changing)");
        }

        AppendPacing(text);
        if (LayerVisibility.Count > 0)
        {
            text.AppendLine("layers             "
                            + string.Join(", ", LayerVisibility.OrderBy(entry => entry.Key)
                                .Select(entry => $"{entry.Key} {(entry.Value ? "drawn" : "hidden")}")));
        }

        AppendIllumination(text);
        text.AppendLine($"ticks              {Ticks} over {SumoSteps} SUMO steps");
        text.AppendLine($"poses computed     {PosesComputed}");
        text.AppendLine($"  approximated Z   {PosesOnAnApproximatedSeatHeight}");
        text.AppendLine($"  no ground        {PosesRefusedForMissingGround}");
        text.AppendLine($"  no measured body {VehicleTicksWithNoMeasuredBody} vehicle-ticks");
        text.AppendLine($"  on the road      {PosesSeatedOnTheRoad}: {PosesAtGrade} at grade, {PosesOnAnApproach} "
                        + $"on an approach, {PosesOnAStructure} on a structure; furthest from the ground "
                        + $"{WorstRoadDepartureFromGroundMetres:+0.000;-0.000;0} m");
        text.AppendLine("  on the ground    "
                        + (_groundSeats.Count == 0
                            ? "0"
                            : string.Join(", ", _groundSeats.OrderBy(entry => entry.Key)
                                .Select(entry => $"{entry.Key} {entry.Value}"))));
        if (RoadMapping is { } mapping)
        {
            text.AppendLine($"road mapping       {mapping}");
        }
        text.AppendLine($"admissions         {Admissions}, every vehicle SUMO had");
        if (LastAdmissionPass is { } pass)
        {
            text.AppendLine($"  last pass        {pass}");
        }

        if (_releases.Count > 0)
        {
            text.AppendLine("releases           "
                            + string.Join(", ", _releases.OrderBy(entry => entry.Key)
                                .Select(entry => $"{entry.Key} {entry.Value}")));
        }

        text.AppendLine($"collided           {Collisions} collision(s) registered");
        foreach (CollisionSpan collision in _collisions)
        {
            text.AppendLine($"  collision        {collision}");
        }

        text.AppendLine($"not inserted       {VehiclesNotInserted} vehicle(s) SUMO gave up inserting; "
                        + $"{VehiclesAwaitingInsertion} still waiting at the last frame read");
        foreach (VehicleNotInserted dropped in _notInserted)
        {
            text.AppendLine($"  not inserted     {dropped}");
        }

        text.AppendLine($"sumo warnings      {SumoWarnings}");
        foreach (string warning in SumoWarningSamples)
        {
            text.AppendLine($"  warning          {warning}");
        }
        text.AppendLine($"bodies             {BodiesSpawned} spawned");
        text.AppendLine($"render set         {RenderSetUpdates} change(s) named to the server"
                        + (RenderSetBodiesNotFound > 0 ? $", {RenderSetBodiesNotFound} body(ies) not found" : string.Empty)
                        + (RenderSetRefused is { } refused ? $"; refused, so other processes list every vehicle actor: {refused}" : string.Empty));
        text.AppendLine("lamps              "
                        + (!VehicleLampsDriven
                            ? "not driven; every body kept the lamps it was spawned with"
                            : $"SUMO's signals mapped bit by bit; "
                              + (Headlights is { } rule ? rule.ToString() : "headlights not driven, no bound sun")
                              + $"; {LampCommandsWritten} lamp commands"));
        text.AppendLine($"batches            {Batches} for {Ticks} ticks, {CommandsWritten} "
                        + $"commands, {BatchFailures} refused");
        foreach (string sample in _batchFailures)
        {
            text.AppendLine($"  batch failure    {sample}");
        }
        foreach ((LaneInterpolationCase which, long count) in _cases.OrderBy(entry => entry.Key))
        {
            text.AppendLine($"  {which,-26} {count}");
        }

        foreach ((UnrenderableReason reason, long count) in _refusedTypes.OrderBy(entry => entry.Key))
        {
            text.AppendLine($"  refused {reason,-12} {count} type(s)");
        }

        if (DivergenceSamples > 0 || VehicleTicksWithNoReadBack > 0)
        {
            text.AppendLine($"divergence         worst {WorstPositionDivergenceMetres:0.000000} m, "
                            + $"mean {MeanPositionDivergenceMetres:0.000000} m over "
                            + $"{DivergenceSamples} vehicle-ticks");
            text.AppendLine($"  worst rotation   yaw {WorstYawDivergenceDegrees:0.0000} deg, "
                            + $"pitch {WorstPitchDivergenceDegrees:0.0000} deg, "
                            + $"roll {WorstRollDivergenceDegrees:0.0000} deg");
            if (WorstDivergence is { } worst)
            {
                text.AppendLine($"  worst on         {worst.VehicleId} as actor {worst.Actor} at "
                                + $"tick {worst.TickIndex} ({worst.SimulatedTimeSeconds:0.00} s)");
            }

            text.AppendLine($"  velocity         worst {WorstVelocityDivergenceMetresPerSecond:0.000000} m/s, "
                            + $"mean {MeanVelocityDivergenceMetresPerSecond:0.000000} m/s against a "
                            + $"mean commanded {MeanCommandedSpeedMetresPerSecond:0.000} m/s");
            if (WorstVelocityDivergence is { } fastest)
            {
                text.AppendLine($"  velocity worst   {fastest.VehicleId} as actor {fastest.Actor} at "
                                + $"tick {fastest.TickIndex} ({fastest.SimulatedTimeSeconds:0.00} s), "
                                + $"commanded ({fastest.Commanded.VelocityX:0.000}, "
                                + $"{fastest.Commanded.VelocityY:0.000}, {fastest.Commanded.VelocityZ:0.000}), "
                                + $"reported ({fastest.ObservedVelocity.X:0.000}, "
                                + $"{fastest.ObservedVelocity.Y:0.000}, {fastest.ObservedVelocity.Z:0.000}) m/s");
            }

            if (VehicleTicksWithNoReadBack > 0)
            {
                text.AppendLine($"  not reported     {VehicleTicksWithNoReadBack} vehicle-ticks");
            }
        }

        text.AppendLine($"bumper residual    worst {WorstBumperResidualMetres:0.000000000} m");
        text.AppendLine($"lane residual      worst {WorstLaneGeometryResidualMetres:0.000000} m, "
                        + $"mean {MeanLaneGeometryResidualMetres:0.000000} m over "
                        + $"{LaneGeometrySamples} frames");
        text.AppendLine($"bridge cost        {BridgeMillisecondsPerTick:0.0000} ms/tick");
        text.Append($"sumo cost          {SumoMillisecondsPerStep:0.0000} ms/step");
        foreach (string sample in _discontinuities.Lines())
        {
            text.AppendLine();
            text.Append($"  discontinuity    {sample}");
        }
        return text.ToString();
    }
}
