using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session runs the files its scenario's compile lock binds, compiled against the catalogue and the
/// epoch the lock records, or refuses before SUMO is started; a scenario with no lock runs and is
/// recorded as uncompiled.
/// </summary>
public sealed class ScenarioLockCheckTests
{
    private readonly ITestOutputHelper _output;

    public ScenarioLockCheckTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ACompiledScenarioWhoseFilesCatalogueAndEpochAgreeIsAdmittedAndRecorded()
    {
        using CompiledFixture compiled = CompiledFixture.Write();

        ScenarioLockCheck check = ScenarioLockCheck.Require(
            compiled.Scenario, CompiledFixture.Catalogue, SolarLeaseTests.PortEpoch());

        Assert.True(check.Compiled);
        Assert.True(check.EpochCompared);
        Assert.Equal(Path.GetFullPath(compiled.Lock), check.Lock!.Path);
        Assert.Contains("RightAngleTurn, compiled by carlacontrol.ScenarioCompiler 1.0.0", check.ToString());
        Assert.Contains("so does the epoch", check.ToString());
        Assert.Equal("duarouter 1.27.0 against the world converter 'Eclipse SUMO netconvert 1.27.0' "
                     + "(SameRelease)", check.RoutedByText);
        Assert.Contains(CompiledFixture.WorldFingerprint, check.WorldText);
        Assert.Contains("SyntheticSurface.cwp", check.WorldText);
    }

    [Fact]
    public void AScenarioWithNoLockBesideItIsRecordedAsUncompiledAndNotRefused()
    {
        // The fixture scenario itself: a configuration written by hand, with no lock beside it.
        ScenarioLockCheck check = ScenarioLockCheck.Require(
            CoSimFixtures.RightAngleTurnScenario, CompiledFixture.Catalogue, SolarLeaseTests.PortEpoch());

        Assert.False(check.Compiled);
        Assert.False(check.EpochCompared);
        Assert.Null(check.Lock);
        Assert.Equal(Path.ChangeExtension(CoSimFixtures.RightAngleTurnScenario, ".lock.json"),
                     check.ExpectedLockPath);
        Assert.Contains("an uncompiled scenario, nothing compared", check.ToString());
        Assert.Contains("not recorded", check.RoutedByText);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("route file")]
    [InlineData("network")]
    public void AFileWhoseBytesMovedAfterItWasCompiledIsRefusedNamingItAndBothDigests(string role)
    {
        using CompiledFixture compiled = CompiledFixture.Write();
        string path = role switch
        {
            "configuration" => compiled.Scenario,
            "route file" => compiled.Routes,
            _ => compiled.Network,
        };
        string recorded = ScenarioLockCheck.Sha256Of(path);

        // One comment appended: the same traffic, and not the bytes the lock binds.
        File.AppendAllText(path, "<!-- edited after it was compiled -->\n");

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioLockCheck.Require(compiled.Scenario, CompiledFixture.Catalogue,
                                            SolarLeaseTests.PortEpoch()));

        _output.WriteLine(refused.Message);
        Assert.Contains($"the {role} {path} digests as {ScenarioLockCheck.Sha256Of(path)} and the lock "
                        + $"records {recorded}", refused.Message);
        Assert.Contains("SUMO has not been started", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
    }

    [Fact]
    public void ACatalogueOtherThanTheOneTheScenarioWasCompiledAgainstIsRefused()
    {
        using CompiledFixture compiled = CompiledFixture.Write();
        JsonObject document = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        document["catalogue"]!["catalogue_digest"] = new string('a', 64);
        compiled.WriteLock(document);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioLockCheck.Require(compiled.Scenario, CompiledFixture.Catalogue,
                                            SolarLeaseTests.PortEpoch()));

