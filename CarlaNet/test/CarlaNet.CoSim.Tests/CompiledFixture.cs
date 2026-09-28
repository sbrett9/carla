using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The fixture scenario copied into a temporary directory with a compile lock beside it, in the shape
/// the scenario compiler writes one, binding the copies as they stand.
/// </summary>
/// <remarks>
/// A test that changes one of the copies, the lock, or what the session is handed, and watches the
/// check answer, needs files it is free to change; the shipped scenario in <c>Import/</c> is the
/// check's real case and is not written to.
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
    public static CompiledFixture Write(SolarEpoch? epoch = null, string? configuration = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "carlanet-lock-" + Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(directory);
        var fixture = new CompiledFixture(directory);
        File.Copy(CoSimFixtures.RightAngleTurnScenario, fixture.Scenario);
        File.Copy(Path.ChangeExtension(CoSimFixtures.RightAngleTurnScenario, ".rou.xml"), fixture.Routes);
        File.Copy(CoSimFixtures.RightAngleTurnNetwork, fixture.Network);
        if (configuration is not null)
        {
            File.WriteAllText(fixture.Scenario, configuration);
        }

        fixture.WriteLock(epoch ?? SolarLeaseTests.PortEpoch());
        return fixture;
    }

    /// <summary>The lock document binding the copies as they stand now.</summary>
    public JsonObject LockDocument(SolarEpoch epoch) => new()
    {
        ["lock_version"] = 1,
        ["scenario_id"] = ScenarioId,
        ["specification"] = "RightAngleTurn.scenario.json",
        ["specification_sha256"] = new string('0', 64),
        ["compiler"] = new JsonObject { ["name"] = "carlacontrol.ScenarioCompiler", ["version"] = "1.0.0" },
        ["files"] = new JsonObject
        {
            ["routes"] = Bound("RightAngleTurn.rou.xml", Routes),
            ["config"] = Bound("RightAngleTurn.sumocfg", Scenario),
            ["network"] = Bound("RightAngleTurn.net.xml", Network),
        },
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
        ["epoch"] = JsonNode.Parse(epoch.CanonicalJson),
        ["epoch_block_sha256"] = epoch.Digest,
    };

    /// <summary>The fixture catalogue, as the session loads it.</summary>
    public static VehicleCatalogue Catalogue => VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);

    /// <summary>Write the lock binding the copies as they stand now.</summary>
    public void WriteLock(SolarEpoch epoch) => WriteLock(LockDocument(epoch));

    /// <summary>Write a lock document as given.</summary>
    public void WriteLock(JsonNode document) =>
        File.WriteAllText(Lock, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

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
    private static JsonObject Bound(string name, string path) =>
        new() { ["path"] = name, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) };
}
