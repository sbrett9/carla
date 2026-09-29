// CarlaNet.Map.WorldPackage — the on-disk record of one generated world.
//
// Offline (no engine, no server): write a package to a temporary directory and read it back. The
// checks that matter are that the per-cell grids survive the float32 round trip exactly, that a
// world reconciled by a constant shift writes no grid file at all -- its absence is what tells a
// reader "there is no per-cell field here", as distinct from one having gone missing -- and that the
// SUMO network is carried, since it cannot be rebuilt once the build that made it has finished.
//
// The two provenance digests are checked against the forms they replaced, because both of them were
// recording something that could never be reproduced: a digest of the OpenDRIVE bytes covers
// netconvert's timestamped header, and a digest of the source extract's bytes covered a file whose
// byte order varied per clip.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CarlaNet.Map.WorldPackage;

namespace CarlaNet.Tests.Map;

public class WorldPackageTests : IDisposable
{
    /// The package these tests write and read back.
    private string Pkg => WorldPackage.PackagePath(_dir, "TestArea");

    private readonly string _dir;

    public WorldPackageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "carlanet-world-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string Xodr =
        "<?xml version=\"1.0\"?><OpenDRIVE><header><geoReference><![CDATA[+proj=tmerc +lat_0=38.9 "
        + "+lon_0=-119.7 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs]]></geoReference></header></OpenDRIVE>";

    /// A minimal SUMO network: enough structure for a fingerprint, small enough to read.
    private const string Net =
        "<net version=\"1.20\"><edge id=\"a\" from=\"n0\" to=\"n1\" priority=\"1\">"
        + "<lane id=\"a_0\" index=\"0\" speed=\"13.89\" length=\"100.00\" shape=\"0,0 100,0\"/>"
        + "</edge><junction id=\"n1\" type=\"priority\" x=\"100\" y=\"0\" incLanes=\"a_0\" intLanes=\"\"/>"
        + "<location netOffset=\"0.00,0.00\" convBoundary=\"0.00,0.00,100.00,0.00\" "
        + "origBoundary=\"0,0,1,1\" projParameter=\"+proj=tmerc\"/></net>";

    /// How netconvert writes an OpenDRIVE: a leading comment carrying the moment it ran and the
    /// whole configuration, including the temporary output path, then a header carrying the date
    /// again. Two runs of one conversion differ in exactly these places and nowhere else.
    private static string StampedXodr(string when, string temporaryOutput)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- generated on {when} by Eclipse SUMO "
           + "netconvert 1.27.0\n<netconvertConfiguration>\n<output>\n"
           + $"<opendrive-output value=\"{temporaryOutput}\"/>\n</output>\n"
           + "</netconvertConfiguration>\n-->\n"
           + $"<OpenDRIVE>\n  <header revMajor=\"1\" revMinor=\"4\" name=\"\" date=\"{when}\" "
           + "north=\"1\" south=\"0\">\n  </header>\n  <road id=\"1\" length=\"10\"/>\n</OpenDRIVE>";

    private static WorldPackageManifest DrapedManifest(int cols, int rows) => new()
    {
        MapName = "TestArea",
        OriginLatitude = 38.91108,
        OriginLongitude = -119.7645965,
        OriginHeightMeters = 1418.61,
        GeoReferenceString = "+proj=tmerc +lat_0=38.9 +lon_0=-119.7",
        HeightAlignMode = "drape",
        DrapeActive = true,
        HeightAlignOffsetMeters = 0.0,
        GridMinXMeters = -838.9,
        GridMinYMeters = -455.09,
        GridCellSizeMeters = 8.0,
        GridNumCols = cols,
        GridNumRows = rows,
        PhotorealIonAssetId = 2275207,
        GroundIonAssetId = 1,
        StagingMinXMeters = -838.9,
        StagingMinYMeters = -455.1,
        StagingMaxXMeters = 839.1,
        StagingMaxYMeters = 456.9,
        StagingMarginMeters = 30.48,
        SourceOsmFileName = "TestArea.osm",
    };

