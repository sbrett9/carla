using System.Globalization;

namespace CarlaNet.Recording;

/// <summary>
/// Formats the world-observer solar block for embedding in recorded artifacts. The block is the
/// 11-double layout streamed on the EpisodeState header (§10.14 extended header):
/// [solar_time, year, month, day, time_zone, lat, lon, elevation_deg, azimuth_deg, advancing, rate].
///
/// The first nine are sun <b>state</b> and are embedded in the imagery. The last two are sun
/// <b>policy</b> -- whether this run was set to advance its sun, and how fast -- and are not: they
/// describe how the simulation was configured, which no fielded observer could derive from its own
/// sensor, navigation solution, clock and public ephemeris, and which discloses that a frame belongs
/// to a controlled sweep. They travel in the CoT sidecar instead (<see cref="CotWriter"/>), which is
/// the truth-side artifact.
/// </summary>
public static class SolarMetadata
{
    /// <summary>Whether the streamed block is populated. The two policy doubles are required to be
    /// present even though they are not published, because a short block means the observer cache has
    /// not filled rather than that a world has no policy.</summary>
    public static bool HasData(IReadOnlyList<double> s) => s is { Count: >= 11 };

    /// PNG tEXt chunks to embed: one "carla:solar" JSON chunk. Empty when there is no solar data,
    /// so a frame is never tagged with a bogus sun.
    public static IEnumerable<(string Keyword, string Text)> PngTextChunks(IReadOnlyList<double> s)
    {
        if (HasData(s))
            yield return ("carla:solar", ToJson(s));
    }

    /// Compact JSON of the observable solar state (ASCII, safe for a PNG tEXt value). "{}" when no
    /// data. Sun policy is deliberately absent; see the type summary.
    public static string ToJson(IReadOnlyList<double> s)
    {
        if (!HasData(s)) return "{}";
        return "{"
            + $"\"solar_time\":{F(s[0])},"
            + $"\"date\":\"{(int)s[1]:D4}-{(int)s[2]:D2}-{(int)s[3]:D2}\","
            + $"\"time_zone\":{F(s[4])},"
            + $"\"lat\":{F(s[5])},\"lon\":{F(s[6])},"
            + $"\"sun_elevation_deg\":{F(s[7])},\"sun_azimuth_deg\":{F(s[8])}"
            + "}";
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
