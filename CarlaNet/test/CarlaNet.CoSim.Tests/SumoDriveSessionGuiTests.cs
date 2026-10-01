using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session asked to launch <c>sumo-gui</c> in place of <c>sumo</c> refuses an installation without
/// one before anything starts, and holds the GUI binary itself to the release pin.
/// </summary>
/// <remarks>
/// Every installation here is a directory of placeholder files: a GUI must never be opened by a test,
/// and a real installation that has <c>sumo-gui</c> staged would open one the moment a start got past
/// its checks. A placeholder cannot report a release, so the tests that reach the release comparison
/// read which binary it was asked of from the refusal. No CARLA world is involved.
/// </remarks>
public sealed class SumoDriveSessionGuiTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionGuiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AnInstallationWithNoGuiIsRefusedBeforeSumoStartsNamingTheFileAndTheSetupScript()
    {
        using SyntheticWorld world = ConvertedBy("Eclipse SUMO netconvert 1.27.0");
        using PlaceholderInstallation placeholder = PlaceholderInstallation.Write(withGui: false);
        List<string> console = [];
        SumoDriveSessionOptions options = Options(world, placeholder.Home);
        options.SumoGui = true;
        options.SumoOutput = console.Add;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Contains(SumoInstallation.At(placeholder.Home).SumoGui, refused.Message,
                        StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CarlaSetup.ps1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("CarlaSetup.sh", refused.Message, StringComparison.Ordinal);
        Assert.Contains("--sumo-gui", refused.Message, StringComparison.Ordinal);
        Assert.Empty(console);
    }

    [Fact]
    public void TheReleaseComparedIsTheGuiBinarysOwn()
    {
        // Both placeholders are unreadable, so both starts are refused by the release comparison; what
        // tells them apart is which binary each says it asked.
        using SyntheticWorld world = ConvertedBy("Eclipse SUMO netconvert 1.27.0");
        using PlaceholderInstallation placeholder = PlaceholderInstallation.Write(withGui: true);
        SumoInstallation installation = SumoInstallation.At(placeholder.Home);
        List<string> console = [];

        SumoDriveSessionOptions withGui = Options(world, placeholder.Home);
        withGui.SumoGui = true;
        withGui.SumoOutput = console.Add;
        CoSimSessionRefusedException gui = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(withGui));

        SumoDriveSessionOptions withoutGui = Options(world, placeholder.Home);
        withoutGui.SumoOutput = console.Add;
        CoSimSessionRefusedException sumo = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(withoutGui));

        _output.WriteLine(gui.Message);
        Assert.Equal(CoSimSessionStage.Validation, gui.Stage);
        Assert.Contains($"would launch, {installation.SumoGui}, is of a release that could not be read",
                        gui.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"would launch, {installation.Sumo}, is", sumo.Message,
                        StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(installation.SumoGui, sumo.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(console);
    }

    [Fact]
    public void TheSessionLaunchesTheGuiBinaryInPlaceOfSumo()
    {
        // A world that records no converter passes the release comparison unchecked, so each start
        // gets as far as the launch -- of a placeholder, which the operating system will not run. The
        // refusal names the file it tried.
        using SyntheticWorld world = ConvertedBy(string.Empty);
        using PlaceholderInstallation placeholder = PlaceholderInstallation.Write(withGui: true);
        SumoInstallation installation = SumoInstallation.At(placeholder.Home);

        SumoDriveSessionOptions withGui = Options(world, placeholder.Home);
        withGui.SumoGui = true;
        CoSimSessionRefusedException gui = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(withGui));
        CoSimSessionRefusedException sumo = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(Options(world, placeholder.Home)));

        _output.WriteLine(gui.Message);
        Assert.Equal(CoSimSessionStage.Launch, gui.Stage);
        Assert.Contains($"Could not start {installation.SumoGui}", gui.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Could not start {installation.Sumo}", sumo.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fixture world, recording <paramref name="netconvertVersion"/> as its converter.</summary>
    private static SyntheticWorld ConvertedBy(string netconvertVersion) =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!", netconvertVersion);

    /// <summary>A session on the fixture scenario, launching from <paramref name="sumoHome"/>.</summary>
    private static SumoDriveSessionOptions Options(SyntheticWorld world, string sumoHome) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
            SumoHome = sumoHome,
        };

    /// <summary>
    /// A directory shaped like a SUMO installation whose binaries are empty files: enough to be
    /// named, never enough to run.
    /// </summary>
    private sealed class PlaceholderInstallation : IDisposable
    {
        private PlaceholderInstallation(string home)
        {
            Home = home;
        }

        public string Home { get; }

        public static PlaceholderInstallation Write(bool withGui)
        {
            string home = Path.Combine(Path.GetTempPath(), "carlanet-placeholder-sumo-" + Guid.NewGuid().ToString("n"));
            string bin = Path.Combine(home, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllBytes(Path.Combine(bin, Executable(SumoInstallation.SumoName)), []);
            if (withGui)
            {
                File.WriteAllBytes(Path.Combine(bin, Executable(SumoInstallation.SumoGuiName)), []);
            }

            return new PlaceholderInstallation(home);
        }

        public void Dispose() => Directory.Delete(Home, recursive: true);

        private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
    }
}
