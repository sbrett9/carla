using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarlaNet.Map;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The fixture scenario copied into a temporary directory with a compile lock beside it, in the shape
/// the scenario compiler writes one, binding the copies as they stand -- and, where asked, a lane
/// closures' additional file and a supervision plan compiled against them.
/// </summary>
/// <remarks>
/// A test that changes one of the copies, the lock, the plan, or what the session is handed, and
/// watches the check answer, needs files it is free to change; the shipped scenarios in <c>Import/</c>
/// are the check's real case and are not written to.
/// </remarks>
internal sealed class CompiledFixture : IDisposable
{
    public const string ScenarioId = "RightAngleTurn";
    public const string RoutedBy = "1.27.0";
    public const string WorldConverter = "Eclipse SUMO netconvert 1.27.0";
    public const string WorldFingerprint = "fixture-network-fingerprint";

    private CompiledFixture(string directory)
    {
        Directory = directory;
    }

    /// <summary>Where the copies and the lock are.</summary>
    public string Directory { get; }

    /// <summary>The copied configuration.</summary>
    public string Scenario => Path.Combine(Directory, "RightAngleTurn.sumocfg");

    /// <summary>The copied route file.</summary>
    public string Routes => Path.Combine(Directory, "RightAngleTurn.rou.xml");

    /// <summary>The copied network.</summary>
    public string Network => Path.Combine(Directory, "RightAngleTurn.net.xml");

    /// <summary>The lane closures' additional file, where the fixture was written with one.</summary>
    public string Additional => Path.Combine(Directory, "RightAngleTurn.add.xml");

    /// <summary>The supervision plan, where the fixture was written with one.</summary>
    public string Plan => Path.Combine(Directory, "RightAngleTurn.supervision.json");

    /// <summary>The lock beside the configuration.</summary>
    public string Lock => Path.Combine(Directory, "RightAngleTurn.lock.json");

    /// <summary>
    /// Copy the fixture scenario and write a lock binding the copies, the fixture catalogue and
    /// <paramref name="epoch"/> -- the port's by default.
    /// </summary>
    /// <param name="epoch">The epoch the lock records the scenario was compiled against.</param>
    /// <param name="configuration">
    /// The configuration to write instead of the fixture's own, for a test about what it sets.
    /// </param>
    /// <param name="additional">
    /// Whether the scenario closes lanes: an additional file beside it, named by the configuration.
    /// </param>
    /// <param name="plan">Whether to write a supervision plan compiled against the copies, and bind it.</param>
    /// <param name="routes">
    /// Another fixture's route file to run in place of the fixture's own, on the same network.
    /// </param>
    public static CompiledFixture Write(SolarEpoch? epoch = null, string? configuration = null,
                                        bool additional = false, bool plan = false, string? routes = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "carlanet-lock-" + Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(directory);
        var fixture = new CompiledFixture(directory);
        File.Copy(CoSimFixtures.RightAngleTurnScenario, fixture.Scenario);
        File.Copy(routes ?? Path.ChangeExtension(CoSimFixtures.RightAngleTurnScenario, ".rou.xml"), fixture.Routes);
        File.Copy(CoSimFixtures.RightAngleTurnNetwork, fixture.Network);
        if (configuration is not null)
        {
            File.WriteAllText(fixture.Scenario, configuration);
        }

        if (additional)
        {
            File.WriteAllText(fixture.Additional, "<additional>\n</additional>\n");
            File.WriteAllText(fixture.Scenario, File.ReadAllText(fixture.Scenario).Replace(
                "<route-files value=\"RightAngleTurn.rou.xml\"/>",
                "<route-files value=\"RightAngleTurn.rou.xml\"/>\n"
                + "        <additional-files value=\"RightAngleTurn.add.xml\"/>",
                StringComparison.Ordinal));
        }

        if (plan)
        {
            fixture.WritePlan(fixture.PlanDocument());
        }

        fixture.WriteLock(epoch ?? SolarLeaseTests.PortEpoch());
        return fixture;
    }