    private static (float[] Offset, float[] Ground) MakeGrids(int cols, int rows)
    {
        int n = cols * rows;
        var offset = new float[n];
        var ground = new float[n];
        for (int i = 0; i < n; i++)
        {
            // Values a lossy round trip would visibly disturb, rather than smooth ramps.
            offset[i] = (float)(-2.01 + (i % 37) * 0.013);
            ground[i] = (float)(1400.0 + (i % 91) * 0.7);
        }
        return (offset, ground);
    }

    [Fact]
    public void DrapedPackageRoundTripsGridsExactly()
    {
        var manifest = DrapedManifest(cols: 21, rows: 13);
        var (offset, ground) = MakeGrids(21, 13);

        WorldPackage.Write(_dir, manifest, Xodr, Net, offset, ground);

        Assert.True(WorldPackage.TryReadGrids(Pkg, out var readOffset, out var readGround));
        Assert.Equal(offset, readOffset);
        Assert.Equal(ground, readGround);
    }

    [Fact]
    public void ManifestRoundTripsEveryField()
    {
        var (offset, ground) = MakeGrids(4, 4);
        var manifest = DrapedManifest(cols: 4, rows: 4) with
        {
            // Written from the grids whatever the caller sets, so set to what they will be.
            BareEarthOffsetSha1 = WorldPackage.HashGrid(offset),
            BareEarthDtmSha1 = WorldPackage.HashGrid(ground),
            OpenDriveSha256 = WorldPackage.HashText(Xodr),
            SampleStepMeters = 10.0,
            TerrainResolutionMeters = 8.0,
            TerrainMarginMeters = 30.48,
            GeneratedAtUtc = "2026-08-23T12:00:00.0000000Z",
            GeneratorVersion = "1.2.3.4",
            NetconvertExtraArgs = ["--keep-edges.by-vclass", "passenger"],
            NetworkFingerprint = CarlaNet.Map.NetworkFingerprint.Compute(Net),
            NetconvertArgv = ["--osm-files", "TestArea.osm", "--output-file", "TestArea.net.xml"],
            NetconvertPath = @"C:\sumo\bin\netconvert.exe",
            NetconvertVersion = "Eclipse SUMO netconvert Version 1.27.0",
        };

        WorldPackage.Write(_dir, manifest, Xodr, Net, offset, ground);
        var read = WorldPackage.ReadManifest(Pkg);

        Assert.True(manifest.ValueEquals(read),
                    "manifest did not survive the JSON round trip unchanged");
    }

    [Fact]
    public void TheGridDigestIsSha1OverLittleEndianFloat32InTheOrderGiven()
    {
        // Pinned against an independent implementation, Python's hashlib over struct.pack('<3f', ...):
        // bytes 0000803f 000020c0 8553b144. The server digests the same bytes with the engine's
        // FSHA1, so a layout or byte-order slip on this side would show here first.
        Assert.Equal("95415533ef5ac8f2aae801a8e2192171b48cd946",
                     WorldPackage.HashGrid([1.0f, -2.5f, 1418.61f]));
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", WorldPackage.HashGrid([]));
    }

    [Fact]
    public void WritingRecordsTheDigestOfEachGridItWrites()
    {
        var (offset, ground) = MakeGrids(21, 13);

        // Whatever the caller's manifest says, the one written describes the grids written.
        var stale = DrapedManifest(cols: 21, rows: 13) with
        {
            BareEarthOffsetSha1 = "not a digest",
            BareEarthDtmSha1 = WorldPackage.HashGrid(offset),
        };
        WorldPackage.Write(_dir, stale, Xodr, Net, offset, ground);

        var read = WorldPackage.ReadManifest(Pkg);
        Assert.Equal(WorldPackage.HashGrid(offset), read.BareEarthOffsetSha1);
        Assert.Equal(WorldPackage.HashGrid(ground), read.BareEarthDtmSha1);
        Assert.NotEqual(read.BareEarthOffsetSha1, read.BareEarthDtmSha1);
    }

    [Fact]
    public void TheGridEntryHashesAsTheGridsItHolds()
    {
        var (offset, ground) = MakeGrids(21, 13);
        WorldPackage.Write(_dir, DrapedManifest(cols: 21, rows: 13), Xodr, Net, offset, ground);

        // Hashed from the entry's bytes without decoding a value, and equal to the digest of the
        // grids the entry decodes to.
        Assert.True(WorldPackage.TryReadGridDigests(Pkg, out string offsetSha1, out string groundSha1));
        Assert.Equal(WorldPackage.HashGrid(offset), offsetSha1);
        Assert.Equal(WorldPackage.HashGrid(ground), groundSha1);
    }

