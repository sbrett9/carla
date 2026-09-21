// The one netconvert invocation a world and its scenario share.
//
// The world build and the SUMO scenario build used to run netconvert separately, with different
// flags, and nothing compared them. They now produce one network between them: the world build runs
// netconvert once, the package carries the result, and the scenario build validates its own flag set
// against the argument list the package records rather than running anything.
//
// That validation is only as good as the two sides agreeing on what the flag set is, and they are
// written in different languages. So the expected argument list lives in one file --
// Fixtures/Network/world_build_argv.txt -- which this test asserts the C# world build produces and
// CarlaControl/test/test_scenario_network_binding.py asserts the Python scenario side accepts.
using CarlaNet.Map;

namespace CarlaNet.Tests.Map;

public class NetconvertArgumentsTests
{
    /// The Arapahoe origin, and the road filter every world build passes as extra arguments.
    private static OsmConversionOptions WorldBuildOptions => new()
    {
        OriginLatitude = 39.59431,
        OriginLongitude = -104.88449,
        ExtraArgs =
        [
            "--keep-edges.by-vclass", "passenger",
            "--keep-edges.components", "1",
            "--remove-edges.isolated", "true",
        ],
    };

    private static string[] Expected => File.ReadAllLines(
        Path.Combine(AppContext.BaseDirectory, "Map", "Fixtures", "Network", "world_build_argv.txt"))
        .Where(line => line.Length > 0).ToArray();

    [Fact]
    public void TheWorldBuildProducesTheSharedArgumentList()
    {
        var converter = new OsmConverter(WorldBuildOptions);

        Assert.Equal(Expected, converter.BuildArguments("map.osm", "map.xodr", "map.net.xml"));
    }

    [Fact]
    public void StreetNamesAreOmittedRatherThanSetFalse()
    {
        // NBEdge::expandableBy guards on whether the option was set at all, so writing "false" here
        // would produce the graph "true" produces while reading as though names were off. The only
        // way to turn them off is to leave the option out.
        var converter = new OsmConverter(WorldBuildOptions with { OutputStreetNames = false });
        var argv = converter.BuildArguments("map.osm", "map.xodr", "map.net.xml");

        Assert.DoesNotContain("--output.street-names", argv);
    }

    [Fact]
    public void TheSignalTypeIsOmittedWhenItIsNetconvertsOwnDefault()
    {
        // Passing "--tls.default-type static" and passing nothing produce the same network, but
        // only one of them compares equal to a scenario that did not ask for it.
        var converter = new OsmConverter(WorldBuildOptions with { TrafficLightDefaultType = "static" });
        var argv = converter.BuildArguments("map.osm", "map.xodr", "map.net.xml");

        Assert.DoesNotContain("--tls.default-type", argv);
    }

    [Fact]
    public void TheJunctionJoinDistanceIsOmittedWhenUnset()
    {
        var converter = new OsmConverter(WorldBuildOptions with { JunctionJoinDistanceMeters = null });
        var argv = converter.BuildArguments("map.osm", "map.xodr", "map.net.xml");

        Assert.DoesNotContain("--junctions.join-dist", argv);
    }

    [Fact]
    public void TheNetworkIsRequestedEvenWithTrafficLightsOff()
    {
        // The network is what a scenario is authored against, not a by-product of guessing signals.
        var converter = new OsmConverter(WorldBuildOptions with { GenerateTrafficLights = false });
        var argv = converter.BuildArguments("map.osm", "map.xodr", "map.net.xml");

        Assert.Contains("--output-file", argv);
        Assert.Contains("--tls.discard-loaded", argv);
    }
}
