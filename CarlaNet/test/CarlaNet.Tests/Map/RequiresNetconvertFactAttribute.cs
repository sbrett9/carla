namespace CarlaNet.Tests.Map;

/// <summary>
/// The netconvert a conversion test runs: <c>CARLA_NETCONVERT</c> when set, otherwise the repository's
/// staged build, found by walking upward from the test binaries to a <c>Build/sumo-install</c>.
/// </summary>
internal static class StagedNetconvert
{
    private static readonly Lazy<string?> Resolved = new(Resolve);

    /// <summary>The executable, or null when none is staged.</summary>
    public static string? Executable => Resolved.Value;

    /// <summary>PROJ's data beside it, which a georeferenced conversion needs.</summary>
    public static string? ProjData
        => Executable is null ? null
           : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Executable)!, "..", "share", "proj"));

    /// <summary>Every place looked, for a skip message that says why.</summary>
    public static List<string> Searched { get; } = [];

    private static string? Resolve()
    {
        string name = OperatingSystem.IsWindows() ? "netconvert.exe" : "netconvert";
        string? fromEnvironment = Environment.GetEnvironmentVariable("CARLA_NETCONVERT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            Searched.Add(fromEnvironment);
            return File.Exists(fromEnvironment) ? fromEnvironment : null;
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Build", "sumo-install", "bin", name);
            Searched.Add(candidate);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

/// <summary>
/// A test that runs the real netconvert. Skipped, naming where it looked, on a machine with none
/// staged, so the rest of the suite still runs there.
/// </summary>
internal sealed class RequiresNetconvertFactAttribute : FactAttribute
{
    public RequiresNetconvertFactAttribute()
    {
        if (StagedNetconvert.Executable is null)
            Skip = "No netconvert staged. Looked in: " + string.Join(", ", StagedNetconvert.Searched);
    }
}
