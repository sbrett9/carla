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
using System.Linq;
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
        var manifest = DrapedManifest(cols: 4, rows: 4) with
        {
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
        var (offset, ground) = MakeGrids(4, 4);

        WorldPackage.Write(_dir, manifest, Xodr, Net, offset, ground);
        var read = WorldPackage.ReadManifest(Pkg);

        Assert.True(manifest.ValueEquals(read),
                    "manifest did not survive the JSON round trip unchanged");
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
