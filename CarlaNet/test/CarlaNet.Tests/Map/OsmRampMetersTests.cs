// Which nodes of an extract are ramp meters.
using CarlaNet.Map;

namespace CarlaNet.Tests.Map;

public class OsmRampMetersTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"carlanet-ramp-meters-{Guid.NewGuid():N}.osm");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ASignalTaggedAsARampMeterIsOne()
    {
        File.WriteAllText(_path, """
            <?xml version="1.0" encoding="utf-8"?>
            <osm version="0.6">
              <node id="9559187336" lat="39.5" lon="-104.9">
                <tag k="highway" v="traffic_signals"/>
                <tag k="traffic_signals" v="ramp_meter"/>
              </node>
              <node id="582784737" lat="39.5" lon="-104.9">
                <tag k="traffic_signals" v="ramp_meter"/>
                <tag k="highway" v="traffic_signals"/>
                <tag k="traffic_signals:direction" v="forward"/>
              </node>
              <node id="103" lat="39.5" lon="-104.9">
                <tag k="highway" v="traffic_signals"/>
                <tag k="traffic_signals" v="traffic_lights"/>
              </node>
              <node id="104" lat="39.5" lon="-104.9">
                <tag k="highway" v="traffic_signals"/>
              </node>
              <node id="105" lat="39.5" lon="-104.9">
                <tag k="traffic_signals" v="ramp_meter"/>
              </node>
              <node id="106" lat="39.5" lon="-104.9"/>
              <way id="900">
                <nd ref="9559187336"/>
                <nd ref="582784737"/>
                <tag k="traffic_signals" v="ramp_meter"/>
                <tag k="highway" v="traffic_signals"/>
              </way>
            </osm>
            """);

        // In ordinal order, so one extract always gives one argument. A node tagged ramp_meter but not
        // highway=traffic_signals is not a signal netconvert builds, and a way is not a node.
        Assert.Equal(["582784737", "9559187336"], OsmRampMeters.Read(_path));
    }

    [Fact]
    public void AnExtractWithNoRampMeterHasNone()
    {
        string osm = Path.Combine(AppContext.BaseDirectory, "Map", "Fixtures", "Network", "street_layout.osm");

        Assert.Empty(OsmRampMeters.Read(osm));
    }
}
