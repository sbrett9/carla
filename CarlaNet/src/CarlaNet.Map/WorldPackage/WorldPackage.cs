// A durable, self-describing record of one generated world.
//
// A world built from OpenStreetMap exists only for the run that built it: the road network is an
// OpenDRIVE string, and the reconciliation between the bare-earth datum and the photoreal imagery
// lives as in-memory grids on the client. A world package writes all of it to disk so the same world
// can be rebuilt, inspected, or turned into an editable level later, by something that was not
// present when it was generated.
//
// Three files per world, named after the map:
//   <name>.xodr             the elevated OpenDRIVE, exactly as the server received it
//   <name>.bareearth.bin    the per-cell surface reconciliation grids, float32 (see the format below)
//   <name>.world.json       the manifest: datum, height reconciliation, layers, sandbox, provenance
//
// The grids are binary rather than JSON because they run to hundreds of thousands of cells. The
// manifest is JSON because it is small, and because a human resolving "why is this world wrong"
// should be able to read it without a tool.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarlaNet.Map.WorldPackage;

/// <summary>
/// How a generated world reconciles the bare-earth datum with the photoreal imagery it is seated on,
/// plus enough provenance to identify what produced it.
/// </summary>
/// <remarks>
/// <see cref="HeightAlignMode"/> and <see cref="HeightAlignOffsetMeters"/> describe the constant-shift
/// modes; when <see cref="DrapeActive"/> the shift varies per cell and the grids in the companion
/// binary are authoritative instead.
/// </remarks>
public sealed record WorldPackageManifest
{
    public required string MapName { get; init; }

    // Datum. Local (0,0) is pinned to this latitude/longitude, and local Z 0 is this height.
    public required double OriginLatitude { get; init; }
    public required double OriginLongitude { get; init; }
    public required double OriginHeightMeters { get; init; }

    /// The OpenDRIVE geoReference projection string, verbatim, so the projection is never re-derived.
    public string GeoReferenceString { get; init; } = string.Empty;

    // Height reconciliation.
    public required string HeightAlignMode { get; init; }
    public required bool DrapeActive { get; init; }
    public required double HeightAlignOffsetMeters { get; init; }

    // Grid geometry, mirroring the companion binary. Zeroed when no drape grid exists.
    public double GridMinXMeters { get; init; }
    public double GridMinYMeters { get; init; }
    public double GridCellSizeMeters { get; init; }
    public int GridNumCols { get; init; }
    public int GridNumRows { get; init; }

    // Streamed imagery layers. The Cesium ion access token is deliberately NOT recorded: it is a
    // credential, and a world package is meant to be copied around.
    public long PhotorealIonAssetId { get; init; }
    public long GroundIonAssetId { get; init; }

    // Sandbox extent and the inward staging ring reserved for traffic entry and exit.
    public double StagingMinXMeters { get; init; }
    public double StagingMinYMeters { get; init; }
    public double StagingMaxXMeters { get; init; }
    public double StagingMaxYMeters { get; init; }
    public double StagingMarginMeters { get; init; }

    // Provenance. Never read by the runtime; read by whoever asks why a world looks the way it does.
    public string SourceOsmFileName { get; init; } = string.Empty;
    public string SourceOsmSha256 { get; init; } = string.Empty;
    public string OpenDriveSha256 { get; init; } = string.Empty;
    public double SampleStepMeters { get; init; }
    public double TerrainResolutionMeters { get; init; }
    public double TerrainMarginMeters { get; init; }
    public string GeneratedAtUtc { get; init; } = string.Empty;
    public string GeneratorVersion { get; init; } = string.Empty;

    /// <summary>
    /// Extra netconvert arguments the road network was converted with.
    /// </summary>
    /// <remarks>
    /// Being a list, this member is compared by reference in the record's generated equality, so two
    /// manifests holding the same arguments are not <c>Equal</c>. Compare it with
    /// <see cref="SequenceEqual"/> rather than relying on <c>==</c> over the whole manifest.
    /// </remarks>
    public List<string> NetconvertExtraArgs { get; init; } = [];

    /// One shared instance, so the comparison below neutralises the list member by making both
    /// copies reference the same object rather than two equally-empty but distinct ones.
    private static readonly List<string> NoExtraArgs = [];

    /// <summary>Value equality including the argument list, which the record's own equality omits.</summary>
    public bool ValueEquals(WorldPackageManifest other)
        => other is not null
           && (this with { NetconvertExtraArgs = NoExtraArgs }) == (other with { NetconvertExtraArgs = NoExtraArgs })
           && NetconvertExtraArgs.SequenceEqual(other.NetconvertExtraArgs);
}

/// <summary>
/// Reads and writes world packages. Static because a package is a value on disk, not a live object.
/// </summary>
public static class WorldPackage
{
    /// "CWP1" — the grid binary's magic number; the trailing digit is the format version.
    private const int GridMagic = 0x43575031;

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ManifestPath(string directory, string mapName)
        => Path.Combine(directory, mapName + ".world.json");

    public static string OpenDrivePath(string directory, string mapName)
        => Path.Combine(directory, mapName + ".xodr");

