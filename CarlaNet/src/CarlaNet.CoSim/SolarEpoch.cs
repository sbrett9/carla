using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CarlaNet.CoSim;

/// <summary>
/// What simulated second zero means in civil time at the site: the scenario epoch, declared.
/// </summary>
/// <remarks>
/// <para><b>Why a session takes one.</b> A SUMO scenario declares a span of simulated seconds and
/// nothing else about time, so without this a capture's sun is whatever the world happened to be
/// holding: a loaded world keeps the previous session's sun, and a world nobody configured holds
/// <c>ACesiumSunSky</c>'s class-default date, 2019-09-21. The epoch is the only statement of what
/// time the scene is, and every civil instant the session derives comes from
/// <see cref="CivilInstantAt"/> and nowhere else.</para>
///
/// <para><b>The offset is the declaration; the zone name is not.</b> The offset is a number, and it
/// is normative: a named zone would have to be resolved against a time-zone database, and two hosts
/// with different databases -- or, measured on this machine, none -- would disagree about what a
/// corpus's frames were lit by. <see cref="TimeZoneId"/> is carried for a reader and is never
/// resolved. Daylight saving is part of the declared offset; <see cref="DstInEffect"/> only says
/// whether it is, so "+02:00 standard" and "+02:00 because it is summer" read differently. Half-hour
/// and quarter-hour offsets are ordinary: the sizing scenario's port is at +03:30.</para>
///
/// <para><b>Declared twice on purpose.</b> The instant is given as a civil time with its offset and
/// again in UTC, and the two must agree to the second. The likeliest authoring error is an offset
/// applied in the wrong direction, which at +03:30 renders a scene seven hours from the declared one
/// that looks entirely plausible. Nothing about the rendered frame can catch that; comparing two
/// statements of one fact can.</para>
///
/// <para>The wire shape is the <c>epoch</c> object of the scenario contract
/// (<c>Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/04_Contracts.md</c> section 11.3). A document
/// that breaks any rule is refused whole, naming every rule it broke, because a time declaration
/// read in part renders six days of a week on the wrong date.</para>
/// </remarks>
public sealed class SolarEpoch
{
    /// <summary>The <c>epoch_version</c> this consumer implements. Any other is refused.</summary>
    public const int SupportedVersion = 1;

    /// <summary>
    /// The range <c>ACesiumSunSky</c> declares for its time zone. An offset outside it would be
    /// clamped by the engine without a word, so it is refused here instead.
    /// </summary>
    private const double EarliestOffsetHours = -12.0;
    private const double LatestOffsetHours = 14.0;

    private static readonly string[] KnownFields =
    [
        "epoch_version", "civil_datetime", "utc_offset_hours", "utc_datetime",
        "calendar_advances", "dst_in_effect", "time_zone_id", "note",
    ];

    // A calendar date and a clock with an explicit zone designator: "Z" or a signed hh:mm offset.
    // Fractional seconds are allowed; a bare local time is not, because a civil time with no offset
    // is the error the epoch exists to eliminate.
    private static readonly Regex IsoInstant = new(
        @"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})T(?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})(?<f>\.\d{1,7})?"
        + @"(?<z>Z|[+-]\d{2}:\d{2})?$",
        RegexOptions.CultureInvariant);

