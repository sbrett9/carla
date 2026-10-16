using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CarlaNet.CoSim.Schemas;

/// <summary>One property of a JSON object schema: its name, its schema, and whether every object has it.</summary>
/// <param name="Name">The property's name, as the writer writes it.</param>
/// <param name="Schema">What its value may be.</param>
/// <param name="Required">Whether every object the writer writes carries it, null or not.</param>
internal sealed record JsonField(string Name, JsonObject Schema, bool Required = true);

/// <summary>
/// The pieces every published JSON Schema here is built of, and the one way they are written, so two
/// builds of one schema are byte for byte alike.
/// </summary>
/// <remarks>
/// Each schema is draft 2020-12. A value's unit is in its description and in <c>x-unit</c>, an annotation
/// validators ignore, as the run configuration's schema carries its own <c>x-</c> annotations. Every object
/// the writers compose is closed (<c>additionalProperties: false</c>): a property this schema does not name
/// is not one of this format version's.
/// </remarks>
internal static class SchemaJson
{
    /// <summary>The JSON Schema dialect every schema here is written in.</summary>
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    private static readonly JsonSerializerOptions TextOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        // A pattern's '+' and a description's apostrophe are written as they read.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A schema document as published: indented by two spaces, LF line endings, a final line break.</summary>
    public static string Write(JsonNode document) => document.ToJsonString(TextOptions) + "\n";

    /// <summary>A string, matching <paramref name="pattern"/> where one is given.</summary>
    public static JsonObject Text(string description, string? pattern = null, string? unit = null)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (pattern is not null)
        {
            schema["pattern"] = pattern;
        }

        return Described(schema, description, unit);
    }

    /// <summary>A number, within the bounds where they are given.</summary>
    public static JsonObject Number(string description, string? unit = null, double? minimum = null,
                                    double? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "number" };
        if (minimum is { } low)
        {
            schema["minimum"] = low;
        }

        if (maximum is { } high)
        {
            schema["maximum"] = high;
        }

        return Described(schema, description, unit);
    }

    /// <summary>An integer, within the bounds where they are given.</summary>
    public static JsonObject Integer(string description, string? unit = null, long? minimum = null,
                                     long? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "integer" };
        if (minimum is { } low)
        {
            schema["minimum"] = low;
        }

        if (maximum is { } high)
        {
            schema["maximum"] = high;
        }

        return Described(schema, description, unit);
    }

    /// <summary>True or false.</summary>
    public static JsonObject Boolean(string description) =>
        Described(new JsonObject { ["type"] = "boolean" }, description, null);

    /// <summary>One of a fixed list of words, in the code's own order.</summary>
    public static JsonObject Words(string description, IEnumerable<string> words) =>
        Described(new JsonObject { ["type"] = "string", ["enum"] = Array(words) }, description, null);

    /// <summary>Exactly one value.</summary>
    public static JsonObject Constant(string description, JsonNode? value) =>
        Described(new JsonObject { ["const"] = value }, description, null);

    /// <summary>A list, each item as <paramref name="items"/> says.</summary>
    public static JsonObject List(string description, JsonObject items, int? length = null)
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = items };
        if (length is { } count)
        {
            schema["minItems"] = count;
            schema["maxItems"] = count;
        }

        return Described(schema, description, null);
    }

    /// <summary>An object of exactly these properties, each written in this order.</summary>
    public static JsonObject Object(string description, params JsonField[] fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (JsonField field in fields)
        {
            properties[field.Name] = field.Schema;
            if (field.Required)
            {
                required.Add(field.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object" };
        Described(schema, description, null);
        schema["properties"] = properties;
        schema["required"] = required;
        schema["additionalProperties"] = false;
        return schema;
    }

    /// <summary>Any JSON object, its properties another file's or the caller's to define.</summary>
    public static JsonObject AnyObject(string description) =>
        Described(new JsonObject { ["type"] = "object" }, description, null);

    /// <summary>The definition <paramref name="name"/> of the same document.</summary>
    public static JsonObject Reference(string name, string? description = null)
    {
        var schema = new JsonObject { ["$ref"] = "#/$defs/" + name };
        return description is null ? schema : Described(schema, description, null);
    }

    /// <summary>
    /// <paramref name="schema"/>, or null: a type list gains <c>null</c>, a word list gains it too, and
    /// anything else is offered beside <c>{"type": "null"}</c>.
    /// </summary>
    public static JsonObject OrNull(JsonObject schema)
    {
        if (schema["type"] is JsonValue type && type.TryGetValue(out string? single) && !schema.ContainsKey("$ref"))
        {
            schema["type"] = new JsonArray(single, "null");
            if (schema["enum"] is JsonArray words)
            {
                words.Add(null);
            }

            return schema;
        }

        string? description = schema["description"]?.GetValue<string>();
        schema.Remove("description");
        var either = new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
        return description is null ? either : Described(either, description, null);
    }

    /// <summary>A JSON array of strings.</summary>
    public static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)value)]);

    /// <summary>A published schema's head: dialect, identity, title, description and what it describes.</summary>
    public static JsonObject Document(string id, string title, string description)
    {
        return new JsonObject
        {
            ["$schema"] = Dialect,
            ["$id"] = id,
            ["title"] = title,
            ["description"] = description,
        };
    }

    private static JsonObject Described(JsonObject schema, string description, string? unit)
    {
        schema["description"] = description;
        if (unit is not null)
        {
            schema["x-unit"] = unit;
        }

        return schema;
    }
}
