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

    /// <summary>
    /// The same cross with two measured vehicles one after the other, so the second borrows the body the
    /// first gave back, and an unmeasured one alone on the network in between.
    /// </summary>
    public static string SuccessionScenario => Fixture("Succession.sumocfg");

    /// <summary>
    /// The same cross with two measured vehicles that each make one stop: one halts on its lane, the
    /// other leaves its lane to park.
    /// </summary>
    public static string DwellScenario => Fixture("Dwell.sumocfg");

    /// <summary>
    /// The same cross with one vehicle halted on the exit and one stuck behind it, in a configuration that
    /// lets SUMO teleport the one stuck.
    /// </summary>
    public static string JamScenario => Fixture("Jam.sumocfg");

    /// <summary>The same network on its own, for reading lane geometry with no simulation running.</summary>
    public static string RightAngleTurnNetwork => Fixture("RightAngleTurn.net.xml");

    /// <summary>
    /// The OpenDRIVE netconvert 1.27.0 writes from that network with <c>--output.original-names</c>, as a
    /// world build asks for it: every road carrying its edge's <c>sumoId</c>, every connector named after
    /// its internal edge, and every road flat at zero.
    /// </summary>
    public static string RightAngleTurnOpenDrive => Fixture("RightAngleTurn.xodr");

    /// <summary>The measured vehicle catalogue, as the blueprint sweep wrote it.</summary>
    public static string VehicleCatalogue => Fixture("vehicles.catalogue.json");

    /// <summary>Open a session on the fixture scenario, keeping SUMO's console output out of the way.</summary>
    public static SumoConnection Open(string configurationPath, SumoLaunchOptions? options = null) =>
        SumoConnection.Start(SumoInstallation.LocateOrThrow(), configurationPath,
                             options ?? new SumoLaunchOptions { Output = _ => { } });

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