    /// <summary>The lock document binding the copies, and the plan where there is one, as they stand now.</summary>
    public JsonObject LockDocument(SolarEpoch epoch)
    {
        var files = new JsonObject
        {
            ["routes"] = Bound("RightAngleTurn.rou.xml", Routes),
            ["config"] = Bound("RightAngleTurn.sumocfg", Scenario),
            ["network"] = Bound("RightAngleTurn.net.xml", Network),
        };
        if (File.Exists(Additional))
        {
            files["additional"] = Bound("RightAngleTurn.add.xml", Additional);
        }

        var document = new JsonObject
        {
            ["lock_version"] = 1,
            ["scenario_id"] = ScenarioId,
            ["specification"] = "RightAngleTurn.scenario.json",
            ["specification_sha256"] = new string('0', 64),
            ["compiler"] = new JsonObject { ["name"] = "carlacontrol.ScenarioCompiler", ["version"] = "1.0.0" },
            ["files"] = files,
            ["world"] = new JsonObject
            {
                ["package"] = "SyntheticSurface.cwp",
                ["map_name"] = "SyntheticSurface",
                ["network_fingerprint"] = WorldFingerprint,
                ["netconvert_version"] = WorldConverter,
                ["opendrive_sha256"] = new string('1', 64),
            },
            ["catalogue"] = new JsonObject
            {
                ["catalogue_id"] = Catalogue.CatalogueId,
                ["catalogue_digest"] = Catalogue.CatalogueDigest,
            },
            ["traffic"] = new JsonObject
            {
                ["sumo_seed"] = 42,
                ["processing"] = new JsonObject { ["time-to-teleport"] = "-1" },
                ["routed_by"] = new JsonObject
                {
                    ["tool"] = "duarouter",
                    ["version"] = RoutedBy,
                    ["world_converter"] = WorldConverter,
                    ["release_agreement"] = "SameRelease",
                    ["mismatch_accepted"] = false,
                },
            },
            // The compiler's SUMO-only run, as SumoDryRun.lock_record writes it: every vehicle entered.
            ["dry_run"] = new JsonObject
            {
                ["ran"] = true,
                ["sumo_release"] = RoutedBy,
                ["begin_s"] = 0.0,
                ["end_s"] = 60.0,
                ["vehicles"] = new JsonObject
                {
                    ["loaded"] = 4, ["inserted"] = 4, ["discarded"] = 0, ["waiting_at_end"] = 0,
                },
                ["planned_vehicles"] = new JsonObject { ["total"] = 1, ["inserted"] = 1 },
                ["collisions"] = 0,
            },
            ["epoch"] = JsonNode.Parse(epoch.CanonicalJson),
            ["epoch_block_sha256"] = epoch.Digest,
        };

        if (File.Exists(Plan))
        {
            files["supervision"] = Bound("RightAngleTurn.supervision.json", Plan);
            JsonNode plan = JsonNode.Parse(File.ReadAllText(Plan))!;
            document["vocabulary"] = new JsonObject
            {
                ["core_version"] = plan["vocabulary_version"]?.DeepClone(),
                ["namespaces"] = new JsonArray(),
                ["vocabulary_digest"] = plan["vocabulary_digest"]?.DeepClone(),
            };
        }

        return document;
    }

