using System.Collections.Immutable;
using System.Text.Json;
using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim;

/// <summary>
/// The vocabulary a supervision plan carries, resolved and import-flattened: the closed core's version
/// and every author namespace the plan's labels, roles, phases and area kinds name
/// (<c>06_Truth_And_Annotation.md</c> §3.7, §3.8, D6.30).
/// </summary>
/// <remarks>
/// <para><b>The core is <see cref="CoreVocabulary"/>, not a copy of it.</b> The compiler publishes the
/// core into every plan from those enumerations, and reading a plan refuses one whose core is not
/// this one -- another version, or the same version with a term added, moved or spelled otherwise --
/// so every core value a run writes is one the plan's vocabulary defines, which is what the release
/// validator holds a corpus to.</para>
///
/// <para><b>Author terms are carried, never interpreted.</b> Nothing in this pipeline branches on one
/// (D6.27); they are here so a record can carry a term's declaration to a consumer who never met its
/// author, and so the vocabulary's digest can be checked against what is carried.</para>
/// </remarks>
public sealed record PlanVocabulary
{
    private PlanVocabulary(JsonElement json, string digest, SupervisionPlanReading reading, string where)
    {
        Digest = digest;
        if (reading.Object(json, "core", where, out JsonElement core))
        {
            CoreVersion = reading.Integer(core, "vocabulary_version", $"{where}.core");
            CoreSource = reading.Text(core, "source", $"{where}.core");
            CheckCore(core, CoreVersion, reading, $"{where}.core");
        }

        Namespaces = reading.Rows(json, "namespaces", where, AuthorNamespace.Read);
    }

    /// <summary>The core's version, which is <see cref="CoreVocabulary.Version"/>: a plan of any other is refused.</summary>
    public int CoreVersion { get; }

    /// <summary>Where the core is defined, as the plan states it.</summary>
    public string CoreSource { get; } = string.Empty;

    /// <summary>
    /// SHA-256 of the vocabulary document as the compiler canonicalises it, which every record that
    /// quotes the vocabulary carries; checked against the document on reading (<c>04_Contracts.md</c>
    /// C3 V3.15).
    /// </summary>
    public string Digest { get; }

    /// <summary>Every author namespace, in the plan's order (by name).</summary>
    public ImmutableArray<AuthorNamespace> Namespaces { get; }

    internal static PlanVocabulary Read(JsonElement json, string digest, SupervisionPlanReading reading,
                                        string where) => new(json, digest, reading, where);

    /// <summary>The core the plan publishes must be this pipeline's, family by family and term by term.</summary>
    private static void CheckCore(JsonElement core, int version, SupervisionPlanReading reading, string where)
    {
        if (version != CoreVocabulary.Version)
        {
            reading.Problem($"{where}.vocabulary_version",
                            $"is {version}, and this session branches on the core at version "
                            + $"{CoreVocabulary.Version}: a plan compiled against another core may not define "
                            + "a value the run writes, and a dataset carrying a term its published vocabulary "
                            + "does not define is refused at release");
            return;
        }

        if (!reading.Object(core, "terms", where, out JsonElement terms))
        {
            return;
        }

        foreach (CoreTermFamily family in CoreVocabulary.Families)
        {
            if (!terms.TryGetProperty(family.Family, out JsonElement published)
                || published.ValueKind != JsonValueKind.Array)
            {
                reading.Problem($"{where}.terms.{family.Family}",
                                "is missing or not a list, and the core at this version publishes it");
                continue;
            }

            string[] spelled = [.. published.EnumerateArray().Select(term =>
                term.ValueKind == JsonValueKind.String ? term.GetString()! : term.GetRawText())];
            if (!spelled.SequenceEqual(family.Terms, StringComparer.Ordinal))
            {
                reading.Problem($"{where}.terms.{family.Family}",
                                $"is [{string.Join(", ", spelled)}], and the core at version "
                                + $"{CoreVocabulary.Version} publishes [{string.Join(", ", family.Terms)}]");
            }
        }

        foreach (JsonProperty family in terms.EnumerateObject())
        {
            if (!CoreVocabulary.Families.Any(known => known.Family == family.Name))
            {
                reading.Problem($"{where}.terms.{family.Name}",
                                $"is no family of the core at version {CoreVocabulary.Version}");
            }
        }
    }
}

/// <summary>One author namespace, versioned on its own (06 D6.29).</summary>
public sealed record AuthorNamespace
{
    private AuthorNamespace(JsonElement json, SupervisionPlanReading reading, string where)
    {
        Namespace = reading.Text(json, "namespace", where);
        Version = reading.Integer(json, "version", where);
        Authority = reading.Text(json, "authority", where);
        Terms = reading.Rows(json, "terms", where, AuthorTerm.Read);
        Roles = reading.Rows(json, "roles", where, AuthorRole.Read, required: false);
        AreaKinds = reading.Rows(json, "area_kinds", where, AuthorAreaKind.Read, required: false);
    }

    /// <summary>The namespace's name, the prefix of every term, role and area kind it declares.</summary>
    public string Namespace { get; }

    /// <summary>Its own version, independent of the core's.</summary>
    public int Version { get; }

    /// <summary>Who declares it, in the author's words.</summary>
    public string Authority { get; }

    /// <summary>Its terms, as declared.</summary>
    public ImmutableArray<AuthorTerm> Terms { get; }

    /// <summary>Its roles, as declared; empty where it declares none.</summary>
    public ImmutableArray<AuthorRole> Roles { get; }

    /// <summary>Its area kinds, as declared; empty where it declares none.</summary>
    public ImmutableArray<AuthorAreaKind> AreaKinds { get; }

