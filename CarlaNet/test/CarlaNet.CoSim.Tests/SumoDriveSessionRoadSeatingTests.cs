using System.Globalization;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;
using Xunit.Abstractions;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The whole bridge seating its bodies on the roads of the world it drives: SUMO driving the fixture
/// scenario, the session seating every pose on the OpenDRIVE netconvert wrote from the same network, and
/// the run report saying where every pose's height came from.
/// </summary>
public sealed class SumoDriveSessionRoadSeatingTests
{
    /// <summary>The climb given to the approach road, rise per metre.</summary>
    private const double Climb = 0.02;

    private readonly ITestOutputHelper _output;

    public SumoDriveSessionRoadSeatingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void EveryBodyOnALaneIsSeatedOnItsRoadAndRidesItContinuouslyThroughTheJunction()
    {
        // The approach climbs at 2 % to the junction; every other road stands level at the height it
        // reaches there, so the connectors meet it. The ground is level at zero throughout, so a body
        // seated on it would sit up to 1.85 m below the road it drives.
        string openDrive = Profiled(File.ReadAllText(CoSimFixtures.RightAngleTurnOpenDrive));
        RoadMap map = OpenDriveParser.Load(openDrive)!;
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!",
                                                          openDrive: openDrive);

        Dictionary<string, List<CoSimPoseRecord>> tracks = [];
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            OnPose = record =>
            {
                if (!tracks.TryGetValue(record.Pose.VehicleId, out List<CoSimPoseRecord>? track))
                {
                    track = [];
                    tracks[record.Pose.VehicleId] = track;
                }

                track.Add(record);
            },
        };

        double tick;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            tick = session.Clock.WorldDeltaSeconds;
            for (int step = 0; step < 400 && session.Advance(); step++)
            {
            }

            _output.WriteLine(session.Report.ToString());
            CoSimRunReport report = session.Report;
            Assert.True(report.PosesComputed > 0);
            Assert.Equal(report.PosesComputed, report.PosesSeatedOnTheRoad);
            Assert.Empty(report.PosesSeatedOnTheGround);
            Assert.NotNull(report.RoadMapping);
            Assert.Equal(report.RoadMapping!.Lanes, report.RoadMapping.MappedLanes);
            Assert.Contains("on the road", report.ToString());
        }

        int climbing = 0;
        double worstStep = 0.0;
        foreach (List<CoSimPoseRecord> track in tracks.Values)
        {
            for (int index = 0; index < track.Count; index++)
            {
                CoSimPoseRecord record = track[index];
                RoadSeat seat = record.Pose.Road!.Value;
                Assert.NotEqual(string.Empty, record.LaneId);

                // The seat is the profile of the road named, as the engine evaluates it.
                CarlaNet.Map.Road.Road road = map.Roads[seat.RoadId];
                if (seat.S >= 0.0 && seat.S <= road.Length)
                {
                    Assert.Equal(RoadMap.GetDirectedPointIn(road, seat.S).Location.Z, seat.SurfaceZMetres, tolerance: 1e-4);
                }

                // The climb is the pitch's slope times SUMO's speed, always; and driving straight up the
                // approach -- on one of its lanes, not changing lane across it or already turning into
                // the junction -- it is the road's 2 %.
                double speed = Math.Sqrt((record.Pose.VelocityX * record.Pose.VelocityX)
                                         + (record.Pose.VelocityY * record.Pose.VelocityY));
                Assert.Equal(Math.Tan(record.Pose.PitchDegrees * (Math.PI / 180.0)) * speed,
                             record.Pose.VelocityZ, tolerance: 1e-6);
                if (road.UserData.TryGetValue("sumoId", out string? edge) && edge == "approach"
                    && record.Case == LaneInterpolationCase.SameLane
                    && record.LaneId.StartsWith("approach_", StringComparison.Ordinal)
                    && seat.S > 1.0 && seat.S < road.Length - 1.0)
                {
                    // To the reference line's single-precision coordinates, as the engine stores them.
                    Assert.Equal(Climb * speed, record.Pose.VelocityZ, tolerance: 1e-4);
                    Assert.Equal(Math.Atan(Climb) * (180.0 / Math.PI), record.Pose.PitchDegrees, tolerance: 1e-3);
                    climbing++;
                }

                if (index > 0 && track[index - 1].TickIndex == record.TickIndex - 1)
                {
                    VehiclePose before = track[index - 1].Pose;
                    double climbed = 0.5 * (before.VelocityZ + record.Pose.VelocityZ) * tick;
                    worstStep = Math.Max(worstStep, Math.Abs(record.Pose.Z - before.Z - climbed));
                }
            }
        }

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{climbing} poses climbing the approach; worst unexplained change of height between ticks {worstStep:0.0000} m"));
        Assert.True(climbing > 0, "no body ever drove the approach");
        Assert.True(worstStep < 0.02, $"a body's height stepped by {worstStep:0.000} m between two ticks");
    }

    [RequiresSumoFact]
    public void OnAWorldWhoseOpenDriveHasNoRoadsEveryBodyIsSeatedOnTheGroundAndCountedSo()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var options = new SumoDriveSessionOptions(
            CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
        };

        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < 200 && session.Advance(); step++)
        {
        }

        CoSimRunReport report = session.Report;
        Assert.True(report.PosesComputed > 0);
        Assert.Equal(0, report.PosesSeatedOnTheRoad);
        Assert.Equal(report.PosesComputed, report.PosesSeatedOnTheGround[GroundSeatReason.NoRoad]);
        Assert.Equal(0, report.RoadMapping!.MappedLanes);
    }

    /// <summary>
    /// The fixture OpenDRIVE with the approach climbing at <see cref="Climb"/> and every other road level
    /// at the height the approach reaches.
    /// </summary>
    private static string Profiled(string openDrive)
    {
        XDocument document = XDocument.Parse(openDrive);
        XElement approach = document.Root!.Elements("road").Single(road =>
            road.Elements("userData").Any(data => (string?)data.Attribute("value") == "approach"));
        double top = Climb * double.Parse((string)approach.Attribute("length")!, CultureInfo.InvariantCulture);
        foreach (XElement road in document.Root.Elements("road"))
        {
            (double a, double b) = road == approach ? (0.0, Climb) : (top, 0.0);
            road.Element("elevationProfile")!.ReplaceNodes(new XElement("elevation",
                new XAttribute("s", "0"),
                new XAttribute("a", a.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("b", b.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("c", "0"),
                new XAttribute("d", "0")));
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }
}
