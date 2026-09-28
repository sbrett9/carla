namespace CarlaNet.Sumo.Tests;

/// <summary>
/// An installation and the converter a world records are compared by release number, not by the
/// string either side printed.
/// </summary>
/// <remarks>
/// None of these launches anything: the installation's release is given, as
/// <see cref="SumoInstallation.Release"/> would report it, so what is under test is the comparison and
/// the verdict alone. The session tests in <c>CarlaNet.CoSim.Tests</c> run the same four cases through
/// a real start.
/// </remarks>
public sealed class SumoReleaseCheckTests
{
    private const string Home = @"C:\sumo";
    private const string Installed = "1.27.0";

    [Theory]
    [InlineData("Eclipse SUMO netconvert 1.27.0")]   // what a world package records
    [InlineData("Eclipse SUMO sumo 1.27.0")]         // the same release, printed by another tool
    [InlineData("1.27.0")]                           // a bare release, as an older record may carry
    [InlineData("v1.27.0")]
    [InlineData("  Eclipse SUMO netconvert 1.27.0\n")]
    public void TheSameReleaseWrittenDifferentlyIsAccepted(string recorded)
    {
        SumoReleaseCheck check = SumoReleaseCheck.Compare(Home, "explicit", Installed, recorded,
                                                          allowMismatch: false);

        Assert.Equal(SumoReleaseAgreement.SameRelease, check.Agreement);
        Assert.False(check.Refused);
        Assert.True(check.Agrees);
        Assert.Equal(Installed, check.RecordedRelease);
    }

    [Theory]
    [InlineData("Eclipse SUMO netconvert 1.27.1")]
    [InlineData("1.28.0")]
    [InlineData("Eclipse SUMO netconvert 1.2.70")]
    public void ADifferentReleaseIsRefused(string recorded)
    {
        SumoReleaseCheck check = SumoReleaseCheck.Compare(Home, "SUMO_HOME", Installed, recorded,
                                                          allowMismatch: false);

        Assert.Equal(SumoReleaseAgreement.Mismatch, check.Agreement);
        Assert.True(check.Refused);
        Assert.Contains(recorded.Trim(), check.Verdict, StringComparison.Ordinal);
        Assert.Contains(Installed, check.Installation, StringComparison.Ordinal);
        Assert.Contains("SUMO_HOME", check.Installation, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentReleaseTheOperatorAcceptedRunsAndSaysSo()
    {
        SumoReleaseCheck check = SumoReleaseCheck.Compare(Home, "explicit", Installed,
                                                          "Eclipse SUMO netconvert 1.27.1",
                                                          allowMismatch: true);

        Assert.Equal(SumoReleaseAgreement.MismatchAccepted, check.Agreement);
        Assert.False(check.Refused);
        Assert.False(check.Agrees);
        Assert.Contains("accepted", check.Verdict, StringComparison.Ordinal);
        Assert.Contains("1.27.1", check.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptingAMismatchDoesNotTurnAMatchIntoOne()
    {
        SumoReleaseCheck check = SumoReleaseCheck.Compare(Home, "explicit", Installed,
                                                          "Eclipse SUMO netconvert 1.27.0",
                                                          allowMismatch: true);

        Assert.Equal(SumoReleaseAgreement.SameRelease, check.Agreement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void APackageThatRecordsNoConverterIsUncheckedRatherThanRefused(string? recorded)
    {
        // A package written before the converter was recorded is not evidence of a mismatch.
        SumoReleaseCheck check = SumoReleaseCheck.Compare(Home, "explicit", Installed, recorded,
                                                          allowMismatch: false);

        Assert.Equal(SumoReleaseAgreement.NotRecorded, check.Agreement);
        Assert.False(check.Refused);
        Assert.False(check.Agrees);
        Assert.Null(check.RecordedConverter);
        Assert.Contains("records no converter", check.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstallationWhoseReleaseCannotBeReadIsRefusedAgainstARecordedConverter()
    {
        SumoReleaseCheck check = SumoReleaseCheck.Compare(Home, "PATH", release: null,
                                                          "Eclipse SUMO netconvert 1.27.0",
                                                          allowMismatch: false);

        Assert.True(check.Refused);
        Assert.Contains("unreadable", check.Installation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Eclipse SUMO sumo 1.27.0\r\n Build features: Windows-10.0.26200 AMD64", "1.27.0")]
    [InlineData("Eclipse SUMO netconvert v1.27.1+0042-abcdef\n", "1.27.1")]
    [InlineData("sumo: command not found", null)]
    [InlineData("", null)]
    public void TheReleaseIsReadOutOfAToolsOwnVersionOutput(string output, string? expected)
    {
        Assert.Equal(expected, SumoRelease.FromToolOutput(output));
    }

    [Fact]
    public void StringsThatNameNoReleaseMatchOnlyThemselves()
    {
        Assert.Equal("a development build", SumoRelease.Of(" a development build "));
        Assert.False(SumoRelease.Same("1.27.0", "a development build"));
        Assert.False(SumoRelease.Same(null, null));
    }
}
