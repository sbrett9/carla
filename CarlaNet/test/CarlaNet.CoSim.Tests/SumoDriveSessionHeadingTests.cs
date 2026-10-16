using CarlaNet.Recording;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A driven body points along the path its bumper takes, keeps its bumper where SUMO put it, moves with
/// the path's velocity, and carries SUMO's reported angle beside its pose and in each frame's render set.
/// </summary>
/// <remarks>
/// Driven over the cross at the fixture's 0.05 s step, one tick to a step: <c>turner</c> turns right
/// through a five-point connector, and with three-second lane changes <c>changer</c> moves across the
/// approach to turn left.
/// </remarks>
public sealed class SumoDriveSessionHeadingTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionHeadingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void ThroughTheTurnTheBodyTurnsNoFasterThanItsRearAxleAllowsAndItsBumperStaysWhereSumoPutIt()
    {
        Drive drive = Run(CoSimFixtures.RightAngleTurnScenario, ticks: 260);
        SumoRoadNetwork network = SumoRoadNetwork.Load(CoSimFixtures.RightAngleTurnNetwork);
        List<CoSimPoseRecord> turner = drive.Track("turner");
        VehicleExtent truck = CompiledFixture.Catalogue.Resolve("measured_truck", "vehicle.fuso.mitsubishi");
        double rearAxle = PathHeading.RearAxleMetres(truck);

        // The first pose points where SUMO said the vehicle pointed.
        Assert.Equal(turner[0].SumoAngleDegrees, Heading(turner[0].Pose), 9);

        double worstBody = 0.0;
        double worstTangent = 0.0;
        for (int index = 1; index < turner.Count; index++)
        {
            CoSimPoseRecord before = turner[index - 1];
            CoSimPoseRecord after = turner[index];
            Assert.True(BumperResidual(after.Pose, truck, after.SumoX, after.SumoY) < 1e-9,
                        $"tick {after.TickIndex}: the bumper left SUMO's position");

            double forward = 0.5 * (Speed(before) + Speed(after)) * drive.Tick;
            double turned = Math.Abs(Math.IEEERemainder(Heading(after.Pose) - Heading(before.Pose), 360.0));
            Assert.True(turned <= (forward / rearAxle * (180.0 / Math.PI)) + 1e-6,
                        $"tick {after.TickIndex}: turned {turned:0.000} degrees over {forward:0.000} m of travel");
            worstBody = Math.Max(worstBody, turned);
            worstTangent = Math.Max(worstTangent, Math.Abs(Math.IEEERemainder(
                Tangent(network, after) - Tangent(network, before), 360.0)));
        }

        _output.WriteLine($"worst heading step {worstBody:0.000} degrees; the lane's tangent stepped {worstTangent:0.000}");
        Assert.True(worstTangent > 10.0 * worstBody,
                    $"the lane's tangent stepped {worstTangent:0.00} degrees and the body {worstBody:0.00}");

        // And once round the corner it points down the exit, due east.
        Assert.Equal(90.0, Heading(turner[^1].Pose), 0);
    }

    [RequiresSumoFact]
    public void ThroughALaneChangeTheBodyTurnsWithItsSidewaysMovementAndItsVelocityIsThePaths()
    {
        string configuration = File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<time-to-teleport value=\"-1\"/>",
            "<time-to-teleport value=\"-1\"/>\n        <lanechange.duration value=\"3\"/>",
            StringComparison.Ordinal);
        using CompiledFixture compiled = CompiledFixture.Write(configuration: configuration);
        Drive drive = Run(compiled.Scenario, ticks: 300);
        List<CoSimPoseRecord> changer = drive.Track("changer");

        // On the approach, which runs due north: the body turns left with the change and back, and its
        // velocity carries the sideways movement, pointing where the bumper went.
        double mostTurned = 0.0;
        double mostAcross = 0.0;
        for (int index = 1; index < changer.Count; index++)
        {
            CoSimPoseRecord before = changer[index - 1];
            CoSimPoseRecord after = changer[index];
            if (!after.LaneId.StartsWith("approach_", StringComparison.Ordinal)
                || !before.LaneId.StartsWith("approach_", StringComparison.Ordinal))
            {
                continue;
            }

            mostTurned = Math.Max(mostTurned, -Math.IEEERemainder(Heading(after.Pose), 360.0));
            double vx = after.Pose.VelocityX;
            double vyNorth = -after.Pose.VelocityY;
            mostAcross = Math.Max(mostAcross, -vx);
            Assert.Equal((after.SumoX - before.SumoX) / drive.Tick, vx, 6);
            Assert.Equal((after.SumoY - before.SumoY) / drive.Tick, vyNorth, 6);
        }

        _output.WriteLine($"turned up to {mostTurned:0.00} degrees; moved across at up to {mostAcross:0.000} m/s");
        Assert.InRange(mostTurned, 2.0, 30.0);
        Assert.Equal(3.35 / 3.0, mostAcross, 2);
    }

    [RequiresSumoFact]
    public void EachFrameSRenderSetCarriesSumoSAngleForTheBodiesItDrew()
    {
        Drive drive = Run(CoSimFixtures.RightAngleTurnScenario, ticks: 120);
        int checkedBodies = 0;
        foreach (CoSimPoseRecord record in drive.Poses.Where(record => record.Actor != 0))
        {
            RenderSet set = drive.RenderSets[(ulong)record.TickIndex + 1];
            Assert.True(set.TryGet(record.Actor, out RenderedVehicle rendered));
            Assert.Equal(record.Pose.VehicleId, rendered.SumoId);
            Assert.Equal(record.SumoAngleDegrees, rendered.SumoAngleDegrees!.Value, 9);
            checkedBodies++;
        }

        Assert.True(checkedBodies > 100, $"only {checkedBodies} drawn bodies were checked");
    }

    private Drive Run(string scenario, int ticks)
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        List<CoSimPoseRecord> poses = [];
        Dictionary<ulong, RenderSet> sets = [];
        var options = new SumoDriveSessionOptions(scenario, world.PackagePath, CoSimFixtures.VehicleCatalogue,
                                                  "test://" + Guid.NewGuid().ToString("n"))
        {
            World = carla,
            Epoch = SolarLeaseTests.PortEpoch(),
            Illumination = IlluminationPolicy.FreezeAtWindowStart(),
            OnPose = poses.Add,
        };

        double tick;
        using (SumoDriveSession session = SumoDriveSession.Start(options))
        {
            for (int count = 0; count < ticks && session.Advance(); count++)
            {
                for (ulong frame = (ulong)sets.Count + 1; frame <= session.RenderSet.NewestFrame; frame++)
                {
                    Assert.True(session.RenderSet.TryGetRenderSet(frame, out RenderSet set), $"frame {frame} is not held");
                    sets[frame] = set;
                }
            }

            _output.WriteLine(session.Report.ToString());
            tick = session.Clock.WorldDeltaSeconds;
        }

        return new Drive(poses, sets, tick);
    }

    /// <summary>A pose's heading, degrees clockwise from north, from its CARLA yaw.</summary>
    private static double Heading(in VehiclePose pose)
    {
        double heading = (pose.YawDegrees + 90.0) % 360.0;
        return heading < 0.0 ? heading + 360.0 : heading;
    }

    private static double Speed(in CoSimPoseRecord record) =>
        Math.Sqrt((record.Pose.VelocityX * record.Pose.VelocityX) + (record.Pose.VelocityY * record.Pose.VelocityY));

    /// <summary>The tangent of the lane under the bumper, as the bridge used to take the heading.</summary>
    private static double Tangent(SumoRoadNetwork network, in CoSimPoseRecord record)
    {
        Assert.True(network.TryGetLane(record.LaneId, out SumoLane lane));
        (_, _, double dx, double dy) = lane.PointAt(record.LanePositionMetres);
        double degrees = Math.Atan2(dx, dy) * (180.0 / Math.PI);
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }

    /// <summary>How far the bumper the pose puts back is from where SUMO had it.</summary>
    private static double BumperResidual(in VehiclePose pose, in VehicleExtent extent, double sumoX, double sumoY)
    {
        double radians = pose.YawDegrees * (Math.PI / 180.0);
        double bumperX = pose.X + (extent.BumperToOriginMetres * Math.Cos(radians))
                         - (extent.LateralOffsetMetres * Math.Sin(radians));
        double bumperY = pose.Y + (extent.BumperToOriginMetres * Math.Sin(radians))
                         + (extent.LateralOffsetMetres * Math.Cos(radians));
        return Math.Sqrt(Math.Pow(bumperX - sumoX, 2) + Math.Pow(-bumperY - sumoY, 2));
    }

    private sealed record Drive(List<CoSimPoseRecord> Poses, Dictionary<ulong, RenderSet> RenderSets, double Tick)
    {
        public List<CoSimPoseRecord> Track(string vehicle) =>
            [.. Poses.Where(record => record.Pose.VehicleId == vehicle).OrderBy(record => record.TickIndex)];
    }
}
