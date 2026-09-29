namespace CarlaNet.CoSim;

/// <summary>
/// Establishes that a route SUMO cannot follow stops the run, and refuses a scenario whose
/// configuration tells SUMO to carry on past one.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> By default SUMO meets a route it cannot follow -- a missing connection between two
/// of its edges -- by quitting with an error that names the vehicle and the edges, and the session stops
/// with it, quoting SUMO. <c>ignore-route-errors</c> turns that off, and what SUMO does instead is not
/// visible to anything reading it. Measured on the fixture network with SUMO 1.27.0: a vehicle routed
/// from <c>ahead</c> to <c>approach</c>, which no connection joins, was inserted, drove <c>ahead</c>
/// and stood at its end until the run ended, with nothing on SUMO's console and its end-of-run
/// statistics counting it as running. Rendered, that is a vehicle parked at a junction that nothing
/// authored, and its truth says it chose to stand there. A run whose routes fail is a scenario defect
/// the author needs to see, so the option is refused rather than accepted with a note.</para>
///
/// <para><b>How SUMO reads it.</b> <c>ignore-route-errors</c> is a boolean registered with no synonym
/// (<c>MSFrame.cpp:393</c>), true for <c>1</c>, <c>yes</c>, <c>true</c>, <c>on</c>, <c>x</c> or
/// <c>t</c> and false for <c>0</c>, <c>no</c>, <c>false</c>, <c>off</c>, <c>-</c> or <c>f</c>, in any
/// case (<c>StringUtils::toBool</c>); anything else SUMO refuses.</para>
///
/// <para><b>What it cannot see.</b> A route that becomes impossible only at run time -- a reroute that
/// finds no path -- is handled by SUMO with a warning and the vehicle keeps its old route; the session
/// records SUMO's warnings on the run report, which is where that shows.</para>
/// </remarks>
public sealed class RouteErrorCheck
{
    /// <summary>The option, as SUMO registers it.</summary>
    public const string OptionName = "ignore-route-errors";

    private static readonly string[] TrueWords = ["1", "yes", "true", "on", "x", "t"];
    private static readonly string[] FalseWords = ["0", "no", "false", "off", "-", "f"];

    private RouteErrorCheck(string? declared)
    {
        Declared = declared;
    }

    /// <summary>The value the configuration sets, as written, or null where it sets none.</summary>
    public string? Declared { get; }

    /// <summary>
    /// Refuse a configuration that sets <c>ignore-route-errors</c> to true, more than once, or to
    /// something SUMO does not read as a boolean.
    /// </summary>
    /// <param name="scenarioPath">The scenario's SUMO configuration.</param>
    /// <exception cref="CoSimSessionRefusedException">It does.</exception>
    public static RouteErrorCheck Require(string scenarioPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        SumoConfiguration configuration = SumoConfiguration.Load(
            scenarioPath, "whether a route SUMO cannot follow stops the run cannot be established");
        IReadOnlyList<string> values = configuration.ValuesOf(OptionName);
        if (values.Count > 1)
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} sets {OptionName} {values.Count} times ("
                + string.Join(", ", values.Select(value => $"'{value}'"))
                + "). SUMO refuses an option set twice.");
        }

        if (values.Count == 0)
        {
            return new RouteErrorCheck(null);
        }

        string declared = values[0];
        string word = declared.Trim().ToLowerInvariant();
        if (FalseWords.Contains(word))
        {
            return new RouteErrorCheck(declared);
        }

        if (!TrueWords.Contains(word))
        {
            throw new CoSimSessionRefusedException(
                $"The scenario {scenarioPath} sets {OptionName} to '{declared}', which SUMO does not read "
                + "as true or false, so SUMO would refuse it too.");
        }

        throw new CoSimSessionRefusedException(
            $"The scenario {scenarioPath} sets {OptionName} to '{declared}'. SUMO then keeps a vehicle "
            + "whose route it cannot follow on the network, standing at the end of the last edge it can "
            + "reach, and says nothing: it would render as a parked vehicle nothing authored, with truth "
            + "saying it chose to stand there. Without the option SUMO stops at the route and names it, "
            + "and the run stops with it, which is how a scenario defect reaches its author. SUMO has not "
            + "been started. Remove the option; the scenario compiler never writes it.");
    }

    /// <summary>What was found, in the report's words.</summary>
    public override string ToString() =>
        "a route SUMO cannot follow stops the run ("
        + (Declared is null ? $"{OptionName} not set" : $"{OptionName} '{Declared}'")
        + ")";
}
