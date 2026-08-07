// Offline (no engine, no server): a vehicle is only removed for running out of road when IT has run
// out of road, not when its lookahead has.
//
// The horizon runs speed x HORIZON_RATE ahead of the vehicle, so a walk that reaches a waypoint with
// no successors has found where the road graph ends, not where the vehicle is. Acting on that
// directly destroyed a vehicle still a hundred metres short of the end and driving perfectly well —
// and because a faster vehicle looks further ahead, raising traffic to motorway speeds turned it
// into the destruction of most of the fleet within about four seconds of spawning. Measured live
// over the telemetry stream: 35 of 41 short-lived vehicles died in the interior, up to 84 m inside
// the staging margin, after covering a median of 52 m.
#nullable enable

using CarlaNet.Map.OpenDrive;
using CarlaNet.Map.Road.Element;
using CarlaNet.TrafficManager;
using CarlaNet.TrafficManager.Stages;
using CarlaNet.Types.Geom;
using Xunit;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.TrafficManager;

public class RoadEndRemovalTests
{
    private const ActorId Vehicle = 1u;
    private const float HorizonSquare = 300.0f * 300.0f;

    private const string FlatXodr =
        "<?xml version=\"1.0\" standalone=\"yes\"?>\n" +
        "<OpenDRIVE>\n" +
        "  <header revMajor=\"1\" revMinor=\"4\" name=\"\" version=\"1\" north=\"0\" south=\"0\" east=\"0\" west=\"0\"/>\n" +
        "  <road name=\"Road 1\" length=\"100.0\" id=\"1\" junction=\"-1\">\n" +
        "    <planView>\n" +
        "      <geometry s=\"0.0\" x=\"0.0\" y=\"0.0\" hdg=\"0.0\" length=\"100.0\"><line/></geometry>\n" +
        "    </planView>\n" +
        "    <lanes>\n" +
        "      <laneSection s=\"0.0\">\n" +
        "        <center><lane id=\"0\" type=\"none\" level=\"false\"/></center>\n" +
        "        <right>\n" +
        "          <lane id=\"-1\" type=\"driving\" level=\"false\">\n" +
        "            <width sOffset=\"0.0\" a=\"3.5\" b=\"0.0\" c=\"0.0\" d=\"0.0\"/>\n" +
        "          </lane>\n" +
        "        </right>\n" +
        "      </laneSection>\n" +
        "    </lanes>\n" +
        "  </road>\n" +
        "</OpenDRIVE>\n";

    /// <summary>A chain of waypoints that simply stops — the shape a clipped road presents.</summary>
    private static List<SimpleWaypoint> DeadEndRoad(int count)
    {
        var road = new List<SimpleWaypoint>(count);
        for (int i = 0; i < count; ++i)
            road.Add(new SimpleWaypoint(
                new Waypoint(roadId: 50u, sectionId: 0u, laneId: -1, s: i * 5.0),
                new Location(i * 5.0f, 0.0f, 0.0f), new Vector3D(1.0f, 0.0f, 0.0f)));
        for (int i = 0; i < count - 1; ++i)
            road[i].SetNextWaypoint(new List<SimpleWaypoint> { road[i + 1] });
        return road;                                   // road[^1] has no successors at all
    }

    private static LocalizationStage BuildStage(out Parameters parameters, out BufferMap buffers,
                                                out List<ActorId> markedForRemoval)
    {
        RoadMap worldMap = OpenDriveParser.Load(FlatXodr)
            ?? throw new InvalidOperationException("test .xodr failed to parse");
        var map = new InMemoryMap(worldMap);
        map.SetUp();
        parameters = new Parameters();
        buffers = new BufferMap();
        markedForRemoval = new List<ActorId>();
        return new LocalizationStage(new SimulationState(), buffers, new TrackTraffic(), parameters,
                                     map, new RandomGenerator(seed: 1), markedForRemoval);
    }

    [Fact]
    public void A_vehicle_whose_lookahead_reaches_the_end_of_the_road_is_not_removed()
    {
        // The whole road fits inside one horizon, so the walk runs off the end of it on the very
        // first tick — while the vehicle is still at the start, with the entire road ahead of it.
        LocalizationStage stage = BuildStage(out Parameters parameters, out BufferMap buffers,
                                             out List<ActorId> markedForRemoval);
        List<SimpleWaypoint> road = DeadEndRoad(40);
        var buffer = new WaypointBuffer { road[0] };
        buffers[Vehicle] = buffer;

        var destination = new Location(10_000.0f, 0.0f, 0.0f);
        parameters.SetCustomPath(Vehicle, new List<Location> { destination }, emptyBuffer: false);
        stage.ImportPath(new List<Location> { destination }, buffer, Vehicle, HorizonSquare);

        Assert.True(buffer.Count > 10,
            $"the walk only reached {buffer.Count} waypoints, so it never got to the dead end and "
            + "this test is not exercising the guard.");
        Assert.Empty(markedForRemoval);
    }

    [Fact]
    public void A_vehicle_that_has_actually_run_out_of_road_is_removed()
    {
        // The capability this must not lose: a vehicle with almost nothing left ahead of it really
        // has nowhere to go, and is removed so it does not sit at the end of a clipped road forever.
        LocalizationStage stage = BuildStage(out Parameters parameters, out BufferMap buffers,
                                             out List<ActorId> markedForRemoval);
        List<SimpleWaypoint> road = DeadEndRoad(40);
        var buffer = new WaypointBuffer { road[^1] };          // sitting on the last waypoint
        buffers[Vehicle] = buffer;

        var destination = new Location(10_000.0f, 0.0f, 0.0f);
        parameters.SetCustomPath(Vehicle, new List<Location> { destination }, emptyBuffer: false);
        stage.ImportPath(new List<Location> { destination }, buffer, Vehicle, HorizonSquare);

        Assert.Contains(Vehicle, markedForRemoval);
    }

    [Fact]
    public void The_unrouted_walk_follows_the_same_rule()
    {
        // Traffic without a route extends its horizon through a different branch of the same stage,
        // which flagged removals in exactly the same way.
        LocalizationStage stage = BuildStage(out Parameters parameters, out BufferMap buffers,
                                             out List<ActorId> markedForRemoval);
        List<SimpleWaypoint> road = DeadEndRoad(40);

        var far = new WaypointBuffer { road[0] };
        buffers[Vehicle] = far;
        stage.ImportRoute(new List<byte> { (byte)RoadOption.Straight }, far, Vehicle, HorizonSquare);
        Assert.True(far.Count > 10, "the walk never reached the dead end.");
        Assert.Empty(markedForRemoval);

        var atEnd = new WaypointBuffer { road[^1] };
        buffers[Vehicle] = atEnd;
        stage.ImportRoute(new List<byte> { (byte)RoadOption.Straight }, atEnd, Vehicle, HorizonSquare);
        Assert.Contains(Vehicle, markedForRemoval);
    }
}
