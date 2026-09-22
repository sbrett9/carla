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
    /// The SUMO step length the run was forced to, where an operator overrode the scenario's own.
    /// </summary>
    /// <remarks>
    /// Recorded because it changes the behaviour being captured, not just the rendering of it:
    /// measured on the shipped port scenario, moving from a one-second step to a tenth of a second
    /// left the demand identical and cut mean time loss per vehicle by 62%.
    /// </remarks>
    public double? SumoStepOverrideSeconds { get; init; }

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

    private readonly List<string> _discontinuities = [];
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

        text.AppendLine($"ticks              {Ticks} over {SumoSteps} SUMO steps");
        text.AppendLine($"poses computed     {PosesComputed}");
        text.AppendLine($"  approximated Z   {PosesOnAnApproximatedSeatHeight}");
        text.AppendLine($"  no ground        {PosesRefusedForMissingGround}");
        text.AppendLine($"  no measured body {VehicleTicksWithNoMeasuredBody} vehicle-ticks");
        text.AppendLine($"admissions         {Admissions}, capacity declines {CapacityDeclines}");
        foreach ((LaneInterpolationCase which, long count) in _cases.OrderBy(entry => entry.Key))
        {
            text.AppendLine($"  {which,-26} {count}");
        }

        foreach ((UnrenderableReason reason, long count) in _refusedTypes.OrderBy(entry => entry.Key))
        {
            text.AppendLine($"  refused {reason,-12} {count} type(s)");
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
