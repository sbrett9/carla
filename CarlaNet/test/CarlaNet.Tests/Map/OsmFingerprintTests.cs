// What identifies an OSM extract, and what the digest it replaced was actually recording.
//
// A world package names the extract its world was built from. That extract is not a file anybody
// downloaded: it is the clipped one the pipeline writes, and until the clipper was fixed it wrote
// its nodes in set-iteration order, so one extract clipped in four processes gave four files and one
// graph. A digest of those bytes named something that could not be produced again, and the shipped
// Arapahoe package proves it -- its recorded SourceOsmSha256 does not match the file it names.
//
// The first test below shows the byte digest failing on exactly that input and the canonical one
// succeeding; the rest establish that the canonical digest still sees everything that matters.
using System.Security.Cryptography;
using System.Text;
using CarlaNet.Map;

namespace CarlaNet.Tests.Map;

public class OsmFingerprintTests
{
    /// Two nodes and the way that joins them.
    private const string Bounds =
        "<bounds minlat=\"39.5\" minlon=\"-104.9\" maxlat=\"39.51\" maxlon=\"-104.89\"/>";

    private const string NodeOne =
        "<node id=\"1\" lat=\"39.5010000\" lon=\"-104.8990000\" version=\"3\"/>";

    private const string NodeTwo =
        "<node id=\"2\" lat=\"39.5020000\" lon=\"-104.8980000\" version=\"7\"/>";

    private const string Way =
        "<way id=\"900\" version=\"2\"><nd ref=\"1\"/><nd ref=\"2\"/>"
        + "<tag k=\"highway\" v=\"residential\"/><tag k=\"name\" v=\"West Street\"/></way>";

    private static string Osm(params string[] elements)
        => "<?xml version=\"1.0\"?><osm version=\"0.6\" generator=\"test\">"
           + string.Concat(elements) + "</osm>";

    private static string ByteDigest(string text)
        => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(text)));

    [Fact]
    public void ElementOrderDoesNotChangeTheFingerprint()
    {
        string written = Osm(Bounds, NodeOne, NodeTwo, Way);
        string writtenInAnotherOrder = Osm(Bounds, NodeTwo, NodeOne, Way);

        // The form this replaced, shown failing on the very thing the clip varied.
        Assert.NotEqual(ByteDigest(written), ByteDigest(writtenInAnotherOrder));

        Assert.Equal(OsmFingerprint.Compute(written),
                     OsmFingerprint.Compute(writtenInAnotherOrder));
    }

    [Fact]
    public void MovingANodeChangesTheFingerprint()
    {
        string moved = NodeOne.Replace("39.5010000", "39.5010001");

        Assert.NotEqual(OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, Way)),
                        OsmFingerprint.Compute(Osm(Bounds, moved, NodeTwo, Way)));
    }

    [Fact]
    public void ReversingAWayChangesTheFingerprint()
    {
        // A way is a sequence: reversing it reverses the road, and on a one-way street that is a
        // different map.
        string reversed = Way.Replace("<nd ref=\"1\"/><nd ref=\"2\"/>", "<nd ref=\"2\"/><nd ref=\"1\"/>");

        Assert.NotEqual(OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, Way)),
                        OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, reversed)));
    }

    [Fact]
    public void ChangingATagChangesTheFingerprint()
    {
        string retagged = Way.Replace("residential", "motorway");

        Assert.NotEqual(OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, Way)),
                        OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, retagged)));
    }

    [Fact]
    public void TagOrderDoesNotChangeTheFingerprint()
    {
        string reordered = Way.Replace(
            "<tag k=\"highway\" v=\"residential\"/><tag k=\"name\" v=\"West Street\"/>",
            "<tag k=\"name\" v=\"West Street\"/><tag k=\"highway\" v=\"residential\"/>");

        Assert.Equal(OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, Way)),
                     OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, reordered)));
    }

    [Fact]
    public void EditBookkeepingDoesNotChangeTheFingerprint()
    {
        // `version`, and its companions `timestamp`, `changeset`, `uid` and `user`, record who
        // touched the data and when. None of them can move a road, and two exports of identical
        // geometry differ in them.
        string reversioned = Osm(Bounds, NodeOne.Replace("version=\"3\"", "version=\"4\""),
                                 NodeTwo, Way);

        Assert.Equal(OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, Way)),
                     OsmFingerprint.Compute(reversioned));
    }

    [Fact]
    public void TheClipBoundsArePartOfTheIdentity()
    {
        // The same ways clipped to a different box are a different extract, even where the surviving
        // geometry happens to coincide.
        string wider = Bounds.Replace("maxlat=\"39.51\"", "maxlat=\"39.52\"");

        Assert.NotEqual(OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, Way)),
                        OsmFingerprint.Compute(Osm(wider, NodeOne, NodeTwo, Way)));
    }

    [Fact]
    public void RelationsArePartOfTheIdentity()
    {
        const string Restriction =
            "<relation id=\"5000\" version=\"1\"><member type=\"way\" ref=\"900\" role=\"from\"/>"
            + "<member type=\"node\" ref=\"2\" role=\"via\"/>"
            + "<tag k=\"type\" v=\"restriction\"/><tag k=\"restriction\" v=\"no_left_turn\"/></relation>";

        Assert.NotEqual(OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, Way)),
                        OsmFingerprint.Compute(Osm(Bounds, NodeOne, NodeTwo, Way, Restriction)));
    }

    [Fact]
    public void AnUnreadableFileGivesAnEmptyFingerprintRatherThanThrowing()
    {
        // Provenance is recorded after the world is already built. Losing the record is a lesser
        // harm than discarding a world that took minutes to make, and empty is the same signal a
        // package built before any of this carries.
        Assert.Equal(string.Empty,
                     OsmFingerprint.ComputeFile(Path.Combine(Path.GetTempPath(),
                                                             Guid.NewGuid().ToString("N") + ".osm")));
    }
}
