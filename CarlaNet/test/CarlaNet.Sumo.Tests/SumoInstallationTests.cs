using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// Which SUMO this process resolved, and whether it is the release the constant table was
/// translated from.
/// </summary>
public class SumoInstallationTests(ITestOutputHelper output)
{
    /// <summary>The SUMO release <c>TraCIConstants</c> was translated from.</summary>
    private const string PinnedRelease = "1.27.0";

    [Fact]
    public void TheUpwardSearchStartsFromThisAssemblyAsWellAsTheApplication()
    {
        // Two starting points, and the second is what makes the search work at all when the runtime
        // is hosted rather than launched: loaded into another process -- as it is when a Python
        // orchestrator drives the co-simulation bridge -- an application has no base directory, and
        // AppContext.BaseDirectory is the empty string. Starting only from it raised on the empty
        // path rather than answering that no installation resolved.
        string[] roots = [.. SumoInstallation.SearchRoots()];

        Assert.NotEmpty(roots);
        Assert.DoesNotContain(roots, root => string.IsNullOrWhiteSpace(root));
        Assert.Contains(roots, root => root.Contains("CarlaNet.Sumo.Tests", StringComparison.Ordinal));
    }

    [RequiresSumoFact]
    public void TheResolvedInstallationSaysWhichRuleFoundIt()
    {
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        output.WriteLine($"{installation.Home} (found by {installation.Source}, "
                         + $"release {installation.Release})");

        Assert.True(File.Exists(installation.Sumo), $"{installation.Sumo} is not there.");
        Assert.False(string.IsNullOrWhiteSpace(installation.Source));
    }

    /// <summary>
    /// The pin, checked from the binary rather than from the handshake.
    /// </summary>
    /// <remarks>
    /// Reported rather than asserted, and deliberately. More than one SUMO can be installed -- this
    /// machine has two of different releases -- and which one resolves is a property of the machine,
    /// not of the client. What must not be silent is <i>which</i>, and a test that printed nothing
    /// and passed would be exactly that. The assertion that a mismatch would be caught lives in
    /// <see cref="SumoConnectionTests.TheServerReportsTheProtocolVersionTheClientWasWrittenFor"/>,
    /// where the server answers for itself.
    /// </remarks>
    [RequiresSumoFact]
    public void TheResolvedReleaseIsReported()
    {
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        Assert.NotNull(installation.Release);

        if (installation.Release != PinnedRelease)
        {
            output.WriteLine($"Resolved SUMO {installation.Release} at {installation.Home}, and the "
                             + $"constant table was translated from {PinnedRelease}.");
        }
    }

    /// <summary>
    /// Every directory the search tried is recorded, found or not. This is what the not-found
    /// message is built from, and "not found" with no list is indistinguishable from a machine that
    /// was never set up.
    /// </summary>
    [Fact]
    public void TheSearchRecordsEveryDirectoryItTried()
    {
        SumoInstallation.Locate();

        Assert.NotEmpty(SumoInstallation.SearchedDirectories);
        Assert.All(SumoInstallation.SearchedDirectories, entry => Assert.True(Path.IsPathRooted(entry)));
        output.WriteLine(string.Join(Environment.NewLine, SumoInstallation.SearchedDirectories));
    }

    [Fact]
    public void AnExecutableNameGetsThePlatformsExtension()
    {
        SumoInstallation? installation = SumoInstallation.Locate();
        if (installation is null)
        {
            return;
        }

        string expected = OperatingSystem.IsWindows() ? "netconvert.exe" : "netconvert";
        Assert.Equal(expected, Path.GetFileName(installation.Netconvert));
    }
}
