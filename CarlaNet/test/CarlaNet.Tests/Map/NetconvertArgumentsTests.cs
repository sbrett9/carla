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

    [Fact]
    public void AnExtractWithNoRampMeterGetsTheSharedArgumentListUnchanged()
    {
        // Every world without a meter -- Gardnerville, Bahonar -- must convert exactly as before.
        var converter = new OsmConverter(WorldBuildOptions);

        Assert.Equal(Expected, converter.BuildArguments("map.osm", "map.xodr", "map.net.xml", rampMeters: []));
    }

    [Fact]
    public void RampMetersAreKeptOutOfJoiningAndGivenTheirProgrammes()
    {
        var converter = new OsmConverter(WorldBuildOptions);
        var argv = converter.BuildArguments("map.osm", "map.xodr", "map.net.xml",
                                            ["2279085566", "582784737"], "meters.tll.xml");

        // The shared list with the meters' two options after the signal type, and nothing else moved.
        var expected = Expected.ToList();
        int after = expected.IndexOf("actuated") + 1;
        expected.InsertRange(after, ["--junctions.join-exclude", "2279085566,582784737",
                                     "--tllogic-files", "meters.tll.xml"]);
        Assert.Equal(expected, argv);
    }

    [Fact]
    public void TheRunThatFindsTheMetersLinksReadsNoProgrammes()
    {
        // The first of a metered extract's two runs: the meters already out of joining, so the links
        // it reports are the links the second run's programmes will drive.
        var converter = new OsmConverter(WorldBuildOptions);
        var argv = converter.BuildArguments("map.osm", "map.xodr", "map.net.xml", ["202"]);

        Assert.Equal("202", argv[argv.ToList().IndexOf("--junctions.join-exclude") + 1]);
        Assert.DoesNotContain("--tllogic-files", argv);
    }

    [Fact]
    public void RampMetersAddNothingWithTrafficLightsOff()
    {
        // Every signal is discarded, the meters with them, so there is nothing to keep apart.
        var converter = new OsmConverter(WorldBuildOptions with { GenerateTrafficLights = false });
        var argv = converter.BuildArguments("map.osm", "map.xodr", "map.net.xml", ["202"], "meters.tll.xml");

        Assert.DoesNotContain("--junctions.join-exclude", argv);
        Assert.DoesNotContain("--tllogic-files", argv);
    }

    [Theory]
    [InlineData("--junctions.join-exclude", "123")]
    [InlineData("--junctions.join-exclude=123", null)]
    [InlineData("--tllogic-files", "own.tll.xml")]
    [InlineData("-i", "own.tll.xml")]
    public void AnOptionTheMetersSetIsRefusedFromTheExtraArguments(string option, string? value)
    {
        // netconvert reads each once; passed twice, one list would silently replace the other.
        var converter = new OsmConverter(WorldBuildOptions with
        {
            ExtraArgs = value is null ? [option] : [option, value],
        });

        var refusal = Assert.Throws<ArgumentException>(
            () => converter.BuildArguments("map.osm", "map.xodr", "map.net.xml", ["202"], "meters.tll.xml"));
        Assert.Contains("MeterRamps", refusal.Message);
    }

    [Fact]
    public void AnOptionTheMetersWouldSetPassesThroughWhenTheExtractHasNone()
    {
        var converter = new OsmConverter(WorldBuildOptions with { ExtraArgs = ["--junctions.join-exclude", "123"] });

        var argv = converter.BuildArguments("map.osm", "map.xodr", "map.net.xml", rampMeters: []);

        Assert.Equal(["--junctions.join-exclude", "123"], argv.TakeLast(2));
    }

    [Fact]
    public void MeteringOffFindsNoMeters()
    {
        string osm = Path.Combine(AppContext.BaseDirectory, "Map", "Fixtures", "Network", "ramp_meter.osm");

        Assert.Equal(["202"], new OsmConverter(WorldBuildOptions).RampMetersOf(osm));
        Assert.Empty(new OsmConverter(WorldBuildOptions with { MeterRamps = false }).RampMetersOf(osm));
        Assert.Empty(new OsmConverter(WorldBuildOptions with { GenerateTrafficLights = false }).RampMetersOf(osm));
    }

    [Fact]
    public void TheProgrammeFileIsRecordedByAFixedName()
    {
        var converter = new OsmConverter(WorldBuildOptions);
        string[] recorded = [.. OsmConverter.RecordedArguments(
            converter.BuildArguments("map.osm", @"C:\t\a.xodr", @"C:\t\b.net.xml", ["202"], @"C:\t\c.tll.xml"),
            @"C:\t\a.xodr", @"C:\t\b.net.xml", @"C:\t\c.tll.xml")];

        Assert.Equal(OsmConverter.RecordedProgramsInput, recorded[Array.IndexOf(recorded, "--tllogic-files") + 1]);
        Assert.Equal("<tllogic-files>", OsmConverter.RecordedProgramsInput);
    }

    [Fact]
    public void TheRecordedArgumentsNameTheOutputsByFixedNamesAndKeepEverythingElse()
    {
        // Two builds of one world write to two random scratch files; what they record must agree.
        var converter = new OsmConverter(WorldBuildOptions);
        string[] first = [.. OsmConverter.RecordedArguments(
            converter.BuildArguments("map.osm", @"C:\t\carlanet_osm_a.xodr", @"C:\t\carlanet_osm_b.net.xml"),
            @"C:\t\carlanet_osm_a.xodr", @"C:\t\carlanet_osm_b.net.xml")];
        string[] second = [.. OsmConverter.RecordedArguments(
            converter.BuildArguments("map.osm", @"C:\t\carlanet_osm_c.xodr", @"C:\t\carlanet_osm_d.net.xml"),
            @"C:\t\carlanet_osm_c.xodr", @"C:\t\carlanet_osm_d.net.xml")];

        Assert.Equal(first, second);
        Assert.Equal(OsmConverter.RecordedOpenDriveOutput, first[Array.IndexOf(first, "--opendrive-output") + 1]);
        Assert.Equal(OsmConverter.RecordedNetworkOutput, first[Array.IndexOf(first, "--output-file") + 1]);
        Assert.Contains("map.osm", first);
    }
}