    [Fact]
    public void AConstantShiftRecordsNoDigestsAndCarriesNoneToRead()
    {
        var constant = DrapedManifest(cols: 4, rows: 4) with
        {
            HeightAlignMode = "area",
            DrapeActive = false,
            HeightAlignOffsetMeters = -1.0938002549446537,
            GridNumCols = 0,
            GridNumRows = 0,
            GridCellSizeMeters = 0.0,
            BareEarthOffsetSha1 = "left over from a draped build",
        };
        WorldPackage.Write(_dir, constant, Xodr, Net, [], []);

        var read = WorldPackage.ReadManifest(Pkg);
        Assert.Equal(string.Empty, read.BareEarthOffsetSha1);
        Assert.Equal(string.Empty, read.BareEarthDtmSha1);
        Assert.False(WorldPackage.TryReadGridDigests(Pkg, out string offsetSha1, out string groundSha1));
        Assert.Equal(string.Empty, offsetSha1);
        Assert.Equal(string.Empty, groundSha1);
    }

    [Fact]
    public void APackageWrittenBeforeTheDigestsReadsWithNone()
    {
        // A manifest as it was written before these fields existed: the fields are simply absent.
        WriteWorld();
        string json;
        using (var archive = ZipFile.OpenRead(Pkg))
        using (var reader = new StreamReader(archive.GetEntry("world.json")!.Open()))
        {
            json = reader.ReadToEnd();
        }
        string[] kept = json.Split('\n')
            .Where(line => !line.Contains("\"BareEarthOffsetSha1\"") && !line.Contains("\"BareEarthDtmSha1\""))
            .ToArray();
        Assert.Equal(json.Split('\n').Length - 2, kept.Length);
        Publish(("world.json", Utf8(string.Join('\n', kept))));

        var read = WorldPackage.ReadManifest(Pkg);
        Assert.Equal(string.Empty, read.BareEarthOffsetSha1);
        Assert.Equal(string.Empty, read.BareEarthDtmSha1);
    }

    [Fact]
    public void OpenDriveIsWrittenVerbatim()
    {
        var manifest = DrapedManifest(cols: 4, rows: 4);
        var (offset, ground) = MakeGrids(4, 4);

        WorldPackage.Write(_dir, manifest, Xodr, Net, offset, ground);

        Assert.Equal(Xodr, WorldPackage.ReadOpenDrive(Pkg));
        Assert.Equal(WorldPackage.HashText(Xodr),
                     WorldPackage.HashText(WorldPackage.ReadOpenDrive(Pkg)));
    }

    [Fact]
    public void ConstantShiftWritesNoGridFile()
    {
        var manifest = DrapedManifest(cols: 4, rows: 4) with
        {
            HeightAlignMode = "area",
            DrapeActive = false,
            HeightAlignOffsetMeters = -1.0938002549446537,
            GridNumCols = 0,
            GridNumRows = 0,
            GridCellSizeMeters = 0.0,
        };

        WorldPackage.Write(_dir, manifest, Xodr, Net, [], []);

        // A constant shift writes no grid entry at all, so its absence is what says "no drape".
        Assert.False(WorldPackage.TryReadGrids(Pkg, out var offset, out var ground));
        Assert.Empty(offset);
        Assert.Empty(ground);
        // The scalar shift is the whole reconciliation in this mode, so it must survive exactly.
        Assert.Equal(-1.0938002549446537, WorldPackage.ReadManifest(Pkg).HeightAlignOffsetMeters);
    }

    [Fact]
    public void RewritingAfterADrapeRemovesTheStaleGrid()
    {
        var draped = DrapedManifest(cols: 6, rows: 5);
        var (offset, ground) = MakeGrids(6, 5);
        WorldPackage.Write(_dir, draped, Xodr, Net, offset, ground);
        Assert.True(WorldPackage.TryReadGrids(Pkg, out _, out _));

        // Rebuilding the same area with a constant shift must not leave the previous per-cell field
        // behind, or a reader would apply a grid that no longer describes the world.
        var constant = draped with { HeightAlignMode = "origin", DrapeActive = false, GridNumCols = 0, GridNumRows = 0 };
        WorldPackage.Write(_dir, constant, Xodr, Net, [], []);

        Assert.False(WorldPackage.TryReadGrids(Pkg, out _, out _));
    }

