// Offline (no engine, no server): a vehicle has to know which signal governs its lane, and how far
// ahead it is, before it reaches that signal's stop line.
//
// The simulator hands a vehicle a traffic light's state only while the vehicle physically overlaps
// the light's stop-line trigger box. Measured on a generated interchange, a vehicle crosses that box
// in 0.2 to 0.8 seconds over 3.7 to 8.0 metres — far less than it needs to stop from the posted
// speed — and the instant it leaves, the reported state reverts to "not at a light, green", which is
// indistinguishable downstream from a real green. Vehicles consequently drove through red lights
// having been told to stop for less than a second, too late and too briefly to act on.
//
// The map already carries what is needed: each <signal> names the lanes it governs in <validity>,
// and its s coordinate is where its stop line sits. These tests check that the road graph resolves,
// for a waypoint, the next signal a vehicle in that lane will actually reach — respecting direction
// of travel, lane validity, and static-versus-dynamic signals.
#nullable enable

using CarlaNet.Map.OpenDrive;
using CarlaNet.TrafficManager;
using Xunit;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.TrafficManager;

public class SignalVisibilityTests
{
    /// <summary>
    /// One 200 m road with two driving lanes each way. A signal sits at s=150 governing only the
    /// right-hand lanes (-1 and -2), as netconvert emits for an approach: traffic running with
    /// increasing s meets it, traffic running against s does not.
    /// </summary>
    private static string Xodr(
        string signalType = "1000001",
        string dynamic = "yes",
        string fromLane = "-2",
        string toLane = "-1") =>
        "<?xml version=\"1.0\" standalone=\"yes\"?>\n" +
        "<OpenDRIVE>\n" +
        "  <header revMajor=\"1\" revMinor=\"4\" name=\"\" version=\"1\" north=\"0\" south=\"0\" east=\"0\" west=\"0\"/>\n" +
        "  <road name=\"Road 1\" length=\"200.0\" id=\"1\" junction=\"-1\">\n" +
        "    <planView>\n" +
        "      <geometry s=\"0.0\" x=\"0.0\" y=\"0.0\" hdg=\"0.0\" length=\"200.0\"><line/></geometry>\n" +
        "    </planView>\n" +
        "    <lanes>\n" +
        "      <laneSection s=\"0.0\">\n" +
        "        <left>\n" +
        "          <lane id=\"1\" type=\"driving\" level=\"false\">\n" +
        "            <width sOffset=\"0.0\" a=\"3.5\" b=\"0.0\" c=\"0.0\" d=\"0.0\"/>\n" +
        "          </lane>\n" +
        "        </left>\n" +
        "        <center><lane id=\"0\" type=\"none\" level=\"false\"/></center>\n" +
        "        <right>\n" +
        "          <lane id=\"-1\" type=\"driving\" level=\"false\">\n" +
        "            <width sOffset=\"0.0\" a=\"3.5\" b=\"0.0\" c=\"0.0\" d=\"0.0\"/>\n" +
        "          </lane>\n" +
        "          <lane id=\"-2\" type=\"driving\" level=\"false\">\n" +
        "            <width sOffset=\"0.0\" a=\"3.5\" b=\"0.0\" c=\"0.0\" d=\"0.0\"/>\n" +
        "          </lane>\n" +
        "        </right>\n" +
        "      </laneSection>\n" +
        "    </lanes>\n" +
        "    <signals>\n" +
        $"      <signal s=\"150.0\" t=\"-8.0\" id=\"sig_A\" name=\"\" dynamic=\"{dynamic}\" " +
        $"orientation=\"-\" zOffset=\"0.0\" country=\"OpenDRIVE\" type=\"{signalType}\" " +
        "subtype=\"-1\" value=\"-1\" height=\"0\" width=\"0\">\n" +
        $"        <validity fromLane=\"{fromLane}\" toLane=\"{toLane}\"/>\n" +
        "      </signal>\n" +
        "    </signals>\n" +
        "  </road>\n" +
        "</OpenDRIVE>\n";

    private static InMemoryMap Build(string xodr)
    {
        RoadMap worldMap = OpenDriveParser.Load(xodr)
            ?? throw new InvalidOperationException("test .xodr failed to parse");
        var map = new InMemoryMap(worldMap);
        map.SetUp();
        return map;
    }

