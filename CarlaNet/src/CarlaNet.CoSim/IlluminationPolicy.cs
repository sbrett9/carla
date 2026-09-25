using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CarlaNet.CoSim;

/// <summary>
/// What a session does with the sun across its window: freeze it at the window's opening instant,
/// let the engine carry it forward, freeze it at a declared hour, or leave it alone.
/// </summary>
/// <remarks>
/// <para><b>Declared, never defaulted.</b> A frozen run and a run nobody configured write
/// byte-identical records, so a default here would make absence indistinguishable from intent.
/// <see cref="FreezeAtWindowStart"/> is the recommended value -- a window is meant to be one
/// lighting condition, and a default 1,800 s window at a real-time rate moves the sun through up to
/// 6.7 degrees of elevation -- but it is recommended, not silent.</para>
///
/// <para><b>The sun's date follows one rule.</b> The date the sun is written with advances with the
/// civil date only when the epoch's calendar advances <i>and</i> the policy lets the date move:
/// always under <see cref="IlluminationPolicyKind.Advance"/>, and under a freeze only when
/// <see cref="FreezeDateAdvances"/> says so. Otherwise it stays on the epoch's own date, so a frozen
/// week of windows keeps one seasonal sun geometry. See <see cref="SunDateAdvances"/>.</para>
/// </remarks>
public sealed class IlluminationPolicy
{
    /// <summary>The <c>illumination_version</c> this consumer implements. Any other is refused.</summary>
    public const int SupportedVersion = 1;

    private static readonly string[] KnownFields =
    [
        "illumination_version", "policy", "rate_sun_s_per_sim_s", "freeze_at_civil_time",
        "freeze_date_advances", "require_sun", "solar_audit_tolerance_s",
        "solar_audit_tolerance_elev_deg", "note",
    ];

    private static readonly string[] ToleranceOverrides =
        ["solar_audit_tolerance_s", "solar_audit_tolerance_elev_deg"];

    private static readonly Regex TimeOfDay = new(@"^(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})$",
                                                  RegexOptions.CultureInvariant);

    private IlluminationPolicy(IlluminationPolicyKind kind,
                               double rate,
                               TimeSpan? freezeAt,
                               bool freezeDateAdvances,
                               bool requireSun,
                               string? note)
    {
        Kind = kind;
        Rate = rate;
        FreezeAtCivilTimeOfDay = freezeAt;
        FreezeDateAdvances = freezeDateAdvances;
        RequireSun = requireSun;
        Note = note;
    }

    /// <summary>Which of the four policies this is.</summary>
    public IlluminationPolicyKind Kind { get; }

    /// <summary>
    /// Sun-clock seconds per simulated second under <see cref="IlluminationPolicyKind.Advance"/>, and
    /// zero under every other policy, which is what the engine is told for a frozen sun.
    /// </summary>
    /// <remarks>
    /// Per simulated second, not per wall-clock second: the engine advances the clock by the world
    /// tick's delta times the rate, and under synchronous ticking that delta is the fixed one.
    /// </remarks>
    public double Rate { get; }

    /// <summary>The civil time of day the sun is held at under <see cref="IlluminationPolicyKind.FreezeAt"/>.</summary>
    public TimeSpan? FreezeAtCivilTimeOfDay { get; }

    /// <summary>
    /// Under a freeze, whether the sun's date still follows the civil date when the epoch's calendar
    /// advances. False keeps a frozen week of windows on one seasonal sun geometry.
    /// </summary>
    public bool FreezeDateAdvances { get; }

    /// <summary>
    /// Whether a world with no sun refuses the session. True unless declared otherwise; a run under
    /// lighting nobody declared is not a run anything can be concluded from.
    /// </summary>
    public bool RequireSun { get; }

    /// <summary>Why this policy, in one sentence.</summary>
    public string? Note { get; }

