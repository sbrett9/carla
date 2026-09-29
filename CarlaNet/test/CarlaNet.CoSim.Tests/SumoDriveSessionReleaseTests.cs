using System.Globalization;
using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session launches the SUMO it was told to, reports it, and refuses one whose release is not the
/// converter the world package records -- before SUMO is started.
/// </summary>
/// <remarks>
/// Every test here names the installation this process resolves -- the repository's pinned build,
/// under a test run from the tree -- and varies what the world package records, so the release under
/// comparison is a real one read from a real <c>sumo</c>, and the mismatch is made by the record
/// rather than by needing a second installation on the machine. No CARLA world is involved: the
/// session ticks a counter.
/// </remarks>
public sealed class SumoDriveSessionReleaseTests
{
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionReleaseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void ASumoOfAnotherReleaseThanTheWorldSConverterIsRefusedNamingBoth()
    {
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        string recorded = $"Eclipse SUMO netconvert {AnotherRelease(installation.Release!)}";
        using SyntheticWorld world = ConvertedBy(recorded);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(Options(world, installation)));

        _output.WriteLine(refused.Message);
        Assert.Contains(recorded, refused.Message, StringComparison.Ordinal);
        Assert.Contains($"release {installation.Release}", refused.Message, StringComparison.Ordinal);
        Assert.Contains(installation.Home, refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--allow-sumo-version-mismatch", refused.Message, StringComparison.Ordinal);
    }

    [RequiresSumoFact]
    public void TheRefusalComesBeforeSumoIsStarted()
    {
        // A configuration that does not exist: had SUMO been launched, it would have failed to load
        // it and said so on its console, and the start would have failed as a TraCI error rather
        // than as this refusal.
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        using SyntheticWorld world = ConvertedBy(
            $"Eclipse SUMO netconvert {AnotherRelease(installation.Release!)}");
        List<string> console = [];
        SumoDriveSessionOptions options = Options(world, installation) with
        {
            ScenarioPath = Path.Combine(world.Directory, "never-written.sumocfg"),
        };
        options.SumoOutput = console.Add;

        Assert.Throws<CoSimSessionRefusedException>(() => SumoDriveSession.Start(options));
        Assert.Empty(console);
    }

    [RequiresSumoFact]
    public void AMismatchTheOperatorAcceptedRunsAndIsRecordedOnTheReport()
    {
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        string other = AnotherRelease(installation.Release!);
        using SyntheticWorld world = ConvertedBy($"Eclipse SUMO netconvert {other}");
        SumoDriveSessionOptions options = Options(world, installation);
        options.AllowSumoVersionMismatch = true;

        using SumoDriveSession session = SumoDriveSession.Start(options);
        Assert.True(session.Advance());

        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.Equal(SumoReleaseAgreement.MismatchAccepted, session.Report.Sumo.Agreement);
        Assert.Equal(installation.Release, session.Report.Sumo.Release);
        Assert.Equal(other, session.Report.Sumo.RecordedRelease);
        Assert.Contains("explicitly accepted", report, StringComparison.Ordinal);
        Assert.Contains(other, report, StringComparison.Ordinal);
    }

    [RequiresSumoFact]
    public void AWorldThatRecordsNoConverterRunsAndIsReportedUnchecked()
    {
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        using SyntheticWorld world = ConvertedBy(string.Empty);

        using SumoDriveSession session = SumoDriveSession.Start(Options(world, installation));
        Assert.True(session.Advance());

        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.Equal(SumoReleaseAgreement.NotRecorded, session.Report.Sumo.Agreement);
        Assert.Null(session.Report.Sumo.RecordedConverter);
        Assert.Contains("records no converter", report, StringComparison.Ordinal);
    }

    [RequiresSumoFact]
    public void TheSameReleaseAsTheConverterPrintedItRunsAndNamesTheInstallation()
    {
        // Recorded exactly as a world build writes it -- the converter's own version line -- against
        // an installation that reports the bare number.
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        string recorded = $"Eclipse SUMO netconvert {installation.Release}";
        using SyntheticWorld world = ConvertedBy(recorded);

        using SumoDriveSession session = SumoDriveSession.Start(Options(world, installation));
        Assert.True(session.Advance());

        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.Equal(SumoReleaseAgreement.SameRelease, session.Report.Sumo.Agreement);
        Assert.Equal(recorded, session.Report.Sumo.RecordedConverter);
        Assert.Equal("explicit", session.Report.Sumo.Source);
        Assert.Equal(Path.GetFullPath(installation.Home), session.Report.Sumo.Home);
        Assert.Contains($"sumo               {installation.Home}", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"release {installation.Release}", report, StringComparison.Ordinal);

        // Which binary ran is on the report, since an installation can hold sumo-gui beside sumo.
        Assert.Equal(installation.Sumo, session.Report.Sumo.Binary);
        Assert.Contains($"  launched         {installation.Sumo}", report, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresSumoFact]
    public void ANamedInstallationThatHoldsNoSumoIsRefusedByName()
    {
        using SyntheticWorld world = ConvertedBy(string.Empty);
        string empty = Path.Combine(world.Directory, "not-a-sumo");
        Directory.CreateDirectory(empty);
        SumoDriveSessionOptions options = Options(world, SumoInstallation.LocateOrThrow());
        options.SumoHome = empty;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));
        Assert.Contains(empty, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fixture world, recording <paramref name="netconvertVersion"/> as its converter.</summary>
    private static SyntheticWorld ConvertedBy(string netconvertVersion) =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!", netconvertVersion);

    /// <summary>A session on the fixture scenario, launching <paramref name="installation"/> by name.</summary>
    private static SumoDriveSessionOptions Options(SyntheticWorld world, SumoInstallation installation) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 60.0,
                                      hysteresisMetres: 15.0, capacity: 8))
        {
            TickWorld = () => true,
            SumoHome = installation.Home,
        };

    /// <summary>A release that is not <paramref name="release"/>: its last component, one higher.</summary>
    private static string AnotherRelease(string release)
    {
        string[] parts = release.Split('.');
        parts[^1] = (int.Parse(parts[^1], CultureInfo.InvariantCulture) + 1)
            .ToString(CultureInfo.InvariantCulture);
        return string.Join('.', parts);
    }
}
