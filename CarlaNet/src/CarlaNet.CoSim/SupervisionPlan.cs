using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim;

/// <summary>
/// A compiled scenario's supervision plan, <c>&lt;scenario_id&gt;.supervision.json</c>, as the scenario
/// compiler (<c>carlacontrol.SupervisionPlanCompiler</c>) writes it beside the configuration: every row
/// of supervision a run may bind, the vocabulary its labels resolve against, and the digests of the
/// files it was compiled against.
/// </summary>
/// <remarks>
/// <para><b>The row set is fixed before the run, and this type graph has no writer for it</b>
/// (<c>06_Truth_And_Annotation.md</c> D6.8, §3.6). Every type a plan reaches is a sealed record made
/// only by reading the plan -- its constructors are private, the one that reads its row and the
/// record's copy -- with no setter and no collection that can be changed: so nothing in a run can make an instance, a participant, a label or an interval,
/// change one, or add one to the plan. The run binds what it holds -- fills in onsets, closes
/// intervals, records observability -- in records of its own, beside these. The plan carries no solar
/// field and no epoch (D6.21).</para>
///
/// <para><b>Core values are read through the core's own enumerations</b>
/// (<c>CarlaNet.Types.Supervision</c>, D6.30): a supervision state, a subject kind, a cadence and an
/// interval anchor are each the member whose published name the plan spells, and a
/// spelling that is none of them is refused, naming every value the family has. The vocabulary the
/// plan carries must publish exactly this core, at <see cref="CoreVocabulary.Version"/>.</para>
///
/// <para><b>What reading checks.</b> The shape version; every field the compiler writes, of the kind it
/// writes; the core values; the anchor grammar; that every instance has a participant and every
/// interval is one of its instance's participants'; that no cohort is nominal and no instance
/// unlabelled; and that the vocabulary digests as the plan says
/// (<c>04_Contracts.md</c> C3 V3.15), by the compiler's own canonical form. Every problem is named in
/// one refusal.</para>
///
/// <para><b>What it does not.</b> Whether the plan was compiled against the files a session runs:
/// <see cref="ScenarioLockCheck"/> compares its digests with them, and the lock's digest with it.
/// Whether its references resolve -- a label to a term, a participant to a vehicle of the route file, a
/// slot to its series -- which the compiler refuses to write a plan without (07 checks 8, 18, 19, 20),
/// and which the lock's digest of the plan carries to the run.</para>
/// </remarks>
public sealed record SupervisionPlan
{
    /// <summary>The <c>supervision_plan_version</c> this reader implements. Any other is refused.</summary>
    public const int SupportedVersion = 1;

    private SupervisionPlan(string path, string sha256, JsonElement root, SupervisionPlanReading reading)
    {
        Path = path;
        Sha256 = sha256;
        PlanId = reading.Text(root, "plan_id", "plan");
        SpecVersion = reading.Integer(root, "spec_version", "plan");
        ScenarioId = reading.Text(root, "scenario_id", "plan");
        RoutesDigest = reading.Text(root, "routes_digest", "plan");
        NetworkDigest = reading.Text(root, "network_digest", "plan");
        ConfigDigest = reading.Text(root, "config_digest", "plan");
        AdditionalDigest = reading.NullableText(root, "additional_digest", "plan");

        int coreVersion = reading.Integer(root, "vocabulary_version", "plan");
        string vocabularyDigest = reading.Text(root, "vocabulary_digest", "plan");
        if (reading.Object(root, "vocabulary", "plan", out JsonElement vocabulary))
        {
            Vocabulary = PlanVocabulary.Read(vocabulary, vocabularyDigest, reading, "vocabulary");
            if (coreVersion != Vocabulary.CoreVersion)
            {
                reading.Problem("plan.vocabulary_version", $"is {coreVersion}, and the vocabulary it carries is at "
                                                           + $"core version {Vocabulary.CoreVersion}");
            }

            string carried = VocabularyDigestOf(vocabulary);
            if (vocabularyDigest.Length > 0 && !string.Equals(carried, vocabularyDigest, StringComparison.Ordinal))
            {
                reading.Problem("plan.vocabulary_digest",
                                $"is {vocabularyDigest}, and the vocabulary the plan carries digests as {carried}: "
                                + "a term's declaration was changed after the plan was compiled, so a label "
                                + "would reach a consumer meaning something its digest does not say");
            }
        }
        else
        {
            // Refused for the missing vocabulary, so this is never seen outside the reader.
            Vocabulary = null!;
        }

        Instances = reading.Rows(root, "instances", "plan", PatternInstance.Read);
        Series = reading.Rows(root, "series", "plan", RecurringSeries.Read);
        Cohorts = reading.Rows(root, "cohorts", "plan", CohortSupervision.Read);
        Entities = reading.Rows(root, "entities", "plan", EntitySupervision.Read);
    }

