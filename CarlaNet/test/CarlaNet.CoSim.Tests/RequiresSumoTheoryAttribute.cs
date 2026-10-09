using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A theory that needs a real SUMO installation on the machine running it, skipped where none
/// resolves -- as <see cref="RequiresSumoFactAttribute"/> is for a fact.
/// </summary>
internal sealed class RequiresSumoTheoryAttribute : TheoryAttribute
{
    public RequiresSumoTheoryAttribute()
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
