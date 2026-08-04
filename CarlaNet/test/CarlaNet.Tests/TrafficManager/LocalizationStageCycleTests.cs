// Offline (no engine, no server): the horizon walks in LocalizationStage must terminate on a
// road graph that contains a cycle.
//
// Every walk in that stage decides it has gone far enough by measuring STRAIGHT-LINE distance —
// either the buffer spans the horizon, or a probe has moved far enough from where it started. A
// cyclic successor chain (a motorway loop ramp, a roundabout) satisfies none of those tests: it
// returns the walk to where it began without ever getting far from it. Before the walks were
// bounded, a single vehicle whose greedy path entered a loop ramp appended waypoints at roughly
// 8.6 GB/s until the client exhausted memory, and because the traffic-manager tick holds the
// registration lock for its whole duration, the calling thread blocked and the viewer froze.
//
// These tests build a ring of waypoints whose successors close back on themselves and drive the
// real walk over it. Each runs on a worker with a deadline, so a regression fails the run instead
// of hanging it.
#nullable enable

using CarlaNet.Map.OpenDrive;
using CarlaNet.Map.Road.Element;
using CarlaNet.TrafficManager;
using CarlaNet.TrafficManager.Stages;
using CarlaNet.Types.Geom;
using Xunit;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.TrafficManager;

public class LocalizationStageCycleTests
{
    private const ActorId Actor = 1u;

    // How long a bounded walk is allowed to take. The bound is 512 steps, so this is orders of
    // magnitude of slack; only a non-terminating walk can exceed it.
    private static readonly TimeSpan WalkDeadline = TimeSpan.FromSeconds(10);

    // A ring small enough that no point on it is ever "far enough" from any other for the
    // horizon test to pass. Radius 20 m against a 300 m horizon: circling never terminates.
    private const int RingWaypoints = 40;
    private const double RingRadiusMetres = 20.0;
    private const float HorizonSquare = 300.0f * 300.0f;

    // One straight 100 m road. The map only has to be non-empty: ImportPath resolves the
    // destination through InMemoryMap.GetWaypoint, and the cycle under test is built directly on
    // the waypoint graph rather than expressed in OpenDRIVE.
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

    private static InMemoryMap BuildMap()
    {
        RoadMap worldMap = OpenDriveParser.Load(FlatXodr)
            ?? throw new InvalidOperationException("test .xodr failed to parse");
        var map = new InMemoryMap(worldMap);
        map.SetUp();
        return map;
    }

    /// <summary>
    /// A closed ring of waypoints: each one's only successor is the next, and the last leads back
    /// to the first. This is the shape a loop ramp presents to the walk.
    /// </summary>
    private static List<SimpleWaypoint> BuildCycle()
    {
        var ring = new List<SimpleWaypoint>(RingWaypoints);
        for (int i = 0; i < RingWaypoints; ++i)
        {
            double angle = 2.0 * Math.PI * i / RingWaypoints;
            var location = new Location(
                (float)(RingRadiusMetres * Math.Cos(angle)),
                (float)(RingRadiusMetres * Math.Sin(angle)),
                0.0f);
            // Distinct (roadId, s) per node so the ids differ — the revisit check keys on id.
            var pod = new Waypoint(roadId: 100u + (uint)i, sectionId: 0u, laneId: -1, s: i * 1.0);
            var forward = new Vector3D((float)-Math.Sin(angle), (float)Math.Cos(angle), 0.0f);
            ring.Add(new SimpleWaypoint(pod, location, forward));
        }
        for (int i = 0; i < RingWaypoints; ++i)
        {
            ring[i].SetNextWaypoint(new List<SimpleWaypoint> { ring[(i + 1) % RingWaypoints] });
        }
        return ring;
    }

    private static LocalizationStage BuildStage(InMemoryMap map, out Parameters parameters,
                                                out BufferMap buffers)
    {
        parameters = new Parameters();
        buffers = new BufferMap();
        return new LocalizationStage(
            new SimulationState(), buffers, new TrackTraffic(), parameters, map,
            new RandomGenerator(seed: 1), new List<ActorId>());
    }

