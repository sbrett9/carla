using System.Globalization;
using CarlaNet.Map.WorldPackage;
using Xunit.Abstractions;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// At grade, which of the two surfaces a body could be seated on follows the road a camera sees: the
/// road's flat-across OpenDRIVE profile, or the draped ground grid. Both measured against the raw
/// photoreal surface the world was built from, at every lane centre of the shipped packages, by distance
/// across the road from the reference line.
/// </summary>
/// <remarks>
/// A measurement, not a check: it asserts only that something was measured. The photoreal surface comes
/// from the world build's drape cache (<see cref="PhotorealSurface"/>), which is not committed; where no
/// cache of a package's grid is found the package is reported and passed over.
/// </remarks>
public sealed class AtGradeSurfaceMeasurementTests
{
    /// <summary>
    /// How far the road may depart from the ground at its reference line and be at grade there, metres:
    /// the roll weight's full-roll limit, judged where the profile and the grid were both sampled, so the
    /// lane's own distance across the road plays no part in choosing the points.
    /// </summary>
    private const double AtGradeAtTheReferenceLineMetres = 0.5;

    /// <summary>
    /// How far the photoreal may stand from bare earth and still be ground: the world build's own
    /// criterion for draping onto it (<c>DrapeTerrain.Despike</c>'s <c>maxDrapeMeters</c>). Further is a
    /// building, a tree or a structure.
    /// </summary>
    private const double PhotorealIsGroundWithinMetres = 5.0;

    private readonly ITestOutputHelper _output;

    public AtGradeSurfaceMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [ShippedWorldPackagesFact]
    public void AtGradeTheProfileAndTheGroundAreMeasuredAgainstThePhotorealByDistanceAcrossTheRoad()
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

            var carriageways = new SurfaceAgreement();
            var connectors = new SurfaceAgreement();
            long sampled = 0;
            long offGrade = 0;
            long notGround = 0;
            long atGradePoints = 0;
            long partialRoll = 0;
            long noRoll = 0;
            foreach (SumoLane lane in network.Lanes)
            {
                for (double position = 0.0; position <= lane.DeclaredLengthMetres; position += 2.0)
                {
                    (double x, double y, double dx, double dy) = lane.PointAt(position);
                    if (!roads.TrySeat(lane.Id, position, x, y, dx, dy, 0.0, out RoadSeat seat, out _)
                        || ground.Sample(x, -y) is not { } grid
                        || photoreal.Surface(x, -y) is not { } surface
                        || photoreal.BareEarth(x, -y) is not { } bareEarth)
                    {
                        continue;
                    }

                    sampled++;
                    RoadProfile road = roads.Roads[seat.RoadId];
                    double s = Math.Clamp(seat.S, 0.0, road.Length);
                    var reference = RoadMap.GetDirectedPointIn(road.Road, s);
                    if (ground.Sample(reference.Location.X, -reference.Location.Y) is not { } atReference
                        || Math.Abs(road.Elevation(s).Z + manifest.OriginHeightMeters - atReference)
                           > AtGradeAtTheReferenceLineMetres)
                    {
                        offGrade++;
                        continue;
                    }

                    if (Math.Abs(surface - bareEarth) > PhotorealIsGroundWithinMetres)
                    {
                        notGround++;
                        continue;
                    }

                    double across = road.Project(x, y, s - 1.0, s + 1.0).Lateral;
                    double profile = seat.SurfaceZMetres + manifest.OriginHeightMeters;
                    (road.IsJunction ? connectors : carriageways).Add(across, profile - surface, grid - surface);
                    measured++;
                    atGradePoints++;

                    // A point of an at-grade road whose lane stands far enough from the ground under it
                    // that the roll weight takes some of the ground's roll away.
                    double departure = Math.Abs(profile - grid);
                    if (departure > PoseConverter.AtGradeDepartureMetres)
                    {
                        partialRoll++;
                    }

                    if (departure >= PoseConverter.OnStructureDepartureMetres)
                    {
                        noRoll++;
                    }
                }
            }

            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileName(package)} against {Path.GetFileName(photoreal.Path)} (bare earth agrees to {photoreal.BareEarthAgreementMetres:0.0000} m): "
                + $"{sampled} lane-centre points every 2 m; {offGrade} on a road not at grade at its reference line, "
                + $"{notGround} where the photoreal is more than {PhotorealIsGroundWithinMetres} m from bare earth, left out"));
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  of {atGradePoints} at-grade points, {partialRoll} ({partialRoll / (double)Math.Max(1, atGradePoints):0.0%}) stand more than "
                + $"{PoseConverter.AtGradeDepartureMetres} m from the ground under the lane and lose some roll, {noRoll} all of it"));
            _output.WriteLine("  carriageways (roads that are not junction connectors)");
            _output.WriteLine(carriageways.Describe());
            _output.WriteLine("  junction connectors");
            _output.WriteLine(connectors.Describe());
        }

        Assert.True(measured > 0 || PhotorealSurface.ForPackage(ShippedWorldPackagesFactAttribute.Arapahoe!) is null,
                    "a drape cache was found and nothing was measured");
    }
}
