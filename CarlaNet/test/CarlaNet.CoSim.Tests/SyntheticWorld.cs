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
    private const int Rows = 101;
    private const double MinX = -100.0;
    private const double MinY = -100.0;
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
    public static SyntheticWorld Write(Func<(double X, double Y), double> heightAbove,
                                       string networkPath,
                                       string geoReference)
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
            GridMinXMeters = MinX,
            GridMinYMeters = MinY,
            GridCellSizeMeters = CellSize,
            GridNumCols = Columns,
            GridNumRows = Rows,
        };

        float[] offset = new float[Columns * Rows];
        float[] bareEarth = new float[Columns * Rows];
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                double x = MinX + (column * CellSize);
                double y = MinY + (row * CellSize);
                bareEarth[(row * Columns) + column] = (float)(OriginHeight + heightAbove((x, y)));
            }
        }

        string network = networkPath.Length > 0 ? File.ReadAllText(networkPath) : string.Empty;
        WorldPackage.Write(directory, manifest, "<OpenDRIVE/>", network, offset, bareEarth);
        return new SyntheticWorld(directory,
                                  WorldPackage.PackagePath(directory, manifest.MapName));
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
