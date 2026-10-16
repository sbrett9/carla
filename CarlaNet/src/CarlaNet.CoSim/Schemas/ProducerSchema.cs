using System.Text.Json.Nodes;
using CarlaNet.Types.Provenance;

namespace CarlaNet.CoSim.Schemas;

/// <summary>
/// The record of what made a file (<see cref="ProducerRecord"/>) as a JSON Schema definition, and the
/// server build identity inside it (<see cref="ServerBuildIdentity"/>), as the C# writers write them.
/// </summary>
internal static class ProducerSchema
{
    /// <summary>The definition name of the record, under <c>$defs</c>.</summary>
    public const string ProducerDefinition = "producer";

    /// <summary>The definition name of the server's build identity, under <c>$defs</c>.</summary>
    public const string ServerDefinition = "server";

    /// <summary>An instant in UTC to the millisecond, with a trailing <c>Z</c>, as every record writes one.</summary>
    public const string UtcMillisecondsPattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$";

    /// <summary>Add the record's and the server's definitions to <paramref name="definitions"/>.</summary>
    public static void Define(JsonObject definitions)
    {
        definitions[ProducerDefinition] = SchemaJson.Object(
            "What made the file: the tool and its release, the carlanet release, the CARLA server's build "
            + "identity where a server was used, the SUMO release where SUMO ran, and when the file was written.",
            new JsonField("tool", SchemaJson.Text(
                "The component that wrote the file, such as carlacontrol.CaptureSession, or the program's name "
                + $"where no component declared itself; {ServerBuildIdentity.Unknown} where neither is known.")),
            new JsonField("tool_version", SchemaJson.OrNull(SchemaJson.Text(
                "The release version of the package the tool comes from; null where the tool did not say."))),
            new JsonField("carlanet", SchemaJson.Text(
                "The carlanet release that wrote the file: 0.10.0 for a tagged release, 0.10.0+g<commit> for "
                + "any other build.")),
            new JsonField("server", SchemaJson.OrNull(SchemaJson.Reference(ServerDefinition,
                "The CARLA server's build identity, where a server was used; null where none was."))),
            new JsonField("sumo", SchemaJson.OrNull(SchemaJson.Text(
                "The SUMO release, such as 1.27.0, where SUMO ran; null where it did not."))),
            new JsonField("written_utc", SchemaJson.Text(
                "When the file was written, in UTC to the millisecond.", UtcMillisecondsPattern),
                Required: false));

        string unknown = ServerBuildIdentity.Unknown;
        JsonObject answered = SchemaJson.Object(
            "A server that answered the build identity call. Each value is the server's own, "
            + $"{unknown} where it cannot know it.",
            new JsonField("available", SchemaJson.Constant("The server answered.", true)),
            new JsonField("release", SchemaJson.Text("The CARLA release the server was compiled with, such as 0.10.0.")),
            new JsonField("world_interface", SchemaJson.Text(
                "The world interface version the server declares, major.minor, such as 1.0.")),
            new JsonField("build", SchemaJson.Text("package for a cooked server, editor for one run from the editor.")),
            new JsonField("configuration", SchemaJson.Text("The build configuration, such as Development or Shipping.")),
            new JsonField("carla_commit", SchemaJson.Text("The CARLA commit the server was built from.")),
            new JsonField("content_commit", SchemaJson.Text("The content commit its package was cooked from.")),
            new JsonField("engine_commit", SchemaJson.Text("The Unreal Engine commit its package was built with.")),
            new JsonField("commits_from", SchemaJson.Text(
                "Where the commits came from: version_file (the package's VERSION file), compiled (the CARLA "
                + "commit compiled into the server) or none.")));
        JsonObject notAnswered = SchemaJson.Object(
            "A server built before the build identity call, with what its other calls said.",
            new JsonField("available", SchemaJson.Constant("The server did not answer the call.", false)),
            new JsonField("release", SchemaJson.Text($"The release its version call reported, or {unknown}.")),
            new JsonField("world_interface", SchemaJson.Text(
                $"The world interface version it reported, or {unknown}.")),
            new JsonField("reason", SchemaJson.Text("Why the identity is not available.")));
        definitions[ServerDefinition] = new JsonObject
        {
            ["description"] = "What the CARLA server says it was built from.",
            ["oneOf"] = new JsonArray(answered, notAnswered),
        };
    }

    /// <summary>The record as a property: required, or not, where a file written before it went without.</summary>
    public static JsonField Field(string name, string description, bool required) =>
        new(name, SchemaJson.Reference(ProducerDefinition, description), required);
}