    /// <summary>Waypoints on one lane, ordered along increasing s.</summary>
    private static List<SimpleWaypoint> Lane(InMemoryMap map, int laneId) =>
        map.GetDenseTopology()
            .Where(w => w.Waypoint.LaneId == laneId)
            .OrderBy(w => w.Waypoint.S)
            .ToList();

    [Fact]
    public void A_vehicle_knows_its_signal_long_before_it_reaches_the_stop_line()
    {
        var lane = Lane(Build(Xodr()), -1);
        Assert.NotEmpty(lane);

        // 100 m back from a stop line at s=150, the signal is already known and correctly placed.
        SimpleWaypoint far = lane.MinBy(w => Math.Abs(w.Waypoint.S - 50.0))!;
        Assert.Equal("sig_A", far.GoverningSignalId);
        Assert.Equal(100.0f, far.DistanceToGoverningSignal, 1.0f);

        // The distance shrinks as the vehicle closes on it, rather than being a flat flag.
        SimpleWaypoint near = lane.MinBy(w => Math.Abs(w.Waypoint.S - 140.0))!;
        Assert.Equal("sig_A", near.GoverningSignalId);
        Assert.Equal(10.0f, near.DistanceToGoverningSignal, 1.0f);
    }

    [Fact]
    public void A_signal_already_passed_does_not_govern_the_road_beyond_it()
    {
        var lane = Lane(Build(Xodr()), -1);
        SimpleWaypoint beyond = lane.MinBy(w => Math.Abs(w.Waypoint.S - 180.0))!;
        Assert.Null(beyond.GoverningSignalId);
    }

    [Fact]
    public void Oncoming_traffic_is_not_governed_by_the_other_carriageway_s_signal()
    {
        // Lane 1 runs against increasing s, so a signal at s=150 is behind a vehicle at s=50 — and
        // the validity range names only the right-hand lanes in any case. Taking whichever signal is
        // nearest in either direction would stop oncoming traffic for a light it never faces.
        var lane = Lane(Build(Xodr()), 1);
        Assert.NotEmpty(lane);
        Assert.All(lane, w => Assert.Null(w.GoverningSignalId));
    }

    [Fact]
    public void A_lane_outside_the_signal_s_validity_is_not_governed_by_it()
    {
        // Validity naming only lane -1 leaves lane -2 ungoverned, which is how an approach whose
        // heads were collapsed onto one pole loses coverage of its other lanes.
        var map = Build(Xodr(fromLane: "-1", toLane: "-1"));
        Assert.All(Lane(map, -1).Where(w => w.Waypoint.S < 150.0),
            w => Assert.Equal("sig_A", w.GoverningSignalId));
        Assert.All(Lane(map, -2), w => Assert.Null(w.GoverningSignalId));
    }

    [Fact]
    public void A_static_sign_does_not_govern_a_lane_as_a_traffic_light()
    {
        // Stop and speed-limit signs never change state, so there is nothing to watch for and
        // nothing that would ever release a vehicle held by one.
        var map = Build(Xodr(signalType: "206", dynamic: "no"));
        Assert.All(Lane(map, -1), w => Assert.Null(w.GoverningSignalId));
    }

    [Fact]
    public void The_nearest_signal_ahead_wins_when_a_lane_meets_several()
    {
        string twoSignals = Xodr().Replace(
            "    </signals>",
            "      <signal s=\"80.0\" t=\"-8.0\" id=\"sig_B\" name=\"\" dynamic=\"yes\" " +
            "orientation=\"-\" zOffset=\"0.0\" country=\"OpenDRIVE\" type=\"1000001\" " +
            "subtype=\"-1\" value=\"-1\" height=\"0\" width=\"0\">\n" +
            "        <validity fromLane=\"-2\" toLane=\"-1\"/>\n" +
            "      </signal>\n" +
            "    </signals>");
        var lane = Lane(Build(twoSignals), -1);

        // Before both: the closer one governs.
        SimpleWaypoint before = lane.MinBy(w => Math.Abs(w.Waypoint.S - 50.0))!;
        Assert.Equal("sig_B", before.GoverningSignalId);
        Assert.Equal(30.0f, before.DistanceToGoverningSignal, 1.0f);

        // Between them: the first is behind, so the second takes over.
        SimpleWaypoint between = lane.MinBy(w => Math.Abs(w.Waypoint.S - 100.0))!;
        Assert.Equal("sig_A", between.GoverningSignalId);
        Assert.Equal(50.0f, between.DistanceToGoverningSignal, 1.0f);
    }
}
