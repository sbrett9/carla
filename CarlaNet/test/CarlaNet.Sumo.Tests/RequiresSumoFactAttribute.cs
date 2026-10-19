using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// A test that needs a real SUMO installation on the machine running it.
/// </summary>
/// <remarks>
/// It is skipped rather than failed where none resolves, so the rest of the CarlaNet suite still
/// runs on a machine that has never built the SUMO toolchain -- which is also the point of
/// <c>CarlaNet.Sumo</c> having no build-time dependency on SUMO at all. The skip message names
/// every directory that was tried, because "skipped" with no reason is indistinguishable from a
/// misconfigured machine.
/// </remarks>
internal sealed class RequiresSumoFactAttribute : FactAttribute
{
    /// <param name="withPythonTools">
    /// Also require SUMO's <c>tools/traci</c>, its own reference client in Python. A bare binary
    /// directory on the executable search path resolves as an installation and does not carry it.
    /// </param>
    public RequiresSumoFactAttribute(bool withPythonTools = false)
    {
        SumoInstallation? installation = SumoInstallation.Locate();
        if (installation is null)
        {
            Skip = "No SUMO installation resolved. Looked in: "
                   + (SumoInstallation.SearchedDirectories.Count == 0
                       ? "nothing was given"
                       : string.Join(", ", SumoInstallation.SearchedDirectories));
            return;
        }

        if (withPythonTools && !File.Exists(ReferenceClient(installation)))
        {
            Skip = $"The SUMO installation at {installation.Home} carries no tools/traci, so SUMO's "
                   + "own reference client is not there to check against.";
        }
    }

    /// <summary>SUMO's own TraCI client in Python, inside an installation's tools directory.</summary>
    public static string ReferenceClient(SumoInstallation installation) =>
        Path.Combine(installation.ToolsDirectory, "traci", "constants.py");
}
