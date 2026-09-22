using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// A test that runs whatever scenario the environment names, rather than the fixture network.
/// </summary>
/// <remarks>
/// <para>Opt-in, and skipped when nothing is named. These tests are the ones worth pointing at a
/// real scenario -- a few hundred vehicles on a city network rather than two on a straight edge --
/// and a real scenario is neither small enough to commit nor stable enough to assert timings from
/// in CI. Naming it from outside keeps the mechanism committed and reviewable while the thing it is
/// pointed at stays a decision made at the moment of running it.</para>
///
/// <para>Set <c>CARLANET_SUMO_SCENARIO</c> to a <c>.sumocfg</c>, and optionally
/// <c>CARLANET_SUMO_SCENARIO_WARMUP</c> to the simulated second to fast-forward to before measuring
/// -- a scenario's population takes time to build up, and measuring the empty minutes in front of
/// it measures nothing.</para>
/// </remarks>
internal sealed class NamedScenarioFactAttribute : FactAttribute
{
    /// <summary>Environment variable naming the <c>.sumocfg</c> to run.</summary>
    public const string ScenarioVariable = "CARLANET_SUMO_SCENARIO";

    /// <summary>Environment variable naming the simulated second to fast-forward to first.</summary>
    public const string WarmupVariable = "CARLANET_SUMO_SCENARIO_WARMUP";

    /// <summary>Environment variable naming how many steps to measure or compare.</summary>
    public const string StepsVariable = "CARLANET_SUMO_SCENARIO_STEPS";

    public NamedScenarioFactAttribute()
    {
        if (Scenario is null)
        {
            Skip = $"Set {ScenarioVariable} to a .sumocfg to run this against a real scenario.";
            return;
        }

        if (SumoInstallation.Locate() is null)
        {
            Skip = "No SUMO installation resolved. Looked in: "
                   + string.Join(", ", SumoInstallation.SearchedDirectories);
        }
    }

    /// <summary>The configuration named by the environment, or <see langword="null"/>.</summary>
    public static string? Scenario
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable(ScenarioVariable);
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
    }

    /// <summary>The simulated second to fast-forward to before measuring. Zero by default.</summary>
    public static double Warmup => Read(WarmupVariable, 0.0);

    /// <summary>How many steps to measure or compare. Fifty by default.</summary>
    public static int Steps => (int)Read(StepsVariable, 50);

    private static double Read(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double value)
            ? value
            : fallback;
}
