using System.Globalization;
using CarlaNet.Types.Illumination;

namespace CarlaNet.Recording;

/// <summary>
/// Formats the world-observer solar block for embedding in recorded artifacts. The block is the
/// layout streamed on the EpisodeState header (§10.14 extended header):
/// [solar_time, year, month, day, time_zone, lat, lon, elevation_deg, azimuth_deg, advancing, rate,
/// corrected_elevation_deg]. The twelfth is present from a server that carries it, and written as
/// sun_corrected_elevation_deg: sun_elevation_deg is the geometric elevation, and the corrected one
/// is the elevation the frame was actually lit at.
/// </summary>
/// <remarks>
/// Beside the sun it writes the sun's illumination band (<see cref="IlluminationBands"/>), derived
/// from this block and nothing else, so the band of every capture comes from the sun the world
/// achieved on its tick and never from the time the run declared
/// (<c>06_Truth_And_Annotation.md</c> D6.23).
/// </remarks>
public static class SolarMetadata
{
    private const int GeometricElevation = 7;
    private const int CorrectedElevation = 11;

    public static bool HasData(IReadOnlyList<double> s) => s is { Count: >= 11 };

    /// <summary>
    /// The illumination band of the sun a block carries, and the elevation it was assigned from: the
    /// one the bands are stated against (<see cref="IlluminationBands.Elevation"/>) where the block
    /// carries it, and the geometric one from a server that carries only that. Null where the block
    /// carries no sun, or an elevation that is not a sun's.
    /// </summary>
    public static (IlluminationBand Band, SolarElevationKind AssignedFrom)? Band(IReadOnlyList<double> s)
    {
        if (!HasData(s)) return null;
        SolarElevationKind kind = IlluminationBands.Elevation == SolarElevationKind.RefractionCorrected
                                  && s.Count > CorrectedElevation
            ? SolarElevationKind.RefractionCorrected
            : SolarElevationKind.Geometric;
        double elevation = kind == SolarElevationKind.RefractionCorrected
            ? s[CorrectedElevation]
            : s[GeometricElevation];
        return IlluminationBands.TryOf(elevation, out IlluminationBand band) ? (band, kind) : null;
    }

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
        var band = Band(s);
        return "{"
            + $"\"solar_time\":{F(s[0])},"
            + $"\"date\":\"{(int)s[1]:D4}-{(int)s[2]:D2}-{(int)s[3]:D2}\","
            + $"\"time_zone\":{F(s[4])},"
            + $"\"lat\":{F(s[5])},\"lon\":{F(s[6])},"
            + $"\"sun_elevation_deg\":{F(s[7])},\"sun_azimuth_deg\":{F(s[8])},"
            + $"\"advancing\":{(s[9] != 0.0 ? "true" : "false")},"
            + $"\"rate\":{F(s[10])}"
            + (s.Count > 11 ? $",\"sun_corrected_elevation_deg\":{F(s[11])}" : string.Empty)
            + (band is { } b
                ? $",\"illumination_band\":\"{IlluminationBands.Name(b.Band)}\""
                  + $",\"illumination_band_elevation\":\"{SolarElevationKinds.Name(b.AssignedFrom)}\""
                : string.Empty)
            + "}";
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