    public static string GridPath(string directory, string mapName)
        => Path.Combine(directory, mapName + ".bareearth.bin");

    /// <summary>
    /// Write a complete package. <paramref name="offsetMeters"/> and <paramref name="bareEarthDtmMeters"/>
    /// are row-major [row * NumCols + col] and must both be NumCols*NumRows long when the manifest
    /// says a drape is active; they are ignored otherwise, since a constant shift needs no grid.
    /// </summary>
    public static void Write(
        string directory,
        WorldPackageManifest manifest,
        string elevatedXodr,
        ReadOnlySpan<float> offsetMeters,
        ReadOnlySpan<float> bareEarthDtmMeters)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(elevatedXodr);

        if (manifest.DrapeActive)
        {
            int expected = manifest.GridNumCols * manifest.GridNumRows;
            if (manifest.GridNumCols < 2 || manifest.GridNumRows < 2 || manifest.GridCellSizeMeters <= 0.0)
            {
                throw new ArgumentException(
                    $"drape is active but the grid is degenerate: {manifest.GridNumCols}x{manifest.GridNumRows}, "
                    + $"cell {manifest.GridCellSizeMeters} m", nameof(manifest));
            }
            if (offsetMeters.Length != expected || bareEarthDtmMeters.Length != expected)
            {
                throw new ArgumentException(
                    $"grid length mismatch: offset {offsetMeters.Length}, ground {bareEarthDtmMeters.Length}, "
                    + $"expected {expected}", nameof(offsetMeters));
            }
        }

        Directory.CreateDirectory(Path.GetFullPath(directory));

        File.WriteAllText(OpenDrivePath(directory, manifest.MapName), elevatedXodr, new UTF8Encoding(false));
        File.WriteAllText(
            ManifestPath(directory, manifest.MapName),
            JsonSerializer.Serialize(manifest, ManifestJson),
            new UTF8Encoding(false));

        if (!manifest.DrapeActive)
        {
            // A constant shift is fully described by the manifest, so no grid file is written at all.
            // Its absence is the signal, which keeps "no drape" from looking like "grid went missing".
            File.Delete(GridPath(directory, manifest.MapName));
            return;
        }

        using var stream = File.Create(GridPath(directory, manifest.MapName));
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: false);
        writer.Write(GridMagic);
        writer.Write(manifest.OriginLatitude);
        writer.Write(manifest.OriginLongitude);
        writer.Write(manifest.OriginHeightMeters);
        writer.Write(manifest.GridMinXMeters);
        writer.Write(manifest.GridMinYMeters);
        writer.Write(manifest.GridCellSizeMeters);
        writer.Write(manifest.GridNumCols);
        writer.Write(manifest.GridNumRows);
        foreach (float v in offsetMeters) { writer.Write(v); }
        foreach (float v in bareEarthDtmMeters) { writer.Write(v); }
    }

    /// <summary>Read a package's manifest. Throws if it is absent or unparseable.</summary>
    public static WorldPackageManifest ReadManifest(string directory, string mapName)
    {
        string json = File.ReadAllText(ManifestPath(directory, mapName));
        return JsonSerializer.Deserialize<WorldPackageManifest>(json, ManifestJson)
            ?? throw new InvalidDataException($"empty world manifest: {ManifestPath(directory, mapName)}");
    }

    /// <summary>
    /// Read the per-cell grids. Returns false with empty outputs when the package has no grid file,
    /// which is the normal case for a world reconciled by a constant shift.
    /// </summary>
    public static bool TryReadGrids(
        string directory, string mapName, out float[] offsetMeters, out float[] bareEarthDtmMeters)
    {
        offsetMeters = [];
        bareEarthDtmMeters = [];
        string path = GridPath(directory, mapName);
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: false);
        if (reader.ReadInt32() != GridMagic)
        {
            throw new InvalidDataException($"not a world-package grid file: {path}");
        }
        reader.ReadDouble();   // origin latitude, carried for self-description
        reader.ReadDouble();   // origin longitude
        reader.ReadDouble();   // origin height
        reader.ReadDouble();   // grid minimum X
        reader.ReadDouble();   // grid minimum Y
        reader.ReadDouble();   // cell size
        int numCols = reader.ReadInt32();
        int numRows = reader.ReadInt32();
        int count = numCols * numRows;
        if (numCols < 2 || numRows < 2)
        {
            throw new InvalidDataException($"degenerate grid {numCols}x{numRows} in {path}");
        }

        offsetMeters = new float[count];
        bareEarthDtmMeters = new float[count];
        for (int i = 0; i < count; i++) { offsetMeters[i] = reader.ReadSingle(); }
        for (int i = 0; i < count; i++) { bareEarthDtmMeters[i] = reader.ReadSingle(); }
        return true;
    }

    /// <summary>Lowercase hexadecimal SHA-256 of a file's bytes, or an empty string if unreadable.</summary>
    public static string HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Lowercase hexadecimal SHA-256 of a UTF-8 string.</summary>
    public static string HashText(string text)
        => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(text)));
}
