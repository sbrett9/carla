using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A run against whatever scenario and world the environment names, rather than the fixture.
/// </summary>
/// <remarks>
/// <para>Opt-in, and skipped when nothing is named. A generated world package is tens of megabytes
/// of measured terrain and is not committed, and a real scenario is neither small enough to commit
/// nor stable enough to assert timings from. Naming them from outside keeps the mechanism committed
/// and reviewable while what it is pointed at stays a decision made at the moment of running it.</para>
///
/// <para>Set <c>CARLANET_COSIM_SCENARIO</c> to a <c>.sumocfg</c> and
/// <c>CARLANET_COSIM_WORLD_PACKAGE</c> to the <c>.cwp</c> the world was built as. Optionally
/// <c>CARLANET_COSIM_STEPS</c>, <c>CARLANET_COSIM_WARMUP</c> and <c>CARLANET_COSIM_STEP_LENGTH</c>.</para>
/// </remarks>
internal sealed class NamedCoSimRunFactAttribute : FactAttribute
{
    public const string ScenarioVariable = "CARLANET_COSIM_SCENARIO";
    public const string WorldPackageVariable = "CARLANET_COSIM_WORLD_PACKAGE";
    public const string StepsVariable = "CARLANET_COSIM_STEPS";
    public const string WarmUpVariable = "CARLANET_COSIM_WARMUP";
    public const string StepLengthVariable = "CARLANET_COSIM_STEP_LENGTH";

    public NamedCoSimRunFactAttribute()
    {
        if (Scenario is null || WorldPackage is null)
        {
            Skip = $"Set {ScenarioVariable} to a .sumocfg and {WorldPackageVariable} to a .cwp to "
                   + "run a co-simulation session against a real scenario and world.";
            return;
        }

        if (SumoInstallation.Locate() is null)
        {
            Skip = "No SUMO installation resolved. Looked in: "
                   + string.Join(", ", SumoInstallation.SearchedDirectories);
        }
    }

    /// <summary>The scenario named by the environment, or <see langword="null"/>.</summary>
    public static string? Scenario => ExistingFile(ScenarioVariable);

    /// <summary>The world package named by the environment, or <see langword="null"/>.</summary>
    public static string? WorldPackage => ExistingFile(WorldPackageVariable);

    /// <summary>How many SUMO steps to run. Two hundred by default.</summary>
    public static int Steps => (int)Read(StepsVariable, 200);

    /// <summary>The simulated second to fast-forward to before the first world tick.</summary>
    public static double WarmUp => Read(WarmUpVariable, 0.0);

    /// <summary>A SUMO step length to force, or null to run what the scenario authored.</summary>
    public static double? StepLength
    {
        get
        {
            double value = Read(StepLengthVariable, 0.0);
            return value > 0.0 ? value : null;
        }
    }

    private static string? ExistingFile(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
    }

    private static double Read(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double value)
            ? value
            : fallback;
}
