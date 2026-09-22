using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A world package written to a temporary file, carrying a ground surface of the caller's choosing.
/// </summary>
/// <remarks>
/// A generated world's smallest package is sixty megabytes of measured terrain, which is neither
/// committable nor a surface whose height at a point is knowable in advance. A test of the seating
/// arithmetic needs a surface it can predict, so it makes one -- through the real package writer and
/// the real reader, so the file layout and the frame the grid is indexed in are the shipped ones.
/// </remarks>
internal static class SyntheticWorld
{
    private const double CellSize = 2.0;
    private const int Columns = 101;
    private const int Rows = 101;
    private const double MinX = -100.0;
    private const double MinY = -100.0;
    private const double OriginHeight = 1000.0;

    /// <param name="heightAbove">
    /// Surface height above the georeference origin at a CARLA-frame cell centre, in metres.
    /// </param>
    public static GroundSurface Build(Func<(double X, double Y), double> heightAbove)
    {
        string directory = Path.Combine(Path.GetTempPath(),
                                        "carlanet-cosim-" + Guid.NewGuid().ToString("n"));
        var manifest = new WorldPackageManifest
        {
            MapName = "SyntheticSurface",
            OriginLatitude = 0.0,
            OriginLongitude = 0.0,
            OriginHeightMeters = OriginHeight,
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
                bareEarth[(row * Columns) + column] =
                    (float)(OriginHeight + heightAbove((x, y)));
            }
        }

        WorldPackage.Write(directory, manifest, "<OpenDRIVE/>", string.Empty, offset, bareEarth);
        try
        {
            return GroundSurface.FromWorldPackage(WorldPackage.PackagePath(directory, manifest.MapName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
