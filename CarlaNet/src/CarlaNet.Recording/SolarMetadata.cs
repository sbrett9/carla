using System.Globalization;

namespace CarlaNet.Recording;

/// <summary>
/// Formats the world-observer solar block for embedding in recorded artifacts. The block is the
/// 11-double layout streamed on the EpisodeState header (§10.14 extended header):
/// [solar_time, year, month, day, time_zone, lat, lon, elevation_deg, azimuth_deg, advancing, rate].
/// </summary>
public static class SolarMetadata
{
    public static bool HasData(IReadOnlyList<double> s) => s is { Count: >= 11 };

    /// PNG tEXt chunks to embed: one "carla:solar" JSON chunk. Empty when there is no solar data,
    /// so a frame is never tagged with a bogus sun.
    public static IEnumerable<(string Keyword, string Text)> PngTextChunks(IReadOnlyList<double> s)
    {
        if (HasData(s))
            yield return ("carla:solar", ToJson(s));
    }

    /// Compact JSON of the solar state (ASCII, safe for a PNG tEXt value). "{}" when no data.
    public static string ToJson(IReadOnlyList<double> s)
    {
        if (!HasData(s)) return "{}";
        return "{"
            + $"\"solar_time\":{F(s[0])},"
            + $"\"date\":\"{(int)s[1]:D4}-{(int)s[2]:D2}-{(int)s[3]:D2}\","
            + $"\"time_zone\":{F(s[4])},"
            + $"\"lat\":{F(s[5])},\"lon\":{F(s[6])},"
            + $"\"sun_elevation_deg\":{F(s[7])},\"sun_azimuth_deg\":{F(s[8])},"
            + $"\"advancing\":{(s[9] != 0.0 ? "true" : "false")},"
            + $"\"rate\":{F(s[10])}"
            + "}";
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
