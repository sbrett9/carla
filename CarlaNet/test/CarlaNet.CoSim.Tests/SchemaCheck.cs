using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Checks a JSON value against one of the published JSON Schemas, for the tests that hold what a writer writes
/// to its schema. The Python suite validates the same files with a full validator; this is the subset of draft
/// 2020-12 the published schemas use, and it refuses a keyword it does not implement rather than pass it by.
/// </summary>
internal sealed class SchemaCheck
{
    private static readonly HashSet<string> Annotations =
        ["$schema", "$id", "title", "description", "x-unit", "x-format-version", "$defs"];

    private readonly JsonObject _root;

    /// <param name="root">The schema. It is read back from its text, as the published file is.</param>
    public SchemaCheck(JsonObject root) => _root = JsonNode.Parse(root.ToJsonString())!.AsObject();

    /// <summary>Every way <paramref name="value"/> breaks the schema, each with where; empty where it keeps it.</summary>
    public List<string> Problems(JsonNode? value)
    {
        List<string> problems = [];
        Check(_root, value, "$", problems);
        return problems;
    }

    /// <summary>Every way a JSON text breaks the schema.</summary>
    public List<string> Problems(string json) => Problems(JsonNode.Parse(json));

    private void Check(JsonObject schema, JsonNode? value, string where, List<string> problems)
    {
        foreach ((string keyword, JsonNode? argument) in schema)
        {
            switch (keyword)
            {
                case var annotation when Annotations.Contains(annotation):
                case "then" or "else":
                    break;
                case "type":
                    string[] types = argument is JsonArray list
                        ? [.. list.Select(type => type!.GetValue<string>())]
                        : [argument!.GetValue<string>()];
                    if (!types.Any(type => IsType(value, type)))
                    {
                        problems.Add($"{where}: {Describe(value)} is not {string.Join(" or ", types)}");
                        return;
                    }

                    break;
                case "const":
                    if (!JsonNode.DeepEquals(argument, value))
                    {
                        problems.Add($"{where}: {Describe(value)} is not {Describe(argument)}");
                    }

                    break;
                case "enum":
                    if (!argument!.AsArray().Any(word => JsonNode.DeepEquals(word, value)))
                    {
                        problems.Add($"{where}: {Describe(value)} is not one of {argument.ToJsonString()}");
                    }

                    break;
                case "pattern":
                    if (value is JsonValue text && text.TryGetValue(out string? s)
                        && !Regex.IsMatch(s, argument!.GetValue<string>()))
                    {
                        problems.Add($"{where}: \"{s}\" does not match {argument}");
                    }

                    break;
                case "minimum":
                    if (Number(value) is { } low && low < argument!.GetValue<double>())
                    {
                        problems.Add($"{where}: {low} is below {argument}");
                    }

                    break;
                case "maximum":
                    if (Number(value) is { } high && high > argument!.GetValue<double>())
                    {
                        problems.Add($"{where}: {high} is above {argument}");
                    }

                    break;
                case "properties":
                    if (value is JsonObject held)
                    {
                        foreach ((string name, JsonNode? property) in argument!.AsObject())
                        {
                            if (held.TryGetPropertyValue(name, out JsonNode? inner))
                            {
                                Check(property!.AsObject(), inner, $"{where}.{name}", problems);
                            }
                        }
                    }

                    break;
                case "required":
                    if (value is JsonObject present)
                    {
                        foreach (JsonNode? name in argument!.AsArray())
                        {
                            if (!present.ContainsKey(name!.GetValue<string>()))
                            {
                                problems.Add($"{where}: has no {name}");
                            }
                        }
                    }

                    break;
                case "additionalProperties":
                    if (argument!.GetValue<bool>())
                    {
                        break;
                    }

                    if (value is JsonObject closed)
                    {
                        JsonObject named = schema["properties"]?.AsObject() ?? [];
                        foreach ((string name, _) in closed)
                        {
                            if (!named.ContainsKey(name))
                            {
                                problems.Add($"{where}: {name} is not a property this schema names");
                            }
                        }
                    }

                    break;
                case "dependentRequired":
                    if (value is JsonObject dependent)
                    {
                        foreach ((string name, JsonNode? needs) in argument!.AsObject())
                        {
                            if (dependent.ContainsKey(name))
                            {
                                foreach (JsonNode? other in needs!.AsArray())
                                {
                                    if (!dependent.ContainsKey(other!.GetValue<string>()))
                                    {
                                        problems.Add($"{where}: has {name} and no {other}");
                                    }
                                }
                            }
                        }
                    }

                    break;
                case "items":
                    if (value is JsonArray items)
                    {
                        for (int index = 0; index < items.Count; index++)
                        {
                            Check(argument!.AsObject(), items[index], $"{where}[{index}]", problems);
                        }
                    }

                    break;
                case "minItems":
                    if (value is JsonArray few && few.Count < argument!.GetValue<int>())
                    {
                        problems.Add($"{where}: has {few.Count} items, fewer than {argument}");
                    }

                    break;
                case "maxItems":
                    if (value is JsonArray many && many.Count > argument!.GetValue<int>())
                    {
                        problems.Add($"{where}: has {many.Count} items, more than {argument}");
                    }

                    break;
                case "$ref":
                    string reference = argument!.GetValue<string>();
                    const string local = "#/$defs/";
                    if (!reference.StartsWith(local, StringComparison.Ordinal))
                    {
                        throw new NotSupportedException($"{where}: a reference outside the document, {reference}");
                    }

                    Check(_root["$defs"]![reference[local.Length..]]!.AsObject(), value, where, problems);
                    break;
                case "allOf":
                    foreach (JsonNode? part in argument!.AsArray())
                    {
                        Check(part!.AsObject(), value, where, problems);
                    }

                    break;
                case "anyOf":
                    if (!argument!.AsArray().Any(part => Passes(part!.AsObject(), value, where)))
                    {
                        problems.Add($"{where}: {Describe(value)} matches none of its alternatives");
                    }

                    break;
                case "oneOf":
                    int passing = argument!.AsArray().Count(part => Passes(part!.AsObject(), value, where));
                    if (passing != 1)
                    {
                        problems.Add($"{where}: {Describe(value)} matches {passing} of its alternatives, not one");
                    }

                    break;
                case "not":
                    if (Passes(argument!.AsObject(), value, where))
                    {
                        problems.Add($"{where}: {Describe(value)} matches what it must not");
                    }

                    break;
                case "if":
                    JsonObject? branch = Passes(argument!.AsObject(), value, where)
                        ? schema["then"]?.AsObject()
                        : schema["else"]?.AsObject();
                    if (branch is not null)
                    {
                        Check(branch, value, where, problems);
                    }

                    break;
                default:
                    throw new NotSupportedException($"{where}: the schema uses {keyword}, which this check does not implement");
            }
        }
    }

