using System.Globalization;

namespace CarlaNet.Recording;

/// <summary>
/// Formats the collection-platform pose for embedding in recorded artifacts, mirroring
/// <see cref="SolarMetadata"/>. Produces one "carla:sensor" JSON PNG tEXt chunk so a still is
/// self-describing from the image alone (the CoT-XML sidecar carries the same data as an air-track event).
/// </summary>
public static class SensorMetadata
{
    /// PNG tEXt chunks to embed: one "carla:sensor" JSON chunk. Empty when there is no pose, so a frame is
    /// never tagged with a bogus platform.
    public static IEnumerable<(string Keyword, string Text)> PngTextChunks(SensorPose? s)
    {
        if (s is not null)
            yield return ("carla:sensor", ToJson(s));
    }

    /// <summary>Compact JSON of the platform pose + intrinsics (ASCII, safe for a PNG tEXt value).</summary>
    public static string ToJson(SensorPose s) =>
        "{"
        + $"\"uid\":\"{Esc(s.Uid)}\",\"type\":\"{Esc(s.CotType)}\",\"callsign\":\"{Esc(s.Callsign)}\","
        + $"\"lat\":{F(s.Lat, "0.0000000")},\"lon\":{F(s.Lon, "0.0000000")},\"hae\":{F(s.Hae, "0.00")},"
        + $"\"align_offset_m\":{F(s.AlignOffsetM, "0.00")},"
        + $"\"az_deg\":{F(s.AzimuthDeg, "0.###")},\"el_deg\":{F(s.ElevationDeg, "0.###")},"
        + $"\"roll_deg\":{F(s.RollDeg, "0.###")},"
        + $"\"course_deg\":{F(s.CourseDeg, "0.#")},\"speed_mps\":{F(s.SpeedMps, "0.00")},"
        + "\"intrinsics\":{"
        + $"\"width\":{s.Width},\"height\":{s.Height},"
        + $"\"fx\":{F(s.Fx, "0.##")},\"fy\":{F(s.Fy, "0.##")},"
        + $"\"cx\":{F(s.Cx, "0.##")},\"cy\":{F(s.Cy, "0.##")},"
        + $"\"hfov_deg\":{F(s.HFovDeg, "0.###")},\"vfov_deg\":{F(s.VFovDeg, "0.###")},"
        + $"\"model\":\"{Esc(s.ProjectionModel)}\",\"distortion\":\"{Esc(s.Distortion)}\","
        + $"\"sensor_model\":\"{Esc(s.SensorModel)}\""
        + "}}";

    // Normalize IEEE negative zero (-0.0) to 0.0 so an exactly-zero field never serializes as "-0".
    private static string F(double v, string fmt) => (v == 0.0 ? 0.0 : v).ToString(fmt, CultureInfo.InvariantCulture);

    private static string Esc(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
