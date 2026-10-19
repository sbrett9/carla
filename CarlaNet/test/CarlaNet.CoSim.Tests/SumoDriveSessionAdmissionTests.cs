using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Every admission pass -- the population, and the vehicles admitted and released -- is published as
/// it is made, not at the end of the run, and its population is every vehicle SUMO has.
/// </summary>
public sealed class SumoDriveSessionAdmissionTests
{
    private const int Steps = 400;
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionAdmissionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void EachPassIsPublishedAsItIsMadeAndItsCountsAgree()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        List<AdmissionPass> passes = [];
        SumoDriveSessionOptions options = Options(world);
        options.OnAdmissionPass = passes.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);

        // Two passes while starting: the fast-forward's frame and the step of lookahead after it.
        Assert.Equal(2, passes.Count);
        Assert.Same(passes[^1], session.Report.LastAdmissionPass);

        for (int step = 0; step < Steps && session.Advance(); step++)
        {
            // Published by the time the advance returns: one new pass, on the report, of the SUMO
            // frame the step read, holding a place for every vehicle in it.
            Assert.Equal(step + 3, passes.Count);
            Assert.Same(passes[^1], session.Report.LastAdmissionPass);
            Assert.Equal(session.RenderedTimeSeconds + session.Clock.SumoStepSeconds,
                         passes[^1].SimulatedTimeSeconds, 9);
            Assert.Equal(session.Report.Ticks, passes[^1].WorldTick);
            Assert.Equal(passes[^1].Population, session.RenderedVehicleIds.Count);
        }

        _output.WriteLine(session.Report.ToString());
        Assert.Equal(session.Report.SumoSteps + 1, passes.Count);

        long admissions = 0;
        int population = 0;
        foreach (AdmissionPass pass in passes)
        {
            admissions += pass.NewlyAdmitted;
            population += pass.NewlyAdmitted - pass.Released;
            Assert.Equal(admissions, pass.TotalAdmissions);
            Assert.Equal(population, pass.Population);
        }

        // Vehicles came and went, and the run's total is the last pass's.
        Assert.Contains(passes, pass => pass.NewlyAdmitted > 0);
        Assert.Contains(passes, pass => pass.Released > 0);
        Assert.Equal(session.Report.Admissions, passes[^1].TotalAdmissions);
        Assert.Contains($"  last pass        {passes[^1]}", session.Report.ToString());
    }

    [RequiresSumoFact]
    public void ThePopulationIsEveryVehicleSumoHasWhereverItIs()
    {
        // The same scenario and seed stepped by a second SUMO alone: its vehicle list at each instant
        // is the population a pass of that instant must report, every one of them in the render set.
        List<AdmissionPass> passes = Run();
        Dictionary<long, int> living = Living();

        Assert.Contains(passes, pass => pass.Population >= 3);
        foreach (AdmissionPass pass in passes)
        {
            Assert.Equal(living[Key(pass.SimulatedTimeSeconds)], pass.Population);
        }

        // Each of the fixture's four vehicles was admitted once, whatever part of the cross it drove.
        Assert.Equal(4, passes[^1].TotalAdmissions);
    }

    /// <summary>Every pass a session on the fixture makes over the test's steps.</summary>
    private static List<AdmissionPass> Run()
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        List<AdmissionPass> passes = [];
        SumoDriveSessionOptions options = Options(world);
        options.OnAdmissionPass = passes.Add;
        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < Steps && session.Advance(); step++)
        {
        }

        return passes;
    }

    /// <summary>How many vehicles a SUMO running the fixture alone has, by instant in milliseconds.</summary>
    private static Dictionary<long, int> Living()
    {
        Dictionary<long, int> living = [];
        using SumoConnection reference = CoSimFixtures.Open(CoSimFixtures.RightAngleTurnScenario);
        living[0] = reference.Vehicles.Ids.Count;
        for (int step = 0; step <= Steps + 1; step++)
        {
            reference.Step();
            living[Key(reference.Time)] = reference.Vehicles.Ids.Count;
        }

        return living;
    }

    private static long Key(double seconds) => (long)Math.Round(seconds * 1000.0);

    private static SumoDriveSessionOptions Options(SyntheticWorld world) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"))
        {
            TickWorld = () => true,
        };
}
