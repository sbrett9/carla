// Phase 2b — draped collision terrain over the OSM sandbox.
//
// This file is the OFFLINE/pure half: build a regular sample grid over the OSM rectangle in the
// CARLA world frame (+X=East, -Y=North), enumerate per-node lat/lon for Cesium sampling (the SAME
// Geodesy projection the road samples use, so no datum drift), and a binary disk cache so the
// (slow) DSM/DTM sampling is paid once per area. The de-spike/clamp/smooth pass and the actual
// Cesium sampling live elsewhere (DrapeTerrain de-spike step; CarlaClient.SampleDrapeGridAsync).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.OpenDrive;

/// <summary>A regular collision-terrain grid in the CARLA world frame. Node (col,row) sits at
/// world (MinX + col*CellSize, MinY + row*CellSize) metres; arrays are row-major [row*NumCols+col].
/// Origin is the georeference origin used to convert nodes to lat/lon.</summary>
public readonly record struct DrapeGridSpec(
    GeoLocation Origin,
    double MinX,
    double MinY,
    double CellSize,
    int NumCols,
    int NumRows)
{
    public int NodeCount => NumCols * NumRows;
    public double MaxX => MinX + (NumCols - 1) * CellSize;
    public double MaxY => MinY + (NumRows - 1) * CellSize;
}

public static class DrapeTerrain
{
    private const int CacheMagic = 0x44525031; // "DRP1"

    /// <summary>Grid covering the local bbox [minX,maxX]x[minY,maxY] expanded by <paramref name="marginMeters"/>,
    /// at <paramref name="cellSize"/> spacing. MinX/MinY are the expanded lower corner.</summary>
    public static DrapeGridSpec MakeGrid(GeoLocation origin,
        double minX, double minY, double maxX, double maxY, double cellSize, double marginMeters)
    {
        if (cellSize <= 0.0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (maxX < minX) (minX, maxX) = (maxX, minX);
        if (maxY < minY) (minY, maxY) = (maxY, minY);
        double lo_x = minX - marginMeters, lo_y = minY - marginMeters;
        double hi_x = maxX + marginMeters, hi_y = maxY + marginMeters;
        int cols = Math.Max(2, (int)Math.Ceiling((hi_x - lo_x) / cellSize) + 1);
        int rows = Math.Max(2, (int)Math.Ceiling((hi_y - lo_y) / cellSize) + 1);
        return new DrapeGridSpec(origin, lo_x, lo_y, cellSize, cols, rows);
    }

    /// <summary>Grid from OSM lat/lon bounds: project the four corners to the CARLA world frame
    /// (so the heightfield is axis-aligned in Unreal) and grid that bbox + margin.</summary>
    public static DrapeGridSpec MakeGridFromGeoBounds(GeoLocation origin,
        double minLat, double minLon, double maxLat, double maxLon, double cellSize, double marginMeters)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (la, lo) in new[] { (minLat, minLon), (minLat, maxLon), (maxLat, minLon), (maxLat, maxLon) })
        {
            var p = Geodesy.GeodeticToCarlaLocal(origin, new GeoLocation(la, lo, 0.0));
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return MakeGrid(origin, minX, minY, maxX, maxY, cellSize, marginMeters);
    }

    /// <summary>Per-node lat/lon (altitude 0) for Cesium sampling, row-major [row*NumCols+col].</summary>
    public static List<GeoLocation> GridGeoPoints(DrapeGridSpec spec)
    {
        var pts = new List<GeoLocation>(spec.NodeCount);
        for (int r = 0; r < spec.NumRows; ++r)
        {
            double y = spec.MinY + r * spec.CellSize;
            for (int c = 0; c < spec.NumCols; ++c)
            {
                double x = spec.MinX + c * spec.CellSize;
                var g = Geodesy.CarlaLocalToGeodetic(spec.Origin, x, y, 0.0);
                pts.Add(new GeoLocation(g.Latitude, g.Longitude, 0.0));
            }
        }
        return pts;
    }

