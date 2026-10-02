using System.Globalization;
using System.Text.Json;

namespace CarlaNet.CoSim;

/// <summary>
/// A compiled scenario's lock, <c>&lt;scenario_id&gt;.lock.json</c>, as the scenario compiler
/// (<c>carlacontrol.ScenarioCompiler</c>) writes it beside the configuration: the digests that bind
/// the files a session runs, the catalogue and the epoch they were compiled against, the SUMO that
/// routed them and the world they were compiled for.
/// </summary>
/// <remarks>
/// <para>Only the fields a session compares or records are read. The ones it compares are required,
/// and a lock that lacks one is refused whole: a lock that cannot be checked is not evidence of
/// anything. The ones it records are carried where present and reported as not recorded where
/// not.</para>
/// </remarks>
public sealed class ScenarioLock
{
    /// <summary>The <c>lock_version</c> this reader implements. Any other is refused.</summary>
    public const int SupportedVersion = 1;

    private ScenarioLock()
    {
    }

    /// <summary>Where the lock was read from.</summary>
    public string Path { get; private init; } = string.Empty;

    /// <summary>The scenario the specification declares.</summary>
    public string ScenarioId { get; private init; } = string.Empty;

    /// <summary>The compiler that wrote it, and its version: <c>carlacontrol.ScenarioCompiler 1.0.0</c>.</summary>
    public string Compiler { get; private init; } = string.Empty;

    /// <summary>SHA-256 of the specification it was compiled from.</summary>
    public string SpecificationSha256 { get; private init; } = string.Empty;

    /// <summary>The configuration the compiler wrote: its path, taken against the lock's directory.</summary>
    public LockedFile Config { get; private init; } = new(string.Empty, string.Empty);

    /// <summary>The route file the compiler wrote.</summary>
    public LockedFile Routes { get; private init; } = new(string.Empty, string.Empty);

    /// <summary>The network the compiler copied from the world package, byte for byte.</summary>
    public LockedFile Network { get; private init; } = new(string.Empty, string.Empty);

    /// <summary>The digest the catalogue the scenario was compiled against declares.</summary>
    public string CatalogueDigest { get; private init; } = string.Empty;

    /// <summary>The catalogue's own id.</summary>
    public string? CatalogueId { get; private init; }

    /// <summary>SHA-256 of the epoch's canonical form: <see cref="SolarEpoch.Digest"/>'s function.</summary>
    public string EpochDigest { get; private init; } = string.Empty;

    /// <summary>The tool that routed the scenario's demand: <c>duarouter</c>.</summary>
    public string? RoutedByTool { get; private init; }

    /// <summary>The release of SUMO that routed it: <c>1.27.0</c>.</summary>
    public string? RoutedByRelease { get; private init; }

    /// <summary>The converter the world package recorded, as the compiler compared the router with it.</summary>
    public string? RoutedAgainstConverter { get; private init; }

    /// <summary>How the routing release stood against the world's converter: <c>SameRelease</c> and so on.</summary>
    public string? RoutingAgreement { get; private init; }

    /// <summary>Whether a routing release other than the world's converter was accepted explicitly.</summary>
    public bool? RoutingMismatchAccepted { get; private init; }

    /// <summary>The world package's file name, as the compiler read it.</summary>
    public string? WorldPackage { get; private init; }

    /// <summary>The world's map name.</summary>
    public string? WorldMapName { get; private init; }

    /// <summary>The canonical fingerprint of the world's network, as the compiler computed it.</summary>
    public string? WorldNetworkFingerprint { get; private init; }

    /// <summary>The digest of the world's OpenDRIVE, as its manifest records it.</summary>
    public string? WorldOpenDriveSha256 { get; private init; }

    /// <summary>The converter that built the world, as its manifest records it.</summary>
    public string? WorldConverter { get; private init; }

    /// <summary>The <c>time-to-teleport</c> the compiler wrote into the configuration.</summary>
    public string? TimeToTeleport { get; private init; }

    /// <summary>
    /// The <c>lanechange.duration</c> the compiler wrote into the configuration; absent from a lock
    /// compiled before the compiler fixed it, whose configuration left SUMO's instantaneous default.
    /// </summary>
    public string? LaneChangeDuration { get; private init; }

