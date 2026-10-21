using System.Globalization;
using System.Text.Json;
using CarlaNet.Recording;

namespace CarlaNet.CoSim;

/// <summary>
/// What a session's live track sends (<see cref="LiveTrackFeed"/>): how many rendered frames it samples
/// per simulated second, how long each event stays fresh, what every uid begins with, and the
/// affiliation each vehicle population is drawn with.
/// </summary>
/// <remarks>
/// <para>A population is what a display convention names: the catalogue class a compiled scenario's
/// vehicle type carries in its <see cref="VehicleCatalogue.ClassParameter"/> parameter, or the type id
/// where it carries none. A population the map does not name takes <see cref="Affiliation"/>.</para>
///
/// <para>Every value is checked where the options are built, and options that break a rule are refused
/// whole, naming every rule they break.</para>
/// </remarks>
public sealed class LiveTrackOptions
{
    /// <summary>Samples per simulated second unless another rate is given.</summary>
    public const double DefaultRateHz = 1.0;

    /// <summary>Seconds after its stamp an event goes stale unless another is given.</summary>
    public const double DefaultStaleSeconds = 3.0;

    /// <summary>The affiliation of a population the map does not name, unless another is given.</summary>
    public const string DefaultAffiliation = "n";

    /// <summary>
    /// The affiliation letters a CoT type's second atom can carry: pending, unknown, assumed friend,
    /// friend, neutral, suspect, hostile, joker, faker, none specified and other.
    /// </summary>
    public const string Affiliations = "puafnshjkox";

    private const string RateField = "rate_hz";
    private const string StaleField = "stale_s";
    private const string UidPrefixField = "uid_prefix";
    private const string AffiliationField = "affiliation";
    private const string MapField = "affiliation_by_type";

    private static readonly string[] KnownFields = [RateField, StaleField, UidPrefixField, AffiliationField, MapField];

    private readonly Dictionary<string, string> _byPopulation;

    /// <param name="rateHz">Samples per simulated second, counted in whole world ticks.</param>
    /// <param name="staleSeconds">Seconds after its stamp each event goes stale.</param>
    /// <param name="uidPrefix">What every uid begins with.</param>
    /// <param name="affiliation">The affiliation of a population <paramref name="affiliationByPopulation"/>
    /// does not name.</param>
    /// <param name="affiliationByPopulation">The affiliation each named population is drawn with.</param>
    /// <exception cref="CoSimSessionRefusedException">A value breaks a rule; the message names every one.</exception>
    public LiveTrackOptions(double rateHz = DefaultRateHz, double staleSeconds = DefaultStaleSeconds,
                            string uidPrefix = CotWriter.DefaultUidPrefix, string affiliation = DefaultAffiliation,
                            IReadOnlyDictionary<string, string>? affiliationByPopulation = null)
    {
        List<string> problems = [];
        Check(rateHz, staleSeconds, uidPrefix, affiliation, affiliationByPopulation, problems);
        if (problems.Count > 0)
        {
            throw Refusal(problems);
        }

        RateHz = rateHz;
        StaleSeconds = staleSeconds;
        UidPrefix = uidPrefix;
        Affiliation = affiliation;
        _byPopulation = affiliationByPopulation is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(affiliationByPopulation, StringComparer.Ordinal);
    }

    /// <summary>Samples per simulated second, counted in whole world ticks (<see cref="LiveTrackFeed.TicksPerSample"/>).</summary>
    public double RateHz { get; }

    /// <summary>Seconds after its stamp each event goes stale: its <c>stale</c> is its <c>time</c> plus this.</summary>
    public double StaleSeconds { get; }

    /// <summary>What every uid begins with: <c>&lt;prefix&gt;-SUMO-&lt;SUMO id&gt;</c>.</summary>
    public string UidPrefix { get; }

    /// <summary>The affiliation of a population the map does not name.</summary>
    public string Affiliation { get; }

    /// <summary>The affiliation each named population is drawn with.</summary>
    public IReadOnlyDictionary<string, string> AffiliationByPopulation => _byPopulation;

    /// <summary>The affiliation a population is drawn with: the map's, or <see cref="Affiliation"/>.</summary>
    public string AffiliationOf(string population) =>
        _byPopulation.TryGetValue(population, out string? named) ? named : Affiliation;

