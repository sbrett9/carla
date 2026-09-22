using System.Text.Json;

namespace CarlaNet.CoSim;

/// <summary>
/// The measured vehicle catalogue: which CARLA blueprint a body is, and how big that body actually
/// is.
/// </summary>
/// <remarks>
/// <para>SUMO reports a vehicle's position at the centre of its front bumper and CARLA places an
/// actor at the body's origin. Converting one to the other needs the body's measured length and the
/// offset of its box centre from that origin, and SUMO holds neither: a vType's <c>length</c> is a
/// number a scenario author wrote, not a measurement of anything that will be drawn.</para>
///
/// <para>Read once at session start and asked on every vehicle placed, so the index is a dictionary
/// built at load rather than a scan of the document. This is the read side of the same artifact the
/// catalogue's Python reader reads, and it makes the same refusals for the same reasons.</para>
/// </remarks>
public sealed class VehicleCatalogue
{
    /// <summary>
    /// The <c>&lt;param&gt;</c> on a SUMO <c>&lt;vType&gt;</c> naming the CARLA blueprint the type
    /// is measured from. Flat and un-namespaced, the way SUMO's own devices read a parameter.
    /// </summary>
    public const string BlueprintParameter = "carla:blueprint";

    /// <summary>
    /// The schema shape this reader implements. A catalogue declaring anything else is refused
    /// rather than read on a best-effort basis: a field that moved silently is worse than one that
    /// is absent.
    /// </summary>
    public const int SupportedCatalogueVersion = 1;

    private readonly Dictionary<string, VehicleExtent> _measured = [];
    private readonly Dictionary<string, string> _failed = [];

    private VehicleCatalogue(JsonElement document)
    {
        if (!document.TryGetProperty("catalogue_version", out JsonElement version)
            || version.GetInt32() != SupportedCatalogueVersion)
        {
            throw new CoSimSessionRefusedException(
                $"The catalogue declares version {(version.ValueKind == JsonValueKind.Number ? version.GetInt32() : -1)}, "
                + $"and version {SupportedCatalogueVersion} is the only shape this reader implements.");
        }

        CatalogueId = Text(document, "catalogue_id");
        CatalogueDigest = Text(document, "catalogue_digest");
        BlueprintSetDigest = Text(document, "blueprint_set_digest");
        ContentBuildId = Text(document, "content_build_id");

        if (!document.TryGetProperty("vehicles", out JsonElement vehicles))
        {
            return;
        }

        foreach (JsonElement entry in vehicles.EnumerateArray())
        {
            string blueprintId = Text(entry, "blueprint_id");
            if (Text(entry, "measurement") != "measured")
            {
                _failed[blueprintId] = Text(entry, "measurement_note") is { Length: > 0 } note
                    ? note
                    : "measurement failed";
                continue;
            }

            JsonElement centre = entry.GetProperty("bbox_centre_m");
            _measured[blueprintId] = new VehicleExtent(
                blueprintId,
                entry.GetProperty("length_m").GetDouble(),
                entry.GetProperty("width_m").GetDouble(),
                entry.GetProperty("height_m").GetDouble(),
                (centre[0].GetDouble(), centre[1].GetDouble(), centre[2].GetDouble()));
        }
    }

    /// <summary>Which blueprint set and content build the measurements were taken against.</summary>
    public string CatalogueId { get; } = string.Empty;

    /// <summary>The catalogue's declared digest, carried into a run manifest verbatim.</summary>
    public string CatalogueDigest { get; } = string.Empty;

    /// <summary>The digest of the blueprint set the sweep saw.</summary>
    public string BlueprintSetDigest { get; } = string.Empty;

    /// <summary>The content build the measurements belong to.</summary>
    public string ContentBuildId { get; } = string.Empty;

    /// <summary>Every blueprint the catalogue holds a successful measurement for.</summary>
    public IReadOnlyCollection<string> MeasuredBlueprintIds => _measured.Keys;

    /// <summary>Read a catalogue from disk, as the blueprint sweep wrote it.</summary>
    public static VehicleCatalogue Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return new VehicleCatalogue(document.RootElement.Clone());
    }

    /// <summary>Read a catalogue from its JSON text.</summary>
    public static VehicleCatalogue Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        return new VehicleCatalogue(document.RootElement.Clone());
    }

    /// <summary>
    /// The measured body a SUMO vehicle type stands for.
    /// </summary>
    /// <param name="vehicleTypeId">The vType, for the refusal to name.</param>
    /// <param name="blueprintId">
    /// What the vType's <see cref="BlueprintParameter"/> said, or an empty string where it declared
    /// none.
    /// </param>
    /// <exception cref="UnrenderableVehicleTypeException">
    /// The type names no blueprint, or names one with no measurement.
    /// </exception>
    public VehicleExtent Resolve(string vehicleTypeId, string blueprintId)
    {
        ArgumentNullException.ThrowIfNull(vehicleTypeId);
        ArgumentNullException.ThrowIfNull(blueprintId);

        if (blueprintId.Length == 0)
        {
            throw new UnrenderableVehicleTypeException(
                vehicleTypeId, UnrenderableReason.NoBlueprint,
                $"no {BlueprintParameter} parameter, so no rendered body is named");
        }

        return _measured.TryGetValue(blueprintId, out VehicleExtent extent)
            ? extent
            : throw new UnrenderableVehicleTypeException(
                vehicleTypeId, UnrenderableReason.UnknownExtent, Why(blueprintId));
    }

    /// <summary>The same decision without the exception, for a caller that carries on.</summary>
    public bool TryResolve(string vehicleTypeId,
                           string blueprintId,
                           out VehicleExtent extent,
                           out UnrenderableReason reason)
    {
        try
        {
            extent = Resolve(vehicleTypeId, blueprintId);
            reason = default;
            return true;
        }
        catch (UnrenderableVehicleTypeException refused)
        {
            extent = default;
            reason = refused.Reason;
            return false;
        }
    }

    private string Why(string blueprintId) =>
        _failed.TryGetValue(blueprintId, out string? note)
            ? $"blueprint '{blueprintId}' was swept and its measurement failed: {note}"
            : $"blueprint '{blueprintId}' is not in catalogue '{CatalogueId}', which holds "
              + $"{_measured.Count} measured blueprints";

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