    internal static AuthorNamespace Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>
/// One author term, self-describing as D6.28 requires: its definition, the kinds of subject it may be
/// asserted of, and its optional relations, each as declared.
/// </summary>
public sealed record AuthorTerm
{
    private AuthorTerm(JsonElement json, SupervisionPlanReading reading, string where)
    {
        Term = reading.Text(json, "term", where);
        Since = reading.Integer(json, "since", where);
        Status = reading.Text(json, "status", where);
        SupersededBy = reading.NullableText(json, "superseded_by", where, required: false);
        AppliesTo = reading.CoreList<SubjectKind>(json, "applies_to", where, CoreVocabulary.Name, "subject_kind");
        Definition = reading.Text(json, "definition", where);
        Broader = reading.NullableText(json, "broader", where, required: false);
        Parameters = reading.Map(json, "parameters", where, TermParameter.Read, required: false);
        Counterfactual = reading.NullableObject(json, "counterfactual", where, out JsonElement counterfactual,
                                                required: false)
            ? Counterfactual.Read(counterfactual, reading, $"{where}.counterfactual")
            : null;
        ContrastWith = reading.Texts(json, "contrast_with", where, required: false);
        HardNegativeFor = reading.Texts(json, "hard_negative_for", where, required: false);
        ExemplarInstances = reading.Texts(json, "exemplar_instances", where, required: false);
    }

    /// <summary>The term, namespaced: <c>bahonar:post_unmanned</c>.</summary>
    public string Term { get; }

    /// <summary>The namespace version that introduced it.</summary>
    public int Since { get; }

    /// <summary><c>active</c>, or <c>deprecated</c> with <see cref="SupersededBy"/>.</summary>
    public string Status { get; }

    /// <summary>The term that replaces a deprecated one; null otherwise.</summary>
    public string? SupersededBy { get; }

    /// <summary>The kinds of subject it may be asserted of: a vehicle, or every vehicle of a flow.</summary>
    public ImmutableArray<SubjectKind> AppliesTo { get; }

    /// <summary>What it means, for a reader who never met the author.</summary>
    public string Definition { get; }

    /// <summary>Its one broader parent, or null (06 D6.33).</summary>
    public string? Broader { get; }

    /// <summary>The parameters a row carrying it may give, each with its type, unit and meaning (06 §3.8).</summary>
    public ImmutableSortedDictionary<string, TermParameter> Parameters { get; }

    /// <summary>What it is the counterfactual of, resolved by the compiler; null where it names none.</summary>
    public Counterfactual? Counterfactual { get; }

    /// <summary>Terms a consumer should tell it apart from.</summary>
    public ImmutableArray<string> ContrastWith { get; }

    /// <summary>
    /// The terms a nominal subject carrying it is a matched negative for; it narrows, never widens, and
    /// empty means unspecified (06 D6.32).
    /// </summary>
    public ImmutableArray<string> HardNegativeFor { get; }

    /// <summary>Instances of this scenario that exemplify it, by their authored names.</summary>
    public ImmutableArray<string> ExemplarInstances { get; }

    internal static AuthorTerm Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>One parameter a term declares: its type, its unit where it has one, and what it means.</summary>
public sealed record TermParameter
{
    private TermParameter(JsonElement json, SupervisionPlanReading reading, string where)
    {
        Type = reading.Text(json, "type", where);
        Unit = reading.NullableText(json, "unit", where, required: false);
        Definition = reading.Text(json, "definition", where);
    }

    /// <summary><c>number</c>, <c>integer</c>, <c>string</c> or <c>boolean</c>.</summary>
    public string Type { get; }

    /// <summary>Its unit, or null.</summary>
    public string? Unit { get; }

    /// <summary>What it means.</summary>
    public string Definition { get; }

    internal static TermParameter Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>
/// A counterfactual: a pointer to the subject or term an instance or a term is to be read against, and
/// never a supervision write (06 D6.33).
/// </summary>
public sealed record Counterfactual
{
    private Counterfactual(JsonElement json, SupervisionPlanReading reading, string where)
    {
        Kind = reading.Text(json, "kind", where);
        Ref = reading.Text(json, "ref", where);
    }

    /// <summary><c>series</c>, <c>cohort</c>, <c>instance</c> or <c>term</c>.</summary>
    public string Kind { get; }

    /// <summary>What it names, resolved by the compiler.</summary>
    public string Ref { get; }

    internal static Counterfactual Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>A role an author namespace declares.</summary>
public sealed record AuthorRole
{
    private AuthorRole(JsonElement json, SupervisionPlanReading reading, string where)
    {
        Role = reading.Text(json, "role", where);
        Definition = reading.Text(json, "definition", where);
    }

    /// <summary>The role, namespaced: <c>bahonar:guard</c>.</summary>
    public string Role { get; }

    /// <summary>What it means.</summary>
    public string Definition { get; }

    internal static AuthorRole Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}

/// <summary>An area kind an author namespace declares.</summary>
public sealed record AuthorAreaKind
{
    private AuthorAreaKind(JsonElement json, SupervisionPlanReading reading, string where)
    {
        Kind = reading.Text(json, "kind", where);
        Definition = reading.NullableText(json, "definition", where, required: false);
    }

    /// <summary>The kind, namespaced: <c>bahonar:gate</c>.</summary>
    public string Kind { get; }

    /// <summary>What it means, where the author said.</summary>
    public string? Definition { get; }

    internal static AuthorAreaKind Read(JsonElement json, SupervisionPlanReading reading, string where) =>
        new(json, reading, where);
}
