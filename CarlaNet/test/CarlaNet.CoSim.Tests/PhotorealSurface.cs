using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The photoreal surface a world was built from -- the raw DSM the world build sampled from the
/// photogrammetry at every grid node, with the bare-earth DTM beside it -- read back out of the build's
/// drape cache (<c>Build/drape-cache</c>), for measuring the road and the ground against what a camera
/// sees.
/// </summary>
/// <remarks>
/// A world package carries the draped surface and bare earth, not the photoreal it was draped from: the
/// draped surface is the photoreal only where it lies within five metres of bare earth, box-blurred, and
/// held to the ground under every deck. The cache holds the raw samples, keyed by the grid's geometry and
/// the two ion assets, so the one a package was built from is the one whose grid geometry is the
/// package's and whose bare earth is the package's bare earth.
/// </remarks>
internal sealed class PhotorealSurface
{
    /// <summary>
    /// The cache's header ahead of its two grids: a magic number, the grid origin's latitude, longitude
    /// and height, its corner and cell size, its columns and rows, and the two ion asset ids.
    /// </summary>
    private const int HeaderBytes = 4 + (6 * 8) + (2 * 4) + (2 * 8);

    private readonly double[] _dsm;
    private readonly double[] _dtm;
    private readonly double _minX;
    private readonly double _minY;
    private readonly double _cell;
    private readonly int _columns;
    private readonly int _rows;

    private PhotorealSurface(string path, double[] dsm, double[] dtm, WorldPackageManifest manifest,
                             double bareEarthAgreement)
    {
        Path = path;
        _dsm = dsm;
        _dtm = dtm;
        _minX = manifest.GridMinXMeters;
        _minY = manifest.GridMinYMeters;
        _cell = manifest.GridCellSizeMeters;
        _columns = manifest.GridNumCols;
        _rows = manifest.GridNumRows;
        BareEarthAgreementMetres = bareEarthAgreement;
    }

    /// <summary>The cache file the surface was read from.</summary>
    public string Path { get; }

    /// <summary>
    /// The largest difference between the cache's bare earth and the package's, over the cells both
    /// sampled: zero to single precision for the cache the package was built from.
    /// </summary>
    public double BareEarthAgreementMetres { get; }

    /// <summary>
    /// The cached photoreal surface of a world package, or <see langword="null"/> where no cache of its
    /// grid is found above the test's output directory.
    /// </summary>
    public static PhotorealSurface? ForPackage(string packagePath)
    {
        WorldPackageManifest manifest = WorldPackage.ReadManifest(packagePath);
        if (!WorldPackage.TryReadGrids(packagePath, out _, out float[] bareEarth)
            || FindCacheDirectory() is not { } directory)
        {
            return null;
        }

        int cells = manifest.GridNumCols * manifest.GridNumRows;
        PhotorealSurface? best = null;
        foreach (string path in Directory.EnumerateFiles(directory, "drape_*.bin"))
        {
            if (new FileInfo(path).Length != HeaderBytes + (16L * cells)
                || !TryRead(path, manifest, cells, out double[] dsm, out double[] dtm))
            {
                continue;
            }

            double worst = 0.0;
            for (int index = 0; index < cells; index++)
            {
                if (double.IsFinite(dtm[index]))
                {
                    worst = Math.Max(worst, Math.Abs(dtm[index] - bareEarth[index]));
                }
            }

            if (best is null || worst < best.BareEarthAgreementMetres)
            {
                best = new PhotorealSurface(path, dsm, dtm, manifest, worst);
            }
        }

        return best is { BareEarthAgreementMetres: < 0.01 } ? best : null;
    }

    /// <summary>The photoreal surface's height, ellipsoidal, at a CARLA-frame position; null where unsampled.</summary>
    public double? Surface(double carlaX, double carlaY) => Bilinear(_dsm, carlaX, carlaY);

    /// <summary>Bare earth's height, ellipsoidal, at a CARLA-frame position; null where unsampled.</summary>
    public double? BareEarth(double carlaX, double carlaY) => Bilinear(_dtm, carlaX, carlaY);

    private double? Bilinear(double[] grid, double x, double y)
    {
        double fx = (x - _minX) / _cell;
        double fy = (y - _minY) / _cell;
        if (fx < 0.0 || fy < 0.0 || fx > _columns - 1.0 || fy > _rows - 1.0)
        {
            return null;
        }

        int c0 = Math.Min((int)fx, _columns - 2);
        int r0 = Math.Min((int)fy, _rows - 2);
        double tx = fx - c0;
        double ty = fy - r0;
        double v00 = grid[(r0 * _columns) + c0];
        double v01 = grid[(r0 * _columns) + c0 + 1];
        double v10 = grid[((r0 + 1) * _columns) + c0];
        double v11 = grid[((r0 + 1) * _columns) + c0 + 1];
        if (!double.IsFinite(v00) || !double.IsFinite(v01) || !double.IsFinite(v10) || !double.IsFinite(v11))
        {
            return null;
        }

        double top = v00 + ((v01 - v00) * tx);
        double bottom = v10 + ((v11 - v10) * tx);
        return top + ((bottom - top) * ty);
    }

    private static bool TryRead(string path, WorldPackageManifest manifest, int cells,
                                out double[] dsm, out double[] dtm)
    {
        dsm = [];
        dtm = [];
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        _ = reader.ReadInt32();
        _ = reader.ReadDouble();
        _ = reader.ReadDouble();
        _ = reader.ReadDouble();
        if (reader.ReadDouble() != manifest.GridMinXMeters || reader.ReadDouble() != manifest.GridMinYMeters
            || reader.ReadDouble() != manifest.GridCellSizeMeters || reader.ReadInt32() != manifest.GridNumCols
            || reader.ReadInt32() != manifest.GridNumRows)
        {
            return false;
        }

        _ = reader.ReadInt64();
        _ = reader.ReadInt64();
        dsm = new double[cells];
        dtm = new double[cells];
        for (int index = 0; index < cells; index++)
        {
            dsm[index] = reader.ReadDouble();
        }

        for (int index = 0; index < cells; index++)
        {
            dtm[index] = reader.ReadDouble();
        }

        return true;
    }

    private static string? FindCacheDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            string candidate = System.IO.Path.Combine(directory.FullName, "Build", "drape-cache");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
