using System.Globalization;
using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim;

/// <summary>
/// Establishes whether a world package describes the world a CARLA server has loaded, from what the
/// server publishes about that world, and refuses a session where it does not.
/// </summary>
/// <remarks>
/// <para><b>Why it is needed.</b> The session converts every SUMO position through the package's
/// georeference and seats every vehicle on the package's ground surface. When the process that
/// builds and views a world is not the one that drives it, nothing ties the package the driver was
/// handed to the world the server holds, and a package from another build puts every vehicle where
/// that build's roads and ground are: inside the sandbox, on plausible-looking pavement, with every
/// truth box in the wrong place.</para>
///
/// <para><b>What is compared, and against what.</b> Every value is the package's own against the
/// server's own, and every one of them exists on both sides:</para>
/// <list type="bullet">
/// <item><b>The bare-earth reference record exists.</b> A generated world publishes it when it is
/// built (<c>CarlaClient.SetBareEarthReferenceAsync</c>) and again when its level loads
/// (<c>GeoreferencedWorldInitializer</c>); a stock map has none. A world with no record has nothing
/// to be checked against, and is refused for that.</item>
/// <item><b>The record describes the package's surface</b>: draped or shifted by a constant as the
/// manifest says; under a drape, the same grid -- corner, cell size, columns and rows against the
/// manifest's <c>Grid*</c> fields -- and the same two grids, cell for cell, against the package's
/// <c>bareearth.bin</c>. The session seats each vehicle on the package's grids; the world's
/// collision surface and its telemetry use the record's.</item>
/// <item><b>The georeference origin</b> (<c>get_cesium_origin</c>) is the manifest's: the point
/// every SUMO position is converted relative to.</item>
/// <item><b>The road network the server serves</b> (<c>get_map_data</c>) is the one the package
/// carries, compared by <see cref="WorldPackage.HashOpenDrive"/> of each -- the manifest's
/// <c>OpenDriveSha256</c> is that same digest of <c>map.xodr</c>, measured equal on all three
/// packages in <c>Build/world-packages</c>. This is the comparison that tells two builds of one area
/// apart when their origin and grid coincide.</item>
/// </list>
///
/// <para><b>How exact.</b> The grids are compared bit for bit: a second client's copy of the record
/// is byte-identical to the building client's (<c>CarlaNet/python/test_bare_earth_reference.py</c>),
/// and the package's grids are written from the same arrays in the same build. Counts are compared
/// exactly. Positions, heights and angles are compared within <see cref="LengthMarginMetres"/> and
/// <see cref="AngleMarginDegrees"/>. That margin is not a measurement: every such value is either the
/// one the building client sent the server or one it read back from it, so an unchanged world gives
/// the identical double; the margin is there so a double that has passed through a restored level's
/// settings asset, a path this check has not been run on, is not refused over its last bit, and it
/// moves no vehicle anywhere a pixel can show. The server's leg of the OpenDRIVE comparison -- the
/// text it received, written to a file or held by a level, and served back -- has not been measured
/// against a running server either.</para>
///
/// <para><b>What it cannot see.</b></para>
/// <list type="bullet">
/// <item><b>The SUMO network itself.</b> The <c>.net.xml</c> never reaches the server, so the loaded
/// world is tied to it only through the package: the loaded OpenDRIVE is the package's, and the
/// package's network came from the same netconvert run by the way the package is written. A package
/// whose <c>map.net.xml</c> was replaced after it was written passes here; the network's own checks
/// in the session, and the lane-geometry residual, are what see that.</item>
/// <item><b>The imagery.</b> The server publishes nothing about which tilesets it streams, so a
/// world rebuilt over other imagery with the same network, origin and surface passes.</item>
/// <item><b>Who published the record.</b> The server accepts a record from any client and does not
/// tie it to the roads it loaded; the record is taken as the world's statement about itself.</item>
/// <item><b>A world loaded after the check.</b> It is taken once, when the session starts.</item>
/// </list>
/// </remarks>
public static class LoadedWorldCheck
{
    /// <summary>How far a position or height may sit from the package's and still be its own.</summary>
    public const double LengthMarginMetres = 1e-3;

    /// <summary>Likewise for the origin's latitude and longitude: about a tenth of a millimetre.</summary>
    public const double AngleMarginDegrees = 1e-9;

    /// <summary>
    /// Refuse a package that does not describe the loaded world, naming everything that differs.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">Anything in <see cref="Disagreements"/>.</exception>
    public static void Require(string packagePath, LoadedWorld loaded)
    {
        IReadOnlyList<string> disagreements = Disagreements(packagePath, loaded);
        if (disagreements.Count == 0)
        {
            return;
        }

        throw new CoSimSessionRefusedException(
            $"The world package {packagePath} does not describe the world the server has loaded: "
            + string.Join("; ", disagreements) + ". A session driven from a package of another "
            + "build seats every vehicle on that build's roads and ground, inside the sandbox and "
            + "entirely ordinary to look at. Load the world the package was written from, or give "
            + "the session the package the loaded world was written as.");
    }

