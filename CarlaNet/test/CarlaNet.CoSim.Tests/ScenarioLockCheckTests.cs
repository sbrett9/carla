using System.Text.Json.Nodes;
using CarlaNet.Map;
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

        // A lock that names no plan binds no supervision, and runs.
        Assert.Null(check.Plan);
        Assert.Equal("none: the lock names no supervision plan, so the run binds no supervision", check.PlanText);
    }

    [Fact]
    public void ACompiledScenarioBindsItsPlanToTheFilesItRunsAndHandsItToTheSession()
    {
        using CompiledFixture compiled = CompiledFixture.Write(plan: true);

        ScenarioLockCheck check = ScenarioLockCheck.Require(
            compiled.Scenario, CompiledFixture.Catalogue, SolarLeaseTests.PortEpoch());

        _output.WriteLine(check.ToString());
        _output.WriteLine(check.PlanText);
        SupervisionPlan plan = Assert.IsType<SupervisionPlan>(check.Plan);
        Assert.Equal(Path.GetFullPath(compiled.Plan), plan.Path);
        Assert.Equal(CompiledFixture.Sha256(compiled.Plan), plan.Sha256);
        Assert.Equal("turner", Assert.Single(Assert.Single(plan.Instances).Participants).SumoId);
        Assert.Equal(4, plan.Entities.Length);
        Assert.Contains("configuration, route file, network, supervision plan and catalogue agree with it; so does "
                        + "the epoch", check.ToString());
        Assert.StartsWith("RightAngleTurn: 1 instance (0 annotated, 1 nominal, 0 absent), 0 series of 0 slots "
                          + "(0 unrealised), 0 cohorts (0 annotated), 4 entities; vocabulary core 2, no author "
                          + "namespace, digest ", check.PlanText);
        Assert.EndsWith("; compiled against the files the run loads", check.PlanText);
    }

    [Fact]
    public void AnUncompiledScenarioReadsNoPlanEvenWhereAFileOfThatNameSitsBesideIt()
    {
        // A legacy supervision sidecar writes the same suffix, and nothing binds it.
        using CompiledFixture compiled = CompiledFixture.Write(plan: true);
        File.Delete(compiled.Lock);
        File.WriteAllText(compiled.Plan, "{ \"marked\": [\"turner\"] }");

        ScenarioLockCheck check = ScenarioLockCheck.Require(
            compiled.Scenario, CompiledFixture.Catalogue, SolarLeaseTests.PortEpoch());

        Assert.False(check.Compiled);
        Assert.Null(check.Plan);
        Assert.Equal("not read: an uncompiled scenario binds no supervision plan", check.PlanText);
    }

    [Fact]
    public void AnEditedPlanIsRefusedNamingItAndBothDigests()
    {
        using CompiledFixture compiled = CompiledFixture.Write(plan: true);
        string recorded = CompiledFixture.Sha256(compiled.Plan);

        // A space added: the same rows, and not the bytes the lock binds.
        File.AppendAllText(compiled.Plan, " ");

        CoSimSessionRefusedException refused = Refusal(compiled);

        _output.WriteLine(refused.Message);
        Assert.Contains($"is not the one its compile lock {compiled.Lock} binds: (1) the supervision plan "
                        + $"{compiled.Plan} digests as {CompiledFixture.Sha256(compiled.Plan)} and the lock records "
                        + recorded, refused.Message);
        Assert.Contains("SUMO has not been started", refused.Message);
    }

    [Fact]
    public void ALockThatNamesAPlanThatIsNotThereOrIsElsewhereIsRefused()
    {
        using CompiledFixture compiled = CompiledFixture.Write(plan: true);
        File.Move(compiled.Plan, compiled.Plan + ".moved");

        string message = Refusal(compiled).Message;
        _output.WriteLine(message);
        Assert.Contains($"(1) the lock binds the supervision plan {compiled.Plan} and it is not there; a compiled "
                        + "scenario's supervision travels in its plan alone", message);

        File.Move(compiled.Plan + ".moved", compiled.Plan);
        JsonObject document = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        document["files"]!["supervision"]!["path"] = "Elsewhere.supervision.json";
        compiled.WriteLock(document);

        message = Refusal(compiled).Message;
        Assert.Contains($"(1) the lock binds the supervision plan {Path.Combine(compiled.Directory, "Elsewhere.supervision.json")}, "
                        + $"and a session reads the plan beside its configuration, at {compiled.Plan}", message);
    }

    [Theory]
    [InlineData("routes_digest")]
    [InlineData("config_digest")]
    [InlineData("network_digest")]
    [InlineData("additional_digest")]
    public void APlanCompiledAgainstOtherFilesIsRefusedNamingTheDigestItWasCompiledAgainst(string field)
    {
        // The plan's own digest moved, and the lock rewritten to bind it: the lock and the files agree,
        // and the plan was compiled against something else.
        using CompiledFixture compiled = CompiledFixture.Write(plan: true);
        string other = new('e', 64);
        JsonObject plan = compiled.PlanDocument();
        plan[field] = other;
        compiled.WritePlan(plan);
        compiled.WriteLock(SolarLeaseTests.PortEpoch());

        CoSimSessionRefusedException refused = Refusal(compiled);

        _output.WriteLine(refused.Message);
        Assert.StartsWith($"The supervision plan {compiled.Plan} was not compiled against the files the scenario "
                          + $"{compiled.Scenario} runs: (1) ", refused.Message);
        Assert.Contains(field switch
        {
            "routes_digest" => $"it was compiled against the route file digest {other} and the route file "
                               + $"{compiled.Routes} digests as {CompiledFixture.Sha256(compiled.Routes)}",
            "config_digest" => $"it was compiled against the configuration digest {other} and the configuration "
                               + $"{compiled.Scenario} digests as {CompiledFixture.Sha256(compiled.Scenario)}",
            "network_digest" => $"it was compiled against the network fingerprint {other} and the network "
                                + $"{compiled.Network} fingerprints as {NetworkFingerprint.ComputeFile(compiled.Network)}",
            _ => $"it was compiled against the additional file digest {other} and the configuration runs none",
        }, refused.Message);
        Assert.DoesNotContain("(2)", refused.Message);
        Assert.Contains("SUMO has not been started. Recompile the scenario.", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
    }

    [Fact]
    public void EveryWayAPlanDisagreesWithTheFilesAndTheLockIsNamedInOneRefusal()
    {
        using CompiledFixture compiled = CompiledFixture.Write(plan: true, additional: true);
        JsonObject plan = compiled.PlanDocument();
        plan["scenario_id"] = "Elsewhere";
        plan["routes_digest"] = new string('e', 64);
        plan["additional_digest"] = null;
        compiled.WritePlan(plan);
        JsonObject document = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        document["vocabulary"]!["vocabulary_digest"] = new string('b', 64);
        compiled.WriteLock(document);

        string message = Refusal(compiled).Message;

        _output.WriteLine(message);
        Assert.Contains("(1) it is the plan of the scenario 'Elsewhere' and the lock binds 'RightAngleTurn'", message);
        Assert.Contains("(2) it was compiled against the route file digest", message);
        Assert.Contains($"(3) it was compiled against no additional file and the configuration runs {compiled.Additional}",
                        message);
        Assert.Contains($"(4) it carries the vocabulary digest {plan["vocabulary_digest"]!.GetValue<string>()} and the lock records "
                        + new string('b', 64), message);
    }

    [Fact]
    public void APlanTheLockBindsThatCannotBeBoundIsRefusedForItsOwnReasons()
    {
        using CompiledFixture compiled = CompiledFixture.Write(plan: true);
        JsonObject plan = compiled.PlanDocument();
        plan["instances"]![0]!["supervision"] = "anomalous";
        compiled.WritePlan(plan);
        compiled.WriteLock(SolarLeaseTests.PortEpoch());

        string message = Refusal(compiled).Message;

        _output.WriteLine(message);
        Assert.StartsWith($"The supervision plan {compiled.Plan} cannot be bound: (1) plan.instances[0].supervision "
                          + "is 'anomalous', which is no supervision_state", message);
    }

    [Fact]
    public void ALaneClosureFileIsBoundByTheLockAndThePlan()
    {
        using CompiledFixture compiled = CompiledFixture.Write(plan: true, additional: true);

        ScenarioLockCheck check = ScenarioLockCheck.Require(
            compiled.Scenario, CompiledFixture.Catalogue, SolarLeaseTests.PortEpoch());
        Assert.Contains("configuration, route file, network, additional file, supervision plan and catalogue agree",
                        check.ToString());
        Assert.Equal(CompiledFixture.Sha256(compiled.Additional), check.Plan!.AdditionalDigest);

        // Its bytes moved after it was compiled.
        string recorded = CompiledFixture.Sha256(compiled.Additional);
        File.AppendAllText(compiled.Additional, "<!-- edited after it was compiled -->\n");
        string message = Refusal(compiled).Message;
        _output.WriteLine(message);
        Assert.Contains($"(1) the additional file {compiled.Additional} digests as "
                        + $"{CompiledFixture.Sha256(compiled.Additional)} and the lock records {recorded}", message);

        // A configuration running an additional file the lock does not name.
        JsonObject document = compiled.LockDocument(SolarLeaseTests.PortEpoch());
        document["files"]!.AsObject().Remove("additional");
        compiled.WriteLock(document);
        message = Refusal(compiled).Message;
        Assert.Contains($"(1) the configuration runs the additional file(s) {compiled.Additional} and the lock binds "
                        + "none", message);
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

        // And it disables teleporting and spreads a lane change over three seconds, in the lock and in
        // the configuration it binds.
        Assert.Equal("-1", check.Lock.TimeToTeleport);
        Assert.Equal("3", check.Lock.LaneChangeDuration);
        Assert.Equal("time-to-teleport '-1', lanechange.duration '3'", check.ProcessingText);

        // Its plan, bound to the files it runs: it asserts nothing, and says so of every flow.
        _output.WriteLine(check.PlanText);
        Assert.Equal("Gardnerville_Centerville_Lane_NeighborhoodOrbit", check.Plan!.PlanId);
        Assert.Empty(check.Plan.Instances);
        Assert.Null(check.Plan.AdditionalDigest);
    }

    [Fact]
    public void TheShippedArapahoeScenarioIsTheOneItsLockBinds()
    {
        // Arapahoe's generator writes a specification and compiles it into Import/, lane closure and
        // all; the session reads that lock as it reads Gardnerville's.
        string scenario = RepositoryFile("Import", "Arapahoe_I25_UnderpassDwell.sumocfg");
        JsonNode specification = JsonNode.Parse(File.ReadAllText(
            RepositoryFile("Import", "Arapahoe_I25_UnderpassDwell.scenario.json")))!;
        SolarEpoch epoch = SolarEpoch.FromJson(specification["epoch"]!.ToJsonString());

        ScenarioLockCheck check = ScenarioLockCheck.Require(scenario, CompiledFixture.Catalogue, epoch);

        _output.WriteLine(check.ToString());
        _output.WriteLine(check.WorldText);
        Assert.True(check.Compiled);
        Assert.True(check.EpochCompared);
        Assert.Equal("Arapahoe_I25_UnderpassDwell", check.Lock!.ScenarioId);
        Assert.Equal(epoch.Digest, check.Lock.EpochDigest);
        Assert.Equal("1.27.0", check.Lock.RoutedByRelease);
        Assert.Equal("Arapahoe_I25.cwp", check.Lock.WorldPackage);
        // The world rebuilt with its ramp meters on their own ramps (2026-10-02).
        Assert.StartsWith("ffe490b1", check.Lock.WorldNetworkFingerprint);
        Assert.Equal("-1", check.Lock.TimeToTeleport);
        Assert.Equal("3", check.Lock.LaneChangeDuration);

        // The lane closure is bound by the lock and by the plan compiled against it.
        Assert.Contains("additional file, supervision plan and catalogue agree with it", check.ToString());
        Assert.Equal(check.Lock.Additional!.Sha256, check.Plan!.AdditionalDigest);
        Assert.Equal("marked", Assert.Single(check.Plan.Entities).EntityId);
    }

    [Fact]
    public void TheShippedBahonarScenarioBindsItsPlan()
    {
        string scenario = RepositoryFile("Import", "Shahid_Bahonar_Port_PatternOfLife.sumocfg");
        JsonNode specification = JsonNode.Parse(File.ReadAllText(
            RepositoryFile("Import", "Shahid_Bahonar_Port_PatternOfLife.scenario.json")))!;
        SolarEpoch epoch = SolarEpoch.FromJson(specification["epoch"]!.ToJsonString());

        ScenarioLockCheck check = ScenarioLockCheck.Require(scenario, CompiledFixture.Catalogue, epoch);

        _output.WriteLine(check.ToString());
        _output.WriteLine(check.PlanText);
        Assert.True(check.EpochCompared);
        Assert.Contains("supervision plan and catalogue agree with it; so does the epoch", check.ToString());
        Assert.Equal(Path.ChangeExtension(Path.GetFullPath(scenario), ".supervision.json"), check.Plan!.Path);
        Assert.Equal(27, check.Plan.Instances.Length);
        Assert.Equal(check.Lock!.VocabularyDigest, check.Plan.Vocabulary.Digest);
        Assert.StartsWith("Shahid_Bahonar_Port_PatternOfLife: 27 instances (5 annotated, 21 nominal, 1 absent)",
                          check.PlanText);
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
