using System.Globalization;
using CarlaNet.Map.WorldPackage;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// At grade, how closely a body's seat follows the road a camera sees: the seat the converter gives, the
/// draped ground grid and the road's flat-across OpenDRIVE profile, each against the raw photoreal
/// surface the world was built from, at every lane centre of the shipped packages' at-grade roads, by
/// distance across the road from the reference line; and how often an at-grade lane loses any of the
/// ground's roll.
/// </summary>
/// <remarks>
/// A measurement more than a check: it asserts only that a body on an at-grade road is seated on the
/// ground, all but a few lane centres in a hundred with the ground's whole seat.
/// The photoreal surface comes from the world build's drape cache (<see cref="PhotorealSurface"/>), which
/// is not committed; where no cache of a package's grid is found the package is reported and passed over.
/// </remarks>
public sealed class AtGradeSurfaceMeasurementTests
{
    /// <summary>
    /// How far a road's profile may depart from the ground anywhere along its reference line and the road
    /// be at grade: one that departs further is a structure somewhere, a deck or a road spanning the
    /// ground beneath one. Judged per road, so whether a lane centre counts as at grade has nothing to do
    /// with the weight the seat gives it.
    /// </summary>
    private const double AtGradeRoadMetres = PoseConverter.OnStructureDepartureMetres;

    /// <summary>
    /// How far the photoreal may stand from bare earth and still be ground: the world build's own
    /// criterion for draping onto it (<c>DrapeTerrain.Despike</c>'s <c>maxDrapeMeters</c>). Further is a
    /// building, a tree or a structure.
    /// </summary>
    private const double PhotorealIsGroundWithinMetres = 5.0;

    /// <summary>A body whose origin is its front bumper and sits on the surface, so its height is the seat's.</summary>
    private static readonly VehicleExtent Point = new("vehicle.test.point", 0.0, 2.0, 1.5, (0.0, 0.0, 0.75));

    private readonly ITestOutputHelper _output;

