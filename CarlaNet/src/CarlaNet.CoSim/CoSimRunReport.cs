using System.Text;

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

    /// <summary>The clock the session resolved.</summary>
    public required CoSimClock Clock { get; init; }

    /// <summary>Which scenario and which world it ran.</summary>
    public required string ScenarioPath { get; init; }

    /// <summary>The world package the ground surface and the road network came from.</summary>
    public required string WorldPackagePath { get; init; }

    /// <summary>The catalogue's declared digest, so a run can be tied to the measurements it used.</summary>
    public required string CatalogueDigest { get; init; }

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

    /// <summary>Vehicle-ticks skipped because the vehicle's type has no measured body.</summary>
    public long VehicleTicksWithNoMeasuredBody { get; internal set; }

    /// <summary>Distinct vehicles that ever held a place in the render set.</summary>
    public long Admissions { get; internal set; }

    /// <summary>Admissions the render-set capacity declined, counted per step per vehicle.</summary>
    public long CapacityDeclines { get; internal set; }

    /// <summary>CARLA actors the session owns, spawned once each and never during a tick.</summary>
    public long BodiesSpawned { get; internal set; }

    /// <summary>
    /// Vehicle-ticks whose pose was computed and written to nothing, because every body of that
    /// blueprint was lent out and the pool was at its ceiling.
    /// </summary>
    /// <remarks>
    /// A budget decision rather than a fault, and counted rather than logged: it is the difference
    /// between "the scene held what the render set admitted" and "the scene held what there were
    /// bodies for", which nothing downstream can tell from the imagery.
    /// </remarks>
    public long PoseDeclinesForNoBody { get; internal set; }

    /// <summary>How many times the pool had no body to lend.</summary>
    public long BodyDeclines { get; internal set; }

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
    public IReadOnlyList<string> DiscontinuitySamples => _discontinuities;

    private const int DiscontinuitySampleLimit = 20;
    private const int BatchFailureSampleLimit = 10;

    private readonly List<string> _batchFailures = [];
    private readonly List<string> _discontinuities = [];
    private double _positionDivergenceTotal;
    private double _laneGeometryTotal;

    internal void SampleDiscontinuity(in CoSimVehicleFrame from,
                                      in CoSimVehicleFrame to,
                                      double? routeDistanceMetres)
    {
        if (_discontinuities.Count >= DiscontinuitySampleLimit)
        {
            return;
        }

        string distance = routeDistanceMetres is { } metres
            ? $"{metres:0.00} m along the route"
            : "no route between them";
        _discontinuities.Add(
            $"{from.Id}: {from.LaneId}@{from.LanePositionMetres:0.00} -> "
            + $"{to.LaneId}@{to.LanePositionMetres:0.00}, {distance}, "
            + $"speed {from.SpeedMetresPerSecond:0.0} to {to.SpeedMetresPerSecond:0.0} m/s");
    }

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
        text.AppendLine($"scenario           {ScenarioPath}");
        text.AppendLine($"world              {WorldPackagePath}");
        text.AppendLine($"catalogue          {CatalogueDigest}");
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
        text.AppendLine($"admissions         {Admissions}, capacity declines {CapacityDeclines}");
        text.AppendLine($"bodies             {BodiesSpawned} spawned, {PoseDeclinesForNoBody} "
                        + "vehicle-ticks with no body to write to");
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
        foreach (string sample in _discontinuities)
        {
            text.AppendLine();
            text.Append($"  discontinuity    {sample}");
        }
        return text.ToString();
    }
}