    /// <summary>
    /// Read the options from a JSON object, refusing it whole if any rule is broken.
    /// </summary>
    /// <param name="json">
    /// An object of up to five members, each optional: <c>rate_hz</c>, a positive number;
    /// <c>stale_s</c>, a positive number; <c>uid_prefix</c>, a string with no whitespace;
    /// <c>affiliation</c>, one affiliation letter; and <c>affiliation_by_type</c>, an object from each
    /// population to its letter, the display convention's own member of that name. A member left out
    /// takes its default. Any other member is refused.
    /// </param>
    /// <exception cref="CoSimSessionRefusedException">
    /// The text is not a JSON object or breaks a rule; the message names every one.
    /// </exception>
    public static LiveTrackOptions FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException failure)
        {
            throw new CoSimSessionRefusedException($"The live track's options are not JSON: {failure.Message}", failure);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CoSimSessionRefusedException(
                    $"The live track's options must be a JSON object; they are {root.ValueKind}.");
            }

            List<string> problems = [];
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (Array.IndexOf(KnownFields, property.Name) < 0)
                {
                    problems.Add($"'{property.Name}' is not a live track option; the options are "
                                 + string.Join(", ", KnownFields));
                }
            }

            double rate = Number(root, RateField, DefaultRateHz, problems);
            double stale = Number(root, StaleField, DefaultStaleSeconds, problems);
            string prefix = Text(root, UidPrefixField, CotWriter.DefaultUidPrefix, problems);
            string affiliation = Text(root, AffiliationField, DefaultAffiliation, problems);
            Dictionary<string, string>? map = null;
            if (root.TryGetProperty(MapField, out JsonElement given))
            {
                if (given.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"{MapField} must be an object from each population to its affiliation");
                }
                else
                {
                    map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonProperty population in given.EnumerateObject())
                    {
                        if (population.Value.ValueKind == JsonValueKind.String)
                        {
                            map[population.Name] = population.Value.GetString()!;
                        }
                        else
                        {
                            problems.Add($"{MapField} gives '{population.Name}' {population.Value.ValueKind}, "
                                         + "where it gives an affiliation letter");
                        }
                    }
                }
            }

            Check(rate, stale, prefix, affiliation, map, problems);
            if (problems.Count > 0)
            {
                throw Refusal(problems);
            }

            return new LiveTrackOptions(rate, stale, prefix, affiliation, map);
        }
    }

    private static void Check(double rateHz, double staleSeconds, string? uidPrefix, string? affiliation,
                              IReadOnlyDictionary<string, string>? byPopulation, List<string> problems)
    {
        if (!double.IsFinite(rateHz) || rateHz <= 0.0)
        {
            problems.Add($"{RateField} {Format(rateHz)} must be a positive number of samples per simulated second");
        }

        if (!double.IsFinite(staleSeconds) || staleSeconds <= 0.0)
        {
            problems.Add($"{StaleField} {Format(staleSeconds)} must be a positive number of seconds");
        }

        if (string.IsNullOrEmpty(uidPrefix) || uidPrefix.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            problems.Add($"{UidPrefixField} '{uidPrefix}' must be one or more characters, none of them whitespace");
        }

        if (!IsAffiliation(affiliation))
        {
            problems.Add($"{AffiliationField} '{affiliation}' must be one of the letters {Affiliations}");
        }

        foreach ((string population, string named) in byPopulation ?? new Dictionary<string, string>())
        {
            if (population.Length == 0)
            {
                problems.Add($"{MapField} names an empty population");
            }

            if (!IsAffiliation(named))
            {
                problems.Add($"{MapField} gives '{population}' '{named}', where it gives one of the letters "
                             + Affiliations);
            }
        }
    }

    private static bool IsAffiliation(string? letter) =>
        letter is { Length: 1 } && Affiliations.Contains(letter[0], StringComparison.Ordinal);

    private static double Number(JsonElement root, string field, double fallback, List<string> problems)
    {
        if (!root.TryGetProperty(field, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }

        problems.Add($"{field} must be a number");
        return fallback;
    }

    private static string Text(JsonElement root, string field, string fallback, List<string> problems)
    {
        if (!root.TryGetProperty(field, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        problems.Add($"{field} must be a string");
        return fallback;
    }

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static CoSimSessionRefusedException Refusal(List<string> problems) =>
        new("The live track's options are refused. "
            + string.Join("; ", problems.Select((problem, index) => $"({index + 1}) {problem}"))
            + ".");
}