    /// <summary>The policy's name as the scenario contract spells it.</summary>
    public string Name => Kind switch
    {
        IlluminationPolicyKind.FreezeAtWindowStart => "freeze_at_window_start",
        IlluminationPolicyKind.Advance => "advance",
        IlluminationPolicyKind.FreezeAt => "freeze_at",
        IlluminationPolicyKind.Ignore => "ignore",
        _ => throw new InvalidOperationException($"Unknown illumination policy {Kind}."),
    };

    /// <summary>Whether the session writes the sun at all.</summary>
    public bool BindsTheSun => Kind != IlluminationPolicyKind.Ignore;

    /// <summary>Whether the engine carries the sun forward with the world tick.</summary>
    public bool Advances => Kind == IlluminationPolicyKind.Advance;

    /// <summary>
    /// Whether every frame is lit by the sun of its own declared civil instant -- true under
    /// <see cref="IlluminationPolicyKind.Advance"/> and <see cref="IlluminationPolicyKind.FreezeAtWindowStart"/>,
    /// false under a declared hour or no binding at all.
    /// </summary>
    public bool HonoursTheEpoch =>
        Kind is IlluminationPolicyKind.Advance or IlluminationPolicyKind.FreezeAtWindowStart;

    /// <summary>
    /// Freeze the sun at the civil instant the window opens. The recommended policy.
    /// </summary>
    public static IlluminationPolicy FreezeAtWindowStart(bool freezeDateAdvances = false,
                                                         bool requireSun = true,
                                                         string? note = null) =>
        new(IlluminationPolicyKind.FreezeAtWindowStart, 0.0, null, freezeDateAdvances, requireSun, note);

    /// <summary>
    /// Start the sun at the civil instant the window opens and let the engine carry it forward.
    /// </summary>
    /// <param name="rate">Sun-clock seconds per simulated second; 1.0 keeps the sun on civil time.</param>
    /// <exception cref="CoSimSessionRefusedException">The rate is not a positive number.</exception>
    public static IlluminationPolicy Advance(double rate, bool requireSun = true, string? note = null)
    {
        if (!double.IsFinite(rate) || rate <= 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"An advancing sun at a rate of {rate} sun-seconds per simulated second is not an "
                + "advancing sun. Declare a positive rate, or freeze it.");
        }

