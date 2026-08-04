using System.Globalization;
using System.Text;
using System.Xml;

namespace CarlaNet.Recording;

/// <summary>
/// Writes a Cursor-on-Target sidecar: one indented &lt;events&gt; document containing a CoT &lt;event&gt; per
/// vehicle (the same UID/type/format as the live cot_telemetry feed), pinned to the capture instant.
/// Indentation is produced by <see cref="XmlWriter"/> (Indent = true) — human-readable by construction.
/// </summary>
public static class CotWriter
{
    public static void WriteToFile(string path, DateTime capturedUtc,
        IReadOnlyList<VehicleTelemetry> recs, string affiliation = "n", double staleSeconds = 3.0,
        IReadOnlyList<double>? solar = null, SensorPose? sensor = null,
        CaptureIdentity? capture = null)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using var w = XmlWriter.Create(path, settings);

        string time = Iso(capturedUtc);
        string stale = Iso(capturedUtc.AddSeconds(staleSeconds));

        w.WriteStartDocument();
        w.WriteStartElement("events");
        w.WriteAttributeString("captured", time);
        w.WriteAttributeString("count", recs.Count.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("source", "truth");

        // Capture identity belongs on this container rather than on the individual events: <events> is
        // this file's own wrapper, whereas each <event> is standard Cursor-on-Target and is also emitted
        // verbatim over the live feed, where a strict client may reject unknown attributes. Every event
        // in a sidecar shares one tick, so recording it once here loses nothing.
        if (capture is not null)
        {
            w.WriteAttributeString("tick", capture.Tick.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("sim_time_s", F(capture.SimTimeSeconds, "0.######"));
            if (!string.IsNullOrEmpty(capture.RunId)) w.WriteAttributeString("run_id", capture.RunId);
            if (!string.IsNullOrEmpty(capture.ScenarioId)) w.WriteAttributeString("scenario_id", capture.ScenarioId);
            if (capture.Seed.HasValue)
                w.WriteAttributeString("seed", capture.Seed.Value.ToString(CultureInfo.InvariantCulture));
        }

        // Scene-level solar state (unbreakably tied to the imagery too, via the PNG tEXt chunk). Written
        // once here, before the per-vehicle events, so it is present even for a vehicle-free frame.
        if (solar is { Count: >= 11 })
        {
            w.WriteStartElement("_solar");
            w.WriteAttributeString("solar_time", F(solar[0], "0.####"));
            w.WriteAttributeString("date",
                $"{(int)solar[1]:D4}-{(int)solar[2]:D2}-{(int)solar[3]:D2}");
            w.WriteAttributeString("time_zone", F(solar[4], "0.####"));
            w.WriteAttributeString("lat", F(solar[5], "0.0000000"));
            w.WriteAttributeString("lon", F(solar[6], "0.0000000"));
            w.WriteAttributeString("sun_elevation_deg", F(solar[7], "0.###"));
            w.WriteAttributeString("sun_azimuth_deg", F(solar[8], "0.###"));
            w.WriteAttributeString("advancing", solar[9] != 0.0 ? "true" : "false");
            w.WriteAttributeString("rate", F(solar[10], "0.####"));
            w.WriteEndElement(); // _solar
        }

        // Collection platform (the airborne EO camera) as a CoT air-track event: standard <sensor> element
        // for boresight/FOV (TAK can render the field-of-view cone) + a <_carla_intrinsics> child for the
        // full pinhole intrinsics. Written before the vehicle tracks so it is present for a vehicle-free frame.
        if (sensor is not null)
        {
            w.WriteStartElement("event");
            w.WriteAttributeString("version", "2.0");
            w.WriteAttributeString("uid", sensor.Uid);
            w.WriteAttributeString("type", sensor.CotType);
            w.WriteAttributeString("how", "m-g");
            w.WriteAttributeString("time", time);
            w.WriteAttributeString("start", time);
            w.WriteAttributeString("stale", stale);

            w.WriteStartElement("point");
            w.WriteAttributeString("lat", F(sensor.Lat, "0.0000000"));
            w.WriteAttributeString("lon", F(sensor.Lon, "0.0000000"));
            w.WriteAttributeString("hae", F(sensor.Hae, "0.00"));
            w.WriteAttributeString("ce", "0.0");
            w.WriteAttributeString("le", "0.0");
            w.WriteEndElement(); // point

            w.WriteStartElement("detail");

            w.WriteStartElement("contact");
            w.WriteAttributeString("callsign", sensor.Callsign);
            w.WriteEndElement(); // contact

            w.WriteStartElement("track");
            w.WriteAttributeString("course", F(sensor.CourseDeg, "0.0"));
            w.WriteAttributeString("speed", F(sensor.SpeedMps, "0.00"));
            w.WriteEndElement(); // track

            w.WriteStartElement("sensor");
            w.WriteAttributeString("azimuth", F(sensor.AzimuthDeg, "0.###"));
            w.WriteAttributeString("elevation", F(sensor.ElevationDeg, "0.###"));
            w.WriteAttributeString("roll", F(sensor.RollDeg, "0.###"));
            w.WriteAttributeString("fov", F(sensor.HFovDeg, "0.###"));
            w.WriteAttributeString("vfov", F(sensor.VFovDeg, "0.###"));
            w.WriteAttributeString("range", "0");
            w.WriteAttributeString("type", "EO");
            w.WriteAttributeString("model", sensor.SensorModel);
            w.WriteEndElement(); // sensor

            w.WriteStartElement("_carla_intrinsics");
            w.WriteAttributeString("width", sensor.Width.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("height", sensor.Height.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fx", F(sensor.Fx, "0.##"));
            w.WriteAttributeString("fy", F(sensor.Fy, "0.##"));
            w.WriteAttributeString("cx", F(sensor.Cx, "0.##"));
            w.WriteAttributeString("cy", F(sensor.Cy, "0.##"));
            w.WriteAttributeString("hfov_deg", F(sensor.HFovDeg, "0.###"));
            w.WriteAttributeString("vfov_deg", F(sensor.VFovDeg, "0.###"));
            w.WriteAttributeString("model", sensor.ProjectionModel);
            w.WriteAttributeString("distortion", sensor.Distortion);
            w.WriteAttributeString("align_offset_m", F(sensor.AlignOffsetM, "0.00"));
            w.WriteEndElement(); // _carla_intrinsics

            w.WriteEndElement(); // detail
            w.WriteEndElement(); // event
        }

        foreach (var r in recs)
        {
            w.WriteStartElement("event");
            w.WriteAttributeString("version", "2.0");
            w.WriteAttributeString("uid", $"CARLA-TRUTH-{r.Id}");
            w.WriteAttributeString("type", $"a-{affiliation}-G-E-V");
            w.WriteAttributeString("how", "m-g");
            w.WriteAttributeString("time", time);
            w.WriteAttributeString("start", time);
            w.WriteAttributeString("stale", stale);

            w.WriteStartElement("point");
            w.WriteAttributeString("lat", F(r.Lat, "0.0000000"));
            w.WriteAttributeString("lon", F(r.Lon, "0.0000000"));
            w.WriteAttributeString("hae", F(r.Hae, "0.00"));
            w.WriteAttributeString("ce", "0.0");
            w.WriteAttributeString("le", "0.0");
            w.WriteEndElement(); // point

            w.WriteStartElement("detail");

            w.WriteStartElement("track");
            w.WriteAttributeString("course", F(r.CourseDeg, "0.0"));
            w.WriteAttributeString("speed", F(r.SpeedMps, "0.00"));
            w.WriteEndElement(); // track

            w.WriteStartElement("contact");
            w.WriteAttributeString("callsign", $"{r.BaseType}-{r.Id}");
            w.WriteEndElement(); // contact

            w.WriteStartElement("_carla");
            w.WriteAttributeString("source", "truth");
            w.WriteAttributeString("actor_id", r.Id.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("type_id", r.TypeId);
            w.WriteAttributeString("base_type", r.BaseType);
            w.WriteAttributeString("special_type", r.SpecialType);
            w.WriteAttributeString("length_m", F(r.LengthM, "0.00"));
            w.WriteAttributeString("width_m", F(r.WidthM, "0.00"));
            w.WriteAttributeString("height_m", F(r.HeightM, "0.00"));
            w.WriteAttributeString("color", r.Color);
            w.WriteAttributeString("role_name", r.RoleName);
            w.WriteAttributeString("vx", F(r.Vx, "0.00"));
            w.WriteAttributeString("vy", F(r.Vy, "0.00"));
            w.WriteAttributeString("vz", F(r.Vz, "0.00"));
            w.WriteEndElement(); // _carla

            w.WriteEndElement(); // detail
            w.WriteEndElement(); // event
        }

        w.WriteEndElement(); // events
        w.WriteEndDocument();
    }

    // CoT timestamp: ISO-8601 UTC, millisecond precision, trailing 'Z'.
    private static string Iso(DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";

    // Normalize IEEE negative zero (-0.0) to 0.0 so an exactly-zero field never serializes as "-0".
    private static string F(double v, string fmt) => (v == 0.0 ? 0.0 : v).ToString(fmt, CultureInfo.InvariantCulture);
}
