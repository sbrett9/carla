using CarlaNet.Recording;
using Xunit.Abstractions;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The window opens at its own instant, which a prewarm renders up to: a frozen sun is pinned there,
/// an advancing one is anchored there, and every prewarm frame is declared and audited against the
/// sun it is actually lit by.
/// </summary>
/// <remarks>
/// The fixture steps at the world's delta, so forty SUMO steps are two simulated seconds, and a
/// window opening at two seconds follows forty prewarm frames.
/// </remarks>
public sealed class SumoDriveSessionWindowTests
{
    private const double WindowOpens = 2.0;
    private readonly ITestOutputHelper _output;

    public SumoDriveSessionWindowTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresSumoFact]
    public void AFrozenSunIsPinnedAtTheWindowSOpeningAndLightsThePrewarmAsDeclared()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, carla, IlluminationPolicy.FreezeAtWindowStart());

        using SumoDriveSession session = SumoDriveSession.Start(options);

        // Rendered from zero, pinned at two seconds: 07:00:02, not 07:00:00.
        Assert.Equal(0.0, session.FirstRenderedSeconds);
        Assert.Equal(WindowOpens, session.WindowOpensAtSeconds);
        Assert.Equal(0, carla.Ticks);
        Assert.Equal((7, 0, 2), SolarPositionModel.EngineClock(carla.Sun!.SolarTime));
        Assert.Equal("2026-03-21T07:00:02+03:30", SolarEpoch.FormatCivil(session.Sun!.Declared.WindowOpenCivil));

        for (int step = 0; step < 60; step++)
        {
            session.Advance();
        }

        // Every frame audited against the sun that lit it, and none stopped the run.
        Assert.Null(session.SunAudit!.Failure);
        Assert.Equal(60, session.SunAudit.AuditedTicks);
        Assert.Equal(2, carla.SolarWrites.Count);

        // The first prewarm frame is 07:00:00 and says it was lit by the window's 07:00:02; the window's
        // own first frame, the forty-first, is 07:00:02 and lit by the same.
        IlluminationDeclaration first = Declaration(session, 1);
        Assert.Equal("2026-03-21T07:00:00+03:30", first.DeclaredCivil);
        Assert.Equal("2026-03-21T07:00:02+03:30", first.SunDeclared);
        IlluminationDeclaration opening = Declaration(session, 41);
        Assert.Equal("2026-03-21T07:00:02+03:30", opening.DeclaredCivil);
        Assert.Equal("2026-03-21T07:00:02+03:30", opening.SunDeclared);

        string report = session.Report.ToString();
        _output.WriteLine(report);
        Assert.Contains("window             opens at t=2 s (2026-03-21T07:00:02+03:30); rendered from "
                        + "t=0 s, 2 s of prewarm first", report);
        Assert.Contains("bound            2026-03-21T07:00:02+03:30", report);
    }

    [RequiresSumoFact]
    public void AnAdvancingSunIsAnchoredAtTheWindowSOpeningAndWrittenForEachPrewarmFrame()
    {
        // At twice the rate of simulated time the anchor shows: anchored at the window's opening the
        // first prewarm frame is lit four sun-seconds before it, and the window's first frame by its
        // own instant.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        using SumoDriveSession session = SumoDriveSession.Start(
            Options(world, carla, IlluminationPolicy.Advance(2.0)));

        for (int step = 0; step < 60; step++)
        {
            session.Advance();
        }

        Assert.Null(session.SunAudit!.Failure);
        Assert.Equal("2026-03-21T06:59:58+03:30", Declaration(session, 1).SunDeclared);
        Assert.Equal("2026-03-21T07:00:02+03:30", Declaration(session, 41).SunDeclared);
        Assert.Equal("2026-03-21T07:00:02+03:30", Declaration(session, 41).DeclaredCivil);

        // The binding, then one write for every frame the prewarm and the window rendered.
        Assert.Equal(2 + 60, carla.SolarWrites.Count);
    }

    [RequiresSumoFact]
    public void AFreezeAtACivilTimeTakesTheDateTheWindowOpensOn()
    {
        // Simulated second zero is a second before midnight; the window opens a second after it. The
        // first rendered frame is on the 21st and the window on the 22nd, and the sun is frozen at
        // noon of the window's date.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(
            world, carla, IlluminationPolicy.FreezeAt(TimeSpan.FromHours(12), freezeDateAdvances: true));
        options.Epoch = SolarEpoch.Declare("2026-03-21T23:59:59+03:30", 3.5, "2026-03-21T20:29:59Z",
                                           calendarAdvances: true, dstInEffect: false);

        using SumoDriveSession session = SumoDriveSession.Start(options);

        Assert.Equal((2026, 3, 22), (carla.Sun!.Year, carla.Sun.Month, carla.Sun.Day));
        Assert.Equal((12, 0, 0), SolarPositionModel.EngineClock(carla.Sun.SolarTime));
    }

    [RequiresSumoFact]
    public void WithNoWindowGivenTheWindowOpensAtTheFirstRenderedFrame()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, carla, IlluminationPolicy.FreezeAtWindowStart());
        options.WindowOpensAtSimulatedSecond = null;
        options.WarmUpToSimulatedSecond = 2.0;

        using SumoDriveSession session = SumoDriveSession.Start(options);

        Assert.Equal(2.0, session.FirstRenderedSeconds, 9);
        Assert.Equal(session.FirstRenderedSeconds, session.WindowOpensAtSeconds);
        Assert.Contains("; its first frame is the first rendered", session.Report.ToString());
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AWindowThatOpensOnNoInstantTheSessionRendersIsRefusedAtValidation(double opens)
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, carla, IlluminationPolicy.FreezeAtWindowStart());
        options.WarmUpToSimulatedSecond = 2.0;
        options.WindowOpensAtSimulatedSecond = opens;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDriveSession.Start(options));

        Assert.Contains("the session renders from 2 s", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Empty(carla.SettingsWrites);
    }

    [RequiresSumoFact]
    public void ARunThatStopsDuringThePrewarmStoppedAtPreRoll()
    {
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        using SumoDriveSession session = SumoDriveSession.Start(
            Options(world, carla, IlluminationPolicy.FreezeAtWindowStart()));
        for (int step = 0; step < 39; step++)
        {
            session.Advance();
        }

        // The fortieth frame, at 1.95 s, is the prewarm's last.
        carla.ProducesFrames = false;
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => session.Advance());

        Assert.Equal(CoSimSessionStage.PreRoll, refused.Stage);
    }

    [RequiresSumoTheory]
    [InlineData(2.0, 40)]
    [InlineData(0.5, 10)]
    public void ARunThatStopsOnTheWindowSFirstFrameStoppedInTheWindow(double opens, int steps)
    {
        // The rendered clock is a running sum of the world's delta, which reaches the window's
        // instant only to within rounding: forty steps sum to 2.000000000000001, ten to
        // 0.49999999999999994. Either way the next frame is the window's first.
        using SyntheticWorld world = Fixture();
        var carla = new RecordedWorld { Loaded = world.AsLoaded() };
        SumoDriveSessionOptions options = Options(world, carla, IlluminationPolicy.FreezeAtWindowStart());
        options.WindowOpensAtSimulatedSecond = opens;
        using SumoDriveSession session = SumoDriveSession.Start(options);
        for (int step = 0; step < steps; step++)
        {
            session.Advance();
        }

        Assert.Equal(opens, session.RenderedTimeSeconds, 9);
        carla.ProducesFrames = false;
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => session.Advance());

        Assert.Equal(CoSimSessionStage.Window, refused.Stage);
    }

    private static IlluminationDeclaration Declaration(SumoDriveSession session, ulong frame)
    {
        Assert.True(session.Illumination.TryGetDeclaration(frame, out IlluminationDeclaration declared));
        return declared;
    }

    private static SyntheticWorld Fixture() =>
        SyntheticWorld.Write(_ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

    private static SumoDriveSessionOptions Options(SyntheticWorld world, RecordedWorld carla,
                                                   IlluminationPolicy policy) =>
        new(CoSimFixtures.RightAngleTurnScenario,
            world.PackagePath,
            CoSimFixtures.VehicleCatalogue,
            "test://" + Guid.NewGuid().ToString("n"),
            new RegionRenderSetPolicy(0.0, 0.0, admitRadiusMetres: 60.0,
                                      hysteresisMetres: 15.0, capacity: 8))
        {
            World = carla,
            Epoch = SolarEpoch.Declare("2026-03-21T07:00:00+03:30", 3.5, "2026-03-21T03:30:00Z",
                                       calendarAdvances: true, dstInEffect: false),
            Illumination = policy,
            WindowOpensAtSimulatedSecond = WindowOpens,
        };
}
