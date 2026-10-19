using System.Globalization;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive;
using Xunit.Abstractions;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The whole bridge seating its bodies by the roads of the world it drives: SUMO driving the fixture
/// scenario, the session seating every pose on the ground or on the OpenDRIVE netconvert wrote from the
/// same network by how far the road stands from the ground, and the run report saying where every pose's
/// height came from.
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
    public void OnAViaductEveryBodyIsSeatedOnItsRoadAndRidesItContinuouslyThroughTheJunction()
    {
        // The whole network stands on a structure: the approach rises at 2 % from two metres above the
        // level ground, and every other road stands level at the height it reaches, so the connectors meet
        // it. A body seated on the ground would sit two to four metres below the road it drives.
        string openDrive = Profiled(File.ReadAllText(CoSimFixtures.RightAngleTurnOpenDrive), Viaduct);
        RoadMap map = OpenDriveParser.Load(openDrive)!;
        (Dictionary<string, List<CoSimPoseRecord>> tracks, CoSimRunReport report, double tick) = Drive(openDrive);
        Assert.Equal(report.PosesSeatedOnTheRoad, report.PosesOnAStructure);

        int climbing = 0;
        foreach (List<CoSimPoseRecord> track in tracks.Values)
        {
            foreach (CoSimPoseRecord record in track)
            {
                RoadSeat seat = record.Pose.Road!.Value;
                Assert.NotEqual(string.Empty, record.LaneId);
                Assert.Equal(0.0, seat.GroundWeight);
                Assert.Equal(0.0, record.Pose.RollDegrees);

                // The seat is the profile of the road named, as the engine evaluates it.
                CarlaNet.Map.Road.Road road = map.Roads[seat.RoadId];
                if (seat.S >= 0.0 && seat.S <= road.Length)
                {
                    Assert.Equal(RoadMap.GetDirectedPointIn(road, seat.S).Location.Z, seat.SurfaceZMetres, tolerance: 1e-4);
                }

                // Driving straight up the approach -- on one of its lanes, not changing lane across it or
                // already turning into the junction -- the climb is the road's 2 %.
                double speed = Math.Sqrt((record.Pose.VelocityX * record.Pose.VelocityX)
                                         + (record.Pose.VelocityY * record.Pose.VelocityY));
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
            }
        }

        double worstStep = AssertTangentAndContinuous(tracks, tick);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{climbing} poses climbing the approach; worst unexplained change of height between ticks {worstStep:0.0000} m"));
        Assert.True(climbing > 0, "no body ever drove the approach");
    }

    [RequiresSumoFact]
    public void ABodyClimbingARampOutOfTheGroundIsSeatedOnTheGroundThenBlendedThenOnTheRoadWithoutAStep()
    {
        // The approach rises at 2 % out of the level ground and every other road stands level at the
        // height it reaches, 1.85 m: at grade for its first 25 m, on a structure beyond 75 m, blended
        // between.
        string openDrive = Profiled(File.ReadAllText(CoSimFixtures.RightAngleTurnOpenDrive), Ramp);
        (Dictionary<string, List<CoSimPoseRecord>> tracks, CoSimRunReport report, double tick) = Drive(openDrive);
        Assert.True(report.PosesAtGrade > 0, "no body was ever at grade");
        Assert.True(report.PosesOnAnApproach > 0, "no body was ever blended");
        Assert.True(report.PosesOnAStructure > 0, "no body was ever on the structure");
        Assert.Equal(report.PosesSeatedOnTheRoad,
                     report.PosesAtGrade + report.PosesOnAnApproach + report.PosesOnAStructure);

        foreach (CoSimPoseRecord record in tracks.Values.SelectMany(track => track))
        {
            RoadSeat seat = record.Pose.Road!.Value;
            if (seat.GroundWeight >= 1.0)
            {
                // At grade on level ground: the ground's own seat, level, with no climb.
                Assert.Equal(0.0, record.Pose.PitchDegrees);
                Assert.Equal(0.0, record.Pose.RollDegrees);
                Assert.Equal(0.0, record.Pose.VelocityZ);
            }
        }

        double worstStep = AssertTangentAndContinuous(tracks, tick);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{report.PosesAtGrade} poses at grade, {report.PosesOnAnApproach} blended, {report.PosesOnAStructure} on the structure; "
            + $"worst unexplained change of height between ticks {worstStep:0.0000} m"));
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

    /// <summary>How high above the level ground the approach starts on the viaduct, metres.</summary>
    private const double Viaduct = 2.0;

    /// <summary>The approach starting on the level ground.</summary>
    private const double Ramp = 0.0;

    /// <summary>
    /// SUMO driving the fixture scenario over a world of level ground at zero with this OpenDRIVE, every
    /// pose recorded by vehicle; the run's report, after asserting that every pose was on a road.
    /// </summary>
    private (Dictionary<string, List<CoSimPoseRecord>> Tracks, CoSimRunReport Report, double Tick) Drive(string openDrive)
    {
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

        using SumoDriveSession session = SumoDriveSession.Start(options);
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
        return (tracks, report, session.Clock.WorldDeltaSeconds);
    }

    /// <summary>
    /// Asserts that every pose climbs at its pitch's slope times SUMO's speed, and that no body's height
    /// changes between two ticks by more than that climb explains; returns the worst unexplained change.
    /// </summary>
    private static double AssertTangentAndContinuous(Dictionary<string, List<CoSimPoseRecord>> tracks, double tick)
    {
        double worstStep = 0.0;
        foreach (List<CoSimPoseRecord> track in tracks.Values)
        {
            for (int index = 0; index < track.Count; index++)
            {
                VehiclePose pose = track[index].Pose;
                double speed = Math.Sqrt((pose.VelocityX * pose.VelocityX) + (pose.VelocityY * pose.VelocityY));
                Assert.Equal(Math.Tan(pose.PitchDegrees * (Math.PI / 180.0)) * speed, pose.VelocityZ, tolerance: 1e-6);
                if (index > 0 && track[index - 1].TickIndex == track[index].TickIndex - 1)
                {
                    VehiclePose before = track[index - 1].Pose;
                    double climbed = 0.5 * (before.VelocityZ + pose.VelocityZ) * tick;
                    worstStep = Math.Max(worstStep, Math.Abs(pose.Z - before.Z - climbed));
                }
            }
        }

        Assert.True(worstStep < 0.02, $"a body's height stepped by {worstStep:0.000} m between two ticks");
        return worstStep;
    }

    /// <summary>
    /// The fixture OpenDRIVE with the approach climbing at <see cref="Climb"/> from
    /// <paramref name="start"/> metres above the ground, and every other road level at the height the
    /// approach reaches.
    /// </summary>
    private static string Profiled(string openDrive, double start)
    {
        XDocument document = XDocument.Parse(openDrive);
        XElement approach = document.Root!.Elements("road").Single(road =>
            road.Elements("userData").Any(data => (string?)data.Attribute("value") == "approach"));
        double top = start + (Climb * double.Parse((string)approach.Attribute("length")!, CultureInfo.InvariantCulture));
        foreach (XElement road in document.Root.Elements("road"))
        {
            (double a, double b) = road == approach ? (start, Climb) : (top, 0.0);
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
