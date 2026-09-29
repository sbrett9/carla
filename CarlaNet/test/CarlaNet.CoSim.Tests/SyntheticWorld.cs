using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A world package written to a temporary directory, carrying a ground surface of the caller's
/// choosing and whichever SUMO network the test is about to drive on.
/// </summary>
/// <remarks>
/// A generated world's smallest package is sixty megabytes of measured terrain, which is neither
/// committable nor a surface whose height at a point is knowable in advance. A test of the seating
/// arithmetic needs a surface it can predict, so it makes one -- through the real package writer and
/// the real reader, so the file layout and the frame the grid is indexed in are the shipped ones.
/// </remarks>
internal sealed class SyntheticWorld : IDisposable
{
    private const double CellSize = 2.0;
    private const int Columns = 101;
    private const double MinX = -100.0;
    private const double OriginHeight = 1000.0;

    private SyntheticWorld(string directory, string packagePath)
    {
        Directory = directory;
        PackagePath = packagePath;
    }

    /// <summary>Where the package was written.</summary>
    public string Directory { get; }

    /// <summary>The package itself.</summary>
    public string PackagePath { get; }

    /// <summary>A surface on its own, for a test that needs no network.</summary>
    /// <param name="heightAbove">
    /// Surface height above the georeference origin at a CARLA-frame cell centre, in metres.
    /// </param>
    public static GroundSurface Build(Func<(double X, double Y), double> heightAbove)
    {
        using SyntheticWorld world = Write(heightAbove, string.Empty, string.Empty);
        return GroundSurface.FromWorldPackage(world.PackagePath);
    }

    /// <summary>
    /// A package a session can be started against: a predictable surface, and a network to drive on.
    /// </summary>
    /// <param name="heightAbove">Surface height above the origin at a CARLA-frame cell centre.</param>
    /// <param name="networkPath">The <c>.net.xml</c> to carry, or an empty string for none.</param>
    /// <param name="geoReference">
    /// The projection the manifest declares, which a session requires the network's own to equal.
    /// </param>
    /// <param name="netconvertVersion">
    /// The converter the manifest records, as a world build writes it -- what netconvert printed,
    /// <c>Eclipse SUMO netconvert 1.27.0</c> -- or empty for a package that records none.
    /// </param>
    /// <param name="recordedNetworkFingerprint">
    /// The network fingerprint the manifest records, as a world build writes it from the network it
    /// converted, or empty for a package that records none.
    /// </param>
    public static SyntheticWorld Write(Func<(double X, double Y), double> heightAbove,
                                       string networkPath,
                                       string geoReference,
                                       string netconvertVersion = "",
                                       string recordedNetworkFingerprint = "") =>
        Write(heightAbove, networkPath, geoReference, CellSize, MinX, Columns, netconvertVersion,
              recordedNetworkFingerprint);

    /// <summary>
    /// A package whose ground surface is a square grid of the caller's choosing, for a test that
    /// needs part of the network to have no ground under it.
    /// </summary>
    /// <param name="heightAbove">Surface height above the origin at a CARLA-frame cell centre.</param>
    /// <param name="networkPath">The <c>.net.xml</c> to carry, or an empty string for none.</param>
    /// <param name="geoReference">The projection the manifest declares.</param>
    /// <param name="cellSize">Grid spacing, metres.</param>
    /// <param name="min">The grid's lowest x and lowest y in the CARLA frame, metres.</param>
    /// <param name="cells">Cells along each side.</param>
    /// <param name="netconvertVersion">The converter the manifest records, or empty for none.</param>
    /// <param name="recordedNetworkFingerprint">
    /// The network fingerprint the manifest records, or empty for none.
    /// </param>
    /// <remarks>
    /// A session accepts a network that overhangs the grid by up to one cell, so a coarse grid a
    /// little smaller than the network is a world the session starts on and in which a vehicle near
    /// the network's edge stands on no ground.
    /// </remarks>
    public static SyntheticWorld Write(Func<(double X, double Y), double> heightAbove,
                                       string networkPath,
                                       string geoReference,
                                       double cellSize,
                                       double min,
                                       int cells,
                                       string netconvertVersion = "",
                                       string recordedNetworkFingerprint = "")
    {
        string directory = Path.Combine(Path.GetTempPath(),
                                        "carlanet-cosim-" + Guid.NewGuid().ToString("n"));
        var manifest = new WorldPackageManifest
        {
            MapName = "SyntheticSurface",
            OriginLatitude = 0.0,
            OriginLongitude = 0.0,
            OriginHeightMeters = OriginHeight,
            GeoReferenceString = geoReference,
            HeightAlignMode = "drape",
            DrapeActive = true,
            HeightAlignOffsetMeters = 0.0,
            GridMinXMeters = min,
            GridMinYMeters = min,
            GridCellSizeMeters = cellSize,
            GridNumCols = cells,
            GridNumRows = cells,
            NetconvertVersion = netconvertVersion,
            NetworkFingerprint = recordedNetworkFingerprint,
        };

        float[] offset = new float[cells * cells];
        float[] bareEarth = new float[cells * cells];
        for (int row = 0; row < cells; row++)
        {
            for (int column = 0; column < cells; column++)
            {
                double x = min + (column * cellSize);
                double y = min + (row * cellSize);
                bareEarth[(row * cells) + column] = (float)(OriginHeight + heightAbove((x, y)));
            }
        }

        string network = networkPath.Length > 0 ? File.ReadAllText(networkPath) : string.Empty;
        WorldPackage.Write(directory, manifest, "<OpenDRIVE/>", network, offset, bareEarth);
        return new SyntheticWorld(directory,
                                  WorldPackage.PackagePath(directory, manifest.MapName));
    }

