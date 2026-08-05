// Offline (no engine, no server): the posted speed limit on a lane has to reach the vehicle.
//
// Nothing ever populated the simulation state's speed limit — ALSM would need one RPC per vehicle
// per tick to do it — so the motion planner fell through to a 30 km/h urban default for every
// vehicle on the map. On a generated interchange that capped traffic at 8.3 m/s on a 65 mph
// motorway, and no amount of per-vehicle speed tuning could lift it, because every knob in the
// traffic manager is expressed as a percentage OF the limit.
//
// The limit is in the map: OpenDRIVE carries a <speed> record per lane. These tests check it is
// parsed, cached on the road graph, and converted to the units the planner works in.
#nullable enable

using CarlaNet.Map.OpenDrive;
using CarlaNet.TrafficManager;
using Xunit;
using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Tests.TrafficManager;

public class SpeedLimitTests
{
    /// <summary>
    /// Two roads: one posting 29.06 m/s (a 65 mph motorway, as netconvert emits it) and one posting
    /// nothing at all.
    /// </summary>
    private static string Xodr(bool withSpeed) =>
        "<?xml version=\"1.0\" standalone=\"yes\"?>\n" +
        "<OpenDRIVE>\n" +
        "  <header revMajor=\"1\" revMinor=\"4\" name=\"\" version=\"1\" north=\"0\" south=\"0\" east=\"0\" west=\"0\"/>\n" +
        "  <road name=\"Road 1\" length=\"200.0\" id=\"1\" junction=\"-1\">\n" +
        "    <planView>\n" +
        "      <geometry s=\"0.0\" x=\"0.0\" y=\"0.0\" hdg=\"0.0\" length=\"200.0\"><line/></geometry>\n" +
        "    </planView>\n" +
        "    <lanes>\n" +
        "      <laneSection s=\"0.0\">\n" +
        "        <center><lane id=\"0\" type=\"none\" level=\"false\"/></center>\n" +
        "        <right>\n" +
        "          <lane id=\"-1\" type=\"driving\" level=\"false\">\n" +
        "            <width sOffset=\"0.0\" a=\"3.5\" b=\"0.0\" c=\"0.0\" d=\"0.0\"/>\n" +
        (withSpeed ? "            <speed sOffset=\"0\" max=\"29.06\" />\n" : "") +
        "          </lane>\n" +
        "        </right>\n" +
        "      </laneSection>\n" +
        "    </lanes>\n" +
        "  </road>\n" +
        "</OpenDRIVE>\n";

    private static InMemoryMap Build(bool withSpeed)
    {
        RoadMap worldMap = OpenDriveParser.Load(Xodr(withSpeed))
            ?? throw new InvalidOperationException("test .xodr failed to parse");
        var map = new InMemoryMap(worldMap);
        map.SetUp();
        return map;
    }

    [Fact]
    public void A_posted_lane_speed_reaches_the_road_graph_in_the_units_the_planner_uses()
    {
        InMemoryMap map = Build(withSpeed: true);
        var topology = map.GetDenseTopology();
        Assert.NotEmpty(topology);

        // 29.06 m/s is how netconvert writes 65 mph. The motion planner works in km/h.
        foreach (var waypoint in topology)
            Assert.Equal(29.06 * 3.6, waypoint.SpeedLimitKph, 1);

        // The value that actually matters: what the planner would cap a vehicle at, in m/s.
        float capMetresPerSecond = topology[0].SpeedLimitKph / 3.6f;
        Assert.Equal(29.06f, capMetresPerSecond, 1);
        Assert.True(capMetresPerSecond > 8.4f,
            "a motorway lane must not cap a vehicle at the 30 km/h urban fallback.");
    }

    [Fact]
    public void A_lane_with_no_posted_speed_reports_none_rather_than_guessing()
    {
        // Zero means "unknown", which is what lets the motion planner tell a genuinely unposted road
        // apart from one posting a very low limit, and apply its own default only to the former.
        InMemoryMap map = Build(withSpeed: false);
        foreach (var waypoint in map.GetDenseTopology())
            Assert.Equal(0f, waypoint.SpeedLimitKph);
    }

    [Theory]
    // The per-vehicle knob is a percentage OFF the limit, so the spread a scenario asks for is only
    // as wide as the limit underneath it. These are the speeds a +/-20% spread produces.
    [InlineData(-20f, 125.6f)]   // 20% over
    [InlineData(0f, 104.6f)]
    [InlineData(20f, 83.7f)]     // 20% under
    public void A_speed_difference_is_applied_to_the_posted_limit(float percentage, float expectedKph)
    {
        InMemoryMap map = Build(withSpeed: true);
        float postedKph = map.GetDenseTopology()[0].SpeedLimitKph;

        var parameters = new Parameters();
        parameters.SetPercentageSpeedDifference(actorId: 1u, percentage);

        Assert.Equal(expectedKph, parameters.GetVehicleTargetVelocity(1u, postedKph), 0);
    }
}
