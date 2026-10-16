// What the network fingerprint must see and must not see.
//
// The fixtures under Map/Fixtures/Network are real netconvert 1.27.0 output, small enough to read.
// `street_layout.osm` is two named residential ways meeting end to end at a plain geometry node,
// plus a signalised crossroads; that is the smallest input on which `--output.street-names` changes
// the graph, because it is the only thing stopping `--geometry.remove` merging the two ways into
// one edge. They were produced with the world build's flag set:
//
//   netconvert --osm-files street_layout.osm --proj "+proj=tmerc +lat_0=39.5 +lon_0=-104.9 +k=1
//              +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs" --default.lanewidth 3.35
//              --default.sidewalk-width 2.8 --tls.guess true --geometry.remove --roundabouts.guess
//              --osm.turn-lanes --offset.disable-normalization --junctions.join
//              --keep-edges.by-vclass passenger --keep-edges.components 1
//              --remove-edges.isolated true --output-file <one of>
//
// with nothing further for street_names_omitted, `--output.street-names true` for
// street_names_true, `--output.street-names false` for street_names_false, and
// `--output.street-names true --tls.default-type actuated` for signals_actuated.
//
// clip_node_order_a and clip_node_order_b come from two clips of one OSM extract made before
// OsmClipper emitted its nodes in a fixed order, so the two extracts held the same graph in a
// different byte order. Their networks differ in bytes and must not differ here.
using System.Text;
using System.Xml.Linq;
using CarlaNet.Map;

namespace CarlaNet.Tests.Map;

public class NetworkFingerprintTests
{
    private static string FixtureDirectory
        => Path.Combine(AppContext.BaseDirectory, "Map", "Fixtures", "Network");

    private static string Text(string name)
        => File.ReadAllText(Path.Combine(FixtureDirectory, name));

    private static string Fingerprint(string name)
        => NetworkFingerprint.Compute(Text(name));

    // The canonical encoding is shared with carlacontrol.NetworkFingerprint, which recomputes it on
    // the scenario side. These are the values both implementations must produce; the Python test
    // CarlaControl/test/test_network_fingerprint.py asserts the same constants over the same files,
    // so a change to either encoding fails on one side or the other rather than silently making a
    // world package unopenable.
    private const string OmittedDigest = "b65c5ca3320b753c76145ddd8b1389708388c4d5d5ab40fdb2381c6a7ab19485";
    private const string StreetNamesDigest = "44194b18d22c91a25e85babe0469d43d064f3fb9aabe15fa62a486cd8fb4e2e0";
    private const string ActuatedDigest = "f3906afa5e66210ad4f8660cffdd1041f284a641110da70931a3f9a2bcd896b0";
    private const string ClipOrderDigest = "f0e00487547612acdd41daedcacb04f6e9aa0e6b71a5610085062f3d2a3a91c4";

    [Fact]
    public void SettingStreetNamesChangesTheGraph()
    {
        // 36 edges without the option, 40 with it: NBEdge::expandableBy will not merge two edges
        // carrying different street names, so asking for names changes what the network contains.
        Assert.NotEqual(Fingerprint("street_names_omitted.net.xml"),
                        Fingerprint("street_names_true.net.xml"));
    }

    [Fact]
    public void StreetNamesSetToFalseIsTheSameGraphAsTrue()
    {
        // NBEdge::expandableBy guards on `!isDefault("output.street-names")` -- whether the option
        // was given at all, not what it was set to. Passing `false` is therefore indistinguishable
        // from passing `true`, and the only way to get the merged graph back is to omit the option.
        Assert.Equal(Fingerprint("street_names_true.net.xml"),
                     Fingerprint("street_names_false.net.xml"));
        Assert.NotEqual(Fingerprint("street_names_omitted.net.xml"),
                        Fingerprint("street_names_false.net.xml"));
    }

    [Fact]
    public void SignalProgrammeTypeIsPartOfTheIdentity()
    {
        // The two fixtures differ only in --tls.default-type. Nothing about the roads changes; the
        // junction's <tlLogic> does, and a scenario authored against one programme and run against
        // the other is a real mismatch.
        Assert.NotEqual(Fingerprint("street_names_true.net.xml"),
                        Fingerprint("signals_actuated.net.xml"));
    }

    [Fact]
    public void TwoClipOrdersOfOneExtractGiveOneFingerprint()
    {
        Assert.NotEqual(Text("clip_node_order_a.net.xml"), Text("clip_node_order_b.net.xml"));
        Assert.Equal(Fingerprint("clip_node_order_a.net.xml"),
                     Fingerprint("clip_node_order_b.net.xml"));
    }

    [Fact]
    public void ReorderingTheDocumentDoesNotChangeTheFingerprint()
    {
        string original = Text("street_names_true.net.xml");
        XDocument document = XDocument.Parse(original);
        XElement root = document.Root!;
        var reordered = root.Elements().Reverse().ToList();
        foreach (XElement element in reordered)
        {
            element.Remove();
        }
        foreach (XElement element in reordered)
        {
            root.Add(element);
        }
        string shuffled = document.ToString();

        Assert.NotEqual(original, shuffled);
        Assert.Equal(NetworkFingerprint.Compute(original), NetworkFingerprint.Compute(shuffled));
    }

