using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace CarlaNet.Types.Provenance;

/// <summary>
/// What made a file: the tool and its release version, the carlanet version, the server's build
/// identity where a server was used, the SUMO release where SUMO ran, and when the file was written.
/// </summary>
/// <remarks>
/// <para>One shape in every file our tools write, so a single file -- one still, one sidecar, one run
/// record -- can be traced to the major.minor release of the distribution that made it. In JSON it is
/// an object, <c>producer</c> in a file whose keys are snake_case:</para>
/// <code>
/// {"tool": "carlacontrol.CaptureSession", "tool_version": "0.10.0+g1a2b3c4d5",
///  "carlanet": "0.10.0+g1a2b3c4d5", "server": {...} | null, "sumo": "1.27.0" | null,
///  "written_utc": "2026-10-07T12:00:00.000Z"}
/// </code>
/// <para>In XML it is a <c>&lt;_producer&gt;</c> element with those as attributes, an absent one left
/// out, and the server as a <c>&lt;_server&gt;</c> child (<see cref="ServerBuildIdentity"/>).</para>
///
/// <para>A file that two runs of one input must write byte for byte -- a compiled supervision plan --
/// carries the record without its time (<see cref="WrittenUtc"/> null), and the file that binds it by
/// digest carries the time.</para>
/// </remarks>
/// <param name="Tool">The component that wrote the file: <c>carlacontrol.CaptureSession</c>, or the
/// program's name where no component declared itself (<see cref="Producer.DeclareTool"/>).</param>
/// <param name="ToolVersion">The release version of the package the tool comes from; null where it did
/// not say.</param>
/// <param name="CarlaNet">The carlanet release version: the CarlaNet assemblies'
/// (<see cref="Producer.CarlaNetVersion"/>).</param>
/// <param name="Server">The server's build identity, where a server was used; null where none was.</param>
/// <param name="Sumo">The SUMO release, where SUMO ran; null where it did not.</param>
/// <param name="WrittenUtc">When the file was written; null for a file written byte for byte alike.</param>
[JsonConverter(typeof(ProducerRecordJsonConverter))]
public sealed record ProducerRecord(string Tool, string? ToolVersion, string CarlaNet,
                                    ServerBuildIdentity? Server, string? Sumo, DateTime? WrittenUtc)
{
    private static readonly JsonWriterOptions CompactOptions = new()
    {
        // A version's local part, 0.10.0+g1a2b3c4d5, is written as it reads rather than escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The record without its time, for a file written byte for byte alike.</summary>
    public ProducerRecord Untimed() => this with { WrittenUtc = null };

    /// <summary>Write the record as a JSON object.</summary>
    public void WriteJson(Utf8JsonWriter json)
    {
        ArgumentNullException.ThrowIfNull(json);
        json.WriteStartObject();
        json.WriteString("tool", Tool);
        if (ToolVersion is null)
        {
            json.WriteNull("tool_version");
        }
        else
        {
            json.WriteString("tool_version", ToolVersion);
        }

        json.WriteString("carlanet", CarlaNet);
        json.WritePropertyName("server");
        if (Server is null)
        {
            json.WriteNullValue();
        }
        else
        {
            Server.WriteJson(json);
        }

        if (Sumo is null)
        {
            json.WriteNull("sumo");
        }
        else
        {
            json.WriteString("sumo", Sumo);
        }

        if (WrittenUtc is { } written)
        {
            json.WriteString("written_utc", Iso(written));
        }

        json.WriteEndObject();
    }

    /// <summary>The record as a compact JSON object, ASCII where its values are.</summary>
    public string ToJson() => Compact(WriteJson);

    /// <summary>Write the record as a <c>&lt;_producer&gt;</c> element.</summary>
    public void WriteXml(XmlWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartElement("_producer");
        writer.WriteAttributeString("tool", Tool);
        if (ToolVersion is not null)
        {
            writer.WriteAttributeString("tool_version", ToolVersion);
        }

        writer.WriteAttributeString("carlanet", CarlaNet);
        if (Sumo is not null)
        {
            writer.WriteAttributeString("sumo", Sumo);
        }

        if (WrittenUtc is { } written)
        {
            writer.WriteAttributeString("written_utc", Iso(written));
        }

        Server?.WriteXml(writer);
        writer.WriteEndElement();
    }

    /// <summary>Read a record written by <see cref="WriteJson"/>.</summary>
    /// <exception cref="JsonException">It is not one.</exception>
    public static ProducerRecord ReadJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"a producer record is a JSON object, not {element.ValueKind}");
        }

        string? Text(JsonElement from, string name) =>
            from.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        ServerBuildIdentity? server = null;
        if (element.TryGetProperty("server", out JsonElement held) && held.ValueKind == JsonValueKind.Object)
        {
            bool available = held.TryGetProperty("available", out JsonElement flag)
                             && flag.ValueKind == JsonValueKind.True;
            server = new ServerBuildIdentity
            {
                Available = available,
                Release = Text(held, "release") ?? ServerBuildIdentity.Unknown,
                WorldInterface = Text(held, "world_interface") ?? ServerBuildIdentity.Unknown,
                Build = Text(held, "build") ?? ServerBuildIdentity.Unknown,
                Configuration = Text(held, "configuration") ?? ServerBuildIdentity.Unknown,
                CarlaCommit = Text(held, "carla_commit") ?? ServerBuildIdentity.Unknown,
                ContentCommit = Text(held, "content_commit") ?? ServerBuildIdentity.Unknown,
                EngineCommit = Text(held, "engine_commit") ?? ServerBuildIdentity.Unknown,
                CommitsFrom = Text(held, "commits_from") ?? ServerBuildIdentity.Unknown,
                Reason = available ? null : Text(held, "reason"),
            };
        }

        DateTime? written = Text(element, "written_utc") is { } stamp
                            && DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
                                                 DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                                 out DateTime parsed)
            ? parsed
            : null;
        return new ProducerRecord(Text(element, "tool") ?? ServerBuildIdentity.Unknown,
                                  Text(element, "tool_version"),
                                  Text(element, "carlanet") ?? ServerBuildIdentity.Unknown,
                                  server, Text(element, "sumo"), written);
    }

    /// <summary>ISO 8601 UTC to the millisecond, with a trailing <c>Z</c>.</summary>
    public static string Iso(DateTime instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";

    /// <summary>What a JSON writer writes, as a compact string.</summary>
    internal static string Compact(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, CompactOptions))
        {
            write(json);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Reads and writes a <see cref="ProducerRecord"/> in its own shape wherever a serializer meets one.</summary>
public sealed class ProducerRecordJsonConverter : JsonConverter<ProducerRecord>
{
    public override ProducerRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return ProducerRecord.ReadJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ProducerRecord value, JsonSerializerOptions options)
        => value.WriteJson(writer);
}
