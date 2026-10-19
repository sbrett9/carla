using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim;

/// <summary>
/// One reading of a supervision plan: the field readers every row's reader takes its fields with, and
/// the problems found on the way, so a plan that cannot be bound is refused once, naming each.
/// </summary>
/// <remarks>
/// <para>A key the compiler always writes must be there, null where it writes null: the compiler
/// writes every key of a row, and a consumer must tell an absent declaration from a zero one
/// (<c>06_Truth_And_Annotation.md</c> D6.4), so a key that has gone missing is a plan of another
/// shape, not an empty field. Only the vocabulary's optional declarations, which an author may leave
/// out, are read as optional.</para>
///
/// <para>A row read from a plan with problems is half-built and never leaves the reader: the plan is
/// refused instead.</para>
/// </remarks>
internal sealed class SupervisionPlanReading
{
    private const int ProblemsNamed = 12;

    private readonly List<string> _problems = [];

    public SupervisionPlanReading(string path)
    {
        Path = path;
    }

    /// <summary>The plan being read.</summary>
    public string Path { get; }

    /// <summary>Whether anything read so far is a problem.</summary>
    public bool Failed => _problems.Count > 0;

    /// <summary>Record a problem at a place in the plan, in a sentence <paramref name="where"/> begins.</summary>
    public void Problem(string where, string what) => _problems.Add($"{where} {what}");

    /// <summary>The refusal naming every problem found, the first dozen in full.</summary>
    public CoSimSessionRefusedException Refusal()
    {
        IEnumerable<string> named = _problems.Take(ProblemsNamed)
            .Select((problem, index) => $"({index + 1}) {problem}");
        string more = _problems.Count > ProblemsNamed
            ? $"; and {_problems.Count - ProblemsNamed} more"
            : string.Empty;
        return new CoSimSessionRefusedException(
            $"The supervision plan {Path} cannot be bound: " + string.Join("; ", named) + more
            + ". The scenario compiler checks each of these before it writes a plan, so a plan that "
            + "fails one was changed after it was compiled or written by something else, and none of "
            + "its rows can be trusted to be the ones its author declared. Recompile the scenario.");
    }

    // -- fields ----------------------------------------------------------------------------------------

    /// <summary>A required non-empty string.</summary>
    public string Text(JsonElement row, string key, string where)
    {
        if (!Field(row, key, where, required: true, out JsonElement value))
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes a non-empty string");
        return string.Empty;
    }