    /// <summary>Where the plan was read from.</summary>
    public string Path { get; }

    /// <summary>Lowercase hex SHA-256 of the bytes read, as the compile lock digests the plan.</summary>
    public string Sha256 { get; }

    /// <summary>The plan's id, which is its scenario's.</summary>
    public string PlanId { get; }

    /// <summary>The specification version it was compiled from.</summary>
    public int SpecVersion { get; }

    /// <summary>The scenario it supervises.</summary>
    public string ScenarioId { get; }

    /// <summary>SHA-256 of the route file it was compiled against.</summary>
    public string RoutesDigest { get; }

    /// <summary>
    /// The canonical fingerprint of the network it was compiled against (<c>NetworkFingerprint</c>): the
    /// network's identity, not its file's bytes, which carry netconvert's build stamp.
    /// </summary>
    public string NetworkDigest { get; }

    /// <summary>SHA-256 of the configuration it was compiled against.</summary>
    public string ConfigDigest { get; }

    /// <summary>SHA-256 of the lane closures' additional file it was compiled against; null where there is none.</summary>
    public string? AdditionalDigest { get; }

    /// <summary>The vocabulary, resolved: the core's version, its digest and every author namespace.</summary>
    public PlanVocabulary Vocabulary { get; }

    /// <summary>Every pattern instance, in the plan's order.</summary>
    public ImmutableArray<PatternInstance> Instances { get; }

    /// <summary>Every recurring series.</summary>
    public ImmutableArray<RecurringSeries> Series { get; }

    /// <summary>Every flow, explicitly, annotated or unlabelled.</summary>
    public ImmutableArray<CohortSupervision> Cohorts { get; }

    /// <summary>Every authored vehicle, explicitly, with its states.</summary>
    public ImmutableArray<EntitySupervision> Entities { get; }

    /// <summary>Read a plan, refusing one this reader does not implement or that fails a check.</summary>
    /// <exception cref="CoSimSessionRefusedException">
    /// The file cannot be read, is not JSON, is not a plan of <see cref="SupportedVersion"/>, or fails
    /// a check; the message names every problem found.
    /// </exception>
    public static SupervisionPlan Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = System.IO.Path.GetFullPath(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            throw new CoSimSessionRefusedException(
                $"The supervision plan {path} cannot be read, so none of its rows can be bound: "
                + unreadable.Message, unreadable);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(WithoutByteOrderMark(bytes));
        }
        catch (JsonException unreadable)
        {
            throw new CoSimSessionRefusedException(
                $"The supervision plan {path} cannot be read as JSON, so none of its rows can be bound: "
                + unreadable.Message, unreadable);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CoSimSessionRefusedException(
                    $"The supervision plan {path} is not a JSON object; it is {root.ValueKind}.");
            }

            int? version = root.TryGetProperty("supervision_plan_version", out JsonElement declared)
                           && declared.ValueKind == JsonValueKind.Number
                           && declared.TryGetInt32(out int number)
                ? number
                : null;
            if (version != SupportedVersion)
            {
                throw new CoSimSessionRefusedException(
                    $"The supervision plan {path} declares supervision_plan_version "
                    + (version?.ToString(CultureInfo.InvariantCulture) ?? "none")
                    + $", and version {SupportedVersion} is the only shape this session reads. A field "
                    + "that moved silently is worse than one that is absent, so it is not read on a "
                    + "best-effort basis.");
            }

            var reading = new SupervisionPlanReading(full);
            var plan = new SupervisionPlan(full, Convert.ToHexStringLower(SHA256.HashData(bytes)), root, reading);
            if (reading.Failed)
            {
                throw reading.Refusal();
            }

