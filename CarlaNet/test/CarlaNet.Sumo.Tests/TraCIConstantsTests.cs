using System.Globalization;
using System.Reflection;
using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// Checks the translated constant table against the SUMO installation this machine resolves.
/// </summary>
/// <remarks>
/// <para>This is the check the plan asks for in as many words: the table is pinned to a SUMO
/// release, and it should be checkable against that release mechanically. It reads
/// <c>tools/traci/constants.py</c> from the resolved installation -- SUMO's own file, which SUMO
/// generates from <c>src/libsumo/TraCIConstants.h</c> -- and compares it name for name and value
/// for value.</para>
///
/// <para>The failure it exists to catch is a SUMO pin that moves without the table being
/// regenerated. A stale identifier does not announce itself: it addresses a different variable, and
/// what comes back decodes cleanly into the wrong thing.</para>
/// </remarks>
public class TraCIConstantsTests
{
    [RequiresSumoFact(withPythonTools: true)]
    public void EveryTranslatedConstantMatchesTheStagedSumo()
    {
        IReadOnlyDictionary<string, double> upstream = ReadUpstreamConstants();

        List<string> disagreements = [];
        foreach ((string name, double value) in Translated())
        {
            if (!upstream.TryGetValue(name, out double expected))
            {
                disagreements.Add($"{name} is in the translated table and not in constants.py");
            }
            else if (expected != value)
            {
                disagreements.Add($"{name} is {value} here and {expected} in constants.py");
            }
        }

        Assert.Empty(disagreements);
    }

    [RequiresSumoFact(withPythonTools: true)]
    public void NothingUpstreamIsMissingFromTheTranslatedTable()
    {
        IReadOnlyDictionary<string, double> upstream = ReadUpstreamConstants();
        HashSet<string> translated = [.. Translated().Select(entry => entry.Name)];
        string[] missing = [.. upstream.Keys.Where(name => !translated.Contains(name)).Order()];
        Assert.Empty(missing);
    }

    /// <summary>
    /// The table is only as good as the check, so the check is pointed at something that is not
    /// there and watched to say so. A name that exists only on one side is exactly the shape of a
    /// constant renamed by a SUMO release.
    /// </summary>
    [RequiresSumoFact(withPythonTools: true)]
    public void TheCheckSeesAConstantThatIsNotUpstream()
    {
        IReadOnlyDictionary<string, double> upstream = ReadUpstreamConstants();
        Assert.NotEmpty(upstream);
        Assert.True(upstream.ContainsKey(nameof(TraCIConstants.VAR_POSITION)));
        Assert.False(upstream.ContainsKey("VAR_POSITION_IMAGINARY"));
    }

    /// <summary>
    /// The handful the wire path depends on most, asserted outright so that a failure names the
    /// identifier rather than a count. These are the numbers a frame is unreadable without, and
    /// they need no SUMO installation to check.
    /// </summary>
    [Fact]
    public void TheIdentifiersTheFrameCodecDependsOnAreWhatSumoDocuments()
    {
        Assert.Equal(0x00, TraCIConstants.CMD_GETVERSION);
        Assert.Equal(0x02, TraCIConstants.CMD_SIMSTEP);
        Assert.Equal(0x7F, TraCIConstants.CMD_CLOSE);
        Assert.Equal(0xA4, TraCIConstants.CMD_GET_VEHICLE_VARIABLE);
        Assert.Equal(0xC4, TraCIConstants.CMD_SET_VEHICLE_VARIABLE);
        Assert.Equal(0xD4, TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE);
        Assert.Equal(0xE4, TraCIConstants.RESPONSE_SUBSCRIBE_VEHICLE_VARIABLE);
        Assert.Equal(0xAB, TraCIConstants.CMD_GET_SIM_VARIABLE);
        Assert.Equal(0x0B, TraCIConstants.TYPE_DOUBLE);
        Assert.Equal(0x0C, TraCIConstants.TYPE_STRING);
        Assert.Equal(0x01, TraCIConstants.POSITION_2D);
        Assert.Equal(0x5B, TraCIConstants.VAR_SIGNALS);

        // A response identifier is its command plus 0x10, which is how a subscription answer is
        // matched to the subscription that asked for it.
        Assert.Equal(TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE + 0x10,
                     TraCIConstants.RESPONSE_SUBSCRIBE_VEHICLE_VARIABLE);
    }

    private static IEnumerable<(string Name, double Value)> Translated() =>
        typeof(TraCIConstants)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (field.Name, Convert.ToDouble(field.GetRawConstantValue(),
                                                           CultureInfo.InvariantCulture)));

    /// <summary>
    /// Read SUMO's own constants.py, resolving the shift and or-fold expressions it uses for the
    /// lane-change flags.
    /// </summary>
    private static IReadOnlyDictionary<string, double> ReadUpstreamConstants()
    {
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        string path = Path.Combine(installation.ToolsDirectory, "traci", "constants.py");

        Dictionary<string, double> constants = [];
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('#'))
            {
                continue;
            }

            int equals = line.IndexOf(" = ", StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            string name = line[..equals];
            if (!name.All(character => char.IsLetterOrDigit(character) || character == '_'))
            {
                continue;
            }

            if (TryEvaluate(line[(equals + 3)..].Trim().Trim('(', ')'), constants, out double value))
            {
                constants[name] = value;
            }
        }

        return constants;
    }

    /// <summary>
    /// Evaluate the three forms constants.py uses: a literal, a left shift, and an or-fold over
    /// names defined above it in the file. Anything else is not a constant and is skipped.
    /// </summary>
    private static bool TryEvaluate(string expression,
                                    IReadOnlyDictionary<string, double> defined,
                                    out double value)
    {
        value = 0;
        string[] terms = expression.Split('|', StringSplitOptions.TrimEntries);
        if (terms.Length == 1 && !terms[0].Contains("<<", StringComparison.Ordinal))
        {
            return TryTerm(terms[0], defined, out value);
        }

        long folded = 0;
        foreach (string term in terms)
        {
            string[] shift = term.Split("<<", StringSplitOptions.TrimEntries);
            if (shift.Length > 2 || !TryTerm(shift[0], defined, out double left))
            {
                return false;
            }

            if (shift.Length == 1)
            {
                folded |= (long)left;
                continue;
            }

            if (!TryTerm(shift[1], defined, out double places))
            {
                return false;
            }

            folded |= (long)left << (int)places;
        }

        value = folded;
        return true;
    }

    private static bool TryTerm(string term, IReadOnlyDictionary<string, double> defined, out double value)
    {
        bool negative = term.StartsWith('-');
        string magnitude = negative ? term[1..] : term;
        bool parsed;

        if (magnitude.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            parsed = long.TryParse(magnitude[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                                   out long hexadecimal);
            value = hexadecimal;
        }
        else
        {
            parsed = double.TryParse(magnitude, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                     || defined.TryGetValue(magnitude, out value);
        }

        if (!parsed)
        {
            value = 0;
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }
}
