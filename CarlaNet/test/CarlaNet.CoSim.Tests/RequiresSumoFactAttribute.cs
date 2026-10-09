using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A test that needs a real SUMO installation on the machine running it.
/// </summary>
/// <remarks>
/// Skipped rather than failed where none resolves, so the rest of the suite still runs on a machine
/// that has never built the SUMO toolchain. The skip message names every directory that was tried,
/// because "skipped" with no reason is indistinguishable from a misconfigured machine.
/// </remarks>
internal sealed class RequiresSumoFactAttribute : FactAttribute
{
    public RequiresSumoFactAttribute()
    {
        if (SumoInstallation.Locate() is null)
        {
            Skip = "No SUMO installation resolved. Looked in: "
                   + (SumoInstallation.SearchedDirectories.Count == 0
                       ? "nothing was given"
                       : string.Join(", ", SumoInstallation.SearchedDirectories));
        }
    }
}
