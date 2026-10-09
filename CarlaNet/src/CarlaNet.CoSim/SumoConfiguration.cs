using System.Xml;
using System.Xml.Linq;

namespace CarlaNet.CoSim;

/// <summary>
/// A SUMO configuration file, read the way SUMO reads one, for the checks a session makes before it
/// starts SUMO on it.
/// </summary>
/// <remarks>
/// <para>SUMO takes an option from an element named for the option or for any of its synonyms, at
/// any depth -- the section elements such as <c>&lt;input&gt;</c> are grouping, not structure --
/// with its value in a <c>value</c> or <c>v</c> attribute or, failing both, in the element's text,
/// and it ignores an empty value (<c>OptionsLoader.cpp</c>). A file option's relative path is taken
/// against the configuration's own directory (<c>OptionsCont::relocateFiles</c>), and a file-list
/// option such as <c>route-files</c> holds several paths separated by commas.</para>
///
/// <para><b>What it cannot see.</b> SUMO's substitutions in a value (<c>${...}</c>, a leading
/// <c>~</c>) are taken literally, and nothing passed on SUMO's command line is visible here; the
/// session passes no option a check reads.</para>
/// </remarks>
public sealed class SumoConfiguration
{
    private readonly XDocument _document;

    private SumoConfiguration(string path, XDocument document)
    {
        Path = path;
        Directory = System.IO.Path.GetDirectoryName(path)!;
        _document = document;
    }

    /// <summary>The configuration's full path.</summary>
    public string Path { get; }

    /// <summary>The directory its relative file options are taken against.</summary>
    public string Directory { get; }

    /// <summary>Read a configuration, refusing one that is not there or is not XML.</summary>
    /// <param name="path">The <c>.sumocfg</c>.</param>
    /// <param name="purpose">
    /// What the reading is for, in words a refusal can finish a sentence with: "the network it runs on
    /// cannot be compared with the world's".
    /// </param>
    /// <exception cref="CoSimSessionRefusedException">It cannot be read as XML.</exception>
    public static SumoConfiguration Load(string path, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        string full = System.IO.Path.GetFullPath(path);
        try
        {
            return new SumoConfiguration(full, XDocument.Load(full));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException
                                               or XmlException)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {path} cannot be read as a SUMO configuration, so {purpose}: "
                + unreadable.Message, unreadable);
        }
    }

    /// <summary>
    /// Every non-empty value the configuration sets for an option, under any of the names given, in
    /// document order.
    /// </summary>
    /// <param name="names">The option, its synonyms and its abbreviation, as SUMO registers them.</param>
    public IReadOnlyList<string> ValuesOf(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        List<string> values = [];
        foreach (XElement option in _document.Descendants())
        {
            if (!names.Contains(option.Name.LocalName, StringComparer.Ordinal))
            {
                continue;
            }

            // SUMO takes the value from either attribute, or failing both from the element's text,
            // and ignores an empty one.
            string value = (string?)option.Attribute("value")
                           ?? (string?)option.Attribute("v")
                           ?? option.Value.Trim();
            if (value.Length > 0)
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>
    /// The files a file-list option names, each as a full path taken against the configuration's
    /// directory where it is relative, in the order they are named.
    /// </summary>
    /// <param name="names">The option, its synonyms and its abbreviation.</param>
    public IReadOnlyList<string> FilesOf(params string[] names) =>
        ValuesOf(names)
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries
                                                 | StringSplitOptions.RemoveEmptyEntries))
            .Select(Resolve)
            .ToList();

    /// <summary>A path the configuration names, as a full path, taken against its directory.</summary>
    public string Resolve(string named) => System.IO.Path.GetFullPath(named, Directory);

    /// <summary>
    /// A SUMO time in seconds: seconds, or <c>hh:mm:ss</c>, or <c>dd:hh:mm:ss</c>, as
    /// <c>string2time</c> reads it; false where the text is none of them, which SUMO refuses too.
    /// </summary>
    internal static bool TryParseTime(string text, out double seconds)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] parts = text.Split(':');
        double[] numbers = new double[parts.Length];
        bool parsed = parts.Length is 1 or 3 or 4;
        for (int index = 0; parsed && index < parts.Length; index++)
        {
            parsed = double.TryParse(parts[index].Trim(), System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out numbers[index])
                     && double.IsFinite(numbers[index]);
        }

        seconds = !parsed
            ? 0.0
            : parts.Length switch
            {
                1 => numbers[0],
                3 => (3600.0 * numbers[0]) + (60.0 * numbers[1]) + numbers[2],
                _ => (86400.0 * numbers[0]) + (3600.0 * numbers[1]) + (60.0 * numbers[2]) + numbers[3],
            };
        return parsed;
    }

    /// <summary>SUMO's <c>TIME2STEPS</c>: a time in milliseconds, rounded half away from zero.</summary>
    internal static long Milliseconds(double seconds) =>
        (long)((seconds * 1000.0) + (seconds >= 0.0 ? 0.5 : -0.5));

    /// <summary>
    /// A SUMO boolean, as <c>StringUtils::toBool</c> reads one: true for <c>1</c>, <c>yes</c>,
    /// <c>true</c>, <c>on</c>, <c>x</c> or <c>t</c>, false for <c>0</c>, <c>no</c>, <c>false</c>,
    /// <c>off</c>, <c>-</c> or <c>f</c>, in any case; false where the text is neither. Measured, SUMO 1.27.0
    /// meets such a value in a configuration with an error on its console and runs on under the option's
    /// default.
    /// </summary>
    internal static bool TryParseBoolean(string text, out bool value)
    {
        ArgumentNullException.ThrowIfNull(text);
        string word = text.Trim().ToLowerInvariant();
        value = TrueWords.Contains(word);
        return value || FalseWords.Contains(word);
    }

    private static readonly string[] TrueWords = ["1", "yes", "true", "on", "x", "t"];
    private static readonly string[] FalseWords = ["0", "no", "false", "off", "-", "f"];
}