        _output.WriteLine(refused.Message);
        Assert.Contains($"declares digest {CompiledFixture.Catalogue.CatalogueDigest} and the scenario was "
                        + $"compiled against {new string('a', 64)}", refused.Message);
    }

    [Fact]
    public void AnEpochOtherThanTheOneTheScenarioWasCompiledAgainstIsRefused()
    {
        // The same instant with the calendar held: another declaration, so another digest.
        using CompiledFixture compiled = CompiledFixture.Write();
        SolarEpoch held = SolarLeaseTests.PortEpoch(calendarAdvances: false);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioLockCheck.Require(compiled.Scenario, CompiledFixture.Catalogue, held));

        _output.WriteLine(refused.Message);
        Assert.Contains($"digests as {held.Digest} and the scenario was compiled against the epoch "
                        + SolarLeaseTests.PortEpoch().Digest, refused.Message);
    }

    [Fact]
    public void ASessionThatDeclaresNoEpochIsNotComparedOnOneAndSaysSo()
    {
        using CompiledFixture compiled = CompiledFixture.Write(
            SolarLeaseTests.PortEpoch(calendarAdvances: false));

        ScenarioLockCheck check = ScenarioLockCheck.Require(compiled.Scenario, CompiledFixture.Catalogue,
                                                            epoch: null);

        Assert.True(check.Compiled);
        Assert.False(check.EpochCompared);
        Assert.Contains("the epoch not compared, as the session declares none", check.ToString());
    }

    [Fact]
    public void EveryDisagreementIsNamedInOneRefusal()
    {
        using CompiledFixture compiled = CompiledFixture.Write();
        File.AppendAllText(compiled.Routes, "\n");
        File.AppendAllText(compiled.Network, "\n");

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioLockCheck.Require(compiled.Scenario, CompiledFixture.Catalogue,
                                            SolarLeaseTests.PortEpoch(calendarAdvances: false)));

        _output.WriteLine(refused.Message);
        Assert.Contains("(1) the route file", refused.Message);
        Assert.Contains("(2) the network", refused.Message);
        Assert.Contains("(3) the session's epoch", refused.Message);
    }

    [Fact]
    public void ALockThatBindsAnotherConfigurationIsRefused()
    {
        using CompiledFixture compiled = CompiledFixture.Write();
        JsonObject document = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        document["files"]!["config"]!["path"] = "Elsewhere.sumocfg";
        compiled.WriteLock(document);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioLockCheck.Require(compiled.Scenario, CompiledFixture.Catalogue,
                                            SolarLeaseTests.PortEpoch()));

        Assert.Contains($"the lock binds the configuration {Path.Combine(compiled.Directory, "Elsewhere.sumocfg")}",
                        refused.Message);
    }

    [Fact]
    public void AConfigurationThatRunsARouteFileTheLockDoesNotBindIsRefused()
    {
        // SUMO takes a list of route files under any of the option's names; the second is loaded as
        // surely as the first, and nothing binds it.
        string configuration = File.ReadAllText(CoSimFixtures.RightAngleTurnScenario).Replace(
            "<route-files value=\"RightAngleTurn.rou.xml\"/>",
            "<r value=\"RightAngleTurn.rou.xml, extra.rou.xml\"/>", StringComparison.Ordinal);
        using CompiledFixture compiled = CompiledFixture.Write(configuration: configuration);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioLockCheck.Require(compiled.Scenario, CompiledFixture.Catalogue,
                                            SolarLeaseTests.PortEpoch()));

        _output.WriteLine(refused.Message);
        Assert.Contains($"runs the route file(s) {compiled.Routes}, "
                        + $"{Path.Combine(compiled.Directory, "extra.rou.xml")} and the lock binds "
                        + compiled.Routes, refused.Message);
    }

    [Fact]
    public void ALockThatCannotBeReadOrDoesNotRecordAComparedFieldIsRefusedWhole()
    {
        using CompiledFixture compiled = CompiledFixture.Write();

        File.WriteAllText(compiled.Lock, "{ not json");
        Assert.Contains("cannot be read as JSON", Refusal(compiled).Message);

        JsonObject later = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        later["lock_version"] = 2;
        compiled.WriteLock(later);
        Assert.Contains("declares lock_version 2", Refusal(compiled).Message);

        JsonObject partial = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        partial["files"]!.AsObject().Remove("routes");
        partial.Remove("epoch_block_sha256");
        compiled.WriteLock(partial);
        string message = Refusal(compiled).Message;
        _output.WriteLine(message);
        Assert.Contains("does not record files.routes.path, files.routes.sha256, epoch_block_sha256", message);
    }

    [Fact]
    public void TheShippedCompiledScenarioIsTheOneItsLockBinds()
    {
        // As compiled into Import/ and stored byte for byte, against the catalogue in the tree and
        // the epoch its own specification declares, read by the session's epoch reader: the digest
        // the compiler wrote is the one the session computes.
        string scenario = RepositoryFile("Import", "Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg");
        JsonNode specification = JsonNode.Parse(File.ReadAllText(
            RepositoryFile("Import", "Gardnerville_Centerville_Lane_NeighborhoodOrbit.scenario.json")))!;
        SolarEpoch epoch = SolarEpoch.FromJson(specification["epoch"]!.ToJsonString());

        ScenarioLockCheck check = ScenarioLockCheck.Require(scenario, CompiledFixture.Catalogue, epoch);

        _output.WriteLine(check.ToString());
        _output.WriteLine(check.RoutedByText);
        _output.WriteLine(check.WorldText);
        Assert.True(check.Compiled);
        Assert.True(check.EpochCompared);
        Assert.Equal("Gardnerville_Centerville_Lane_NeighborhoodOrbit", check.Lock!.ScenarioId);
        Assert.Equal(epoch.Digest, check.Lock.EpochDigest);
        Assert.Equal("1.27.0", check.Lock.RoutedByRelease);
        Assert.Equal("Eclipse SUMO netconvert 1.27.0", check.Lock.WorldConverter);
        Assert.Equal("Gardnerville_Centerville_Lane.cwp", check.Lock.WorldPackage);
        Assert.StartsWith("a50ac545", check.Lock.WorldNetworkFingerprint);

        // And it disables teleporting, in the lock and in the configuration it binds.
        Assert.Equal("-1", check.Lock.TimeToTeleport);
    }

    [Fact]
    public void TheShippedGeneratedScenarioHasNoLockAndRunsAsUncompiled()
    {
        string scenario = RepositoryFile("Import", "Arapahoe_I25_UnderpassDwell.sumocfg");

        ScenarioLockCheck check = ScenarioLockCheck.Require(scenario, CompiledFixture.Catalogue,
                                                            SolarLeaseTests.PortEpoch());

        Assert.False(check.Compiled);
        Assert.Contains("an uncompiled scenario", check.ToString());
    }

    private static CoSimSessionRefusedException Refusal(CompiledFixture compiled) =>
        Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioLockCheck.Require(compiled.Scenario, CompiledFixture.Catalogue,
                                            SolarLeaseTests.PortEpoch()));

    /// <summary>A file in the repository, found upward from the test's own output directory.</summary>
    internal static string RepositoryFile(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{Path.Combine(parts)} is not above {AppContext.BaseDirectory}.");
    }
}