    [Fact]
    public void TheGenerationCommentIsNotPartOfTheIdentity()
    {
        // netconvert's leading comment carries the moment it ran and the whole configuration,
        // including the temporary paths the world build hands it. A byte digest of a network is
        // therefore never reproducible; this is why a fingerprint exists at all.
        string original = Text("street_names_true.net.xml");
        int start = original.IndexOf("<!--", StringComparison.Ordinal);
        int end = original.IndexOf("-->", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the fixture should carry netconvert's own comment");
        string withoutComment = original.Remove(start, end + 3 - start);

        Assert.NotEqual(original, withoutComment);
        Assert.Equal(NetworkFingerprint.Compute(original),
                     NetworkFingerprint.Compute(withoutComment));
    }

    [Fact]
    public void StreetNameValuesAreNotPartOfTheIdentity()
    {
        // The option changes the graph; the names it writes are annotation. Renaming a street must
        // not invalidate a world package.
        XDocument document = XDocument.Parse(Text("street_names_true.net.xml"));
        var named = document.Root!.Elements("edge")
            .Where(e => e.Attribute("name") is not null).ToList();
        Assert.NotEmpty(named);
        foreach (XElement edge in named)
        {
            edge.SetAttributeValue("name", "Renamed Avenue");
        }

        Assert.Equal(Fingerprint("street_names_true.net.xml"),
                     NetworkFingerprint.Compute(document.ToString()));
    }

    [Fact]
    public void RemovingAnInternalEdgeChangesTheFingerprint()
    {
        // The lanes inside a junction are where a turn restriction that was honoured and one that
        // was dropped differ. Excluding internal edges would make the clip's handling of turn
        // restrictions invisible to this check.
        XDocument document = XDocument.Parse(Text("street_names_true.net.xml"));
        XElement internalEdge = document.Root!.Elements("edge")
            .First(e => e.Attribute("function")?.Value == "internal");
        internalEdge.Remove();

        Assert.NotEqual(Fingerprint("street_names_true.net.xml"),
                        NetworkFingerprint.Compute(document.ToString()));
    }

    [Fact]
    public void RemovingAConnectionChangesTheFingerprint()
    {
        XDocument document = XDocument.Parse(Text("street_names_true.net.xml"));
        document.Root!.Elements("connection").First().Remove();

        Assert.NotEqual(Fingerprint("street_names_true.net.xml"),
                        NetworkFingerprint.Compute(document.ToString()));
    }

    [Fact]
    public void AMissingAttributeIsNotAnEmptyOne()
    {
        // Encoding an absent attribute as "" would let a network that lost an attribute fingerprint
        // as one that carries it empty.
        const string WithWidth =
            "<net><edge id=\"a\"><lane id=\"a_0\" width=\"\"/></edge><location netOffset=\"0,0\" "
            + "convBoundary=\"0,0,1,1\" origBoundary=\"0,0,1,1\" projParameter=\"!\"/></net>";
        const string WithoutWidth =
            "<net><edge id=\"a\"><lane id=\"a_0\"/></edge><location netOffset=\"0,0\" "
            + "convBoundary=\"0,0,1,1\" origBoundary=\"0,0,1,1\" projParameter=\"!\"/></net>";

        Assert.NotEqual(NetworkFingerprint.Compute(WithWidth),
                        NetworkFingerprint.Compute(WithoutWidth));
    }

    [Fact]
    public void TheProjectionIsPartOfTheIdentity()
    {
        XDocument document = XDocument.Parse(Text("street_names_true.net.xml"));
        XElement location = document.Root!.Element("location")!;
        location.SetAttributeValue("projParameter",
            location.Attribute("projParameter")!.Value.Replace("lat_0=39.5", "lat_0=39.6"));

        Assert.NotEqual(Fingerprint("street_names_true.net.xml"),
                        NetworkFingerprint.Compute(document.ToString()));
    }

    [Fact]
    public void TheEncodingMatchesTheOneTheScenarioSideRecomputes()
    {
        Assert.Equal(OmittedDigest, Fingerprint("street_names_omitted.net.xml"));
        Assert.Equal(StreetNamesDigest, Fingerprint("street_names_true.net.xml"));
        Assert.Equal(StreetNamesDigest, Fingerprint("street_names_false.net.xml"));
        Assert.Equal(ActuatedDigest, Fingerprint("signals_actuated.net.xml"));
        Assert.Equal(ClipOrderDigest, Fingerprint("clip_node_order_a.net.xml"));
        Assert.Equal(ClipOrderDigest, Fingerprint("clip_node_order_b.net.xml"));
    }

    [Fact]
    public void AnEmptyDocumentIsRejected()
    {
        Assert.ThrowsAny<Exception>(() => NetworkFingerprint.Compute(string.Empty));
    }

    [Fact]
    public void FingerprintsAreLowercaseHexSha256()
    {
        string fingerprint = Fingerprint("street_names_true.net.xml");
        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.Equal(Encoding.UTF8.GetByteCount(fingerprint), fingerprint.Length);
    }
}