            return plan;
        }
    }

    /// <summary>What the plan holds, in the report's words.</summary>
    public override string ToString()
    {
        int annotated = Instances.Count(instance => instance.Supervision == SupervisionState.Annotated);
        int nominal = Instances.Count(instance => instance.Supervision == SupervisionState.Nominal);
        int slots = Series.Sum(series => series.Slots.Length);
        int annotatedCohorts = Cohorts.Count(cohort => cohort.Supervision == SupervisionState.Annotated);
        string namespaces = Vocabulary.Namespaces.Length == 0
            ? "no author namespace"
            : string.Join(", ", Vocabulary.Namespaces.Select(name => $"{name.Namespace} {name.Version}"));
        return $"{PlanId}: {Counted(Instances.Length, "instance")} ({annotated} annotated, {nominal} nominal), "
               + $"{Counted(Series.Length, "series", "series")} of {Counted(slots, "slot")}, "
               + $"{Counted(Cohorts.Length, "cohort")} ({annotatedCohorts} annotated), "
               + $"{Counted(Entities.Length, "entity", "entities")}; "
               + $"vocabulary core {Vocabulary.CoreVersion}, {namespaces}, digest {Vocabulary.Digest}";
    }

    private static string Counted(int count, string one, string? many = null) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? one : many ?? one + "s")}";

    private static ReadOnlyMemory<byte> WithoutByteOrderMark(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsMemory(3) : bytes;

    /// <summary>
    /// The vocabulary's digest by the compiler's own canonical form: SHA-256 of the document's UTF-8 as
    /// Python's <c>json.dumps(sort_keys=True, indent=2, ensure_ascii=False)</c> writes it
    /// (<c>carlacontrol.AnnotationVocabulary.digest</c>).
    /// </summary>
    /// <remarks>
    /// Keys sorted by code point; each member or item on its own line, indented two spaces a level,
    /// separated by a comma at the line's end, with <c>": "</c> after a key; an empty object or list
    /// written <c>{}</c> or <c>[]</c>; text beyond ASCII written as it is, and only <c>"</c>, <c>\</c> and
    /// the control characters escaped. A number is written as the plan spells it, which is Python's
    /// spelling, since the compiler wrote the plan with the same function.
    /// </remarks>
    internal static string VocabularyDigestOf(JsonElement vocabulary)
    {
        var text = new StringBuilder();
        WriteCanonical(text, vocabulary, 0);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void WriteCanonical(StringBuilder text, JsonElement value, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                List<JsonProperty> properties = [.. value.EnumerateObject()];
                if (properties.Count == 0)
                {
                    text.Append("{}");
                    return;
                }

                properties.Sort((left, right) => CompareCodePoints(left.Name, right.Name));
                text.Append('{');
                for (int index = 0; index < properties.Count; index++)
                {
                    text.Append(index == 0 ? "\n" : ",\n").Append(' ', 2 * (depth + 1));
                    WriteString(text, properties[index].Name);
                    text.Append(": ");
                    WriteCanonical(text, properties[index].Value, depth + 1);
                }

                text.Append('\n').Append(' ', 2 * depth).Append('}');
                return;
            case JsonValueKind.Array:
                if (value.GetArrayLength() == 0)
                {
                    text.Append("[]");
                    return;
                }

                text.Append('[');
                int item = 0;
                foreach (JsonElement element in value.EnumerateArray())
                {
                    text.Append(item++ == 0 ? "\n" : ",\n").Append(' ', 2 * (depth + 1));
                    WriteCanonical(text, element, depth + 1);
                }

                text.Append('\n').Append(' ', 2 * depth).Append(']');
                return;
            case JsonValueKind.String:
                WriteString(text, value.GetString()!);
                return;
            case JsonValueKind.True:
                text.Append("true");
                return;
            case JsonValueKind.False:
                text.Append("false");
                return;
            case JsonValueKind.Null:
                text.Append("null");
                return;
            default:
                text.Append(value.GetRawText());
                return;
        }
    }

    /// <summary>A string as Python's encoder writes it with <c>ensure_ascii=False</c>.</summary>
    private static void WriteString(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (char letter in value)
        {
            switch (letter)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                case < ' ':
                    text.Append("\\u").Append(((int)letter).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    text.Append(letter);
                    break;
            }
        }

        text.Append('"');
    }

    /// <summary>Python orders keys by code point; UTF-16 order differs from it only above the BMP.</summary>
    private static int CompareCodePoints(string left, string right)
    {
        StringRuneEnumerator leftRunes = left.EnumerateRunes();
        StringRuneEnumerator rightRunes = right.EnumerateRunes();
        while (true)
        {
            bool leftMore = leftRunes.MoveNext();
            bool rightMore = rightRunes.MoveNext();
            if (!leftMore || !rightMore)
            {
                return leftMore.CompareTo(rightMore);
            }

            int order = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
            if (order != 0)
            {
                return order;
            }
        }
    }
}
