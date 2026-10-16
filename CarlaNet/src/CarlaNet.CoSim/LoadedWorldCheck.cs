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
/// manifest's <c>Grid*</c> fields -- and the same two grids, by digest: the SHA-1 the server computed
/// of each of its record's grids when the record was set (<c>get_bare_earth_digest</c>) against
/// <see cref="WorldPackage.HashGrid"/> of the package's, hashed from its <c>bareearth.bin</c>. The
/// session seats each vehicle on the package's grids; the world's collision surface and its telemetry
/// use the record's.</item>
/// <item><b>The package agrees with itself</b>: where its manifest records the digests of its grids
/// (<c>BareEarthOffsetSha1</c>, <c>BareEarthDtmSha1</c>), they are what its <c>bareearth.bin</c>
/// hashes to. A package written before they were recorded has none, and nothing else depends on
/// them.</item>
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
/// and the package's grids are written from the same arrays in the same build. Both digests are over
/// the same bytes -- each grid's float32 values, little-endian, row-major, exactly as
/// <c>bareearth.bin</c> holds them -- so a digest that differs is a grid that differs, in one bit of
/// one cell or in all of them. Digests rather than the grids themselves because the grids are the
/// size of the drape: 7,611,381 floats each on the Bahonar world, whose two fetches took 146 s and
/// 153 s. SHA-1 because the server computes it with the engine's own Core, which has no SHA-256 that
/// runs on Windows or Linux; it tells one build's grids from another's, and an adversary is outside
/// what this check can see anyway (below). A digest cannot say which cells differ, so a refusal
/// names the two digests and not a cell. Counts are compared exactly. Positions, heights and angles
/// are compared within <see cref="LengthMarginMetres"/> and <see cref="AngleMarginDegrees"/>. That
/// margin is not a measurement: every such value is either the one the building client sent the
/// server or one it read back from it, so an unchanged world gives the identical double; the margin
/// is there so a double that has passed through a restored level's settings asset, a path this check
/// has not been run on, is not refused over its last bit, and it moves no vehicle anywhere a pixel
/// can show. The server's leg of the OpenDRIVE comparison -- the text it received, written to a file
/// or held by a level, and served back -- has not been measured against a running server either, and
/// nor has its leg of the grid comparison, the digests it computes as the record is set.</para>
///
/// <para><b>What it cannot see.</b></para>
/// <list type="bullet">
/// <item><b>The SUMO network itself.</b> The <c>.net.xml</c> never reaches the server, so the loaded
/// world is tied to it only through the package: the loaded OpenDRIVE is the package's, and the
/// package's network came from the same netconvert run by the way the package is written. A package
/// whose <c>map.net.xml</c> was replaced after it was written passes here; the session's
/// <see cref="ScenarioNetworkCheck"/> refuses it where the package records the fingerprint of the
/// network it was written with. Where it records none, only the session's frame check sees the
/// replacement, and only where the projection, offset or extent differ.</item>
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
    /// under a drape the grid it was reconciled on and the digest of each of its grids.
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

        if (!WorldPackage.TryReadGridDigests(packagePath, out string offset, out string ground))
        {
            found.Add("the package declares a draped surface and carries no grid of it");
            return;
        }

        CompareRecorded("bare-earth ground", manifest.BareEarthDtmSha1, ground, found);
        CompareRecorded("surface offset", manifest.BareEarthOffsetSha1, offset, found);

        if (string.IsNullOrEmpty(record.GroundGridSha1) || string.IsNullOrEmpty(record.OffsetGridSha1))
        {
            found.Add("the loaded world's server publishes no digest of its bare-earth grids "
                      + "(get_bare_earth_digest), so they cannot be compared with the package's: a "
                      + "server built before it published them answers with none");
            return;
        }

        CompareDigests("bare-earth ground", record.GroundGridSha1, ground, found);
        CompareDigests("surface offset", record.OffsetGridSha1, offset, found);
    }

    /// <summary>One grid of the loaded world against the package's, by digest, naming both.</summary>
    private static void CompareDigests(string what, string loaded, string packaged, List<string> found)
    {
        if (!string.Equals(loaded, packaged, StringComparison.OrdinalIgnoreCase))
        {
            found.Add($"the loaded world's {what} grid has SHA-1 {loaded} and the package's has "
                      + $"SHA-1 {packaged}");
        }
    }

    /// <summary>
    /// The digest a package's manifest records for one of its grids, where it records one, against
    /// what the grid it carries hashes to.
    /// </summary>
    private static void CompareRecorded(string what, string recorded, string carried, List<string> found)
    {
        if (!string.IsNullOrEmpty(recorded)
            && !string.Equals(recorded, carried, StringComparison.OrdinalIgnoreCase))
        {
            found.Add($"the package's manifest records its {what} grid as SHA-1 {recorded} and its "
                      + $"bareearth.bin holds one with SHA-1 {carried}");
        }
    }

    private static string Origin(double latitude, double longitude, double height) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.#########}, {longitude:0.#########} at {height:0.###} m");

    private static string Grid(int columns, int rows, double cell, double minX, double minY) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{columns}x{rows} cells of {cell:0.###} m from ({minX:0.###}, {minY:0.###})");
}
