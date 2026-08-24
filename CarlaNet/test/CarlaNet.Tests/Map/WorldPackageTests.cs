// CarlaNet.Map.WorldPackage — the on-disk record of one generated world.
//
// Offline (no engine, no server): write a package to a temporary directory and read it back. The
// checks that matter are that the per-cell grids survive the float32 round trip exactly, and that a
// world reconciled by a constant shift writes no grid file at all -- its absence is what tells a
// reader "there is no per-cell field here", as distinct from one having gone missing.
using System;
using System.IO;
using System.Linq;
using CarlaNet.Map.WorldPackage;

namespace CarlaNet.Tests.Map;

public class WorldPackageTests : IDisposable
{
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

        WorldPackage.Write(_dir, manifest, Xodr, offset, ground);

        Assert.True(WorldPackage.TryReadGrids(_dir, "TestArea", out var readOffset, out var readGround));
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
        };
        var (offset, ground) = MakeGrids(4, 4);

        WorldPackage.Write(_dir, manifest, Xodr, offset, ground);
        var read = WorldPackage.ReadManifest(_dir, "TestArea");

        Assert.True(manifest.ValueEquals(read),
                    "manifest did not survive the JSON round trip unchanged");
    }

    [Fact]
    public void OpenDriveIsWrittenVerbatim()
    {
        var manifest = DrapedManifest(cols: 4, rows: 4);
        var (offset, ground) = MakeGrids(4, 4);

        WorldPackage.Write(_dir, manifest, Xodr, offset, ground);

        Assert.Equal(Xodr, File.ReadAllText(WorldPackage.OpenDrivePath(_dir, "TestArea")));
        Assert.Equal(WorldPackage.HashText(Xodr),
                     WorldPackage.HashFile(WorldPackage.OpenDrivePath(_dir, "TestArea")));
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

        WorldPackage.Write(_dir, manifest, Xodr, [], []);

        Assert.False(File.Exists(WorldPackage.GridPath(_dir, "TestArea")));
        Assert.False(WorldPackage.TryReadGrids(_dir, "TestArea", out var offset, out var ground));
        Assert.Empty(offset);
        Assert.Empty(ground);
        // The scalar shift is the whole reconciliation in this mode, so it must survive exactly.
        Assert.Equal(-1.0938002549446537, WorldPackage.ReadManifest(_dir, "TestArea").HeightAlignOffsetMeters);
    }

    [Fact]
    public void RewritingAfterADrapeRemovesTheStaleGrid()
    {
        var draped = DrapedManifest(cols: 6, rows: 5);
        var (offset, ground) = MakeGrids(6, 5);
        WorldPackage.Write(_dir, draped, Xodr, offset, ground);
        Assert.True(File.Exists(WorldPackage.GridPath(_dir, "TestArea")));

        // Rebuilding the same area with a constant shift must not leave the previous per-cell field
        // behind, or a reader would apply a grid that no longer describes the world.
        var constant = draped with { HeightAlignMode = "origin", DrapeActive = false, GridNumCols = 0, GridNumRows = 0 };
        WorldPackage.Write(_dir, constant, Xodr, [], []);

        Assert.False(File.Exists(WorldPackage.GridPath(_dir, "TestArea")));
    }

    [Fact]
    public void DegenerateDrapeGridIsRejected()
    {
        var manifest = DrapedManifest(cols: 1, rows: 1);
        Assert.Throws<ArgumentException>(() => WorldPackage.Write(_dir, manifest, Xodr, [0f], [0f]));
    }

    [Fact]
    public void ShortDrapeGridIsRejected()
    {
        var manifest = DrapedManifest(cols: 8, rows: 8);
        var (offset, ground) = MakeGrids(8, 8);
        Assert.Throws<ArgumentException>(
            () => WorldPackage.Write(_dir, manifest, Xodr, offset.Take(10).ToArray(), ground));
    }
}