    /// <summary>Read a lock, refusing one that is not a version this reader implements or lacks a compared field.</summary>
    /// <exception cref="CoSimSessionRefusedException">
    /// The file is not JSON, is not a lock of <see cref="SupportedVersion"/>, or lacks a field the
    /// session compares; the message names every missing field.
    /// </exception>
    public static ScenarioLock Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException
                                               or UnauthorizedAccessException)
        {
            throw new CoSimSessionRefusedException(
                $"The compile lock {path} cannot be read as JSON, so nothing it would bind can be "
                + $"checked: {unreadable.Message}", unreadable);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CoSimSessionRefusedException(
                    $"The compile lock {path} is not a JSON object; it is {root.ValueKind}.");
            }

            int? version = root.TryGetProperty("lock_version", out JsonElement declared)
                           && declared.ValueKind == JsonValueKind.Number
                           && declared.TryGetInt32(out int number)
                ? number
                : null;
            if (version != SupportedVersion)
            {
                throw new CoSimSessionRefusedException(
                    $"The compile lock {path} declares lock_version "
                    + (version?.ToString(CultureInfo.InvariantCulture) ?? "none")
                    + $", and version {SupportedVersion} is the only shape this session reads. A "
                    + "field that moved silently is worse than one that is absent, so it is not read "
                    + "on a best-effort basis.");
            }

            List<string> missing = [];
            var read = new ScenarioLock
            {
                Path = System.IO.Path.GetFullPath(path),
                ScenarioId = Text(root, missing, required: false, "scenario_id") ?? string.Empty,
                Compiler = string.Join(' ', new[]
                {
                    Text(root, missing, required: false, "compiler", "name"),
                    Text(root, missing, required: false, "compiler", "version"),
                }.Where(part => part is { Length: > 0 })),
                SpecificationSha256 = Text(root, missing, required: false, "specification_sha256")
                                      ?? string.Empty,
                Config = LockedFileOf(root, missing, "config"),
                Routes = LockedFileOf(root, missing, "routes"),
                Network = LockedFileOf(root, missing, "network"),
                CatalogueDigest = Text(root, missing, required: true, "catalogue", "catalogue_digest")
                                  ?? string.Empty,
                CatalogueId = Text(root, missing, required: false, "catalogue", "catalogue_id"),
                EpochDigest = Text(root, missing, required: true, "epoch_block_sha256") ?? string.Empty,
                RoutedByTool = Text(root, missing, required: false, "traffic", "routed_by", "tool"),
                RoutedByRelease = Text(root, missing, required: false, "traffic", "routed_by", "version"),
                RoutedAgainstConverter = Text(root, missing, required: false, "traffic", "routed_by",
                                              "world_converter"),
                RoutingAgreement = Text(root, missing, required: false, "traffic", "routed_by",
                                        "release_agreement"),
                RoutingMismatchAccepted = Flag(root, "traffic", "routed_by", "mismatch_accepted"),
                WorldPackage = Text(root, missing, required: false, "world", "package"),
                WorldMapName = Text(root, missing, required: false, "world", "map_name"),
                WorldNetworkFingerprint = Text(root, missing, required: false, "world",
                                               "network_fingerprint"),
                WorldOpenDriveSha256 = Text(root, missing, required: false, "world", "opendrive_sha256"),
                WorldConverter = Text(root, missing, required: false, "world", "netconvert_version"),
                TimeToTeleport = Text(root, missing, required: false, "traffic", "processing",
                                      "time-to-teleport"),
                LaneChangeDuration = Text(root, missing, required: false, "traffic", "processing",
                                          SumoLaneChangeDuration.OptionName),
            };

            if (missing.Count > 0)
            {
                throw new CoSimSessionRefusedException(
                    $"The compile lock {path} does not record "
                    + string.Join(", ", missing)
                    + ". A session compares each of those with what it is about to run, and a lock "
                    + "that cannot be compared binds nothing. Recompile the scenario.");
            }

            return read;
        }
    }

    private static LockedFile LockedFileOf(JsonElement root, List<string> missing, string role) =>
        new(Text(root, missing, required: true, "files", role, "path") ?? string.Empty,
            Text(root, missing, required: true, "files", role, "sha256") ?? string.Empty);

    private static string? Text(JsonElement root, List<string> missing, bool required,
                                params string[] keys)
    {
        JsonElement at = root;
        foreach (string key in keys)
        {
            if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(key, out at))
            {
                at = default;
                break;
            }
        }

        if (at.ValueKind == JsonValueKind.String && at.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        if (required)
        {
            missing.Add(string.Join('.', keys));
        }

        return null;
    }

    private static bool? Flag(JsonElement root, params string[] keys)
    {
        JsonElement at = root;
        foreach (string key in keys)
        {
            if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(key, out at))
            {
                return null;
            }
        }

        return at.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