    private bool Passes(JsonObject schema, JsonNode? value, string where)
    {
        List<string> found = [];
        Check(schema, value, where, found);
        return found.Count == 0;
    }

    private static bool IsType(JsonNode? value, string type) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => Number(value) is not null,
        "integer" => Number(value) is { } n && Math.Floor(n) == n,
        _ => throw new NotSupportedException($"type {type}"),
    };

    private static double? Number(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static string Describe(JsonNode? value) =>
        value is null ? "null" : value.ToJsonString().Length > 80 ? value.ToJsonString()[..80] + "..." : value.ToJsonString();
}

/// <summary>Checks the rows of a CSV against the Table Schema published for it.</summary>
internal static class TableSchemaCheck
{
    /// <summary>Every way the CSV's header and rows break the Table Schema.</summary>
    public static List<string> Problems(JsonObject schema, IReadOnlyList<string> lines)
    {
        List<string> problems = [];
        JsonArray fields = schema["fields"]!.AsArray();
        string[] names = [.. fields.Select(field => field!["name"]!.GetValue<string>())];
        if (string.Join(',', names) != lines[0])
        {
            problems.Add($"the header is {lines[0]}, not the schema's columns");
            return problems;
        }

        for (int line = 1; line < lines.Count; line++)
        {
            List<string> cells = Split(lines[line]);
            if (cells.Count != names.Length)
            {
                problems.Add($"row {line}: {cells.Count} cells, not {names.Length}");
                continue;
            }

            for (int column = 0; column < names.Length; column++)
            {
                JsonObject field = fields[column]!.AsObject();
                string cell = cells[column];
                JsonObject constraints = field["constraints"]?.AsObject() ?? [];
                if (cell.Length == 0)
                {
                    if (constraints["required"]?.GetValue<bool>() is true)
                    {
                        problems.Add($"row {line}: {names[column]} is empty and required");
                    }

                    continue;
                }

                string? problem = Cell(field, constraints, cell);
                if (problem is not null)
                {
                    problems.Add($"row {line}: {names[column]} \"{cell}\" {problem}");
                }
            }
        }

        return problems;
    }

    private static string? Cell(JsonObject field, JsonObject constraints, string cell)
    {
        string type = field["type"]!.GetValue<string>();
        double? number = null;
        switch (type)
        {
            case "string":
                break;
            case "number":
                if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    return "is not a number";
                }

                number = parsed;
                break;
            case "integer":
                if (!long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                {
                    return "is not an integer";
                }

                number = whole;
                break;
            case "boolean":
                if (!field["trueValues"]!.AsArray().Concat(field["falseValues"]!.AsArray())
                        .Any(word => word!.GetValue<string>() == cell))
                {
                    return "is not one of its true or false values";
                }

                break;
            case "datetime":
                string format = field["format"]!.GetValue<string>();
                if (format != "%Y-%m-%dT%H:%M:%S.%fZ"
                    || !DateTime.TryParseExact(cell, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture,
                                               DateTimeStyles.AdjustToUniversal, out _))
                {
                    return $"is not a datetime of format {format}";
                }

                break;
            default:
                throw new NotSupportedException($"Table Schema type {type}");
        }

        if (constraints["enum"] is JsonArray words && !words.Any(word => word!.GetValue<string>() == cell))
        {
            return "is not one of " + words.ToJsonString();
        }

        if (constraints["pattern"] is JsonValue pattern && !Regex.IsMatch(cell, "^(?:" + pattern.GetValue<string>() + ")$"))
        {
            return "does not match " + pattern;
        }

        if (number is { } value && constraints["minimum"] is JsonValue minimum && value < minimum.GetValue<double>())
        {
            return "is below " + minimum;
        }

        if (number is { } top && constraints["maximum"] is JsonValue maximum && top > maximum.GetValue<double>())
        {
            return "is above " + maximum;
        }

        return null;
    }

    private static List<string> Split(string line)
    {
        List<string> cells = [];
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char c = line[index];
            if (quoted)
            {
                if (c == '"' && index + 1 < line.Length && line[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}
