// The world build's conversion of an extract with a ramp meter, through the real netconvert.
//
// Fixtures/Network/ramp_meter.osm is the Arapahoe defect in miniature: a three-lane freeway gaining a
// fourth lane where a two-lane on-ramp joins it, with the ramp's meter 16 m short of the merge --
// inside the 25 m join distance, as meter 582785322 is on Arapahoe. Converted without metering,
// netconvert joins the meter with the merge and the joined signal holds the freeway on red every
// cycle; that conversion is the control, so the fixture is known to exercise the join.
using System.Xml.Linq;
using CarlaNet.Map;

namespace CarlaNet.Tests.Map;

public class RampMeterConversionTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "Map", "Fixtures", "Network", name);

    private static OsmConversionOptions Options => new()
    {
        NetconvertPath = StagedNetconvert.Executable,
        ProjDataDirectory = StagedNetconvert.ProjData,
        OriginLatitude = 39.5,
        OriginLongitude = -104.9,
    };

    /// The signal programmes of a network, each with the edges its links leave.
    private static Dictionary<string, (XElement Logic, HashSet<string> FromEdges)> Signals(string network)
    {
        XElement root = XDocument.Parse(network).Root!;
        var signals = root.Elements("tlLogic").ToDictionary(
            l => l.Attribute("id")!.Value, l => (Logic: l, FromEdges: new HashSet<string>()));
        foreach (XElement connection in root.Elements("connection"))
            if (connection.Attribute("tl")?.Value is string tl && signals.TryGetValue(tl, out var signal))
                signal.FromEdges.Add(connection.Attribute("from")!.Value);
        return signals;
    }

    private static HashSet<string> EdgesOfType(string network, string type)
        => [.. XDocument.Parse(network).Root!.Elements("edge")
               .Where(e => e.Attribute("type")?.Value == type).Select(e => e.Attribute("id")!.Value)];

    [RequiresNetconvertFact]
    public async Task WithoutMeteringTheMeterIsJoinedIntoASignalOverTheFreeway()
    {
        var result = await new OsmConverter(Options with { MeterRamps = false })
            .ConvertFileWithNetworkAsync(Fixture("ramp_meter.osm"));

        var freeway = EdgesOfType(result.Network, "highway.motorway");
        var joined = Assert.Single(Signals(result.Network));
        Assert.StartsWith("cluster_", joined.Key);
        Assert.Contains(joined.Value.FromEdges, freeway.Contains);
        Assert.Null(result.RampMeters);
    }

    [RequiresNetconvertFact]
    public async Task TheMeterControlsItsRampAloneOnTheMeteringCycle()
    {
        var result = await new OsmConverter(Options).ConvertFileWithNetworkAsync(Fixture("ramp_meter.osm"));

        var signals = Signals(result.Network);
        var freeway = EdgesOfType(result.Network, "highway.motorway");
        var ramp = EdgesOfType(result.Network, "highway.motorway_link");

        // The meter is its own junction, every link of it leaves the ramp, and nothing signals the
        // freeway: the merge is a priority merge.
        var (logic, fromEdges) = Assert.Single(signals).Value;
        Assert.Equal("202", logic.Attribute("id")?.Value);
        Assert.All(fromEdges, edge => Assert.Contains(edge, ramp));
        Assert.DoesNotContain(signals.Values, s => s.FromEdges.Overlaps(freeway));

        // Its programme is the metering cycle: two lanes released alternately, red and green only.
        Assert.Equal("static", logic.Attribute("type")?.Value);
        Assert.Equal(["2:sr", "1:rr", "2:rs", "1:rr"],
                     logic.Elements("phase").Select(p => $"{p.Attribute("duration")?.Value}:{p.Attribute("state")?.Value}"));

        var plan = Assert.IsType<RampMeterPlan>(result.RampMeters);
        Assert.Equal("202", Assert.Single(plan.Metered).Id);
        Assert.Empty(plan.NotMetered);
        Assert.Empty(RampMeterProgram.Verify(result.Network, plan));
    }

    [RequiresNetconvertFact]
    public async Task TheRecordedInvocationIsTheOneThatProducedTheNetwork()
    {
        var result = await new OsmConverter(Options).ConvertFileWithNetworkAsync(Fixture("ramp_meter.osm"));
        var argv = result.NetconvertArgv.ToList();

        Assert.Equal("202", argv[argv.IndexOf("--junctions.join-exclude") + 1]);
        Assert.Equal(OsmConverter.RecordedProgramsInput, argv[argv.IndexOf("--tllogic-files") + 1]);
        Assert.Equal(OsmConverter.RecordedNetworkOutput, argv[argv.IndexOf("--output-file") + 1]);
        Assert.Contains("<tlLogic id=\"202\"", result.RampMeters!.ProgramFile);
    }

    [RequiresNetconvertFact]
    public async Task AnExtractWithNoMeterIsConvertedAsItAlwaysWas()
    {
        var converter = new OsmConverter(Options);
        var result = await converter.ConvertFileWithNetworkAsync(Fixture("street_layout.osm"));

        Assert.Null(result.RampMeters);
        Assert.Equal(OsmConverter.RecordedArguments(
                         converter.BuildArguments(Fixture("street_layout.osm"), "x.xodr", "x.net.xml"),
                         "x.xodr", "x.net.xml"),
                     result.NetconvertArgv);
    }
}
