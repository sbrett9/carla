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
        IReadOnlyList<VehicleTelemetry> recs, string affiliation = "n", double staleSeconds = 3.0)
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

    private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);
}