    /// <summary>
    /// Every way a package disagrees with the loaded world, each described with both values; empty
    /// where the package describes it.
    /// </summary>
    public static IReadOnlyList<string> Disagreements(string packagePath, LoadedWorld loaded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(loaded);

        WorldPackageManifest manifest = WorldPackage.ReadManifest(packagePath);
        List<string> found = [];

        if (loaded.BareEarth is not { } record)
        {
            found.Add("the loaded world carries no bare-earth reference record, which a generated "
                      + "world publishes when it is built and again when its level loads, and a "
                      + "stock map never has");
        }
        else
        {
            CompareSurface(packagePath, manifest, record, found);
        }

        if (Math.Abs(loaded.OriginLatitude - manifest.OriginLatitude) > AngleMarginDegrees
            || Math.Abs(loaded.OriginLongitude - manifest.OriginLongitude) > AngleMarginDegrees
            || Math.Abs(loaded.OriginHeightMetres - manifest.OriginHeightMeters) > LengthMarginMetres)
        {
            found.Add($"the loaded world's origin is {Origin(loaded.OriginLatitude, loaded.OriginLongitude, loaded.OriginHeightMetres)} "
                      + $"and the package's is {Origin(manifest.OriginLatitude, manifest.OriginLongitude, manifest.OriginHeightMeters)}");
        }

        string packaged = WorldPackage.HashOpenDrive(WorldPackage.ReadOpenDrive(packagePath));
        if (string.IsNullOrEmpty(loaded.OpenDrive))
        {
            found.Add("the loaded world serves no OpenDRIVE");
        }
        else if (WorldPackage.HashOpenDrive(loaded.OpenDrive) is var served && served != packaged)
        {
            found.Add($"the loaded world serves an OpenDRIVE with digest {served[..12]} and the "
                      + $"package carries one with digest {packaged[..12]}");
        }

        return found;
    }

    /// <summary>
    /// The record against the manifest and the package's grids: how the surface was reconciled, and
    /// under a drape the grid it was reconciled on and every cell of it.
    /// </summary>
    private static void CompareSurface(string packagePath,
                                       WorldPackageManifest manifest,
                                       BareEarthRecord record,
                                       List<string> found)
    {
        if (record.DrapeActive != manifest.DrapeActive)
        {
            found.Add(record.DrapeActive
                ? "the loaded world's surface was draped cell by cell and the package's was "
                  + $"shifted by a constant {manifest.HeightAlignOffsetMeters:0.###} m"
                : $"the loaded world's surface was shifted by a constant {record.OffsetMetres:0.###} m "
                  + "and the package's was draped cell by cell");
            return;
        }

        if (!record.DrapeActive)
        {
            if (Math.Abs(record.OffsetMetres - manifest.HeightAlignOffsetMeters) > LengthMarginMetres)
            {
                found.Add($"the loaded world's surface was shifted by {record.OffsetMetres:0.###} m "
                          + $"and the package's by {manifest.HeightAlignOffsetMeters:0.###} m");
            }

            return;
        }

        if (record.Columns != manifest.GridNumCols || record.Rows != manifest.GridNumRows
            || Math.Abs(record.CellSizeMetres - manifest.GridCellSizeMeters) > LengthMarginMetres
            || Math.Abs(record.MinXMetres - manifest.GridMinXMeters) > LengthMarginMetres
            || Math.Abs(record.MinYMetres - manifest.GridMinYMeters) > LengthMarginMetres)
        {
            found.Add($"the loaded world's surface grid is {Grid(record.Columns, record.Rows, record.CellSizeMetres, record.MinXMetres, record.MinYMetres)} "
                      + $"and the package's is {Grid(manifest.GridNumCols, manifest.GridNumRows, manifest.GridCellSizeMeters, manifest.GridMinXMeters, manifest.GridMinYMeters)}");
            return;
        }

        if (!WorldPackage.TryReadGrids(packagePath, out float[] offset, out float[] ground))
        {
            found.Add("the package declares a draped surface and carries no grid of it");
            return;
        }

        CompareCells("bare-earth ground", record.GroundGrid, ground, record.Columns, found);
        CompareCells("surface offset", record.OffsetGrid, offset, record.Columns, found);
    }

    /// <summary>One grid against the other, bit for bit, naming how many cells differ and the first.</summary>
    private static void CompareCells(string what, float[] loaded, float[] packaged, int columns,
                                     List<string> found)
    {
        if (loaded.Length != packaged.Length)
        {
            found.Add($"the loaded world's {what} grid holds {loaded.Length} cells and the package's "
                      + $"{packaged.Length}");
            return;
        }

        int differing = 0;
        int first = -1;
        for (int cell = 0; cell < loaded.Length; cell++)
        {
            if (BitConverter.SingleToInt32Bits(loaded[cell]) != BitConverter.SingleToInt32Bits(packaged[cell]))
            {
                differing++;
                if (first < 0)
                {
                    first = cell;
                }
            }
        }

        if (differing > 0)
        {
            found.Add($"{differing} of {loaded.Length} cells of the loaded world's {what} grid differ "
                      + $"from the package's, the first at column {first % columns}, row "
                      + $"{first / columns}: {loaded[first].ToString("0.###", CultureInfo.InvariantCulture)} m "
                      + $"against {packaged[first].ToString("0.###", CultureInfo.InvariantCulture)} m");
        }
    }

    private static string Origin(double latitude, double longitude, double height) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.#########}, {longitude:0.#########} at {height:0.###} m");

    private static string Grid(int columns, int rows, double cell, double minX, double minY) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{columns}x{rows} cells of {cell:0.###} m from ({minX:0.###}, {minY:0.###})");
}