    [Fact]
    public void TheSumoNetworkIsWrittenVerbatim()
    {
        var manifest = DrapedManifest(cols: 4, rows: 4);
        var (offset, ground) = MakeGrids(4, 4);

        WorldPackage.Write(_dir, manifest, Xodr, Net, offset, ground);

        Assert.True(WorldPackage.HasNetwork(Pkg));
        Assert.Equal(Net, WorldPackage.ReadNetwork(Pkg));
    }

    [Fact]
    public void APackageWithNoNetworkSaysSoRatherThanReturningNothing()
    {
        // A package written before the network was carried. Reading it must fail loudly: the network
        // cannot be regenerated, so there is no quiet fallback that would be correct.
        var manifest = DrapedManifest(cols: 4, rows: 4);
        var (offset, ground) = MakeGrids(4, 4);
        WorldPackage.Write(_dir, manifest, Xodr, string.Empty, offset, ground);

        Assert.False(WorldPackage.HasNetwork(Pkg));
        var failure = Assert.Throws<InvalidDataException>(() => WorldPackage.ReadNetwork(Pkg));
        Assert.Contains("cannot be regenerated", failure.Message);
    }

    [Fact]
    public void TheOpenDriveDigestReproducesAcrossTwoConversions()
    {
        // The same world converted twice: netconvert stamps a different moment and a different
        // temporary output path into each. Nothing about the road changed.
        string first = StampedXodr("2026-09-15T10:50:37.037018-08:00", @"C:\Temp\carlanet_a.xodr");
        string second = StampedXodr("2026-09-21T13:40:19.354678-08:00", @"C:\Temp\carlanet_b.xodr");

        // The form this replaced, shown failing: a digest over the whole document never reproduced,
        // so the recorded OpenDriveSha256 could not be checked against a rebuild.
        Assert.NotEqual(WorldPackage.HashText(first), WorldPackage.HashText(second));

        Assert.Equal(WorldPackage.HashOpenDrive(first), WorldPackage.HashOpenDrive(second));
    }

    [Fact]
    public void TheOpenDriveDigestStillSeesTheRoad()
    {
        string original = StampedXodr("2026-09-15T10:50:37.037018-08:00", @"C:\Temp\carlanet_a.xodr");
        string shortened = original.Replace("length=\"10\"", "length=\"11\"");

        Assert.NotEqual(WorldPackage.HashOpenDrive(original), WorldPackage.HashOpenDrive(shortened));
    }

