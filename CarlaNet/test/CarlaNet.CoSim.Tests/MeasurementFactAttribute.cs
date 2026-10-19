namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A measurement: a test that reports what something costs rather than asserting a behaviour.
/// </summary>
/// <remarks>
/// <para>Opt-in, and skipped unless asked for. A timing asserted in CI is a flaky test, and what a
/// measurement establishes is a number to put beside another number, so it runs when someone wants the
/// number, on a machine whose state they know, and not on every build.</para>
///
/// <para>Set <c>CARLANET_COSIM_MEASURE</c> to <c>1</c> to run them.</para>
/// </remarks>
internal sealed class MeasurementFactAttribute : FactAttribute
{
    public const string MeasureVariable = "CARLANET_COSIM_MEASURE";

    public MeasurementFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(MeasureVariable) != "1")
        {
            Skip = $"Set {MeasureVariable} to 1 to run the measurements.";
        }
    }
}