    /// <summary>Run <paramref name="walk"/> with a deadline so a non-terminating walk fails the
    /// test rather than hanging the whole run.</summary>
    private static void RunBounded(Action walk, string what)
    {
        var task = Task.Run(walk);
        Assert.True(task.Wait(WalkDeadline),
            $"{what} did not terminate within {WalkDeadline.TotalSeconds:F0}s on a cyclic road "
            + "graph — the horizon walk is unbounded again.");
        task.GetAwaiter().GetResult();   // surface any exception from the walk
    }

    [Fact]
    public void ImportPath_terminates_on_a_cyclic_road_graph()
    {
        InMemoryMap map = BuildMap();
        LocalizationStage stage = BuildStage(map, out Parameters parameters, out BufferMap buffers);
        List<SimpleWaypoint> ring = BuildCycle();

        var buffer = new WaypointBuffer { ring[0] };
        buffers[Actor] = buffer;

        // A destination the ring never reaches, so only the bound can stop the walk.
        var destination = new Location(10_000.0f, 10_000.0f, 0.0f);
        parameters.SetCustomPath(Actor, new List<Location> { destination }, emptyBuffer: false);

        RunBounded(() => stage.ImportPath(new List<Location> { destination }, buffer, Actor, HorizonSquare),
                   nameof(LocalizationStage.ImportPath));

        Assert.True(buffer.Count > RingWaypoints / 2,
            $"walk stopped after {buffer.Count} waypoints — it never entered the cycle, so this "
            + "test is not exercising the guard.");
        Assert.True(buffer.Count <= 600,
            $"horizon buffer grew to {buffer.Count} waypoints on a {RingWaypoints}-node ring; "
            + "the walk is appending far beyond the bound.");
    }

    [Fact]
    public void ImportRoute_terminates_on_a_cyclic_road_graph()
    {
        InMemoryMap map = BuildMap();
        LocalizationStage stage = BuildStage(map, out Parameters parameters, out BufferMap buffers);
        List<SimpleWaypoint> ring = BuildCycle();

        var buffer = new WaypointBuffer { ring[0] };
        buffers[Actor] = buffer;

        // A road option the ring never satisfies, for the same reason.
        var route = new List<byte> { (byte)RoadOption.Left };
        parameters.SetImportedRoute(Actor, route, emptyBuffer: false);

        RunBounded(() => stage.ImportRoute(route, buffer, Actor, HorizonSquare),
                   nameof(LocalizationStage.ImportRoute));

        Assert.True(buffer.Count > RingWaypoints / 2,
            $"walk stopped after {buffer.Count} waypoints — it never entered the cycle, so this "
            + "test is not exercising the guard.");
        Assert.True(buffer.Count <= 600,
            $"horizon buffer grew to {buffer.Count} waypoints on a {RingWaypoints}-node ring; "
            + "the walk is appending far beyond the bound.");
    }

    [Fact]
    public void A_cycle_that_avoids_the_front_waypoint_still_terminates()
    {
        // The original guard only broke when the successor returned to buffer[0], so a cycle
        // closing anywhere else in the buffer ran forever. Enter the ring one node in, leaving the
        // front waypoint off the cycle entirely.
        InMemoryMap map = BuildMap();
        LocalizationStage stage = BuildStage(map, out Parameters parameters, out BufferMap buffers);
        List<SimpleWaypoint> ring = BuildCycle();

        var approach = new SimpleWaypoint(
            new Waypoint(roadId: 99u, sectionId: 0u, laneId: -1, s: 0.0),
            new Location((float)RingRadiusMetres + 5.0f, 0.0f, 0.0f),
            new Vector3D(-1.0f, 0.0f, 0.0f));
        approach.SetNextWaypoint(new List<SimpleWaypoint> { ring[0] });

        var buffer = new WaypointBuffer { approach };
        buffers[Actor] = buffer;

        var destination = new Location(10_000.0f, 10_000.0f, 0.0f);
        parameters.SetCustomPath(Actor, new List<Location> { destination }, emptyBuffer: false);

        RunBounded(() => stage.ImportPath(new List<Location> { destination }, buffer, Actor, HorizonSquare),
                   "ImportPath on a cycle that excludes the front waypoint");

        Assert.True(buffer.Count > RingWaypoints / 2,
            $"walk stopped after {buffer.Count} waypoints — it never entered the cycle, so this "
            + "test is not exercising the guard.");
        Assert.True(buffer.Count <= 600,
            $"horizon buffer grew to {buffer.Count} waypoints; a cycle that does not pass through "
            + "the front waypoint is not being detected.");
    }
}
