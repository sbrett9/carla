using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// The SUMO configurations these tests run, in the fixture directory the build copies to the test
/// output. Both sit on the same hand-written 200 m single-lane network, small enough to read.
/// </summary>
internal static class SumoFixtures
{
    /// <summary>The network with no vehicles at all.</summary>
    public static string EmptySimulation => Configuration("SingleEdge.sumocfg");

    /// <summary>The same network with two vehicles on it.</summary>
    public static string TwoVehicles => Configuration("SingleEdgeTraffic.sumocfg");

    /// <summary>A path that exists but is not a SUMO configuration.</summary>
    public static string NotAConfiguration => Configuration("SingleEdge.nod.xml");

    /// <summary>
    /// Open a session on one of these configurations, keeping SUMO's console output out of the test
    /// output unless something asks for it.
    /// </summary>
    public static SumoConnection Open(string configurationPath, SumoLaunchOptions? options = null) =>
        SumoConnection.Start(SumoInstallation.LocateOrThrow(), configurationPath,
                             options ?? new SumoLaunchOptions { Output = _ => { } });

    private static string Configuration(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
