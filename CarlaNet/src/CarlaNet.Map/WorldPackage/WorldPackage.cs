// A durable, self-describing record of one generated world.
//
// A world built from OpenStreetMap exists only for the run that built it: the road network is an
// OpenDRIVE string, and the reconciliation between the bare-earth datum and the photoreal imagery
// lives as in-memory grids on the client. A world package writes all of it to disk so the same world
// can be rebuilt, inspected, or turned into an editable level later, by something that was not
// present when it was generated.
//
// One file per world -- <name>.cwp -- which is a zip archive holding three entries:
//   world.json      the manifest: datum, height reconciliation, layers, sandbox, provenance
//   map.xodr        the elevated OpenDRIVE, exactly as the server received it
//   bareearth.bin   the per-cell surface reconciliation grids, float32 (see the format below)
//
// One file rather than three loose ones because a world is a thing you move, keep, and hand to
// someone: it can be copied, archived and versioned without a folder convention to preserve, and
// importing it is choosing a file rather than naming a directory and a world within it. The entries
// are named for their role rather than for the map, since the archive already carries the name.
//
// The grids are binary rather than JSON because they run to hundreds of thousands of cells. The
// manifest is JSON because it is small, and because a human resolving "why is this world wrong"
// should be able to read it without a tool -- renaming the package to .zip opens it in anything.
//
// Entries are STORED, never deflated. The editor reads packages with FZipArchiveReader, which
// handles uncompressed archives only, so compressing here would produce a package the importer
// cannot open. It costs perhaps three times the bytes on disk; the alternative is a package that
// writes cleanly and fails to import, which is a far worse trade.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

    /// <summary>Extension of a world package. A zip, so anything can open it once renamed.</summary>
    public const string Extension = ".cwp";

    private const string ManifestEntry = "world.json";
    private const string OpenDriveEntry = "map.xodr";
    private const string GridEntry = "bareearth.bin";

    /// <summary>The package a world of this name occupies inside a directory.</summary>
    public static string PackagePath(string directory, string mapName)
        => Path.Combine(directory, mapName + Extension);

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
        string package = PackagePath(directory, manifest.MapName);

        // Written whole and moved into place, so a package that exists is always complete: an import
        // reading one while a build is part-way through writing it would otherwise see a manifest
        // describing grids that are not there yet.
        string staging = package + ".partial";
        File.Delete(staging);

        using (var archive = ZipFile.Open(staging, ZipArchiveMode.Create))
        {
            WriteTextEntry(archive, ManifestEntry, JsonSerializer.Serialize(manifest, ManifestJson));
            WriteTextEntry(archive, OpenDriveEntry, elevatedXodr);

            // A constant shift is fully described by the manifest, so no grid entry is written at
            // all. Its absence is the signal, which keeps "no drape" from looking like "the grids
            // went missing".
            if (manifest.DrapeActive)
            {
                using Stream grid = archive.CreateEntry(GridEntry, CompressionLevel.NoCompression).Open();
                using var writer = new BinaryWriter(grid, new UTF8Encoding(false), leaveOpen: false);
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
        }

        File.Move(staging, package, overwrite: true);
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        using Stream entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        using var writer = new StreamWriter(entry, new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>Read a package's manifest. Throws if it is absent or unparseable.</summary>
    public static WorldPackageManifest ReadManifest(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry entry = archive.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException($"not a world package, no {ManifestEntry}: {packagePath}");
        using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
        return JsonSerializer.Deserialize<WorldPackageManifest>(reader.ReadToEnd(), ManifestJson)
            ?? throw new InvalidDataException($"empty world manifest: {packagePath}");
    }

    /// <summary>The road network a package carries, as the server produced it.</summary>
    public static string ReadOpenDrive(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry entry = archive.GetEntry(OpenDriveEntry)
            ?? throw new InvalidDataException($"world package has no road network: {packagePath}");
        using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    /// <summary>Every world a directory holds, by name, for a caller offering a choice.</summary>
    public static IReadOnlyList<string> ListPackages(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.GetFiles(directory, "*" + Extension)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Read the per-cell grids. Returns false with empty outputs when the package has no grid file,
    /// which is the normal case for a world reconciled by a constant shift.
    /// </summary>
    public static bool TryReadGrids(
        string packagePath, out float[] offsetMeters, out float[] bareEarthDtmMeters)
    {
        offsetMeters = [];
        bareEarthDtmMeters = [];

        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry? grid = archive.GetEntry(GridEntry);
        if (grid is null)
        {
            return false;
        }

        // Zip entries are not seekable, and the reader below only ever moves forward, so the stream
        // is consumed as it comes.
        using var stream = grid.Open();
        using var reader = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: false);
        if (reader.ReadInt32() != GridMagic)
        {
            throw new InvalidDataException($"not a world-package grid: {packagePath}");
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
            throw new InvalidDataException($"degenerate grid {numCols}x{numRows} in {packagePath}");
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
