using CarlaNet.Sumo;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Every admission pass -- the population, the eligible, the admitted and the shed -- is published as
/// it is made, not at the end of the run.
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
        // A capacity of one, so the cross holds more eligible vehicles than places and the capacity
        // sheds some.
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        List<AdmissionPass> passes = [];
        SumoDriveSessionOptions options = Options(world, capacity: 1);
        options.OnAdmissionPass = passes.Add;

        using SumoDriveSession session = SumoDriveSession.Start(options);

        // Two passes while starting: the fast-forward's frame and the step of lookahead after it.
        Assert.Equal(2, passes.Count);
        Assert.Same(passes[^1], session.Report.LastAdmissionPass);

        for (int step = 0; step < Steps && session.Advance(); step++)
        {
            // Published by the time the advance returns: one new pass, on the report, of the SUMO
            // frame the step read.
            Assert.Equal(step + 3, passes.Count);
            Assert.Same(passes[^1], session.Report.LastAdmissionPass);
            Assert.Equal(session.RenderedTimeSeconds + session.Clock.SumoStepSeconds,
                         passes[^1].SimulatedTimeSeconds, 9);
            Assert.Equal(session.Report.Ticks, passes[^1].WorldTick);
        }

        _output.WriteLine(session.Report.ToString());
        Assert.Equal(session.Report.SumoSteps + 1, passes.Count);

        long admissions = 0;
        long declines = 0;
        foreach (AdmissionPass pass in passes)
        {
            Assert.InRange(pass.Subscribed, 0, pass.Population);
            Assert.InRange(pass.Eligible, 0, pass.Subscribed);
            Assert.Equal(Math.Min(pass.Eligible, pass.Capacity), pass.Admitted);
            Assert.Equal(pass.Eligible - pass.Admitted, pass.Shed);
            Assert.Equal(1, pass.Capacity);
            admissions += pass.NewlyAdmitted;
            declines += pass.Shed;
            Assert.Equal(admissions, pass.TotalAdmissions);
            Assert.Equal(declines, pass.TotalCapacityDeclines);
        }

        // The capacity shed vehicles on some passes, and the run's totals are the last pass's.
        Assert.Contains(passes, pass => pass.Shed > 0);
        Assert.Contains(passes, pass => pass.Released > 0);
        Assert.Equal(session.Report.Admissions, passes[^1].TotalAdmissions);
        Assert.Equal(session.Report.CapacityDeclines, passes[^1].TotalCapacityDeclines);
        Assert.Contains($"  last pass        {passes[^1]}", session.Report.ToString());
    }

    [RequiresSumoFact]
    public void ThePopulationIsEveryVehicleSumoHasWhetherOrNotItIsNearTheRegion()
    {
        // The same scenario and seed stepped by a second SUMO alone: its vehicle list at each instant
        // is the population a pass of that instant must report. The region is narrow, so some
        // vehicles SUMO has are outside the subscription margin and still counted.
        List<AdmissionPass> passes = Run(capacity: 8, radius: 20.0);
        Dictionary<long, int> living = Living();

        Assert.Contains(passes, pass => pass.Population >= 3);
        Assert.Contains(passes, pass => pass.Subscribed < pass.Population);
        foreach (AdmissionPass pass in passes)
        {
            Assert.Equal(living[Key(pass.SimulatedTimeSeconds)], pass.Population);
        }
    }

    [RequiresSumoFact]
    public void ARegionTakingInTheWholeNetworkWithRoomForAllAdmitsTheWholePopulation()
    {
        List<AdmissionPass> passes = Run(capacity: 8, radius: 400.0);
        Dictionary<long, int> living = Living();

        foreach (AdmissionPass pass in passes)
        {
            Assert.Equal(living[Key(pass.SimulatedTimeSeconds)], pass.Population);
            Assert.Equal(pass.Population, pass.Eligible);
            Assert.Equal(pass.Population, pass.Admitted);
            Assert.Equal(0, pass.Shed);
        }
    }

    /// <summary>Every pass a session on the fixture makes over the test's steps.</summary>
    private static List<AdmissionPass> Run(int capacity, double radius)
    {
        using SyntheticWorld world = SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        List<AdmissionPass> passes = [];
        SumoDriveSessionOptions options = Options(world, capacity, radius);
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

    private static SumoDriveSessionOptions Options(SyntheticWorld world, int capacity, double radius = 60.0) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: radius,
                                      hysteresisMetres: 15.0, capacity: capacity))
        {
            TickWorld = () => true,
        };
}
