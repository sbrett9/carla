using System.Globalization;
using CarlaNet.Map.WorldPackage;
using Xunit.Abstractions;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The road seating against the shipped world packages: every SUMO lane joined to a road, and a body
/// on Arapahoe's decks and on the road beneath I-25 seated on its own road's profile rather than on the
/// one height the ground surface holds there.
/// </summary>
public sealed class RoadSurfaceShippedTests
{
    private readonly ITestOutputHelper _output;

    public RoadSurfaceShippedTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [ShippedWorldPackagesFact]
    public void EveryShippedNetworkIsJoinedToItsRoadsLaneByLane()
    {
        foreach (string package in new[]
                 {
                     ShippedWorldPackagesFactAttribute.Arapahoe!,
                     ShippedWorldPackagesFactAttribute.Bahonar!,
                     ShippedWorldPackagesFactAttribute.Gardnerville!,
                 })
        {
            RoadSurface roads = RoadSurface.FromWorldPackage(package, SumoRoadNetwork.FromWorldPackage(package));
            RoadMappingSummary mapping = roads.Mapping;
            _output.WriteLine($"{Path.GetFileName(package)}: {mapping}");
            if (mapping.UnmappedLanes.Count > 0)
            {
                _output.WriteLine("  unmapped: " + string.Join(" ", mapping.UnmappedLanes));
            }

            // Measured on the packages as shipped: every lane on Arapahoe and Bahonar.
            Assert.True(mapping.MappedLanes >= 0.99 * mapping.Lanes,
                        $"{Path.GetFileName(package)}: {mapping.MappedLanes} of {mapping.Lanes} lanes joined");
            Assert.True(mapping.Lateral.P99Absolute < 1.5, $"lateral residual {mapping.Lateral}");
            Assert.True(mapping.AlongTrack.P99Absolute < 4.0, $"along-track residual {mapping.AlongTrack}");
        }
    }

    [ShippedWorldPackagesFact]
    public void OnArapahoeSDecksAndBeneathI25ABodySitsOnItsRoadSProfileAndNotOnTheGround()
    {
        string package = ShippedWorldPackagesFactAttribute.Arapahoe!;
        SumoRoadNetwork network = SumoRoadNetwork.FromWorldPackage(package);
        GroundSurface ground = GroundSurface.FromWorldPackage(package);
        RoadSurface roads = RoadSurface.FromWorldPackage(package, network);
        RoadMap map = CarlaNet.Map.OpenDrive.OpenDriveParser.Load(WorldPackage.ReadOpenDrive(package))!;

        // Every normal edge whose road stands well clear of the ground somewhere along its lanes: above
        // it, a deck; below it, a road spanning the ground beneath one.
        Dictionary<string, (double High, double Low, uint Road)> edges = [];
        foreach (SumoLane lane in network.Lanes.Where(lane => !lane.IsInternal))
        {
            for (double position = 0.0; position <= lane.DeclaredLengthMetres; position += 2.0)
            {
                (double x, double y, double dx, double dy) = lane.PointAt(position);
                if (!roads.TrySeat(lane.Id, position, x, y, dx, dy, 0.0, out RoadSeat seat, out _)
                    || ground.SampleForSumoPosition(x, y) is not { } height)
                {
                    continue;
                }

                double departure = seat.SurfaceZMetres - (height - ground.OriginHeightMetres);
                (double high, double low, _) = edges.TryGetValue(lane.EdgeId, out var known)
                    ? known
                    : (double.NegativeInfinity, double.PositiveInfinity, 0u);
                edges[lane.EdgeId] = (Math.Max(high, departure), Math.Min(low, departure), seat.RoadId);
            }
        }

        List<string> decks = [.. edges.Where(entry => entry.Value.High > 3.0).Select(entry => entry.Key).Order()];
        List<string> beneath = [.. edges.Where(entry => entry.Value.Low < -2.0).Select(entry => entry.Key).Order()];
        foreach (string edge in decks.Concat(beneath))
        {
            (double high, double low, uint road) = edges[edge];
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {edge,-16} road {road,5} {map.Roads[road].Name,-22} {low:+0.00;-0.00} .. {high:+0.00;-0.00} m from the ground"));
        }

        // I-25's deck over Arapahoe Road and Yosemite Street's, and East Arapahoe Road beneath I-25.
        Assert.True(decks.Count >= 7, $"{decks.Count} deck edges: {string.Join(", ", decks)}");
        Assert.Contains("223306870", beneath);
        Assert.Contains(decks, edge => map.Roads[edges[edge].Road].Name == "South Valley Highway");

        // Along every one of them, the seat is the road's profile, densely evaluated by the engine's own
        // reference line and elevation, and nowhere near the ground where the road is clear of it.
        double worst = 0.0;
        int checkedPoints = 0;
        foreach (string edge in decks.Concat(beneath))
        {
            foreach (SumoLane lane in network.LanesOfEdge(edge))
            {
                // Inside the lane, where its road is the road under it rather than the junction's.
                for (double position = 0.5; position <= lane.DeclaredLengthMetres - 0.5; position += 1.0)
                {
                    (double x, double y, double dx, double dy) = lane.PointAt(position);
                    Assert.True(roads.TrySeat(lane.Id, position, x, y, dx, dy, 0.0, out RoadSeat seat, out _));
                    CarlaNet.Map.Road.Road road = map.Roads[seat.RoadId];
                    double profile = Dense(road, x, y, seat.S);
                    worst = Math.Max(worst, Math.Abs(seat.SurfaceZMetres - profile));
                    checkedPoints++;
                }
            }
        }

        _output.WriteLine($"{checkedPoints} points on {decks.Count} deck edges and {beneath.Count} beneath; "
                          + $"worst gap to the profile {worst:0.0000} m");
        Assert.True(worst < 0.02, $"a seat sat {worst:0.000} m off its road's profile");
    }