    /// <summary>Lat/lon for a rectangular sub-block of the grid (cols [c0,c0+nc), rows [r0,r0+nr)),
    /// row-major within the block. Used for chunked sampling to bound Cesium tile streaming.</summary>
    public static List<GeoLocation> BlockGeoPoints(DrapeGridSpec spec, int c0, int r0, int nc, int nr)
    {
        var pts = new List<GeoLocation>(nc * nr);
        for (int r = r0; r < r0 + nr; ++r)
        {
            double y = spec.MinY + r * spec.CellSize;
            for (int c = c0; c < c0 + nc; ++c)
            {
                double x = spec.MinX + c * spec.CellSize;
                var g = Geodesy.CarlaLocalToGeodetic(spec.Origin, x, y, 0.0);
                pts.Add(new GeoLocation(g.Latitude, g.Longitude, 0.0));
            }
        }
        return pts;
    }

    // ── Disk cache ───────────────────────────────────────────────────────────
    // Keyed by the grid geometry + the ion asset ids, so re-running the same area reuses the
    // (minutes-long) DSM/DTM sampling. Stores two row-major double[] (DSM, DTM), NaN-permitting.

    public static string CacheFileName(DrapeGridSpec spec, long photoAsset, long groundAsset)
    {
        string key = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:F7},{1:F7},{2:F7}|{3:F3},{4:F3},{5:F4}|{6}x{7}|{8},{9}",
            spec.Origin.Latitude, spec.Origin.Longitude, spec.Origin.Altitude,
            spec.MinX, spec.MinY, spec.CellSize, spec.NumCols, spec.NumRows, photoAsset, groundAsset);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "drape_" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".bin";
    }

    public static bool TryReadCache(string path, DrapeGridSpec spec, long photoAsset, long groundAsset,
        out double[] dsm, out double[] dtm)
    {
        dsm = Array.Empty<double>(); dtm = Array.Empty<double>();
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadInt32() != CacheMagic) return false;
            // Validate the geometry header matches the requested spec.
            if (br.ReadDouble() != spec.Origin.Latitude || br.ReadDouble() != spec.Origin.Longitude
                || br.ReadDouble() != spec.Origin.Altitude || br.ReadDouble() != spec.MinX
                || br.ReadDouble() != spec.MinY || br.ReadDouble() != spec.CellSize) return false;
            if (br.ReadInt32() != spec.NumCols || br.ReadInt32() != spec.NumRows) return false;
            if (br.ReadInt64() != photoAsset || br.ReadInt64() != groundAsset) return false;
            int n = spec.NodeCount;
            dsm = new double[n]; dtm = new double[n];
            for (int i = 0; i < n; ++i) dsm[i] = br.ReadDouble();
            for (int i = 0; i < n; ++i) dtm[i] = br.ReadDouble();
            return true;
        }
        catch { dsm = Array.Empty<double>(); dtm = Array.Empty<double>(); return false; }
    }

    public static void WriteCache(string path, DrapeGridSpec spec, long photoAsset, long groundAsset,
        double[] dsm, double[] dtm)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(CacheMagic);
        bw.Write(spec.Origin.Latitude); bw.Write(spec.Origin.Longitude); bw.Write(spec.Origin.Altitude);
        bw.Write(spec.MinX); bw.Write(spec.MinY); bw.Write(spec.CellSize);
        bw.Write(spec.NumCols); bw.Write(spec.NumRows);
        bw.Write(photoAsset); bw.Write(groundAsset);
        foreach (double v in dsm) bw.Write(v);
        foreach (double v in dtm) bw.Write(v);
    }

    // ── De-spike / clamp / smooth → draped grid + offset field ─────────────────

    /// <summary>The draped collision/seating surface (row-major, ellipsoidal metres) plus the
    /// per-cell telemetry offset = DrapedZ − bare-earth DTM (so reported HAE = physical − offset
    /// recovers DTM truth everywhere, on- and off-road).</summary>
    public readonly record struct DrapeResult(double[] DrapedZ, double[] Offset);

    /// <summary>
    /// Turn raw DSM (photoreal) + DTM (bare earth) grids into a driveable draped surface. Open
    /// ground/road conforms to the photoreal; cells where |DSM−DTM| exceeds
    /// <paramref name="maxDrapeMeters"/> (buildings, tree canopy, L-tracks, sustained DSM bias)
    /// fall back to the bare-earth DTM — so the surface never climbs onto rooftops (the original
    /// road-Z bug) yet still hugs the photoreal where it's plausible ground. A low-pass
    /// (<paramref name="smoothRadius"/>, <paramref name="smoothPasses"/>) removes residual noise and
    /// softens the DSM↔DTM switches so vehicles don't jitter. NaN samples are neighbour-filled first.
    /// </summary>
    public static DrapeResult Despike(double[] dsm, double[] dtm, DrapeGridSpec spec,
        double maxDrapeMeters = 5.0, int smoothRadius = 1, int smoothPasses = 2)
    {
        int nc = spec.NumCols, nr = spec.NumRows, n = nc * nr;
        if (dsm.Length != n || dtm.Length != n)
            throw new ArgumentException($"dsm/dtm length must be {n} (got {dsm.Length}/{dtm.Length})");

        var DTM = (double[])dtm.Clone(); FillNaN(DTM, nc, nr);
        var DSM = (double[])dsm.Clone(); FillNaN(DSM, nc, nr);

        // DTM-anchored selection.
        var draped = new double[n];
        for (int i = 0; i < n; ++i)
        {
            double gap = DSM[i] - DTM[i];
            draped[i] = Math.Abs(gap) <= maxDrapeMeters ? DSM[i] : DTM[i];
        }

        for (int p = 0; p < smoothPasses && smoothRadius > 0; ++p)
            draped = BoxBlur(draped, nc, nr, smoothRadius);

        var offset = new double[n];
        for (int i = 0; i < n; ++i) offset[i] = draped[i] - DTM[i];
        return new DrapeResult(draped, offset);
    }

    /// <summary>Replace NaN cells with the mean of valid 4-neighbours, iterated until filled (bounded);
    /// any cells still NaN (fully isolated / empty grid) are set to the global mean (0 if none).</summary>
    private static void FillNaN(double[] a, int nc, int nr)
    {
        bool anyNaN = false;
        double sum = 0; int cnt = 0;
        for (int i = 0; i < a.Length; ++i)
        {
            if (double.IsNaN(a[i])) anyNaN = true;
            else { sum += a[i]; ++cnt; }
        }
        if (!anyNaN) return;
        double globalMean = cnt > 0 ? sum / cnt : 0.0;

        var next = new double[a.Length];
        int maxPasses = Math.Max(8, Math.Min(nc, nr));
        for (int pass = 0; pass < maxPasses; ++pass)
        {
            bool stillNaN = false;
            for (int r = 0; r < nr; ++r)
            {
                for (int c = 0; c < nc; ++c)
                {
                    int i = r * nc + c;
                    if (!double.IsNaN(a[i])) { next[i] = a[i]; continue; }
                    double s = 0; int k = 0;
                    if (c > 0      && !double.IsNaN(a[i - 1]))  { s += a[i - 1];  ++k; }
                    if (c < nc - 1 && !double.IsNaN(a[i + 1]))  { s += a[i + 1];  ++k; }
                    if (r > 0      && !double.IsNaN(a[i - nc])) { s += a[i - nc]; ++k; }
                    if (r < nr - 1 && !double.IsNaN(a[i + nc])) { s += a[i + nc]; ++k; }
                    if (k > 0) next[i] = s / k;
                    else { next[i] = double.NaN; stillNaN = true; }
                }
            }
            Array.Copy(next, a, a.Length);
            if (!stillNaN) break;
        }
        for (int i = 0; i < a.Length; ++i) if (double.IsNaN(a[i])) a[i] = globalMean;
    }

    /// <summary>Separable box blur of the given radius (edge-clamped). Cheap low-pass; repeat a few
    /// passes to approximate a Gaussian.</summary>
    private static double[] BoxBlur(double[] a, int nc, int nr, int radius)
    {
        var tmp = new double[a.Length];
        var outp = new double[a.Length];
        // horizontal
        for (int r = 0; r < nr; ++r)
        {
            int row = r * nc;
            for (int c = 0; c < nc; ++c)
            {
                double s = 0; int k = 0;
                for (int d = -radius; d <= radius; ++d)
                {
                    int cc = Math.Clamp(c + d, 0, nc - 1);
                    s += a[row + cc]; ++k;
                }
                tmp[row + c] = s / k;
            }
        }
        // vertical
        for (int c = 0; c < nc; ++c)
        {
            for (int r = 0; r < nr; ++r)
            {
                double s = 0; int k = 0;
                for (int d = -radius; d <= radius; ++d)
                {
                    int rr = Math.Clamp(r + d, 0, nr - 1);
                    s += tmp[rr * nc + c]; ++k;
                }
                outp[r * nc + c] = s / k;
            }
        }
        return outp;
    }
}
