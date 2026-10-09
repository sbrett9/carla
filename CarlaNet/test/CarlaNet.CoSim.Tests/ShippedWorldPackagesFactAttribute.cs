namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A test that reads the world packages the repository's world builds wrote to
/// <c>Build/world-packages</c>.
/// </summary>
/// <remarks>
/// Skipped where they are absent. A generated world package is megabytes of measured terrain and is
/// not committed, so a clone that has never built a world has none; where they are present, they
/// are the only real packages there are to point a check at.
/// </remarks>
internal sealed class ShippedWorldPackagesFactAttribute : FactAttribute
{
    public ShippedWorldPackagesFactAttribute()
    {
        if (Arapahoe is null || Gardnerville is null || Bahonar is null)
        {
            Skip = "The shipped world packages are not in Build/world-packages: build the worlds with "
                   + "--emit-world-package to run this.";
        }
    }

    /// <summary>The Arapahoe I-25 corridor, or null where it has not been built.</summary>
    public static string? Arapahoe => Find("Arapahoe_I25.cwp");

    /// <summary>Gardnerville's Centerville Lane, or null where it has not been built.</summary>
    public static string? Gardnerville => Find("Gardnerville_Centerville_Lane.cwp");

    /// <summary>Shahid Bahonar port, or null where it has not been built.</summary>
    public static string? Bahonar => Find("Shahid_Bahonar_Port.cwp");

    /// <summary>The package of that name, looked for above the test's own output directory.</summary>
    private static string? Find(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Build", "world-packages", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