        return new IlluminationPolicy(IlluminationPolicyKind.Advance, rate, null, false, requireSun, note);
    }

    /// <summary>
    /// Hold the sun at a declared civil time of day, whatever instant the window opens at.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The time is not a time of day.</exception>
    public static IlluminationPolicy FreezeAt(TimeSpan civilTimeOfDay,
                                              bool freezeDateAdvances = false,
                                              bool requireSun = true,
                                              string? note = null)
    {
        if (civilTimeOfDay < TimeSpan.Zero || civilTimeOfDay >= TimeSpan.FromDays(1))
        {
            throw new CoSimSessionRefusedException(
                $"A sun frozen at {civilTimeOfDay} is not frozen at a time of day; it must lie in "
                + "[00:00:00, 24:00:00).");
        }

        return new IlluminationPolicy(IlluminationPolicyKind.FreezeAt, 0.0, civilTimeOfDay,
                                      freezeDateAdvances, requireSun, note);
    }

    /// <summary>
    /// Leave the sun alone. The run's lighting is whatever the world held and is recorded as not
    /// honouring any epoch: the escape hatch for diagnostics, and the only policy a run with no
    /// epoch can declare. A world with no sun is still refused unless <paramref name="requireSun"/>
    /// is false.
    /// </summary>
    public static IlluminationPolicy Ignore(bool requireSun = true, string? note = null) =>
        new(IlluminationPolicyKind.Ignore, 0.0, null, false, requireSun, note);

    /// <summary>
    /// Read an illumination object, refusing it whole if any rule is broken.
    /// </summary>
    /// <param name="json">
    /// The <c>illumination</c> object of a scenario, as JSON text (<c>04_Contracts.md</c> section 11.5).
    /// </param>
    /// <remarks>
    /// <para>A field belonging to another policy is refused rather than ignored -- a rate given
    /// beside a freeze is an operator asking for a moving sun and getting a still one -- and so is a
    /// field this consumer does not read, because a field nobody honours is one somebody will later
    /// believe was honoured.</para>
    ///
    /// <para>The two audit-tolerance overrides the contract names are refused too. The contract
    /// bounds them by a value doc 10 has not yet set, and an override with no upper bound is an off
    /// switch for the audit. Until the bound exists the audit runs at its measured floor.</para>
    /// </remarks>
    /// <exception cref="CoSimSessionRefusedException">
    /// The document is not an illumination object or breaks one or more of the rules; the message
    /// names every one of them.
    /// </exception>
    public static IlluminationPolicy FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException failure)
        {
            throw new CoSimSessionRefusedException(
                $"The illumination policy is not JSON: {failure.Message}", failure);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CoSimSessionRefusedException(
                    $"The illumination policy must be a JSON object; it is {root.ValueKind}.");
            }

            return Read(root);
        }
    }

    /// <summary>
    /// Whether the sun's date moves with the civil date under this policy and epoch: the epoch's
    /// calendar advances, and the policy advances or lets a frozen sun's date follow it.
    /// </summary>
    public bool SunDateAdvances(SolarEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        return epoch.CalendarAdvances && (Advances || FreezeDateAdvances);
    }

    private static IlluminationPolicy Read(JsonElement root)
    {
        List<string> problems = [];
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (Array.IndexOf(KnownFields, property.Name) < 0)
            {
                problems.Add($"'{property.Name}' is not an illumination field");
            }
        }

        if (!root.TryGetProperty("illumination_version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number))
        {
            problems.Add("illumination_version is required and must be an integer");
        }
        else if (number != SupportedVersion)
        {
            problems.Add($"illumination_version {number} is not one this consumer implements "
                         + $"({SupportedVersion})");
        }

        foreach (string tolerance in ToleranceOverrides)
        {
            if (root.TryGetProperty(tolerance, out _))
            {
                problems.Add($"{tolerance} overrides the solar audit's tolerance, and the contract "
                             + "bounds an override by a value that has not been set; without the "
                             + "bound it is an off switch, so the audit runs at its measured floor");
            }
        }

        string? name = root.TryGetProperty("policy", out JsonElement policy)
                       && policy.ValueKind == JsonValueKind.String
            ? policy.GetString()
            : null;
        IlluminationPolicyKind? kind = name switch
        {
            "freeze_at_window_start" => IlluminationPolicyKind.FreezeAtWindowStart,
            "advance" => IlluminationPolicyKind.Advance,
            "freeze_at" => IlluminationPolicyKind.FreezeAt,
            "ignore" => IlluminationPolicyKind.Ignore,
            _ => null,
        };
        if (kind is null)
        {
            problems.Add((name is null ? "policy is required and must be one of " : $"policy '{name}' is not one of ")
                         + "freeze_at_window_start, advance, freeze_at or ignore. There is no "
                         + "default; freeze_at_window_start is the recommended one");
        }

        double? rate = OptionalNumber(root, "rate_sun_s_per_sim_s", problems);
        bool freezeAtGiven = root.TryGetProperty("freeze_at_civil_time", out _);
        TimeSpan? freezeAt = OptionalTimeOfDay(root, problems);
        bool? freezeDateAdvances = OptionalBoolean(root, "freeze_date_advances", problems);
        bool requireSun = OptionalBoolean(root, "require_sun", problems) ?? true;
        string? note = root.TryGetProperty("note", out JsonElement noted)
                       && noted.ValueKind == JsonValueKind.String
            ? noted.GetString()
            : null;

        if (kind == IlluminationPolicyKind.Advance)
        {
            if (rate is null)
            {
                problems.Add("an advancing sun needs rate_sun_s_per_sim_s, the sun-clock seconds it "
                             + "moves per simulated second; 1.0 keeps it on civil time");
            }
            else if (!double.IsFinite(rate.Value) || rate.Value <= 0.0)
            {
                problems.Add($"rate_sun_s_per_sim_s {rate.Value} must be positive");
            }
        }
        else if (rate is not null && kind is not null)
        {
            problems.Add($"rate_sun_s_per_sim_s belongs to the advance policy; under '{name}' it "
                         + "would be read by nothing, and whoever wrote it would believe the sun "
                         + "was moving");
        }

        if (kind == IlluminationPolicyKind.FreezeAt && !freezeAtGiven)
        {
            problems.Add("freeze_at needs freeze_at_civil_time, the HH:MM:SS civil time of day the "
                         + "sun is held at");
        }
        else if (kind is not null and not IlluminationPolicyKind.FreezeAt && freezeAtGiven)
        {
            problems.Add($"freeze_at_civil_time belongs to the freeze_at policy, not '{name}'");
        }

        if (freezeDateAdvances is not null
            && kind is IlluminationPolicyKind.Advance or IlluminationPolicyKind.Ignore)
        {
            problems.Add($"freeze_date_advances belongs to a freeze, not '{name}'");
        }

        if (problems.Count > 0)
        {
            throw new CoSimSessionRefusedException(
                "The illumination policy is refused. "
                + string.Join("; ", problems.Select((problem, index) => $"({index + 1}) {problem}"))
                + ".");
        }

        return kind switch
        {
            IlluminationPolicyKind.FreezeAtWindowStart =>
                FreezeAtWindowStart(freezeDateAdvances ?? false, requireSun, note),
            IlluminationPolicyKind.Advance => Advance(rate!.Value, requireSun, note),
            IlluminationPolicyKind.FreezeAt =>
                FreezeAt(freezeAt!.Value, freezeDateAdvances ?? false, requireSun, note),
            _ => Ignore(requireSun, note),
        };
    }

    private static double? OptionalNumber(JsonElement root, string field, List<string> problems)
    {
        if (!root.TryGetProperty(field, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number))
        {
            problems.Add($"{field} must be a number");
            return null;
        }

        return number;
    }

    private static bool? OptionalBoolean(JsonElement root, string field, List<string> problems)
    {
        if (!root.TryGetProperty(field, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problems.Add($"{field} must be true or false");
            return null;
        }

        return value.GetBoolean();
    }

    private static TimeSpan? OptionalTimeOfDay(JsonElement root, List<string> problems)
    {
        if (!root.TryGetProperty("freeze_at_civil_time", out JsonElement value))
        {
            return null;
        }

        Match match = value.ValueKind == JsonValueKind.String
            ? TimeOfDay.Match(value.GetString()!)
            : Match.Empty;
        if (!match.Success)
        {
            problems.Add("freeze_at_civil_time must be a civil time of day written HH:MM:SS");
            return null;
        }

        int hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59 || seconds > 59)
        {
            problems.Add($"freeze_at_civil_time '{value.GetString()}' is not a time of day in "
                         + "[00:00:00, 24:00:00)");
            return null;
        }

        return new TimeSpan(hours, minutes, seconds);
    }

    /// <summary>The policy in one line, as a run report states it.</summary>
    public override string ToString() => Kind switch
    {
        IlluminationPolicyKind.Advance =>
            $"{Name} at {Rate.ToString("0.######", CultureInfo.InvariantCulture)} sun-s per simulated s",
        IlluminationPolicyKind.FreezeAt =>
            $"{Name} {FreezeAtCivilTimeOfDay!.Value.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture)} civil"
            + (FreezeDateAdvances ? ", date follows the calendar" : ", date held"),
        IlluminationPolicyKind.FreezeAtWindowStart =>
            Name + (FreezeDateAdvances ? ", date follows the calendar" : ", date held"),
        _ => Name,
    };
}