    private SolarEpoch(DateTimeOffset civil,
                       DateTimeOffset utc,
                       bool calendarAdvances,
                       bool dstInEffect,
                       string? timeZoneId,
                       string? note,
                       string canonicalJson)
    {
        CivilDateTime = civil;
        UtcDateTime = utc;
        CalendarAdvances = calendarAdvances;
        DstInEffect = dstInEffect;
        TimeZoneId = timeZoneId;
        Note = note;
        CanonicalJson = canonicalJson;
        Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    /// <summary>The civil instant simulated second zero corresponds to, at the declared offset.</summary>
    public DateTimeOffset CivilDateTime { get; }

    /// <summary>The site's civil offset from UTC, including daylight saving where it applies.</summary>
    public TimeSpan UtcOffset => CivilDateTime.Offset;

    /// <summary>The same offset in signed decimal hours: 3.5 for +03:30.</summary>
    public double UtcOffsetHours => UtcOffset.TotalHours;

    /// <summary>The same instant in UTC, as declared and checked against the civil one.</summary>
    public DateTimeOffset UtcDateTime { get; }

    /// <summary>
    /// Whether the civil date advances when simulated time crosses a civil midnight. False holds
    /// the sun's date at the epoch's own while the clock still runs, which is a declared choice --
    /// one sun geometry across a week of behaviour -- and never a default.
    /// </summary>
    public bool CalendarAdvances { get; }

    /// <summary>Whether the declared offset already includes daylight saving.</summary>
    public bool DstInEffect { get; }

    /// <summary>The IANA zone the site is in, for a reader. Never resolved.</summary>
    public string? TimeZoneId { get; }

    /// <summary>One sentence saying what simulated second zero is in the scenario's own terms.</summary>
    public string? Note { get; }

    /// <summary>
    /// The epoch object with its keys sorted, two-space indented, as Python's
    /// <c>json.dumps(epoch, sort_keys=True, indent=2)</c> writes it.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than left to a serialiser because the scenario compiler that produces this
    /// object is Python, and two implementations that disagree about a space or an escape disagree
    /// about identity.
    /// </remarks>
    public string CanonicalJson { get; }

    /// <summary>Lowercase hex SHA-256 of <see cref="CanonicalJson"/>, so a record names the epoch.</summary>
    public string Digest { get; }

    /// <summary>The declared civil date and time of simulated second zero, as written.</summary>
    public string CivilDateTimeText => FormatCivil(CivilDateTime);

    /// <summary>
    /// Read an epoch object, refusing it whole if any rule is broken.
    /// </summary>
    /// <param name="json">The <c>epoch</c> object of a scenario, as JSON text.</param>
    /// <exception cref="CoSimSessionRefusedException">
    /// The document is not an epoch object, or breaks one or more of the rules; the message names
    /// every one of them.
    /// </exception>
    public static SolarEpoch FromJson(string json)
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
                $"The scenario epoch is not JSON: {failure.Message}", failure);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CoSimSessionRefusedException(
                    "The scenario epoch must be a JSON object; it is "
                    + $"{document.RootElement.ValueKind}.");
            }

            return Read(document.RootElement);
        }
    }

    /// <summary>
    /// Declare an epoch from its parts, under exactly the rules <see cref="FromJson"/> applies.
    /// </summary>
    /// <param name="civilDateTime">ISO-8601 with an explicit offset: <c>2026-03-21T00:00:00+03:30</c>.</param>
    /// <param name="utcOffsetHours">The same offset as signed decimal hours.</param>
    /// <param name="utcDateTime">The same instant in UTC: <c>2026-03-20T20:30:00Z</c>.</param>
    /// <param name="calendarAdvances">Whether the civil date advances at a civil midnight.</param>
    /// <param name="dstInEffect">Whether the offset already includes daylight saving.</param>
    /// <param name="timeZoneId">The IANA zone, for a reader; never resolved.</param>
    /// <param name="note">What simulated second zero is, in the scenario's own terms.</param>
    public static SolarEpoch Declare(string civilDateTime,
                                     double utcOffsetHours,
                                     string utcDateTime,
                                     bool calendarAdvances,
                                     bool dstInEffect,
                                     string? timeZoneId = null,
                                     string? note = null)
    {
        var text = new StringBuilder("{");
        text.Append("\"epoch_version\":").Append(SupportedVersion);
        text.Append(",\"civil_datetime\":").Append(JsonSerializer.Serialize(civilDateTime));
        text.Append(",\"utc_offset_hours\":").Append(PythonFloat(utcOffsetHours));
        text.Append(",\"utc_datetime\":").Append(JsonSerializer.Serialize(utcDateTime));
        text.Append(",\"calendar_advances\":").Append(calendarAdvances ? "true" : "false");
        text.Append(",\"dst_in_effect\":").Append(dstInEffect ? "true" : "false");
        if (timeZoneId is not null)
        {
            text.Append(",\"time_zone_id\":").Append(JsonSerializer.Serialize(timeZoneId));
        }

        if (note is not null)
        {
            text.Append(",\"note\":").Append(JsonSerializer.Serialize(note));
        }

        return FromJson(text.Append('}').ToString());
    }

    /// <summary>
    /// The civil instant a simulated second corresponds to: the epoch plus that many seconds, at the
    /// declared offset.
    /// </summary>
    /// <remarks>
    /// The one place a simulated second becomes a civil time. Every consumer -- the sun binding, the
    /// audit, the record -- asks here, because two implementations of this function is how imagery
    /// and truth come to disagree about what time it was.
    /// </remarks>
    public DateTimeOffset CivilInstantAt(double simulatedSeconds)
    {
        if (!double.IsFinite(simulatedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(simulatedSeconds), simulatedSeconds,
                                                  "A simulated instant has to be a number.");
        }

        return CivilDateTime.AddTicks((long)Math.Round(simulatedSeconds * TimeSpan.TicksPerSecond));
    }

    /// <summary>A civil instant written as the epoch writes one: ISO-8601 with its offset.</summary>
    public static string FormatCivil(DateTimeOffset instant) =>
        instant.ToString(instant.Ticks % TimeSpan.TicksPerSecond == 0
                             ? "yyyy-MM-dd'T'HH:mm:ssK"
                             : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
                         CultureInfo.InvariantCulture);

    /// <summary>A civil instant written in UTC, with the <c>Z</c> designator.</summary>
    public static string FormatUtc(DateTimeOffset instant)
    {
        DateTime utc = instant.UtcDateTime;
        return utc.ToString(utc.Ticks % TimeSpan.TicksPerSecond == 0
                                ? "yyyy-MM-dd'T'HH:mm:ss'Z'"
                                : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                            CultureInfo.InvariantCulture);
    }

    /// <summary>What this epoch is, in one line.</summary>
    public override string ToString() =>
        $"t = 0 is {CivilDateTimeText} ({FormatUtc(UtcDateTime)}), UTC{FormatOffset(UtcOffset)}"
        + $"{(DstInEffect ? " including daylight saving" : string.Empty)}, calendar "
        + $"{(CalendarAdvances ? "advances" : "held at the epoch's date")}"
        + $"{(TimeZoneId is { Length: > 0 } zone ? $", {zone}" : string.Empty)}";

    /// <summary>An offset as a signed <c>hh:mm</c>.</summary>
    public static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture);

    private static SolarEpoch Read(JsonElement root)
    {
        List<string> problems = [];

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (Array.IndexOf(KnownFields, property.Name) < 0)
            {
                problems.Add($"'{property.Name}' is not an epoch field. A field this consumer does "
                             + "not read is one somebody will later believe was honoured");
            }
        }

        RequireVersion(root, problems);
        double? offsetHours = RequireOffset(root, problems);
        DateTimeOffset? civil = RequireInstant(root, "civil_datetime", problems, out bool civilIsUtcDesignator);
        DateTimeOffset? utc = RequireInstant(root, "utc_datetime", problems, out _);
        bool? calendarAdvances = RequireBoolean(root, "calendar_advances", problems);
        bool? dstInEffect = RequireBoolean(root, "dst_in_effect", problems);
        string? zone = OptionalString(root, "time_zone_id", problems);
        string? note = OptionalString(root, "note", problems);

        if (civil is { } civilInstant && offsetHours is { } declaredOffset)
        {
            double carried = civilInstant.Offset.TotalHours;
            if (Math.Abs(carried - declaredOffset) > 1e-9)
            {
                problems.Add($"civil_datetime carries the offset {FormatOffset(civilInstant.Offset)} "
                             + $"and utc_offset_hours says {declaredOffset:0.##} h. They are two "
                             + "declarations of one fact and must agree");
            }
            else if (civilIsUtcDesignator && declaredOffset != 0.0)
            {
                problems.Add("civil_datetime uses the Z designator for a site whose offset is not "
                             + "zero; write the numeric offset");
            }
        }

        if (civil is { } civilAt && utc is { } utcAt)
        {
            if (utcAt.Offset != TimeSpan.Zero)
            {
                problems.Add($"utc_datetime carries the offset {FormatOffset(utcAt.Offset)}; it must "
                             + "be written in UTC, with Z or +00:00");
            }
            else
            {
                TimeSpan disagreement = utcAt.UtcDateTime - civilAt.UtcDateTime;
                if (Math.Abs(disagreement.TotalSeconds) >= 1.0)
                {
                    bool backwards = Math.Abs(disagreement.TotalSeconds
                                              - (2.0 * civilAt.Offset.TotalSeconds)) < 1.0
                                     && civilAt.Offset != TimeSpan.Zero;
                    problems.Add(
                        $"utc_datetime is {FormatUtc(utcAt)} but civil_datetime "
                        + $"{FormatCivil(civilAt)} is {FormatUtc(civilAt)} in UTC, "
                        + $"{Math.Abs(disagreement.TotalHours):0.####} h "
                        + $"{(disagreement > TimeSpan.Zero ? "earlier" : "later")}"
                        + (backwards
                            ? $": the {FormatOffset(civilAt.Offset)} offset was applied in the wrong "
                              + "direction. Local time minus the offset is UTC"
                            : string.Empty));
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new CoSimSessionRefusedException(
                "The scenario epoch is refused. "
                + string.Join("; ", problems.Select((problem, index) => $"({index + 1}) {problem}"))
                + ".");
        }

        return new SolarEpoch(civil!.Value, utc!.Value, calendarAdvances!.Value, dstInEffect!.Value,
                              zone, note, Canonicalise(root));
    }

    private static void RequireVersion(JsonElement root, List<string> problems)
    {
        if (!root.TryGetProperty("epoch_version", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int version))
        {
            problems.Add("epoch_version is required and must be an integer");
        }
        else if (version != SupportedVersion)
        {
            problems.Add($"epoch_version {version} is not one this consumer implements "
                         + $"({SupportedVersion}); a time declaration is never read in part");
        }
    }

    private static double? RequireOffset(JsonElement root, List<string> problems)
    {
        if (!root.TryGetProperty("utc_offset_hours", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double hours)
            || !double.IsFinite(hours))
        {
            problems.Add("utc_offset_hours is required and must be a number of hours, such as 3.5 "
                         + "for +03:30");
            return null;
        }

        bool wholeQuarterHours = Math.Abs((hours * 4.0) - Math.Round(hours * 4.0)) < 1e-9;
        if (!wholeQuarterHours)
        {
            problems.Add($"utc_offset_hours {hours} is not a whole number of quarter hours. Civil "
                         + "offsets are, including the half- and quarter-hour zones");
        }

        if (hours < EarliestOffsetHours || hours > LatestOffsetHours)
        {
            problems.Add($"utc_offset_hours {hours} is outside -12 to +14, which is every civil "
                         + "offset in use and the range the engine's sun would otherwise clamp to "
                         + "without saying so");
        }

        return wholeQuarterHours && hours >= EarliestOffsetHours && hours <= LatestOffsetHours
            ? hours
            : null;
    }

    private static DateTimeOffset? RequireInstant(JsonElement root,
                                                  string field,
                                                  List<string> problems,
                                                  out bool usedUtcDesignator)
    {
        usedUtcDesignator = false;
        if (!root.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{field} is required and must be an ISO-8601 string with an explicit offset");
            return null;
        }

        string text = value.GetString()!;
        Match match = IsoInstant.Match(text);
        if (!match.Success)
        {
            problems.Add($"{field} '{text}' is not an ISO-8601 date and time of the form "
                         + "2026-03-21T00:00:00+03:30");
            return null;
        }

        if (!match.Groups["z"].Success)
        {
            problems.Add($"{field} '{text}' has no offset. A civil time without one is not a civil "
                         + "time: write the offset even when it is zero");
            return null;
        }

        int year = Parse(match, "y");
        int month = Parse(match, "mo");
        int day = Parse(match, "d");
        int hour = Parse(match, "h");
        int minute = Parse(match, "mi");
        int second = Parse(match, "s");
        if (year < 1 || month < 1 || month > 12)
        {
            problems.Add($"{field} '{text}' names month {month} of year {year}, which is not a "
                         + "calendar month");
            return null;
        }

        if (day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            problems.Add($"{field} '{text}' names day {day} of a month with "
                         + $"{DateTime.DaysInMonth(year, month)} days. The engine's own date setter "
                         + "would clamp it and render a sun at -180 degrees of elevation");
            return null;
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            problems.Add($"{field} '{text}' is not a time of day");
            return null;
        }

        string zone = match.Groups["z"].Value;
        TimeSpan offset;
        if (zone == "Z")
        {
            usedUtcDesignator = true;
            offset = TimeSpan.Zero;
        }
        else
        {
            int offsetHours = int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture);
            int offsetMinutes = int.Parse(zone.AsSpan(4, 2), CultureInfo.InvariantCulture);
            if (offsetMinutes > 59)
            {
                problems.Add($"{field} '{text}' carries an offset with {offsetMinutes} minutes");
                return null;
            }

            offset = new TimeSpan(offsetHours, offsetMinutes, 0);
            if (zone[0] == '-')
            {
                offset = -offset;
            }

            if (offset.TotalHours < EarliestOffsetHours || offset.TotalHours > LatestOffsetHours)
            {
                problems.Add($"{field} '{text}' carries an offset outside -12:00 to +14:00");
                return null;
            }
        }

        long fraction = 0;
        if (match.Groups["f"].Success)
        {
            string digits = match.Groups["f"].Value[1..].PadRight(7, '0');
            fraction = long.Parse(digits, CultureInfo.InvariantCulture);
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, offset).AddTicks(fraction);
        }
        catch (ArgumentOutOfRangeException)
        {
            problems.Add($"{field} '{text}' is outside the representable calendar");
            return null;
        }
    }

    private static bool? RequireBoolean(JsonElement root, string field, List<string> problems)
    {
        if (!root.TryGetProperty(field, out JsonElement value)
            || (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
        {
            problems.Add($"{field} is required and must be true or false; it is never assumed");
            return null;
        }

        return value.GetBoolean();
    }

    private static string? OptionalString(JsonElement root, string field, List<string> problems)
    {
        if (!root.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{field} must be a string when it is given");
            return null;
        }

        return value.GetString();
    }

    private static int Parse(Match match, string group) =>
        int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// The object as Python's <c>json.dumps(epoch, sort_keys=True, indent=2)</c> writes it after
    /// <c>json.loads</c>: keys in code-point order, two-space indent, non-ASCII escaped, an integer
    /// written as an integer and a real number in Python's shortest round-trip form.
    /// </summary>
    private static string Canonicalise(JsonElement root)
    {
        List<JsonProperty> properties = [.. root.EnumerateObject()];
        properties.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));

        var text = new StringBuilder("{");
        for (int index = 0; index < properties.Count; index++)
        {
            text.Append(index == 0 ? "\n  " : ",\n  ");
            text.Append(PythonString(properties[index].Name)).Append(": ");
            JsonElement value = properties[index].Value;
            text.Append(value.ValueKind switch
            {
                JsonValueKind.String => PythonString(value.GetString()!),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                JsonValueKind.Number => PythonNumber(value),
                _ => value.GetRawText(),
            });
        }

        return text.Append(properties.Count == 0 ? "}" : "\n}").ToString();
    }

    private static string PythonNumber(JsonElement value)
    {
        string raw = value.GetRawText();
        return raw.IndexOfAny(['.', 'e', 'E']) < 0
            ? long.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : PythonFloat(value.GetDouble());
    }

    /// <summary>A double as Python's <c>repr</c> writes it: shortest round trip, never an integer.</summary>
    private static string PythonFloat(double value)
    {
        string shortest = value.ToString("R", CultureInfo.InvariantCulture);
        int exponentAt = shortest.IndexOf('E');
        if (exponentAt < 0)
        {
            return shortest.Contains('.') ? shortest : shortest + ".0";
        }

        // Python switches to scientific notation outside 1e-4 <= |x| < 1e16 and writes the exponent
        // with a sign and at least two digits.
        string mantissa = shortest[..exponentAt];
        int exponent = int.Parse(shortest[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        if (exponent is >= -4 and < 16)
        {
            string fixedForm = ((decimal)value).ToString(CultureInfo.InvariantCulture);
            return fixedForm.Contains('.') ? fixedForm : fixedForm + ".0";
        }

        return $"{mantissa}e{(exponent < 0 ? "-" : "+")}{Math.Abs(exponent):00}";
    }

    private static string PythonString(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char character in value)
        {
            switch (character)
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
                default:
                    if (character < ' ' || character > '~')
                    {
                        text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(character);
                    }

                    break;
            }
        }

        return text.Append('"').ToString();
    }
}