    /// Publish entries into a written package as the world build does: stored, beside the world.
    private void Publish(params (string Name, byte[] Data)[] entries)
    {
        using var archive = ZipFile.Open(Pkg, ZipArchiveMode.Update);
        foreach (var (name, data) in entries)
        {
            archive.GetEntry(name)?.Delete();
            using Stream stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
            stream.Write(data);
        }
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private const string GeoJson =
        "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":"
        + "{\"type\":\"Point\",\"coordinates\":[-119.76,38.91]},\"properties\":{\"id\":\"kerb\","
        + "\"name\":\"Kerb\",\"radius_m\":10}}]}";

    private static string AreaTable(string sourceSha256)
        => "{\"resolved_version\": 1, \"source_sha256\": \"" + sourceSha256 + "\", \"areas\": []}";

    private void WriteWorld()
    {
        var (offset, ground) = MakeGrids(4, 4);
        WorldPackage.Write(_dir, DrapedManifest(cols: 4, rows: 4), Xodr, Net, offset, ground);
    }

    [Fact]
    public void APackageFromBeforeTheReferenceSetCarriesNone()
    {
        WriteWorld();

        Assert.False(WorldPackage.TryReadAreasOfInterest(Pkg, out _));
        Assert.False(WorldPackage.TryReadAreasOfInterestSource(Pkg, out _));
        Assert.False(WorldPackage.TryReadPlaceIndex(Pkg, out _));
        Assert.False(WorldPackage.TryReadSolarFrame(Pkg, out _));
    }

    [Fact]
    public void TheReferenceSetReadsBackAsPublished()
    {
        WriteWorld();
        byte[] source = Utf8(GeoJson);
        string table = AreaTable(Convert.ToHexStringLower(SHA256.HashData(source)));
        const string places = "{\"place_index_version\": 1, \"streets\": []}";
        const string solar = "{\"solar_frame_version\": 1, \"engine_time_zone\": \"+03:44:43\"}";
        Publish((WorldPackage.AreasOfInterestEntry, Utf8(table)),
                (WorldPackage.AreasOfInterestSourceEntry, source),
                (WorldPackage.PlaceIndexEntry, Utf8(places)),
                (WorldPackage.SolarFrameEntry, Utf8(solar)));

        Assert.True(WorldPackage.TryReadAreasOfInterest(Pkg, out string readTable));
        Assert.Equal(table, readTable);
        Assert.True(WorldPackage.TryReadAreasOfInterestSource(Pkg, out byte[] readSource));
        Assert.Equal(source, readSource);
        Assert.True(WorldPackage.TryReadPlaceIndex(Pkg, out string readPlaces));
        Assert.Equal(places, readPlaces);
        Assert.True(WorldPackage.TryReadSolarFrame(Pkg, out string readSolar));
        Assert.Equal(solar, readSolar);
        // The world itself still reads as it did.
        Assert.Equal(Net, WorldPackage.ReadNetwork(Pkg));
    }

    [Fact]
    public void AnEmptyAreaTableCarriesNoSource()
    {
        WriteWorld();
        Publish((WorldPackage.AreasOfInterestEntry, Utf8(AreaTable(string.Empty))));

        Assert.True(WorldPackage.TryReadAreasOfInterest(Pkg, out string table));
        Assert.Contains("\"areas\": []", table);
    }

    [Fact]
    public void AnAreaTableResolvedFromAnotherSourceIsRefused()
    {
        // C5 V5.11: the table records the digest of the GeoJSON it came from, and a different
        // GeoJSON beside it means the table describes areas this package does not declare.
        WriteWorld();
        byte[] other = Utf8(GeoJson.Replace("\"kerb\"", "\"gate\""));
        Publish((WorldPackage.AreasOfInterestEntry,
                 Utf8(AreaTable(Convert.ToHexStringLower(SHA256.HashData(Utf8(GeoJson)))))),
                (WorldPackage.AreasOfInterestSourceEntry, other));

        var refusal = Assert.Throws<InvalidDataException>(
            () => WorldPackage.TryReadAreasOfInterest(Pkg, out _));
        Assert.Contains("not the areas this package declares", refusal.Message);
    }

    [Fact]
    public void AnAreaTableWhoseSourceWentMissingIsRefused()
    {
        WriteWorld();
        Publish((WorldPackage.AreasOfInterestEntry,
                 Utf8(AreaTable(Convert.ToHexStringLower(SHA256.HashData(Utf8(GeoJson)))))));

        Assert.Throws<InvalidDataException>(() => WorldPackage.TryReadAreasOfInterest(Pkg, out _));
    }

    [Fact]
    public void WritingAWorldAgainDropsItsReferenceSet()
    {
        // A rebuilt world's network can differ; a place index derived from the old one must not
        // survive into the new package.
        WriteWorld();
        Publish((WorldPackage.PlaceIndexEntry, Utf8("{\"place_index_version\": 1}")));
        Assert.True(WorldPackage.TryReadPlaceIndex(Pkg, out _));

        WriteWorld();

        Assert.False(WorldPackage.TryReadPlaceIndex(Pkg, out _));
    }

    [Fact]
    public void DegenerateDrapeGridIsRejected()
    {
        var manifest = DrapedManifest(cols: 1, rows: 1);
        Assert.Throws<ArgumentException>(() => WorldPackage.Write(_dir, manifest, Xodr, Net, [0f], [0f]));
    }

    [Fact]
    public void ShortDrapeGridIsRejected()
    {
        var manifest = DrapedManifest(cols: 8, rows: 8);
        var (offset, ground) = MakeGrids(8, 8);
        Assert.Throws<ArgumentException>(
            () => WorldPackage.Write(_dir, manifest, Xodr, Net, offset.Take(10).ToArray(), ground));
    }
}