    public AtGradeSurfaceMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [ShippedWorldPackagesFact]
    public void AtGradeTheSeatIsTheGroundAndIsMeasuredAgainstThePhotorealByDistanceAcrossTheRoad()
    {
        int measured = 0;
        foreach (string package in new[] { ShippedWorldPackagesFactAttribute.Arapahoe!, ShippedWorldPackagesFactAttribute.Bahonar! })
        {
            if (PhotorealSurface.ForPackage(package) is not { } photoreal)
            {
                _output.WriteLine($"{Path.GetFileName(package)}: no drape cache of its grid; not measured");
                continue;
            }

            WorldPackageManifest manifest = WorldPackage.ReadManifest(package);
            SumoRoadNetwork network = SumoRoadNetwork.FromWorldPackage(package);
            GroundSurface ground = GroundSurface.FromWorldPackage(package);
            RoadSurface roads = RoadSurface.FromWorldPackage(package, network);
            var converter = new PoseConverter(ground, roads: roads);
            double h0 = manifest.OriginHeightMeters;

            // Which roads are at grade: their profile never further from the ground than a structure's
            // smallest lift, anywhere along the reference line; and the profile's departure there, which
            // the weight is taken from.
            HashSet<uint> atGradeRoads = [];
            List<double> referenceDepartures = [];
            foreach (RoadProfile road in roads.Roads.Values)
            {
                double worst = 0.0;
                List<double> departures = [];
                for (double s = 0.0; s <= road.Length; s += 2.0)
                {
                    (double x, double y) = road.ReferencePoint(s);
                    if (ground.SampleForSumoPosition(x, y) is { } height)
                    {
                        double departure = road.Elevation(s).Z + h0 - height;
                        departures.Add(Math.Abs(departure));
                        worst = Math.Max(worst, Math.Abs(departure));
                    }
                }

                if (worst < AtGradeRoadMetres)
                {
                    atGradeRoads.Add(road.Id);
                    referenceDepartures.AddRange(departures);
                }
            }

            var carriageways = new SurfaceAgreement("seat", "ground", "profile");
            var connectors = new SurfaceAgreement("seat", "ground", "profile");
            long points = 0;
            long notGround = 0;
            long lostByLane = 0;
            long lostByReference = 0;
            List<double> partialWeights = [];
            double worstOffTheGround = 0.0;
            foreach (SumoLane lane in network.Lanes)
            {
                for (double position = 0.0; position <= lane.DeclaredLengthMetres; position += 2.0)
                {
                    (double x, double y, double dx, double dy) = lane.PointAt(position);
                    double heading = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                    if (converter.Convert("m", Point, x, y, heading, 0.0, lane.Id, position) is not { Road: { } seat } pose
                        || !atGradeRoads.Contains(seat.RoadId)
                        || ground.Sample(x, -y) is not { } grid
                        || photoreal.Surface(x, -y) is not { } surface
                        || photoreal.BareEarth(x, -y) is not { } bareEarth)
                    {
                        continue;
                    }

                    points++;
                    double profile = seat.SurfaceZMetres + h0;

                    // Any roll lost: by the weight taken at the lane, as first built, and at the reference
                    // line, as the seat now takes it.
                    if (PoseConverter.GroundWeight(profile - grid) < 1.0)
                    {
                        lostByLane++;
                    }

                    if (seat.GroundWeight < 1.0)
                    {
                        lostByReference++;
                        partialWeights.Add(seat.GroundWeight);
                        worstOffTheGround = Math.Max(worstOffTheGround, Math.Abs(pose.Z + h0 - grid));
                    }

                    if (Math.Abs(surface - bareEarth) > PhotorealIsGroundWithinMetres)
                    {
                        notGround++;
                        continue;
                    }

                    RoadProfile road = roads.Roads[seat.RoadId];
                    double across = road.Project(x, y, seat.S - 1.0, seat.S + 1.0).Lateral;
                    (road.IsJunction ? connectors : carriageways)
                        .Add(across, pose.Z + h0 - surface, grid - surface, profile - surface);
                    measured++;
                }
            }

            referenceDepartures.Sort();
            double Q(double quantile) => referenceDepartures[(int)(quantile * (referenceDepartures.Count - 1))];
            double within = referenceDepartures.Count(value => value <= PoseConverter.AtGradeDepartureMetres)
                            / (double)referenceDepartures.Count;
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileName(package)} against {Path.GetFileName(photoreal.Path)} (bare earth agrees to {photoreal.BareEarthAgreementMetres:0.0000} m): "
                + $"{atGradeRoads.Count} of {roads.Roads.Count} roads at grade"));
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  their profile's departure from the ground at the reference line, every 2 m: p50 {Q(0.5):0.000}, p90 {Q(0.9):0.000}, "
                + $"p99 {Q(0.99):0.000}, p99.9 {Q(0.999):0.000} m; {within:0.00%} within {PoseConverter.AtGradeDepartureMetres} m"));
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {points} lane-centre points on them: {lostByLane} ({lostByLane / (double)points:0.00%}) would lose some roll by the "
                + $"departure at the lane, {lostByReference} ({lostByReference / (double)points:0.00%}) do by the departure at the "
                + $"reference line; {notGround} where the photoreal is more than {PhotorealIsGroundWithinMetres} m from bare earth left out of the table"));
            partialWeights.Sort();
            _output.WriteLine(partialWeights.Count == 0
                ? "  none given less than the ground's whole seat"
                : string.Create(CultureInfo.InvariantCulture,
                    $"  those given less than the ground's whole seat: weight median {partialWeights[partialWeights.Count / 2]:0.000}, "
                    + $"p10 {partialWeights[(int)(0.1 * (partialWeights.Count - 1))]:0.000}, lowest {partialWeights[0]:0.000}; "
                    + $"seated at most {worstOffTheGround:0.000} m off the ground"));
            _output.WriteLine("  carriageways (roads that are not junction connectors)");
            _output.WriteLine(carriageways.Describe());
            _output.WriteLine("  junction connectors");
            _output.WriteLine(connectors.Describe());
            Assert.True(lostByReference < 0.02 * points,
                        $"{lostByReference} of {points} lane centres on at-grade roads were given less than the ground's whole seat");
        }

        Assert.True(measured > 0 || PhotorealSurface.ForPackage(ShippedWorldPackagesFactAttribute.Arapahoe!) is null,
                    "a drape cache was found and nothing was measured");
    }
}