    /// <summary>
    /// A supervision plan compiled against the copies as they stand now: one nominal instance over
    /// <c>turner</c>, anchored to its departure, and every vehicle explicit. Its vocabulary, and that
    /// vocabulary's digest, are the shipped Gardnerville plan's, which declares no author namespace --
    /// digested by the compiler, so a reader that digested the vocabulary wrongly would refuse it.
    /// </summary>
    public JsonObject PlanDocument()
    {
        JsonNode shipped = JsonNode.Parse(File.ReadAllText(SupervisionPlanTests.ShippedPlan(
            "Gardnerville_Centerville_Lane_NeighborhoodOrbit")))!;
        return new JsonObject
        {
            ["supervision_plan_version"] = 1,
            ["plan_id"] = ScenarioId,
            ["spec_version"] = 1,
            ["scenario_id"] = ScenarioId,
            ["routes_digest"] = Sha256(Routes),
            ["network_digest"] = NetworkFingerprint.ComputeFile(Network),
            ["config_digest"] = Sha256(Scenario),
            ["additional_digest"] = File.Exists(Additional) ? Sha256(Additional) : null,
            ["vocabulary_version"] = shipped["vocabulary_version"]!.DeepClone(),
            ["vocabulary_digest"] = shipped["vocabulary_digest"]!.DeepClone(),
            ["vocabulary"] = shipped["vocabulary"]!.DeepClone(),
            ["instances"] = new JsonArray
            {
                new JsonObject
                {
                    ["instance_id"] = $"{ScenarioId}/turn_nominal",
                    ["supervision"] = "nominal",
                    ["realisation"] = "present",
                    ["labels"] = new JsonArray(),
                    ["parameters"] = new JsonObject(),
                    ["hard_negative_for"] = null,
                    ["counterfactual"] = null,
                    ["series_ref"] = null,
                    ["slot_ref"] = null,
                    ["aoi_refs"] = new JsonArray(),
                    ["participants"] = new JsonArray
                    {
                        new JsonObject { ["entity_id"] = "turner", ["role"] = "subject", ["sumo_id"] = "turner" },
                    },
                    ["intervals"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["entity_id"] = "turner",
                            ["phase"] = "turn",
                            ["anchor"] = new JsonObject
                            {
                                ["start"] = new JsonObject { ["event"] = "depart" },
                                ["end"] = null,
                            },
                            ["declared_start_s"] = 0.0,
                            ["declared_start_civil"] = "2026-09-29T07:00:00+03:30",
                            ["declared_end_s"] = null,
                            ["declared_end_civil"] = null,
                            ["declared_duration_s"] = null,
                        },
                    },
                },
            },
            ["series"] = new JsonArray(),
            ["cohorts"] = new JsonArray(),
            ["entities"] = new JsonArray
            {
                Entity("changer", "unlabelled"),
                Entity("goer", "unlabelled"),
                Entity("turner", "nominal", $"{ScenarioId}/turn_nominal"),
                Entity("unrenderable", "unlabelled"),
            },
        };
    }

    /// <summary>The fixture catalogue, as the session loads it.</summary>
    public static VehicleCatalogue Catalogue => VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);

    /// <summary>Write the lock binding the copies as they stand now.</summary>
    public void WriteLock(SolarEpoch epoch) => WriteLock(LockDocument(epoch));

    /// <summary>Write a lock document as given.</summary>
    public void WriteLock(JsonNode document) => File.WriteAllText(Lock, Indented(document));

    /// <summary>Write a plan document as given; the lock is not rewritten.</summary>
    public void WritePlan(JsonNode document) => File.WriteAllText(Plan, Indented(document));

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still open somewhere is not worth failing a test over; the temporary directory
            // is the operating system's to clean up.
        }
    }

    // Digested here rather than by the check's own digest, so a check that digested the wrong thing
    // would disagree with the lock rather than agree with itself.
    /// <summary>Lowercase hex SHA-256 of a file's bytes.</summary>
    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static JsonObject Bound(string name, string path) => new() { ["path"] = name, ["sha256"] = Sha256(path) };

    private static JsonObject Entity(string id, string state, params string[] refs) => new()
    {
        ["entity_id"] = id,
        ["supervision"] = new JsonArray(state),
        ["refs"] = new JsonArray(refs.Select(reference => (JsonNode?)JsonValue.Create(reference)).ToArray()),
    };

    private static string Indented(JsonNode document) =>
        document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
