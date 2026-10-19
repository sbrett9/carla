using System.Globalization;

namespace CarlaNet.Recording;

/// <summary>
/// Formats the collection-platform pose for embedding in recorded artifacts, mirroring
/// <see cref="SolarMetadata"/>. Produces one "carla:sensor" JSON PNG tEXt chunk so a still is
/// self-describing from the image alone (the CoT-XML sidecar carries the same data as an air-track event).
/// </summary>
public static class SensorMetadata
{
    /// <summary>The format of the <c>carla:sensor</c> chunk, written in it as <c>format_version</c>.</summary>
    public const int FormatVersion = 1;

    /// PNG tEXt chunks to embed: one "carla:sensor" JSON chunk. Empty when there is no pose, so a frame is
    /// never tagged with a bogus platform.
    public static IEnumerable<(string Keyword, string Text)> PngTextChunks(SensorPose? s)
    {
        if (s is not null)
            yield return ("carla:sensor", ToJson(s));
    }

    /// <summary>
    /// Compact JSON of the platform pose and intrinsics, every string escaped as JSON requires
    /// (<see cref="PngChunkJson"/>).
    /// </summary>
    public static string ToJson(SensorPose s) => PngChunkJson.Object(json =>
    {
        PngChunkJson.Number(json, "format_version", FormatVersion.ToString(CultureInfo.InvariantCulture));
        json.WriteString("uid", s.Uid ?? "");
        json.WriteString("type", s.CotType ?? "");
        json.WriteString("callsign", s.Callsign ?? "");
        PngChunkJson.Number(json, "lat", F(s.Lat, "0.0000000"));
        PngChunkJson.Number(json, "lon", F(s.Lon, "0.0000000"));
        PngChunkJson.Number(json, "hae", F(s.Hae, "0.00"));
        PngChunkJson.Number(json, "align_offset_m", F(s.AlignOffsetM, "0.00"));
        PngChunkJson.Number(json, "az_deg", F(s.AzimuthDeg, "0.###"));
        PngChunkJson.Number(json, "el_deg", F(s.ElevationDeg, "0.###"));
        PngChunkJson.Number(json, "roll_deg", F(s.RollDeg, "0.###"));
        PngChunkJson.Number(json, "course_deg", F(s.CourseDeg, "0.#"));
        PngChunkJson.Number(json, "speed_mps", F(s.SpeedMps, "0.00"));
        json.WriteStartObject("intrinsics");
        json.WriteNumber("width", s.Width);
        json.WriteNumber("height", s.Height);
        PngChunkJson.Number(json, "fx", F(s.Fx, "0.##"));
        PngChunkJson.Number(json, "fy", F(s.Fy, "0.##"));
        PngChunkJson.Number(json, "cx", F(s.Cx, "0.##"));
        PngChunkJson.Number(json, "cy", F(s.Cy, "0.##"));
        PngChunkJson.Number(json, "hfov_deg", F(s.HFovDeg, "0.###"));
        PngChunkJson.Number(json, "vfov_deg", F(s.VFovDeg, "0.###"));
        json.WriteString("model", s.ProjectionModel ?? "");
        json.WriteString("distortion", s.Distortion ?? "");
        json.WriteString("sensor_model", s.SensorModel ?? "");
        json.WriteEndObject();
    });

    // Normalize IEEE negative zero (-0.0) to 0.0 so an exactly-zero field never serializes as "-0".
    private static string F(double v, string fmt) => (v == 0.0 ? 0.0 : v).ToString(fmt, CultureInfo.InvariantCulture);
}
