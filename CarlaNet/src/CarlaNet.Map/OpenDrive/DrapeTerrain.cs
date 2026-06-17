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
}
