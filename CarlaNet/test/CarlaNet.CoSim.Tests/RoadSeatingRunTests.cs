using System.Globalization;
using CarlaNet.Map.OpenDrive;
using CarlaNet.Map.WorldPackage;
using Xunit.Abstractions;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A world-less run against the scenario and world the environment names, measuring where every body
/// was seated against the road it was on and against the ground surface the bridge used to seat it on.
/// </summary>
/// <remarks>
/// Opt-in, as <see cref="NamedCoSimRunFactAttribute"/> is: set <c>CARLANET_COSIM_SCENARIO</c> and
/// <c>CARLANET_COSIM_WORLD_PACKAGE</c> (and optionally <c>CARLANET_COSIM_STEPS</c>,
/// <c>CARLANET_COSIM_WARMUP</c>). The run's report and the seating's breakdown are written to the test
/// output.
/// </remarks>
public sealed class RoadSeatingRunTests
{
    private readonly ITestOutputHelper _output;

    public RoadSeatingRunTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [NamedCoSimRunFact]
    public void EveryBodyOnARoadIsSeatedOnItsProfileAndMovesContinuously()
    {
        string package = NamedCoSimRunFactAttribute.WorldPackage!;
        WorldPackageManifest manifest = WorldPackage.ReadManifest(package);
        GroundSurface ground = GroundSurface.FromWorldPackage(package);
        RoadMap map = OpenDriveParser.Load(WorldPackage.ReadOpenDrive(package))!;
        SumoRoadNetwork network = SumoRoadNetwork.FromWorldPackage(package);
        RoadSurface roads = RoadSurface.FromWorldPackage(package, network);

        Dictionary<string, List<CoSimPoseRecord>> byVehicle = [];
        var options = new SumoDriveSessionOptions(
            NamedCoSimRunFactAttribute.Scenario!, package, CoSimFixtures.VehicleCatalogue,
            "named://" + Guid.NewGuid().ToString("n"))
        {
            WarmUpToSimulatedSecond = NamedCoSimRunFactAttribute.WarmUp,
            SumoStepOverrideSeconds = NamedCoSimRunFactAttribute.StepLength,
            TickWorld = () => true,
            OnPose = record =>
            {
                if (!byVehicle.TryGetValue(record.Pose.VehicleId, out List<CoSimPoseRecord>? track))
                {
                    track = [];
                    byVehicle[record.Pose.VehicleId] = track;
                }

                track.Add(record);
            },
        };

        double tickSeconds;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            tickSeconds = session.Clock.WorldDeltaSeconds;
            for (int step = 0; step < NamedCoSimRunFactAttribute.Steps && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
        }

        // Every pose on a road, checked against the profile evaluated independently: the road's own
        // reference line searched densely for the point nearest the body's origin, on the side of it the
        // origin is on, and the engine's evaluation of the elevation there.
        // Roads to tabulate whatever their departure, named by CARLANET_SEATING_ROADS (comma-separated
        // OpenDRIVE road ids): a road beneath a deck the ground already holds level with it, say.
        HashSet<uint> watched = [.. (Environment.GetEnvironmentVariable("CARLANET_SEATING_ROADS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => uint.Parse(id, CultureInfo.InvariantCulture))];
        double worstProfile = 0.0;
        double worstEvaluation = 0.0;
        double worstOnStructure = 0.0;
        long steepOnRoad = 0;
        long steepOnGround = 0;
        string worstProfileAt = string.Empty;
        HashSet<uint> decks = [];

        // At grade -- a body taking the ground's roll in full -- how far its seat on the profile stands from
        // where the ground would have seated it, and how each follows the photoreal, by distance across the
        // road from its reference line. The photoreal comes from the build's drape cache, where found.
        PhotorealSurface? photoreal = PhotorealSurface.ForPackage(package);
        var atGrade = new SurfaceAgreement();
        var seatGap = new List<(double Across, double Gap)>();
        var groups = new SortedDictionary<string, Group>(StringComparer.Ordinal);
        long onRoad = 0;
        foreach (CoSimPoseRecord record in byVehicle.Values.SelectMany(track => track))
        {
            if (record.Pose.Road is not { } seat)
            {
                continue;
            }

            onRoad++;
            double seatHeight = record.Pose.Z - seat.SurfaceZMetres;
            if (seat.RollWeight >= 1.0 && onRoad % 5 == 0)
            {
                RoadProfile seatedOn = roads.Roads[seat.RoadId];
                double across = seatedOn.Project(record.Pose.X, -record.Pose.Y, seat.S - 1.0, seat.S + 1.0).Lateral;
                seatGap.Add((across, Math.Abs(seat.DepartureFromGroundMetres)));
                // Where the photoreal is the ground: within the five metres of bare earth the world build
                // drapes onto, and not a canopy, a gantry or a tree over the road.
                if (photoreal?.Surface(record.Pose.X, record.Pose.Y) is { } surface
                    && photoreal.BareEarth(record.Pose.X, record.Pose.Y) is { } bareEarth
                    && Math.Abs(surface - bareEarth) <= 5.0
                    && ground.Sample(record.Pose.X, record.Pose.Y) is { } height)
                {
                    atGrade.Add(across, seat.SurfaceZMetres + ground.OriginHeightMetres - surface, height - surface);
                }
            }
            if (Math.Abs(record.Pose.PitchDegrees) > SteepDegrees)
            {
                steepOnRoad++;
            }

            if (Math.Abs(GroundPitch(ground, record.Pose)) > SteepDegrees)
            {
                steepOnGround++;
            }
            double old = ground.Sample(record.Pose.X, record.Pose.Y)!.Value - ground.OriginHeightMetres;
            CarlaNet.Map.Road.Road road = map.Roads[seat.RoadId];

            // The profile at the seat's own s, as the engine evaluates it, for every pose; and where the
            // s itself came from, densely, on every road whose reference line is a carriageway's edge
            // rather than a connector's swing across a junction.
            if (seat.S >= 0.0 && seat.S <= road.Length)
            {
                double exact = RoadMap.GetDirectedPointIn(road, seat.S).Location.Z;
                worstEvaluation = Math.Max(worstEvaluation, Math.Abs(seat.SurfaceZMetres - exact));
            }

            bool structure = Math.Abs(seat.DepartureFromGroundMetres) > PoseConverter.OnStructureDepartureMetres;
            if (!road.IsJunction && (onRoad % 7 == 0 || structure))
            {
                double reference = DenseProfile(road, record.Pose.X, -record.Pose.Y, seat.S, out double denseS);
                double gap = Math.Abs(seat.SurfaceZMetres - reference);
                if (structure)
                {
                    worstOnStructure = Math.Max(worstOnStructure, gap);
                }

                if (gap > worstProfile)
                {
                    worstProfile = gap;
                    worstProfileAt = string.Create(CultureInfo.InvariantCulture,
                        $"{record.Pose.VehicleId} tick {record.TickIndex} on road {road.Id} '{road.Name}' (length {road.Length:0.00}) s {seat.S:0.00} z {seat.SurfaceZMetres:0.000}; dense s {denseS:0.00} z {reference:0.000}; at ({record.Pose.X:0.00}, {record.Pose.Y:0.00}) yaw {record.Pose.YawDegrees:0.0}");
                }
            }

            if (seat.DepartureFromGroundMetres > PoseConverter.OnStructureDepartureMetres)
            {
                decks.Add(seat.RoadId);
            }

            string kind = seat.DepartureFromGroundMetres > PoseConverter.OnStructureDepartureMetres
                ? "deck"
                : seat.DepartureFromGroundMetres < -PoseConverter.OnStructureDepartureMetres
                    ? "under"
                    : watched.Contains(seat.RoadId) ? "named" : null!;
            if (kind is null)
            {
                continue;
            }

            string over = kind == "under" ? Above(roads, record.Pose.X, -record.Pose.Y, seat.RoadId) : string.Empty;
            string key = $"{kind,-5} road {road.Id,5} {(road.Name.Length > 0 ? road.Name : "(unnamed)")}{over}";
            if (!groups.TryGetValue(key, out Group? group))
            {
                group = new Group();
                groups[key] = group;
            }

            group.Add(record.Pose.VehicleId, seat.SurfaceZMetres + seatHeight - (old + seatHeight),
                      record.Pose.PitchDegrees, record.Pose.RollDegrees);
        }

        _output.WriteLine($"poses on a road {onRoad}; worst gap to the engine's evaluation at the seat's s "
                          + $"{worstEvaluation:0.000000} m; worst gap to the densely evaluated profile on a "
                          + $"carriageway {worstProfile:0.0000} m ({worstProfileAt}), on a structure "
                          + $"{worstOnStructure:0.0000} m");
        _output.WriteLine($"at grade (full roll), every fifth pose: |profile seat - ground seat| over {seatGap.Count} poses");
        foreach ((string name, double low, double high) in new[]
                 {
                     ("< 3 m", 0.0, 3.0), ("3-6.5 m", 3.0, 6.5), ("6.5-10 m", 6.5, 10.0),
                     ("10-13.5 m", 10.0, 13.5), (">= 13.5 m", 13.5, double.PositiveInfinity), ("all", 0.0, double.PositiveInfinity),
                 })
        {
            double[] gaps = [.. seatGap.Where(each => Math.Abs(each.Across) >= low && Math.Abs(each.Across) < high)
                                       .Select(each => each.Gap).Order()];
            _output.WriteLine(gaps.Length == 0
                ? $"    {name,-12} 0"
                : string.Create(CultureInfo.InvariantCulture,
                    $"    {name,-12} {gaps.Length,8}  p50 {gaps[gaps.Length / 2]:0.000}  p90 {gaps[(int)(0.9 * (gaps.Length - 1))]:0.000}  p99 {gaps[(int)(0.99 * (gaps.Length - 1))]:0.000}  max {gaps[^1]:0.000} m"));
        }

        _output.WriteLine(photoreal is null
            ? "  no drape cache of the package's grid, so neither is measured against the photoreal"
            : $"  against the photoreal ({Path.GetFileName(photoreal.Path)}):{Environment.NewLine}{atGrade.Describe()}");
        _output.WriteLine($"poses on a road pitched more than {SteepDegrees} degrees: {steepOnRoad} by their road's "
                          + $"profile, against {steepOnGround} the ground's gradient would have pitched so");
        _output.WriteLine("bodies seated off the ground by more than "
                          + $"{PoseConverter.OnStructureDepartureMetres} m, by the road they were on "
                          + "(height above the ground surface the bridge used to seat them on):");
        foreach ((string key, Group group) in groups)
        {
            _output.WriteLine($"  {key,-48} {group}");
        }

        // Continuity: a body's height from one tick to the next against the climb its own vertical
        // velocity says it made, so a step -- a road's end that does not meet the next road -- is a
        // residual of its own rather than a slope.
        var steps = new List<(double Residual, string Where)>();
        long transitions = 0;
        double worstTransition = 0.0;
        string worstTransitionAt = string.Empty;
        long deckEnds = 0;
        double worstDeckEnd = 0.0;
        string worstDeckEndAt = string.Empty;
        double worstDeckEndClimbRate = 0.0;
        foreach ((string vehicle, List<CoSimPoseRecord> track) in byVehicle)
        {
            for (int index = 1; index < track.Count; index++)
            {
                CoSimPoseRecord before = track[index - 1];
                CoSimPoseRecord after = track[index];
                if (after.TickIndex != before.TickIndex + 1 || before.Pose.Road is not { } from
                    || after.Pose.Road is not { } to
                    || after.Case == LaneInterpolationCase.Discontinuous)
                {
                    continue;
                }

                double climb = 0.5 * (before.Pose.VelocityZ + after.Pose.VelocityZ) * tickSeconds;
                double residual = Math.Abs((after.Pose.Z - before.Pose.Z) - climb);
                double turned = Math.Abs(Math.IEEERemainder(after.Pose.YawDegrees - before.Pose.YawDegrees, 360.0));
                double moved = Math.Sqrt(Math.Pow(after.Pose.X - before.Pose.X, 2) + Math.Pow(after.Pose.Y - before.Pose.Y, 2));
                string where = string.Create(CultureInfo.InvariantCulture,
                    $"{vehicle} tick {after.TickIndex} road {from.RoadId}->{to.RoadId} s {from.S:0.00}->{to.S:0.00} dz {after.Pose.Z - before.Pose.Z:+0.000;-0.000} climb {climb:+0.000;-0.000}; origin moved {moved:0.00} m, yaw turned {turned:0.0} deg; {before.Case}->{after.Case}; lane {before.LaneId}@{before.LanePositionMetres:0.00} -> {after.LaneId}@{after.LanePositionMetres:0.00}");
                steps.Add((residual, where));
                if (from.RoadId != to.RoadId)
                {
                    transitions++;
                    if (residual > worstTransition)
                    {
                        worstTransition = residual;
                        worstTransitionAt = where;
                    }

                    if (decks.Contains(from.RoadId) || decks.Contains(to.RoadId))
                    {
                        deckEnds++;
                        worstDeckEndClimbRate = Math.Max(worstDeckEndClimbRate,
                                                         Math.Abs(after.Pose.Z - before.Pose.Z) / Math.Max(moved, 1e-3));
                        if (residual > worstDeckEnd)
                        {
                            worstDeckEnd = residual;
                            worstDeckEndAt = where;
                        }
                    }
                }
            }
        }

        steps.Sort((a, b) => b.Residual.CompareTo(a.Residual));
        double P(double q) => steps.Count == 0 ? 0.0 : steps[(int)((1.0 - q) * (steps.Count - 1))].Residual;
        _output.WriteLine($"continuity over {steps.Count} consecutive road-seated pairs: residual p50 "
                          + $"{P(0.5):0.0000} m, p99 {P(0.99):0.0000} m, p99.9 {P(0.999):0.0000} m, worst "
                          + $"{(steps.Count > 0 ? steps[0].Residual : 0.0):0.0000} m");
        _output.WriteLine($"  across {transitions} changes of road: worst {worstTransition:0.0000} m at {worstTransitionAt}");
        _output.WriteLine($"  across {deckEnds} changes onto or off a deck road ({string.Join(", ", decks.OrderBy(id => id))}): "
                          + $"worst {worstDeckEnd:0.0000} m at {worstDeckEndAt}; steepest rise per metre moved "
                          + $"{worstDeckEndClimbRate:0.000}");
        foreach ((double residual, string where) in steps.Take(8))
        {
            _output.WriteLine($"  {residual:0.0000} m  {where}");
        }

        _output.WriteLine($"package origin height {manifest.OriginHeightMeters:0.000} m");
        Assert.True(onRoad > 0, "no body was ever seated on a road");
        Assert.True(worstEvaluation < 1e-3, $"a seat's height differs from the engine's profile by {worstEvaluation} m");
        Assert.True(worstOnStructure < 0.05,
                    $"a body on a structure sat {worstOnStructure:0.000} m off its road's densely evaluated profile");
    }

    /// <summary>A pitch steeper than any road a vehicle is built for, degrees.</summary>
    private const double SteepDegrees = 10.0;

    /// <summary>
    /// The pitch the ground surface's gradient gives a body at a pose, as the bridge seated every body
    /// before it read the road: a central difference one cell either side along the yaw.
    /// </summary>
    private static double GroundPitch(GroundSurface ground, in VehiclePose pose)
    {
        double radians = pose.YawDegrees * (Math.PI / 180.0);
        double step = ground.CellSizeMetres;
        double? ahead = ground.Sample(pose.X + (step * Math.Cos(radians)), pose.Y + (step * Math.Sin(radians)));
        double? behind = ground.Sample(pose.X - (step * Math.Cos(radians)), pose.Y - (step * Math.Sin(radians)));
        return ahead is { } a && behind is { } b ? Math.Atan((a - b) / (2.0 * step)) * (180.0 / Math.PI) : 0.0;
    }

    /// <summary>
    /// The deck a position under a structure lies beneath: the non-junction road, other than the one
    /// it is on, whose reference line passes within fifteen metres of it and stands highest there.
    /// </summary>
    private static string Above(RoadSurface roads, double x, double y, uint under)
    {
        string best = string.Empty;
        double highest = double.NegativeInfinity;
        foreach (RoadProfile road in roads.Roads.Values)
        {
            if (road.Id == under || road.IsJunction)
            {
                continue;
            }

            RoadProjection there = road.Project(x, y, 0.0, road.Length);
            if (there.S < 0.0 || there.S > road.Length || Math.Abs(there.Lateral) > 15.0)
            {
                continue;
            }

            double z = road.Elevation(there.S).Z;
            if (z > highest)
            {
                highest = z;
                best = $" beneath road {road.Id} {(road.Name.Length > 0 ? road.Name : "(unnamed)")}";
            }
        }

        return best;
    }

    /// <summary>
    /// The road's elevation at the point of its reference line nearest a position, found by evaluating
    /// the reference line every ten centimetres near <paramref name="nearS"/> -- independently of the
    /// seating's own sampled polyline and record search.
    /// </summary>
    private static double DenseProfile(CarlaNet.Map.Road.Road road, double x, double y, double nearS,
                                       out double bestS)
    {
        bestS = Math.Clamp(nearS, 0.0, road.Length);
        double best = double.PositiveInfinity;
        for (double s = Math.Max(0.0, nearS - 6.0); s <= Math.Min(road.Length, nearS + 6.0); s += 0.1)
        {
            var point = RoadMap.GetDirectedPointIn(road, s);
            double dx = x - point.Location.X;
            double dy = y - point.Location.Y;

            // Along-track offset of the position from this point of the reference line: zero at the foot
            // of its perpendicular.
            double along = Math.Abs((dx * Math.Cos(point.Tangent)) + (dy * Math.Sin(point.Tangent)));
            if (along < best)
            {
                best = along;
                bestS = s;
            }
        }

        if (nearS < 0.0 || nearS > road.Length)
        {
            // Off the road's end: carried on at the end's slope, as the seating carries it.
            double end = nearS < 0.0 ? 0.0 : road.Length;
            double step = nearS < 0.0 ? 0.01 : -0.01;
            double z0 = RoadMap.GetDirectedPointIn(road, end).Location.Z;
            double z1 = RoadMap.GetDirectedPointIn(road, end + step).Location.Z;
            return z0 + ((z1 - z0) / step * (nearS - end));
        }

        return RoadMap.GetDirectedPointIn(road, bestS).Location.Z;
    }

    private sealed class Group
    {
        private readonly HashSet<string> _vehicles = [];
        private long _poses;
        private double _minimum = double.PositiveInfinity;
        private double _maximum = double.NegativeInfinity;
        private double _worstPitch;
        private double _worstRoll;

        public void Add(string vehicle, double aboveTheGround, double pitch, double roll)
        {
            _vehicles.Add(vehicle);
            _poses++;
            _minimum = Math.Min(_minimum, aboveTheGround);
            _maximum = Math.Max(_maximum, aboveTheGround);
            _worstPitch = Math.Max(_worstPitch, Math.Abs(pitch));
            _worstRoll = Math.Max(_worstRoll, Math.Abs(roll));
        }

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"{_vehicles.Count,4} vehicles {_poses,7} poses, {_minimum:+0.00;-0.00} .. {_maximum:+0.00;-0.00} m off the ground, |pitch| <= {_worstPitch:0.00} deg, |roll| <= {_worstRoll:0.00} deg");
    }
}