    /// <summary>A string or null; the key must be there unless <paramref name="required"/> is false.</summary>
    public string? NullableText(JsonElement row, string key, string where, bool required = true)
    {
        if (!Field(row, key, where, required, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes a non-empty string or null");
        return null;
    }

    /// <summary>A required finite number.</summary>
    public double Number(JsonElement row, string key, string where) =>
        Field(row, key, where, required: true, out JsonElement value) ? NumberOf(value, key, where) ?? 0.0 : 0.0;

    /// <summary>A finite number or null; the key must be there.</summary>
    public double? NullableNumber(JsonElement row, string key, string where)
    {
        if (!Field(row, key, where, required: true, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return NumberOf(value, key, where);
    }

    /// <summary>A required non-negative integer.</summary>
    public int Integer(JsonElement row, string key, string where)
    {
        if (!Field(row, key, where, required: true, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= 0)
        {
            return number;
        }

        Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes a non-negative integer");
        return 0;
    }

    /// <summary>A list of non-empty strings; absent is empty where it is not <paramref name="required"/>.</summary>
    public ImmutableArray<string> Texts(JsonElement row, string key, string where, bool required = true)
    {
        if (!Field(row, key, where, required, out JsonElement value))
        {
            return [];
        }

        return TextsOf(value, key, where) ?? [];
    }

    /// <summary>A list of non-empty strings, or null; the key must be there.</summary>
    public ImmutableArray<string>? NullableTexts(JsonElement row, string key, string where)
    {
        if (!Field(row, key, where, required: true, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return TextsOf(value, key, where);
    }

    /// <summary>
    /// A row's <c>parameters</c>: each key with the value the author gave it, carried as the plan
    /// writes it. The compiler checked each against its label's declaration (07 check 56).
    /// </summary>
    public ImmutableSortedDictionary<string, JsonElement> Values(JsonElement row, string key, string where)
    {
        if (!Field(row, key, where, required: true, out JsonElement value))
        {
            return ImmutableSortedDictionary<string, JsonElement>.Empty.WithComparers(StringComparer.Ordinal);
        }

        ImmutableSortedDictionary<string, JsonElement>.Builder values =
            ImmutableSortedDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
        {
            Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes an object");
            return values.ToImmutable();
        }

        foreach (JsonProperty parameter in value.EnumerateObject())
        {
            values[parameter.Name] = parameter.Value.Clone();
        }

        return values.ToImmutable();
    }

    /// <summary>
    /// A map of named declarations, each an object read by <paramref name="read"/>; absent is empty
    /// where it is not <paramref name="required"/>.
    /// </summary>
    public ImmutableSortedDictionary<string, T> Map<T>(JsonElement row, string key, string where,
                                                       Func<JsonElement, SupervisionPlanReading, string, T> read,
                                                       bool required = true)
    {
        ImmutableSortedDictionary<string, T>.Builder map =
            ImmutableSortedDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
        if (!Field(row, key, where, required, out JsonElement value))
        {
            return map.ToImmutable();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes an object");
            return map.ToImmutable();
        }

        foreach (JsonProperty entry in value.EnumerateObject())
        {
            string at = $"{where}.{key}.{entry.Name}";
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                Problem(at, $"is {Shown(entry.Value)}, where the compiler writes an object");
                continue;
            }

            map[entry.Name] = read(entry.Value, this, at);
        }

        return map.ToImmutable();
    }

    /// <summary>
    /// A list of rows, each an object read by <paramref name="read"/>; absent is empty where it is not
    /// <paramref name="required"/>.
    /// </summary>
    public ImmutableArray<T> Rows<T>(JsonElement row, string key, string where,
                                     Func<JsonElement, SupervisionPlanReading, string, T> read,
                                     bool required = true)
    {
        if (!Field(row, key, where, required, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes a list");
            return [];
        }

        ImmutableArray<T>.Builder rows = ImmutableArray.CreateBuilder<T>(value.GetArrayLength());
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string at = $"{where}.{key}[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                Problem(at, $"is {Shown(item)}, where the compiler writes an object");
                continue;
            }

            rows.Add(read(item, this, at));
        }

        return rows.DrainToImmutable();
    }

    /// <summary>A required object.</summary>
    public bool Object(JsonElement row, string key, string where, out JsonElement value)
    {
        if (!Field(row, key, where, required: true, out value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes an object");
        return false;
    }

    /// <summary>
    /// An object or null; the key must be there unless <paramref name="required"/> is false. False
    /// for null or absent.
    /// </summary>
    public bool NullableObject(JsonElement row, string key, string where, out JsonElement value,
                               bool required = true)
    {
        if (!Field(row, key, where, required, out value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes an object or null");
        return false;
    }

    // -- the closed core -------------------------------------------------------------------------------

    /// <summary>
    /// A required core term, read through the enumeration the pipeline branches on: the value whose
    /// published name (<paramref name="name"/>, one of <c>CoreVocabulary.Name</c>'s overloads) the plan
    /// spells, or a problem naming every value the family has.
    /// </summary>
    public TEnum Core<TEnum>(JsonElement row, string key, string where, Func<TEnum, string> name,
                             string family) where TEnum : struct, Enum =>
        Field(row, key, where, required: true, out JsonElement value)
            ? CoreOf(value, $"{where}.{key}", name, family)
            : default;

    /// <summary>A required list of core terms of one family, at least one.</summary>
    public ImmutableArray<TEnum> CoreList<TEnum>(JsonElement row, string key, string where,
                                                 Func<TEnum, string> name, string family)
        where TEnum : struct, Enum
    {
        if (!Field(row, key, where, required: true, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes a list of at least one "
                                      + $"{family}");
            return [];
        }

        ImmutableArray<TEnum>.Builder terms = ImmutableArray.CreateBuilder<TEnum>(value.GetArrayLength());
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            terms.Add(CoreOf(item, $"{where}.{key}[{index++}]", name, family));
        }

        return terms.DrainToImmutable();
    }

    /// <summary>The member of <typeparamref name="TEnum"/> whose published name is <paramref name="spelled"/>.</summary>
    public bool TryCore<TEnum>(string spelled, Func<TEnum, string> name, out TEnum term)
        where TEnum : struct, Enum
    {
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(name(value), spelled, StringComparison.Ordinal))
            {
                term = value;
                return true;
            }
        }

        term = default;
        return false;
    }

    /// <summary>Every published name of a family, for a refusal to list.</summary>
    public static string Names<TEnum>(Func<TEnum, string> name) where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetValues<TEnum>().Select(name));

    private TEnum CoreOf<TEnum>(JsonElement value, string where, Func<TEnum, string> name, string family)
        where TEnum : struct, Enum
    {
        if (value.ValueKind == JsonValueKind.String && TryCore(value.GetString()!, name, out TEnum term))
        {
            return term;
        }

        Problem(where, $"is {Shown(value)}, which is no {family} of the core vocabulary at version "
                       + $"{CoreVocabulary.Version} ({Names(name)})");
        return default;
    }

    // -- underneath ------------------------------------------------------------------------------------

    private bool Field(JsonElement row, string key, string where, bool required, out JsonElement value)
    {
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out value))
        {
            return true;
        }

        if (required)
        {
            Problem($"{where}.{key}", "is missing");
        }

        value = default;
        return false;
    }

    private double? NumberOf(JsonElement value, string key, string where)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            && double.IsFinite(number))
        {
            return number;
        }

        Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes a number");
        return null;
    }

    private ImmutableArray<string>? TextsOf(JsonElement value, string key, string where)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            Problem($"{where}.{key}", $"is {Shown(value)}, where the compiler writes a list of strings");
            return null;
        }

        ImmutableArray<string>.Builder texts = ImmutableArray.CreateBuilder<string>(value.GetArrayLength());
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                texts.Add(text);
            }
            else
            {
                Problem($"{where}.{key}[{index}]", $"is {Shown(item)}, where the compiler writes a non-empty "
                                                    + "string");
            }

            index++;
        }

        return texts.DrainToImmutable();
    }

    /// <summary>A JSON value as a refusal quotes it: a string in quotes, anything else by its kind.</summary>
    private static string Shown(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"'{value.GetString()}'",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        _ => value.ValueKind.ToString().ToLower(CultureInfo.InvariantCulture),
    };
}
