// Helpers that mirror pugi::xml_attribute's permissive `as_*()` accessors:
// missing attribute -> sensible default (0 for numbers, empty string for text,
// false for bool). Centralised here so individual parsers stay terse.
using System.Globalization;
using System.Xml.Linq;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class XmlExt
{
    public static double AsDouble(XAttribute? attr, double @default = 0.0)
    {
        if (attr == null) return @default;
        return double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : @default;
    }

    public static int AsInt(XAttribute? attr, int @default = 0)
    {
        if (attr == null) return @default;
        return int.TryParse(attr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : @default;
    }

    public static uint AsUInt(XAttribute? attr, uint @default = 0u)
    {
        if (attr == null) return @default;
        return uint.TryParse(attr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : @default;
    }

    public static bool AsBool(XAttribute? attr, bool @default = false)
    {
        if (attr == null) return @default;
        var v = attr.Value;
        return v == "1" || string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
    }

    public static string AsString(XAttribute? attr, string @default = "") =>
        attr?.Value ?? @default;
}
