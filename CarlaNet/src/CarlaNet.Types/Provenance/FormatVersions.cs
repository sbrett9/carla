using System.Globalization;
using System.Text.Json;

namespace CarlaNet.Types.Provenance;

/// <summary>
/// The rule every reader of our files holds a file's format version to: a version it knows is read, a
/// newer one is refused by name, and a file written before it carried one is version 1.
/// </summary>
/// <remarks>
/// A format version is an integer of its own file, independent of the release version: it changes when
/// the file's shape does, and only then. A reader refuses one newer than it knows rather than reading
/// it in part, because a field that moved silently is worse than one that is absent; the refusal names
/// the file, the version it declares and the newest this reader supports, so whoever meets it knows
/// what to upgrade.
/// </remarks>
public static class FormatVersions
{
    /// <summary>The version of a file written before its kind carried one.</summary>
    public const int Legacy = 1;

    /// <summary>
    /// Why a file that declares <paramref name="declared"/> in <paramref name="field"/> cannot be read by
    /// a reader that supports up to <paramref name="supported"/>; null where it can. An absent version
    /// is <see cref="Legacy"/>.
    /// </summary>
    public static string? Refusal(string file, string field, int? declared, int supported)
    {
        int version = declared ?? Legacy;
        if (version < 1)
        {
            return $"{file} declares {field} {version.ToString(CultureInfo.InvariantCulture)}, which is "
                   + "not a format version: they start at 1.";
        }

        if (version > supported)
        {
            return $"{file} declares {field} {version.ToString(CultureInfo.InvariantCulture)}, and this "
                   + $"reader supports {field} {supported.ToString(CultureInfo.InvariantCulture)} and earlier. "
                   + "It was written by a newer release; read it with that release's tools.";
        }

        return null;
    }

    /// <summary>
    /// The integer a JSON object declares in <paramref name="field"/>, null where it declares none, or a
    /// refusal where it declares something that is not an integer.
    /// </summary>
    public static int? Declared(JsonElement root, string file, string field, out string? refusal)
    {
        refusal = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(field, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        refusal = $"{file} declares {field} {value.GetRawText()}, which is not an integer format version.";
        return null;
    }

    /// <summary>The integer an XML attribute declares, null where it is absent, or a refusal where it is not one.</summary>
    public static int? Declared(string? attribute, string file, string field, out string? refusal)
    {
        refusal = null;
        if (attribute is null)
        {
            return null;
        }

        if (int.TryParse(attribute, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return number;
        }

        refusal = $"{file} declares {field} \"{attribute}\", which is not an integer format version.";
        return null;
    }
}
