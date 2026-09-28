using System.Globalization;
using System.Xml.Linq;
using CarlaNet.Map;
using CarlaNet.Map.WorldPackage;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session runs only where the network its scenario names is the network its world package
/// carries, compared by canonical fingerprint, and the package carries the network it records.
/// </summary>
/// <remarks>
/// The networks that differ are made from the fixture's by changing what a second netconvert run
/// changes -- a lane's shape, by a tenth of a metre -- so the projection, the offset and the boundary
/// the session's frame checks compare are identical on both sides. The shipped scenarios and world
/// packages are the real case: each network is a separate conversion of its world's area.
/// </remarks>
public sealed class ScenarioNetworkCheckTests : IDisposable
{
    /// <summary>A point on one of the fixture's junction lanes, and where the other network has it.</summary>
    private const string FixtureShapePoint = "4.50,-2.58";
    private const string MovedShapePoint = "4.60,-2.58";

    private readonly ITestOutputHelper _output;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-scenario-" + Guid.NewGuid().ToString("n"));

    public ScenarioNetworkCheckTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void TheFixtureScenarioRunsOnTheNetworkItsWorldCarries()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        ScenarioNetworkCheck.Require(CoSimFixtures.RightAngleTurnScenario, world.PackagePath);
    }

    [Fact]
    public void APackageThatRecordsTheNetworkItCarriesIsAdmitted()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!",
            recordedNetworkFingerprint: NetworkFingerprint.ComputeFile(CoSimFixtures.RightAngleTurnNetwork));

        ScenarioNetworkCheck.Require(CoSimFixtures.RightAngleTurnScenario, world.PackagePath);
    }

    [Fact]
    public void AScenarioOnAnotherNetworkIsRefusedNamingBothNetworksAndBothFingerprints()
    {
        string network = WriteMovedNetwork("RightAngleTurn.net.xml");
        string scenario = WriteConfiguration("scenario.sumocfg", "<net-file value=\"RightAngleTurn.net.xml\"/>");
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        // Everything the frame checks compare is the same on both sides.
        SumoRoadNetwork theirs = SumoRoadNetwork.Load(network);
        SumoRoadNetwork ours = SumoRoadNetwork.FromWorldPackage(world.PackagePath);
        Assert.Equal(ours.Projection, theirs.Projection);
        Assert.Equal(ours.NetOffset, theirs.NetOffset);
        Assert.Equal(ours.ConvBoundary, theirs.ConvBoundary);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioNetworkCheck.Require(scenario, world.PackagePath));

        _output.WriteLine(refused.Message);
        Assert.Contains(scenario, refused.Message, StringComparison.Ordinal);
        Assert.Contains(network, refused.Message, StringComparison.Ordinal);
        Assert.Contains(world.PackagePath, refused.Message, StringComparison.Ordinal);
        Assert.Contains(NetworkFingerprint.ComputeFile(network), refused.Message, StringComparison.Ordinal);
        Assert.Contains(NetworkFingerprint.ComputeFile(CoSimFixtures.RightAngleTurnNetwork),
                        refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANetworkThatDiffersOnlyInItsBytesIsTheSameNetwork()
    {
        // Re-stamped, its edges written in the opposite order and carrying street names, and
        // re-serialised: every byte a second writer could change, and no part of the graph.
        XDocument document = XDocument.Load(CoSimFixtures.RightAngleTurnNetwork);
        document.Nodes().OfType<XComment>().First().Value = " generated on another day, by another run ";
        XElement net = document.Root!;
        List<XElement> edges = [.. net.Elements("edge")];
        foreach (XElement edge in edges)
        {
            edge.Remove();
            if (edge.Attribute("function") is null)
            {
                edge.SetAttributeValue("name", "Centre Street");
            }
        }

        net.Element("location")!.AddAfterSelf(Enumerable.Reverse(edges));
        string network = Path.Combine(_directory, "RightAngleTurn.net.xml");
        document.Save(network);
        Assert.NotEqual(File.ReadAllBytes(CoSimFixtures.RightAngleTurnNetwork), File.ReadAllBytes(network));

        string scenario = WriteConfiguration("scenario.sumocfg", "<net-file value=\"RightAngleTurn.net.xml\"/>");
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        ScenarioNetworkCheck.Require(scenario, world.PackagePath);
    }

    [Fact]
    public void APackageCarryingANetworkOtherThanTheOneItRecordsIsRefused()
    {
        // The scenario runs on the network the package carries; only the record disagrees.
        string recorded = NetworkFingerprint.ComputeFile(WriteMovedNetwork("other.net.xml"));
        string carried = NetworkFingerprint.ComputeFile(CoSimFixtures.RightAngleTurnNetwork);
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!", recordedNetworkFingerprint: recorded);

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioNetworkCheck.Require(CoSimFixtures.RightAngleTurnScenario, world.PackagePath));

        _output.WriteLine(refused.Message);
        Assert.Contains(world.PackagePath, refused.Message, StringComparison.Ordinal);
        Assert.Contains(recorded, refused.Message, StringComparison.Ordinal);
        Assert.Contains(carried, refused.Message, StringComparison.Ordinal);
        Assert.Contains("two builds", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<net-file value=\"networks/cross.net.xml\"/>")]
    [InlineData("<net value=\"networks/cross.net.xml\"/>")]
    [InlineData("<n v=\"networks/cross.net.xml\"/>")]
    [InlineData("<net-file>networks/cross.net.xml</net-file>")]
    public void TheNetworkIsReadUnderEveryNameSumoTakesItByAndFromTheConfigurationSDirectory(string option)
    {
        // The configuration is in a directory of its own and the test runs from its output
        // directory, so a path resolved against anything but the configuration is elsewhere.
        string scenario = WriteConfiguration("scenario.sumocfg", option);

        Assert.Equal(Path.Combine(_directory, "networks", "cross.net.xml"),
                     ScenarioNetworkCheck.NetworkFileOf(scenario));
    }

    [Fact]
    public void AScenarioThatNamesNoNetworkIsRefused()
    {
        // An empty value is one SUMO ignores, so it names nothing either.
        string scenario = WriteConfiguration(
            "scenario.sumocfg", "<net-file value=\"\"/><route-files value=\"cross.rou.xml\"/>");

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioNetworkCheck.NetworkFileOf(scenario));
        Assert.Contains("names no network", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScenarioThatNamesItsNetworkTwiceIsRefusedNamingBoth()
    {
        string scenario = WriteConfiguration(
            "scenario.sumocfg", "<net-file value=\"first.net.xml\"/><n value=\"second.net.xml\"/>");

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioNetworkCheck.NetworkFileOf(scenario));
        Assert.Contains("'first.net.xml'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'second.net.xml'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScenarioWhoseNetworkIsNotThereIsRefusedNamingWhereItLooked()
    {
        string scenario = WriteConfiguration("scenario.sumocfg", "<net-file value=\"missing.net.xml\"/>");
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioNetworkCheck.Require(scenario, world.PackagePath));
        Assert.Contains(Path.Combine(_directory, "missing.net.xml"), refused.Message, StringComparison.Ordinal);
    }

    [RequiresSumoFact]
    public void ASessionOnAnotherNetworkIsRefusedBeforeSumoIsStarted()
    {
        // The route file the configuration names is never written: had SUMO been launched, it would
        // have failed to load it and said so on its console, and the start would have failed as a
        // TraCI error rather than as this refusal.
        WriteMovedNetwork("RightAngleTurn.net.xml");
        string scenario = WriteConfiguration(
            "scenario.sumocfg",
            "<net-file value=\"RightAngleTurn.net.xml\"/><route-files value=\"never-written.rou.xml\"/>");
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        List<string> console = [];
        var options = new SumoDriveSessionOptions(
            scenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 60.0,
                                      hysteresisMetres: 15.0, capacity: 8))
        {
            TickWorld = () => true,
            SumoOutput = console.Add,
        };

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        _output.WriteLine(refused.Message);
        Assert.Contains("different road graphs", refused.Message, StringComparison.Ordinal);
        Assert.Empty(console);
    }

    [ShippedWorldPackagesFact]
    public void EachShippedWorldSOwnNetworkWrittenBesideAConfigurationIsAdmitted()
    {
        // As the scenario compiler writes it: the package's own network, byte for byte, named by the
        // configuration beside it.
        foreach (string package in ShippedPackages())
        {
            string map = WorldPackage.ReadManifest(package).MapName;
            File.WriteAllText(Path.Combine(_directory, map + ".net.xml"), WorldPackage.ReadNetwork(package));
            string scenario = WriteConfiguration(map + ".sumocfg", $"<net-file value=\"{map}.net.xml\"/>");

            ScenarioNetworkCheck.Require(scenario, package);
        }
    }

    [ShippedWorldPackagesFact]
    public void TheShippedScenariosRunOnTheirWorldsOwnNetworks()
    {
        // Gardnerville is compiled into Import/ with its package's network written beside the
        // configuration; Arapahoe's generator writes its package's network with two `opposite`
        // attributes added, which the fingerprint does not cover. Each was a separate conversion of
        // its world's area before it was regenerated, and refused.
        (string Scenario, string Package)[] shipped =
        [
            (RepositoryFile("Import", "Arapahoe_I25_UnderpassDwell.sumocfg"),
             ShippedWorldPackagesFactAttribute.Arapahoe!),
            (RepositoryFile("Import", "Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg"),
             ShippedWorldPackagesFactAttribute.Gardnerville!),
        ];

        foreach ((string scenario, string package) in shipped)
        {
            ScenarioNetworkCheck.Require(scenario, package);
        }

        // And each is refused against the other's world, which shares nothing with it.
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => ScenarioNetworkCheck.Require(shipped[0].Scenario, shipped[1].Package));
        _output.WriteLine(refused.Message);
        Assert.Contains("different road graphs", refused.Message, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still open somewhere is not worth failing a test over; the temporary directory
            // is the operating system's to clean up.
        }
    }

    /// <summary>
    /// The fixture network with one junction lane's shape moved by a tenth of a metre, written into
    /// the test's directory; its full path.
    /// </summary>
    private string WriteMovedNetwork(string name)
    {
        string text = File.ReadAllText(CoSimFixtures.RightAngleTurnNetwork);
        Assert.Equal(1, CountOf(text, FixtureShapePoint));
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, text.Replace(FixtureShapePoint, MovedShapePoint, StringComparison.Ordinal));
        return path;
    }

    /// <summary>A SUMO configuration in the test's directory holding the given options; its full path.</summary>
    private string WriteConfiguration(string name, string options)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, string.Format(
            CultureInfo.InvariantCulture,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<configuration>\n    <input>\n        {0}\n"
            + "    </input>\n</configuration>\n",
            options));
        return path;
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static IEnumerable<string> ShippedPackages() =>
        [
            ShippedWorldPackagesFactAttribute.Arapahoe!,
            ShippedWorldPackagesFactAttribute.Gardnerville!,
            ShippedWorldPackagesFactAttribute.Bahonar!,
        ];

    /// <summary>A file in the repository, found upward from the test's own output directory.</summary>
    private static string RepositoryFile(params string[] parts)
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