    [ShippedWorldPackagesFact]
    public void OnArapahoeSDecksTheTruthHeightIsTheDeckSAndTheGroundStaysTheGround()
    {
        // The recorded truth and the live pull report hae = physical - offset(x, y) and hae_dtm = the
        // bare earth at (x, y), sampling the package's two grids with the heightfield's triangulation.
        // A deck carries no grid of its own: under every deck the build anchored the drape to bare earth
        // plus the systematic photoreal-against-bare-earth offset, which is the offset the deck's own
        // height was measured against. So for a body seated on a deck the offset taken off is that same
        // systematic one, hae is the deck's altitude in the bare-earth datum, and hae_dtm is the ground
        // beneath it -- metres below, which no check may read as a body in the air.
        string package = ShippedWorldPackagesFactAttribute.Arapahoe!;
        WorldPackageManifest manifest = WorldPackage.ReadManifest(package);
        Assert.True(WorldPackage.TryReadGrids(package, out float[] offsets, out float[] bareEarth));
        SumoRoadNetwork network = SumoRoadNetwork.FromWorldPackage(package);
        GroundSurface ground = GroundSurface.FromWorldPackage(package);
        RoadSurface roads = RoadSurface.FromWorldPackage(package, network);

        List<double> atGrade = [];
        List<double> underDecks = [];
        List<double> clearance = [];
        foreach (SumoLane lane in network.Lanes.Where(lane => !lane.IsInternal))
        {
            for (double position = 0.0; position <= lane.DeclaredLengthMetres; position += 2.0)
            {
                (double x, double y, double dx, double dy) = lane.PointAt(position);
                if (!roads.TrySeat(lane.Id, position, x, y, dx, dy, 0.0, out RoadSeat seat, out _)
                    || ground.SampleForSumoPosition(x, y) is not { } surface)
                {
                    continue;
                }

                double departure = seat.SurfaceZMetres - (surface - ground.OriginHeightMetres);
                double offset = Triangulated(offsets, manifest, x, -y);
                if (Math.Abs(departure) < 0.2)
                {
                    atGrade.Add(offset);
                }
                else if (departure > 3.0)
                {
                    underDecks.Add(offset);
                    double hae = seat.SurfaceZMetres + manifest.OriginHeightMeters - offset;
                    clearance.Add(hae - Triangulated(bareEarth, manifest, x, -y));
                }
            }
        }

        atGrade.Sort();
        double systematic = atGrade[atGrade.Count / 2];
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"offset at grade (median) {systematic:0.000} m; under the decks {underDecks.Min():0.000} .. {underDecks.Max():0.000} m "
            + $"over {underDecks.Count} points; hae - hae_dtm with no pivot {clearance.Min():0.00} .. {clearance.Max():0.00} m"));

        Assert.NotEmpty(underDecks);
        Assert.All(underDecks, offset => Assert.InRange(offset, systematic - 0.5, systematic + 0.5));
        Assert.True(clearance.Min() > 2.0, "a deck's truth height came out near the ground beneath it");
    }

    /// <summary>A package grid sampled as the truth telemetry samples it, on the heightfield's triangles.</summary>
    private static double Triangulated(float[] grid, WorldPackageManifest manifest, double carlaX, double carlaY)
    {
        int columns = manifest.GridNumCols;
        double fc = Math.Clamp((carlaX - manifest.GridMinXMeters) / manifest.GridCellSizeMeters, 0.0, columns - 1.0);
        double fr = Math.Clamp((carlaY - manifest.GridMinYMeters) / manifest.GridCellSizeMeters, 0.0, manifest.GridNumRows - 1.0);
        int c0 = (int)fc;
        int r0 = (int)fr;
        int c1 = Math.Min(c0 + 1, columns - 1);
        int r1 = Math.Min(r0 + 1, manifest.GridNumRows - 1);
        double tx = fc - c0;
        double ty = fr - r0;
        double v00 = grid[(r0 * columns) + c0];
        double v01 = grid[(r0 * columns) + c1];
        double v10 = grid[(r1 * columns) + c0];
        double v11 = grid[(r1 * columns) + c1];
        return ty <= tx
            ? v00 + ((v01 - v00) * tx) + ((v11 - v01) * ty)
            : v00 + ((v11 - v10) * tx) + ((v10 - v00) * ty);
    }

    /// <summary>
    /// The road's elevation at the foot of the perpendicular from a position to its reference line,
    /// searched every ten centimetres near <paramref name="nearS"/>.
    /// </summary>
    private static double Dense(CarlaNet.Map.Road.Road road, double x, double y, double nearS)
    {
        double bestS = Math.Clamp(nearS, 0.0, road.Length);
        double best = double.PositiveInfinity;
        for (double s = Math.Max(0.0, nearS - 5.0); s <= Math.Min(road.Length, nearS + 5.0); s += 0.1)
        {
            var point = RoadMap.GetDirectedPointIn(road, s);
            double along = Math.Abs(((x - point.Location.X) * Math.Cos(point.Tangent))
                                    + ((y - point.Location.Y) * Math.Sin(point.Tangent)));
            if (along < best)
            {
                best = along;
                bestS = s;
            }
        }

        return RoadMap.GetDirectedPointIn(road, bestS).Location.Z;
    }
}