    /// <summary>
    /// A package whose surface was shifted by one constant rather than draped: no grid entry, and a
    /// manifest that says so. It carries no network, so it is for the checks that need none.
    /// </summary>
    /// <param name="offsetMetres">The constant surface shift the manifest records.</param>
    public static SyntheticWorld WriteShiftedByAConstant(double offsetMetres)
    {
        string directory = Path.Combine(Path.GetTempPath(),
                                        "carlanet-cosim-" + Guid.NewGuid().ToString("n"));
        var manifest = new WorldPackageManifest
        {
            MapName = "SyntheticConstant",
            OriginLatitude = 0.0,
            OriginLongitude = 0.0,
            OriginHeightMeters = OriginHeight,
            HeightAlignMode = "area",
            DrapeActive = false,
            HeightAlignOffsetMeters = offsetMetres,
        };

        WorldPackage.Write(directory, manifest, "<OpenDRIVE/>", string.Empty, [], []);
        return new SyntheticWorld(directory, WorldPackage.PackagePath(directory, manifest.MapName));
    }

    /// <summary>
    /// What a server holding this world answers when asked which world it has loaded: the origin
    /// the package declares, the OpenDRIVE it carries, and the bare-earth record its build published
    /// -- the same values, and the digests the server computes of the same grids, as the building
    /// client sends them.
    /// </summary>
    public LoadedWorld AsLoaded() => Describe(PackagePath);

    /// <summary>What a server holding the world a package was written from answers.</summary>
    /// <remarks>
    /// The digests are taken from the grids as decoded, not from the entry's bytes, which is what a
    /// server does with the floats it was sent; the check takes the package's from the entry. Equal
    /// answers from the two are part of what an admitted package shows.
    /// </remarks>
    public static LoadedWorld Describe(string packagePath)
    {
        WorldPackageManifest manifest = WorldPackage.ReadManifest(packagePath);
        BareEarthRecord record = WorldPackage.TryReadGrids(packagePath, out float[] offset, out float[] ground)
            ? new BareEarthRecord(manifest.HeightAlignOffsetMeters, manifest.DrapeActive,
                                  manifest.GridMinXMeters, manifest.GridMinYMeters,
                                  manifest.GridCellSizeMeters, manifest.GridNumCols,
                                  manifest.GridNumRows, WorldPackage.HashGrid(offset),
                                  WorldPackage.HashGrid(ground))
            : new BareEarthRecord(manifest.HeightAlignOffsetMeters, false, 0.0, 0.0, 0.0, 0, 0,
                                  string.Empty, string.Empty);
        return new LoadedWorld(manifest.OriginLatitude, manifest.OriginLongitude,
                               manifest.OriginHeightMeters, WorldPackage.ReadOpenDrive(packagePath),
                               record);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A package still open somewhere is not worth failing a test over; the temporary
            // directory is the operating system's to clean up.
        }
    }
}
