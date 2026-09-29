// A durable, self-describing record of one generated world.
//
// A world built from OpenStreetMap exists only for the run that built it: the road network is an
// OpenDRIVE string, and the reconciliation between the bare-earth datum and the photoreal imagery
// lives as in-memory grids on the client. A world package writes all of it to disk so the same world
// can be rebuilt, inspected, or turned into an editable level later, by something that was not
// present when it was generated.
//
// One file per world -- <name>.cwp -- which is a zip archive. Write produces the world itself:
//   world.json      the manifest: datum, height reconciliation, layers, sandbox, provenance
//   map.xodr        the elevated OpenDRIVE, exactly as the server received it
//   map.net.xml     the SUMO network from the SAME netconvert run as map.xodr
//   bareearth.bin   the per-cell surface reconciliation grids, float32 (see the format below)
//
// The world build then publishes the authoring reference set into the same archive
// (carlacontrol.AuthoringReferenceSet), rewriting it whole and copying these four entries across
// byte for byte:
//   areas.resolved.json   areas of interest in CARLA metres and on SUMO lanes; always written
//   areas.aoi.geojson     the GeoJSON those areas were resolved from, when any were declared
//   places.json           the place index: street names to edges, and its coverage
//   solar.json            the solar frame: the origin and the time zone the engine derives
// Each is self-describing JSON carrying its own schema version, and is read here as text; the
// schemas are in 04_Contracts.md C5 and 07_Scenario_Authoring.md section 2. Write replaces the
// archive, so writing a world again drops its reference set until the build publishes it again --
// a rebuilt world's network can differ, and a set derived from the old one must not survive it.
//
// map.net.xml is carried rather than regenerated because it cannot be regenerated. Asking netconvert
// for OpenDRIVE output makes it default rectangular-lane-cut to true, which feeds junction shape
// computation, so a second run with byte-identical flags produces a different graph -- measured, 743
// of 4,978 canonical rows differ and lane lengths move by up to 3.3 m. A scenario authored against a
// regenerated network binds to edges the rendered world does not have.
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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// SHA-1 of the offset grid the companion binary carries (<see cref="WorldPackage.HashGrid"/>):
    /// lowercase hexadecimal, over its float32 values as little-endian bytes, row-major -- the same
    /// digest a server holding this world serves for its record's grid (<c>get_bare_earth_digest</c>).
    /// Empty when no drape grid exists, and on a package written before the digests were recorded.
    /// </summary>
    /// <remarks>
    /// Written by <see cref="WorldPackage.Write"/> from the grid it writes, replacing whatever the
    /// caller set. It is the grid's recorded identity, for a reader with no server to ask. Nothing
    /// requires it to be present, since older packages have none; where it is, the co-simulation
    /// session's loaded-world check requires it to be what the grid entry hashes to.
    /// </remarks>
    public string BareEarthOffsetSha1 { get; init; } = string.Empty;

    /// <summary>
    /// Likewise for the bare-earth ground grid (<see cref="BareEarthOffsetSha1"/>).
    /// </summary>
    public string BareEarthDtmSha1 { get; init; } = string.Empty;

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

    // Provenance. Never read by the runtime; read by whoever asks why a world looks the way it does,
    // and by a scenario build deciding whether the network in front of it is the one this world was
    // made from.

    public string SourceOsmFileName { get; init; } = string.Empty;

    /// <summary>
    /// The canonical fingerprint of the OSM extract the world was built from
    /// (<see cref="OsmFingerprint"/>) -- over the parsed document, not the file's bytes.
    /// </summary>
    /// <remarks>
    /// A byte digest named a file that could not be reproduced: the extract is the clipped one the
    /// pipeline writes, and the clipper emitted its nodes in set-iteration order, so the same extract
    /// clipped twice gave two files and one graph. Packages written before this landed carry a byte
    /// digest here instead; an empty <see cref="NetworkFingerprint"/> is what distinguishes them.
    /// </remarks>
    public string SourceOsmSha256 { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 of the OpenDRIVE body: everything from the root element onwards, with the header's
    /// <c>date</c> attribute blanked (<see cref="WorldPackage.HashOpenDrive"/>).
    /// </summary>
    /// <remarks>
    /// netconvert writes the moment it ran into a leading comment and into the OpenDRIVE header,
    /// and echoes its whole configuration -- including the temporary output paths -- into that same
    /// comment, so a digest of the whole document never reproduces. Normalising both makes two
    /// builds of one world comparable, which is the only thing this field is for.
    /// </remarks>
    public string OpenDriveSha256 { get; init; } = string.Empty;

    /// <summary>
    /// The canonical fingerprint of <c>map.net.xml</c> (<see cref="CarlaNet.Map.NetworkFingerprint"/>).
    /// Empty on a package written before the network was carried at all.
    /// </summary>
    public string NetworkFingerprint { get; init; } = string.Empty;

    /// <summary>
    /// Every argument netconvert was given, in order, as the world build passed them.
    /// </summary>
    /// <remarks>
    /// The full list rather than a summary, because a scenario build validates its own flag set
    /// against this one and names the difference when they disagree. Input and output paths are part
    /// of the record but are not comparable between builds; see
    /// <c>carlacontrol.SumoScenarioBuilder</c> for which arguments the comparison skips. Like
    /// <see cref="NetconvertExtraArgs"/> this is a list, so it is excluded from the record's
    /// generated equality and compared by <see cref="ValueEquals"/>.
    /// </remarks>
    public List<string> NetconvertArgv { get; init; } = [];

    /// <summary>The netconvert executable that ran, resolved. Empty on an older package.</summary>
    public string NetconvertPath { get; init; } = string.Empty;

    /// <summary>
    /// The version that executable reported when asked, at the moment it converted this world.
    /// </summary>
    /// <remarks>
    /// Recorded from the invocation rather than inferred later from a path or from whatever
    /// <c>SUMO_HOME</c> now points at -- the pinned toolchain and the ambient one have already
    /// diverged once. Empty on an older package, or when the executable could not be asked.
    /// </remarks>
    public string NetconvertVersion { get; init; } = string.Empty;
    public double SampleStepMeters { get; init; }
    public double TerrainResolutionMeters { get; init; }
    public double TerrainMarginMeters { get; init; }
    public string GeneratedAtUtc { get; init; } = string.Empty;
    public string GeneratorVersion { get; init; } = string.Empty;

    /// <summary>
    /// The extra netconvert arguments the caller supplied on top of the standard flag set -- the
    /// road filter, an offset. <see cref="NetconvertArgv"/> is the authoritative record of what
    /// actually ran; this says which part of it the caller chose.
    /// </summary>
    /// <remarks>
    /// Being a list, this member is compared by reference in the record's generated equality, so two
    /// manifests holding the same arguments are not <c>Equal</c>. Compare it with
    /// <see cref="SequenceEqual"/> rather than relying on <c>==</c> over the whole manifest.
    /// </remarks>
    public List<string> NetconvertExtraArgs { get; init; } = [];

    /// One shared instance, so the comparison below neutralises the list members by making both
    /// copies reference the same object rather than two equally-empty but distinct ones.
    private static readonly List<string> NoArguments = [];

    /// <summary>Value equality including the argument lists, which the record's own equality omits.</summary>
    public bool ValueEquals(WorldPackageManifest other)
        => other is not null
           && (this with { NetconvertExtraArgs = NoArguments, NetconvertArgv = NoArguments })
              == (other with { NetconvertExtraArgs = NoArguments, NetconvertArgv = NoArguments })
           && NetconvertExtraArgs.SequenceEqual(other.NetconvertExtraArgs)
           && NetconvertArgv.SequenceEqual(other.NetconvertArgv);
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
    private const string NetworkEntry = "map.net.xml";
    private const string GridEntry = "bareearth.bin";

    /// <summary>The resolved area table of the authoring reference set.</summary>
    public const string AreasOfInterestEntry = "areas.resolved.json";

    /// <summary>The GeoJSON the area table was resolved from, byte for byte.</summary>
    public const string AreasOfInterestSourceEntry = "areas.aoi.geojson";

    /// <summary>The place index of the authoring reference set.</summary>
    public const string PlaceIndexEntry = "places.json";

    /// <summary>The solar frame of the authoring reference set.</summary>
    public const string SolarFrameEntry = "solar.json";

    /// <summary>The package a world of this name occupies inside a directory.</summary>
    public static string PackagePath(string directory, string mapName)
        => Path.Combine(directory, mapName + Extension);

    /// <summary>
    /// Write a complete package. <paramref name="sumoNetwork"/> is the SUMO network from the same
    /// netconvert run as <paramref name="elevatedXodr"/>; an empty string writes no network entry,
    /// which is how a package built before the network was carried looks.
    /// <paramref name="offsetMeters"/> and <paramref name="bareEarthDtmMeters"/>
    /// are row-major [row * NumCols + col] and must both be NumCols*NumRows long when the manifest
    /// says a drape is active; they are ignored otherwise, since a constant shift needs no grid.
    /// The manifest is written with <see cref="WorldPackageManifest.BareEarthOffsetSha1"/> and
    /// <see cref="WorldPackageManifest.BareEarthDtmSha1"/> set to the digests of the grids written,
    /// or empty where none are.
    /// </summary>
    public static void Write(
        string directory,
        WorldPackageManifest manifest,
        string elevatedXodr,
        string sumoNetwork,
        ReadOnlySpan<float> offsetMeters,
        ReadOnlySpan<float> bareEarthDtmMeters)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(elevatedXodr);
        ArgumentNullException.ThrowIfNull(sumoNetwork);

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

        // The digests describe the grids this call writes, whatever the caller's manifest carried.
        manifest = manifest with
        {
            BareEarthOffsetSha1 = manifest.DrapeActive ? HashGrid(offsetMeters) : string.Empty,
            BareEarthDtmSha1 = manifest.DrapeActive ? HashGrid(bareEarthDtmMeters) : string.Empty,
        };

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

            // The network from the same netconvert run as the OpenDRIVE. Absent rather than empty
            // when there is none, so "this world predates the network being carried" and "the
            // network went missing" do not look alike, the same way the grid entry works.
            if (sumoNetwork.Length > 0)
            {
                WriteTextEntry(archive, NetworkEntry, sumoNetwork);
            }

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

    /// <summary>
    /// The SUMO road network a package carries, from the same netconvert run as its OpenDRIVE.
    /// </summary>
    /// <remarks>
    /// Throws when the package has none. That is a package written before the network was carried,
    /// and the right answer for a scenario build is to stop: the network cannot be reconstructed by
    /// running netconvert again, so there is nothing to fall back to and rebuilding the world is the
    /// only way forward.
    /// </remarks>
    public static string ReadNetwork(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry entry = archive.GetEntry(NetworkEntry)
            ?? throw new InvalidDataException(
                $"world package carries no {NetworkEntry}: {packagePath}. It was built before the "
                + "SUMO network was kept, and the network cannot be regenerated -- asking netconvert "
                + "for OpenDRIVE output changes the graph it produces. Rebuild the world from its "
                + "original extract.");
        using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    /// <summary>Whether a package carries a SUMO network at all.</summary>
    public static bool HasNetwork(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return archive.GetEntry(NetworkEntry) is not null;
    }

    /// <summary>
    /// The resolved area table, as the JSON text the package carries. False when the package has no
    /// reference set; an empty area list when the world was built with no areas declared.
    /// </summary>
    /// <remarks>
    /// Throws when the table's <c>source_sha256</c> does not match the GeoJSON carried beside it
    /// (C5 V5.11): the table would then describe areas other than the ones the package declares, and
    /// a reader that returned it anyway would be the one component nobody checks.
    /// </remarks>
    public static bool TryReadAreasOfInterest(string packagePath, out string resolvedJson)
    {
        resolvedJson = string.Empty;
        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry? table = archive.GetEntry(AreasOfInterestEntry);
        if (table is null)
        {
            return false;
        }
        using (var reader = new StreamReader(table.Open(), new UTF8Encoding(false)))
        {
            resolvedJson = reader.ReadToEnd();
        }

        string recorded;
        using (JsonDocument document = JsonDocument.Parse(resolvedJson))
        {
            recorded = document.RootElement.TryGetProperty("source_sha256", out JsonElement digest)
                ? digest.GetString() ?? string.Empty
                : string.Empty;
        }
        ZipArchiveEntry? source = archive.GetEntry(AreasOfInterestSourceEntry);
        string carried = string.Empty;
        if (source is not null)
        {
            using Stream stream = source.Open();
            carried = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        if (!string.Equals(recorded, carried, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{packagePath}: {AreasOfInterestEntry} was resolved from a source with digest "
                + $"{(recorded.Length > 0 ? recorded : "(none)")}, but the package carries "
                + $"{(carried.Length > 0 ? carried : "no " + AreasOfInterestSourceEntry)}; the areas "
                + "it describes are not the areas this package declares. Republish the reference set.");
        }
        return true;
    }

    /// <summary>The GeoJSON the area table was resolved from, byte for byte, when any was declared.</summary>
    public static bool TryReadAreasOfInterestSource(string packagePath, out byte[] geoJson)
    {
        geoJson = [];
        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry? entry = archive.GetEntry(AreasOfInterestSourceEntry);
        if (entry is null)
        {
            return false;
        }
        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        geoJson = buffer.ToArray();
        return true;
    }

    /// <summary>The place index, as JSON text. False when the package has no reference set.</summary>
    public static bool TryReadPlaceIndex(string packagePath, out string placeIndexJson)
        => TryReadText(packagePath, PlaceIndexEntry, out placeIndexJson);

    /// <summary>The solar frame, as JSON text. False when the package has no reference set.</summary>
    public static bool TryReadSolarFrame(string packagePath, out string solarFrameJson)
        => TryReadText(packagePath, SolarFrameEntry, out solarFrameJson);

    private static bool TryReadText(string packagePath, string entryName, out string text)
    {
        text = string.Empty;
        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry? entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return false;
        }
        using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
        text = reader.ReadToEnd();
        return true;
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
        (int numCols, int numRows) = ReadGridHeader(reader, packagePath);
        int count = numCols * numRows;

        offsetMeters = new float[count];
        bareEarthDtmMeters = new float[count];
        for (int i = 0; i < count; i++) { offsetMeters[i] = reader.ReadSingle(); }
        for (int i = 0; i < count; i++) { bareEarthDtmMeters[i] = reader.ReadSingle(); }
        return true;
    }

    /// <summary>
    /// The digest of each per-cell grid the package carries (<see cref="HashGrid"/>), taken from the
    /// grid entry's bytes as they stand. Returns false with empty outputs when the package has no
    /// grid file, as <see cref="TryReadGrids"/> does.
    /// </summary>
    /// <remarks>
    /// The entry holds each grid as float32 little-endian values, row-major, which are the bytes the
    /// digest is defined over, so they are hashed as they are read: no value is decoded and neither
    /// grid is held in memory. This is what the entry holds, whatever the manifest records.
    /// </remarks>
    public static bool TryReadGridDigests(
        string packagePath, out string offsetSha1, out string bareEarthDtmSha1)
    {
        offsetSha1 = string.Empty;
        bareEarthDtmSha1 = string.Empty;

        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry? grid = archive.GetEntry(GridEntry);
        if (grid is null)
        {
            return false;
        }

        // BinaryReader reads exactly the bytes each value needs and nothing ahead of them, so once
        // the header is read the stream stands at the first offset value.
        using var stream = grid.Open();
        using var reader = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: true);
        (int numCols, int numRows) = ReadGridHeader(reader, packagePath);
        long bytes = (long)numCols * numRows * sizeof(float);
        offsetSha1 = HashNext(stream, bytes, packagePath);
        bareEarthDtmSha1 = HashNext(stream, bytes, packagePath);
        return true;
    }

    /// <summary>
    /// Read the grid entry's header, leaving the reader at the first offset value, and answer the
    /// grid's columns and rows.
    /// </summary>
    private static (int Columns, int Rows) ReadGridHeader(BinaryReader reader, string packagePath)
    {
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
        if (numCols < 2 || numRows < 2)
        {
            throw new InvalidDataException($"degenerate grid {numCols}x{numRows} in {packagePath}");
        }
        return (numCols, numRows);
    }

    /// <summary>SHA-1 of the next <paramref name="count"/> bytes of a grid entry, which must hold them.</summary>
    private static string HashNext(Stream stream, long count, string packagePath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[1 << 20];
        while (count > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
            {
                throw new InvalidDataException($"world-package grid ends before its last cell: {packagePath}");
            }
            hash.AppendData(buffer, 0, read);
            count -= read;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Lowercase hexadecimal SHA-1 of a per-cell grid: its float32 values as little-endian bytes, in
    /// the order given, which for every grid here is row-major [row * NumCols + col].
    /// </summary>
    /// <remarks>
    /// <para>These are the bytes <c>bareearth.bin</c> holds for the grid, and the bytes a server
    /// holding the world digests for its bare-earth record (<c>get_bare_earth_digest</c>), so equal
    /// digests are equal grids, bit for bit, without either side sending the other a grid.</para>
    ///
    /// <para>SHA-1 rather than the SHA-256 this class uses elsewhere because the server has to compute
    /// it too, and the engine's Core has no SHA-256 that runs on Windows or Linux without bringing in
    /// OpenSSL. It is there to tell one build's grids from another's, not to resist an adversary: the
    /// server takes its record from any client.</para>
    /// </remarks>
    public static string HashGrid(ReadOnlySpan<float> grid)
    {
        if (BitConverter.IsLittleEndian)
        {
            return Convert.ToHexStringLower(SHA1.HashData(MemoryMarshal.AsBytes(grid)));
        }

        byte[] bytes = new byte[grid.Length * sizeof(float)];
        for (int i = 0; i < grid.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), grid[i]);
        }
        return Convert.ToHexStringLower(SHA1.HashData(bytes));
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

    /// <summary>
    /// SHA-256 of an OpenDRIVE document with the parts that record the run rather than the road
    /// removed, so two builds of one world produce the same digest.
    /// </summary>
    /// <remarks>
    /// Two things vary between otherwise identical netconvert runs, and both are dropped here:
    /// the leading comment, which carries the moment it ran and echoes the whole configuration
    /// including the temporary output paths this pipeline generates per run; and the
    /// <c>date</c> attribute on the OpenDRIVE header. Measured: with those two normalised, two
    /// conversions of one extract are byte-identical, and without them they never are.
    /// </remarks>
    public static string HashOpenDrive(string xodr)
        => HashText(NormaliseOpenDrive(xodr));

    /// <summary>The OpenDRIVE body: from the root element, with the header date blanked.</summary>
    internal static string NormaliseOpenDrive(string xodr)
    {
        ArgumentNullException.ThrowIfNull(xodr);

        // Everything before the root element is the XML declaration and netconvert's comment.
        int root = xodr.IndexOf("<OpenDRIVE", StringComparison.Ordinal);
        string body = root < 0 ? xodr : xodr[root..];

        int header = body.IndexOf("<header", StringComparison.Ordinal);
        if (header < 0) { return body; }
        int headerEnd = body.IndexOf('>', header);
        if (headerEnd < 0) { return body; }

        const string DateAttribute = " date=\"";
        int date = body.IndexOf(DateAttribute, header, StringComparison.Ordinal);
        if (date < 0 || date > headerEnd) { return body; }
        int valueStart = date + DateAttribute.Length;
        int valueEnd = body.IndexOf('"', valueStart);
        if (valueEnd < 0 || valueEnd > headerEnd) { return body; }
        return body.Remove(valueStart, valueEnd - valueStart);
    }
}
