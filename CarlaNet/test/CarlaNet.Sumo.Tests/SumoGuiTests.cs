namespace CarlaNet.Sumo.Tests;

/// <summary>
/// <c>sumo-gui</c> launched in place of <c>sumo</c>: found in the same installation, given the same
/// arguments and the ones it needs to follow a client, and held to the release pin by its own release.
/// </summary>
/// <remarks>
/// Nothing here launches anything. The command line is asserted as it is built, and the installations
/// are either directories of placeholder files or ones whose releases are read by a stand-in for
/// <c>--version</c>, because the point is which binary is asked -- and no real installation on the
/// machine is guaranteed to hold two binaries that disagree.
/// </remarks>
public sealed class SumoGuiTests
{
    private const string Configuration = "scenario.sumocfg";
    private const int Port = 8813;

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "carlanet-sumo-gui-home");

    [Fact]
    public void TheGuiIsLaunchedFromTheSameInstallationInPlaceOfSumo()
    {
        SumoInstallation installation = Probed(_ => null);

        (string sumo, _) = SumoConnection.CommandLine(installation, Configuration, Port, new SumoLaunchOptions());
        (string gui, _) = SumoConnection.CommandLine(installation, Configuration, Port,
                                                     new SumoLaunchOptions { Gui = true });

        Assert.Equal(installation.Sumo, sumo);
        Assert.Equal(installation.SumoGui, gui);
        Assert.Equal(Path.GetDirectoryName(sumo), Path.GetDirectoryName(gui));
        Assert.Equal(OperatingSystem.IsWindows() ? "sumo-gui.exe" : "sumo-gui", Path.GetFileName(gui));
    }

    [Fact]
    public void TheGuiIsGivenSumosArgumentsUnchangedAndWhatItNeedsToFollowAClient()
    {
        SumoInstallation installation = Probed(_ => null);
        string[] overrides = ["--step-length", "0.1"];

        (_, IReadOnlyList<string> sumo) = SumoConnection.CommandLine(
            installation, Configuration, Port, new SumoLaunchOptions { ExtraArguments = overrides });
        (_, IReadOnlyList<string> gui) = SumoConnection.CommandLine(
            installation, Configuration, Port, new SumoLaunchOptions { ExtraArguments = overrides, Gui = true });

        // The configuration and the port first, the GUI's own arguments next, and the caller's
        // overrides last, in both: the GUI's command line is sumo's with the GUI arguments inserted.
        string[] sumoExpected = ["-c", Configuration, "--remote-port", "8813", "--step-length", "0.1"];
        string[] guiExpected =
        [
            "-c", Configuration, "--remote-port", "8813",
            "--start", "--quit-on-end", "--delay", "0", "--message-log", "stdout", "--error-log", "stderr",
            "--step-length", "0.1",
        ];
        Assert.Equal(sumoExpected, sumo);
        Assert.Equal(guiExpected, gui);
        Assert.Equal(sumo, gui.Where((_, index) => index < 4 || index >= 4 + SumoConnection.GuiArguments.Count));
    }

    [Fact]
    public void SumoIsGivenNoneOfTheGuisArguments()
    {
        (_, IReadOnlyList<string> sumo) = SumoConnection.CommandLine(
            Probed(_ => null), Configuration, Port, new SumoLaunchOptions());

        Assert.DoesNotContain("--start", sumo);
        Assert.DoesNotContain("--quit-on-end", sumo);
        Assert.DoesNotContain("--delay", sumo);
        Assert.DoesNotContain("--error-log", sumo);
    }

    [Fact]
    public void TheGuiIsLookedForInTheInstallationsOwnBinaryDirectory()
    {
        string home = Path.Combine(Path.GetTempPath(), "carlanet-sumo-gui-" + Guid.NewGuid().ToString("n"));
        string bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, Executable("sumo")), []);
        try
        {
            SumoInstallation installation = SumoInstallation.At(home);

            Assert.Equal(Path.Combine(installation.BinaryDirectory, Executable("sumo-gui")), installation.SumoGui);
            Assert.False(installation.HasSumoGui);

            File.WriteAllBytes(installation.SumoGui, []);
            Assert.True(installation.HasSumoGui);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void AnExecutablesReleaseIsProbedFromThatExecutableAndOnlyOnce()
    {
        List<string> asked = [];
        SumoInstallation installation = Probed(executable =>
        {
            asked.Add(executable);
            return Path.GetFileNameWithoutExtension(executable) == "sumo-gui" ? "1.27.1" : "1.27.0";
        });

        Assert.Equal("1.27.1", installation.ReleaseOf(SumoInstallation.SumoGuiName));
        Assert.Equal("1.27.0", installation.Release);
        Assert.Equal("1.27.1", installation.ReleaseOf(SumoInstallation.SumoGuiName));

        string[] expected = [installation.SumoGui, installation.Sumo];
        Assert.Equal(expected, asked);
    }

    [Fact]
    public void ThePinIsTheGuisOwnReleaseEvenWhereSumoAgreesWithTheWorld()
    {
        // sumo is the world's release and sumo-gui is not: a check made for the GUI refuses, because
        // it is the GUI that would run.
        SumoInstallation installation = Probed(executable =>
            Path.GetFileNameWithoutExtension(executable) == "sumo-gui" ? "1.27.1" : "1.27.0");

        SumoReleaseCheck gui = SumoReleaseCheck.Of(installation, SumoInstallation.SumoGuiName,
                                                   "Eclipse SUMO netconvert 1.27.0", allowMismatch: false);
        SumoReleaseCheck sumo = SumoReleaseCheck.Of(installation, "Eclipse SUMO netconvert 1.27.0",
                                                    allowMismatch: false);

        Assert.True(gui.Refused);
        Assert.Equal("1.27.1", gui.Release);
        Assert.Equal(installation.SumoGui, gui.Binary);
        Assert.Contains(installation.SumoGui, gui.ToString(), StringComparison.Ordinal);
        Assert.True(sumo.Agrees);
        Assert.Equal(installation.Sumo, sumo.Binary);
    }

    [Fact]
    public void AGuiOfTheWorldsReleaseRunsEvenWhereSumoWouldNot()
    {
        SumoInstallation installation = Probed(executable =>
            Path.GetFileNameWithoutExtension(executable) == "sumo-gui" ? "1.27.0" : "1.27.1");

        SumoReleaseCheck gui = SumoReleaseCheck.Of(installation, SumoInstallation.SumoGuiName,
                                                   "Eclipse SUMO netconvert 1.27.0", allowMismatch: false);

        Assert.Equal(SumoReleaseAgreement.SameRelease, gui.Agreement);
    }

    [Fact]
    public void AGuiWhoseReleaseCannotBeReadIsRefusedAgainstARecordedConverter()
    {
        SumoInstallation installation = Probed(executable =>
            Path.GetFileNameWithoutExtension(executable) == "sumo-gui" ? null : "1.27.0");

        SumoReleaseCheck gui = SumoReleaseCheck.Of(installation, SumoInstallation.SumoGuiName,
                                                   "Eclipse SUMO netconvert 1.27.0", allowMismatch: false);

        Assert.True(gui.Refused);
        Assert.Null(gui.Release);
        Assert.Contains("unreadable", gui.Installation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuiPrintsItsVersionLineInTheFormTheReleaseIsReadFrom()
    {
        // guisim_main.cpp names the application "Eclipse SUMO GUI " VERSION_STRING, and --version prints
        // that name as its first line; a parser that wanted a lower-case tool name would read nothing.
        Assert.Equal("1.27.0", SumoRelease.FromToolOutput("Eclipse SUMO GUI 1.27.0\n Build features: Windows"));
    }

    /// <summary>An installation at a fixed path whose releases <paramref name="probe"/> supplies.</summary>
    private static SumoInstallation Probed(Func<string, string?> probe) => new(Home, "explicit", probe);

    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
}
