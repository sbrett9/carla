using System.Text.Json;
using System.Xml;

namespace CarlaNet.Types.Provenance;

/// <summary>
/// What a CARLA server says it was built from, as the <c>get_build_identity</c> call answers it, or as
/// much of that as a server built before the call can say.
/// </summary>
/// <remarks>
/// <para>The server answers a map of strings, each value <c>unknown</c> where the server cannot know it
/// and never a guess: the release version it was compiled with, the world interface version it
/// declares, whether it runs from a package or the editor, its build configuration, and the CARLA,
/// content and Unreal Engine commits. A package reads the three commits from the <c>VERSION</c> file it
/// carries (<c>commits_from</c> <c>version_file</c>); an editor run has no such file, and reports the
/// CARLA commit compiled into it (<c>compiled</c>) and the other two as unknown.</para>
///
/// <para>A server built before the call answers that it has no such function. Its identity is then
/// recorded as not available (<see cref="Available"/> false) with the reason, and with what the calls it
/// has still say: its release from <c>version</c> and its world interface from
/// <c>get_world_interface_version</c>.</para>
/// </remarks>
public sealed record ServerBuildIdentity
{
    /// <summary>What a value is where the server cannot know it, or did not say.</summary>
    public const string Unknown = "unknown";

    /// <summary>Whether the server answered <c>get_build_identity</c>.</summary>
    public bool Available { get; init; }

    /// <summary>The release version the server was compiled with: <c>CARLA_VERSION</c>.</summary>
    public string Release { get; init; } = Unknown;

    /// <summary>The world interface version it declares, <c>Major.Minor</c>.</summary>
    public string WorldInterface { get; init; } = Unknown;

    /// <summary><c>package</c> for a cooked server, <c>editor</c> for one run from the editor.</summary>
    public string Build { get; init; } = Unknown;

    /// <summary>The build configuration: <c>Development</c>, <c>Shipping</c> and so on.</summary>
    public string Configuration { get; init; } = Unknown;

    /// <summary>The CARLA commit the server was built from.</summary>
    public string CarlaCommit { get; init; } = Unknown;

    /// <summary>The content commit its package was cooked from.</summary>
    public string ContentCommit { get; init; } = Unknown;

    /// <summary>The Unreal Engine commit its package was built with.</summary>
    public string EngineCommit { get; init; } = Unknown;

    /// <summary>
    /// Where the commits came from: <c>version_file</c>, the package's <c>VERSION</c>; <c>compiled</c>,
    /// the CARLA commit compiled into the server; or <c>none</c>.
    /// </summary>
    public string CommitsFrom { get; init; } = Unknown;

    /// <summary>Why the identity is not available, where it is not.</summary>
    public string? Reason { get; init; }

    /// <summary>The identity a server's answer to <c>get_build_identity</c> gives.</summary>
    public static ServerBuildIdentity FromAnswer(IReadOnlyDictionary<string, string> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        string Value(string key) => answer.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : Unknown;
        return new ServerBuildIdentity
        {
            Available = true,
            Release = Value("release"),
            WorldInterface = Value("world_interface"),
            Build = Value("build"),
            Configuration = Value("configuration"),
            CarlaCommit = Value("carla_commit"),
            ContentCommit = Value("content_commit"),
            EngineCommit = Value("engine_commit"),
            CommitsFrom = Value("commits_from"),
        };
    }

    /// <summary>
    /// The identity of a server that did not answer <c>get_build_identity</c>, with what its other calls
    /// said: its release and world interface, each <see cref="Unknown"/> where that call failed too.
    /// </summary>
    public static ServerBuildIdentity NotAnswered(string reason, string? release = null, string? worldInterface = null)
        => new()
        {
            Available = false,
            Release = string.IsNullOrWhiteSpace(release) ? Unknown : release,
            WorldInterface = string.IsNullOrWhiteSpace(worldInterface) ? Unknown : worldInterface,
            Reason = reason,
        };

    /// <summary>Write the identity as a JSON object, as every JSON record of what made a file holds it.</summary>
    public void WriteJson(Utf8JsonWriter json)
    {
        ArgumentNullException.ThrowIfNull(json);
        json.WriteStartObject();
        json.WriteBoolean("available", Available);
        json.WriteString("release", Release);
        json.WriteString("world_interface", WorldInterface);
        if (Available)
        {
            json.WriteString("build", Build);
            json.WriteString("configuration", Configuration);
            json.WriteString("carla_commit", CarlaCommit);
            json.WriteString("content_commit", ContentCommit);
            json.WriteString("engine_commit", EngineCommit);
            json.WriteString("commits_from", CommitsFrom);
        }
        else
        {
            json.WriteString("reason", Reason ?? Unknown);
        }

        json.WriteEndObject();
    }

    /// <summary>The identity as a compact JSON object.</summary>
    public string ToJson() => ProducerRecord.Compact(WriteJson);

    /// <summary>Write the identity as a <c>&lt;_server&gt;</c> element, as an XML record of what made a file holds it.</summary>
    public void WriteXml(XmlWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartElement("_server");
        writer.WriteAttributeString("available", Available ? "true" : "false");
        writer.WriteAttributeString("release", Release);
        writer.WriteAttributeString("world_interface", WorldInterface);
        if (Available)
        {
            writer.WriteAttributeString("build", Build);
            writer.WriteAttributeString("configuration", Configuration);
            writer.WriteAttributeString("carla_commit", CarlaCommit);
            writer.WriteAttributeString("content_commit", ContentCommit);
            writer.WriteAttributeString("engine_commit", EngineCommit);
            writer.WriteAttributeString("commits_from", CommitsFrom);
        }
        else
        {
            writer.WriteAttributeString("reason", Reason ?? Unknown);
        }

        writer.WriteEndElement();
    }
}
