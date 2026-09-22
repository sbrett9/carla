using CarlaNet.Sumo;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The network, the scenario and the catalogue these tests run against, in the fixture directory
/// the build copies to the test output.
/// </summary>
internal static class CoSimFixtures
{
    /// <summary>The cross of four approaches, and the four vehicles crossing it.</summary>
    public static string RightAngleTurnScenario => Fixture("RightAngleTurn.sumocfg");

    /// <summary>The same network on its own, for reading lane geometry with no simulation running.</summary>
    public static string RightAngleTurnNetwork => Fixture("RightAngleTurn.net.xml");

    /// <summary>The measured vehicle catalogue, as the blueprint sweep wrote it.</summary>
    public static string VehicleCatalogue => Fixture("vehicles.catalogue.json");

    /// <summary>Open a session on the fixture scenario, keeping SUMO's console output out of the way.</summary>
    public static SumoConnection Open(string configurationPath, SumoLaunchOptions? options = null) =>
        SumoConnection.Start(SumoInstallation.LocateOrThrow(), configurationPath,
                             options ?? new SumoLaunchOptions { Output = _ => { } });

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
