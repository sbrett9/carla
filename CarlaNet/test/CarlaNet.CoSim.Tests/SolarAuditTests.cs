using CarlaNet.Types.Rpc.Environment;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The per-tick comparison of the world's sun against the declared one, at both sites, and every
/// way a sun can stop being the declared one partway through a run.
/// </summary>
/// <remarks>
/// <para>Both sites, because a single site hides the defect the declaration exists to remove: at
/// Arapahoe a sun left at longitude/15 is 0.46 minutes from civil time, at the Bahonar port 14.72.
/// The windows are placed at 17:00 on the December solstice, where the port's sun is just below the
/// horizon and the refraction-corrected elevation differs from the geometric one by a fifth of a
/// degree.</para>
///
/// <para>The world here is a simulated sun that evaluates the engine's algorithm through the engine's
/// clock decomposition. What these establish is that the audit compares the right things against
/// the right declaration and fails when they disagree; that the running engine agrees with the
/// algorithm is established separately, by the readings the model tests are pinned to.</para>
/// </remarks>
public sealed class SolarAuditTests
{
    private const double WorldDelta = 0.05;

    public static TheoryData<string, double, double> Sites => new()
    {
        { "Arapahoe", 39.59431, -104.88449 },
        { "Bahonar", 27.15012, 56.18065 },
    };

    /// <summary>Midnight at the start of 21 December, in each site's civil offset.</summary>
    private static SolarEpoch SolsticeEpoch(string site) => site == "Bahonar"
        ? SolarEpoch.Declare("2026-12-21T00:00:00+03:30", 3.5, "2026-12-20T20:30:00Z", true, false, "Asia/Tehran")
        : SolarEpoch.Declare("2026-12-21T00:00:00-07:00", -7.0, "2026-12-21T07:00:00Z", true, false, "America/Denver");

    [Theory]
    [MemberData(nameof(Sites))]
    public void AFrozenTerminatorWindowHoldsTheDeclaredSunAtBothSites(string site, double latitude,
                                                                      double longitude)
    {
        (RecordedWorld world, SolarAudit audit) = Bind(latitude, longitude, SolsticeEpoch(site),
                                                       IlluminationPolicy.FreezeAtWindowStart(), 61_200);

        Run(world, audit, 61_200, 200);

        Assert.Equal(200, audit.AuditedTicks);
        Assert.Equal(200, audit.TicksWithCorrectedElevation);
        Assert.True(audit.WorstAngle!.AngleResidualDegrees < 1e-4,
                    $"{site}: {audit.WorstAngle.AngleResidualDegrees} degrees");
        Assert.True(Math.Abs(audit.WorstCorrected!.CorrectedResidualDegrees!.Value) < 1e-4);

        // The recorded clock sits the written millisecond past the declared second, and no further.
        Assert.Equal(0.001, audit.WorstClock!.ClockResidualSeconds, 6);
    }

    [Fact]
    public void TheDeclaredSunAtThePortIsTheOneTheEngineWasMeasuredToRender()
    {
        (_, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                     IlluminationPolicy.FreezeAtWindowStart(), 61_200);

        // get_solar_state read these at 17:00 on the solstice at +03:30 on a running server: a sun
        // below the horizon either way, but by 1.58 degrees geometrically and 1.37 by the light.
        SunPosition declared = audit.AtWindowOpen!.Modelled;
        Assert.Equal(-1.58296, declared.ElevationDegrees, 1e-4);
        Assert.Equal(-1.37418, declared.CorrectedElevationDegrees, 1e-4);
    }

    [Fact]
    public void AnAdvancingWindowAtRealTimeAgreesToTheFloorAndReadsOneTickAhead()
    {
        (RecordedWorld world, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                                       IlluminationPolicy.Advance(1.0), 25_200);

        Run(world, audit, 25_200, 40);

        // The engine's controller advances the clock before the snapshot is published, so each
        // tick's sun reads one tick's advance past the instant its poses were written for: 0.05 s
        // at a real-time rate, a hundredth of the tolerance. Measured here from the simulated sun,
        // which is built to tick that way; a live run is what establishes the engine does.
        Assert.Equal(40, audit.AuditedTicks);
        Assert.Equal(0.05, audit.WorstClock!.ClockResidualSeconds, 6);
        Assert.True(audit.WorstAngle!.AngleResidualDegrees < SolarPositionModel.ResolutionFloorDegrees);
    }

    [Fact]
    public void AnAdvancingSunIsStoppedWhereTheEngineRendersTheMinuteBefore()
    {
        // Two seconds before 07:01. When the carried clock reaches 07:00:59.5 the engine rounds its
        // seconds to sixty and drops the minute: the world holds the declared clock and renders the
        // sun of 07:00:00, a quarter of a degree of hour angle early.
        (RecordedWorld world, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                                       IlluminationPolicy.Advance(1.0), 25_258);

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => Run(world, audit, 25_258, 60));

        Assert.Contains("GetHMSFromSolarTime", failed.Message);
        Assert.Contains("remedy is that carry in the engine", failed.Message);
        double seconds = TimeSpan.FromHours(failed.Sample!.Observed.SolarTimeHours).TotalSeconds % 60.0;
        Assert.InRange(seconds, 59.5, 60.0);
        Assert.Same(failed.Sample, audit.Failure);
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void AZoneLeftAtLongitudeOverFifteenIsCaughtAtBothSites(string site, double latitude,
                                                                   double longitude)
    {
        (RecordedWorld world, SolarAudit audit) = Bind(latitude, longitude, SolsticeEpoch(site),
                                                       IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        Run(world, audit, 61_200, 10);

        // Another client configures the georeference mid-run, which resets the zone to local mean
        // solar time. At Arapahoe that moves the sun a tenth of a degree, which an angle check
        // alone could read as noise; the zone is compared exactly, so it cannot.
        world.Sun!.TimeZone = longitude / 15.0;

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditTick(10, 61_200.5, TickOnce(world)));
        Assert.Contains("local mean solar time", failed.Message);
        Assert.Contains("tick 10", failed.Message);
    }

    [Fact]
    public void AClockAnotherClientMovedStopsTheRunRatherThanBeingRewritten()
    {
        (RecordedWorld world, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                                       IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        world.Sun!.SolarTime += 30.0 / 3600.0;
        int writes = world.SolarWrites.Count;

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditTick(0, 61_200, TickOnce(world)));

        Assert.Contains("30.001 s from the declared instant", failed.Message);
        Assert.Equal(writes, world.SolarWrites.Count);
    }

    [Fact]
    public void ADateADayOutIsNamedAsTheDate()
    {
        (RecordedWorld world, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                                       IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        world.Sun!.Day += 1;

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditTick(0, 61_200, TickOnce(world)));

        Assert.Contains("a whole 1 day(s): the date is wrong", failed.Message);
    }

    [Fact]
    public void AnAdvanceAnotherClientSwitchedOnIsCaught()
    {
        (RecordedWorld world, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                                       IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        world.Sun!.WriteAdvance(true, 60.0);

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditTick(0, 61_200, TickOnce(world)));

        Assert.Contains("something other than this session is driving it", failed.Message);
    }

    [Fact]
    public void ASunComputedForAnotherPlaceIsNamedAsSuch()
    {
        // The world's georeference is at the port and the package the session drives is Arapahoe's.
        var world = new RecordedWorld();
        world.Sun!.Latitude = 27.15012;
        world.Sun.Longitude = 56.18065;
        var declared = new DeclaredSun(SolsticeEpoch("Arapahoe"), IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        using SolarLease lease = SolarLease.Take(world, declared);
        var audit = new SolarAudit(declared, 39.59431, -104.88449, WorldDelta);

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditWindowOpen(lease.AtWindowOpen!.Value));

        Assert.Contains("not the world package's origin", failed.Message);
        Assert.Contains("window open", failed.Message);
    }

    [Fact]
    public void ASnapshotWithNoSunStopsTheRun()
    {
        (RecordedWorld world, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                                       IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        world.ObserverPublishesNoSun = true;

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditTick(3, 61_200.15, TickOnce(world)));

        Assert.Contains("published no sun", failed.Message);
        Assert.Null(failed.Sample);
    }

    [Fact]
    public void TheCorrectedElevationIsComparedEachTickOnlyWhereTheSnapshotCarriesIt()
    {
        // A server built before the observer header carried the corrected elevation: it is compared
        // once, on demand, when the window opens, and the audit says it was not compared per tick.
        (RecordedWorld world, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                                       IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        world.ObserverCarriesCorrectedElevation = false;

        Run(world, audit, 61_200, 20);

        Assert.Equal(20, audit.AuditedTicks);
        Assert.Equal(0, audit.TicksWithCorrectedElevation);
        Assert.NotNull(audit.AtWindowOpen!.CorrectedResidualDegrees);
    }

    [Fact]
    public void ACorrectedElevationThatIsNotTheDeclaredOneIsCaughtWhenTheWindowOpens()
    {
        var world = new RecordedWorld();
        world.Sun!.Latitude = 27.15012;
        world.Sun.Longitude = 56.18065;
        world.Sun.CorrectedElevationError = 0.1;
        var declared = new DeclaredSun(SolsticeEpoch("Bahonar"), IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        using SolarLease lease = SolarLease.Take(world, declared);
        var audit = new SolarAudit(declared, 27.15012, 56.18065, WorldDelta);

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditWindowOpen(lease.AtWindowOpen!.Value));

        Assert.Contains("refraction-corrected elevation, which its light is rotated by", failed.Message);
    }

    [Fact]
    public void TheTolerancesWidenWithTheRateAndNeverFallBelowTheFloor()
    {
        var epoch = SolsticeEpoch("Bahonar");
        var frozen = new SolarAudit(new DeclaredSun(epoch, IlluminationPolicy.FreezeAtWindowStart(), 0), 0, 0, WorldDelta);
        var realTime = new SolarAudit(new DeclaredSun(epoch, IlluminationPolicy.Advance(1.0), 0), 0, 0, WorldDelta);
        var fast = new SolarAudit(new DeclaredSun(epoch, IlluminationPolicy.Advance(3600.0), 0), 0, 0, WorldDelta);

        Assert.Equal((0.5, 0.01), (frozen.ToleranceSeconds, frozen.ToleranceDegrees));
        Assert.Equal((0.5, 0.01), (realTime.ToleranceSeconds, realTime.ToleranceDegrees));

        // An hour of sun per simulated second moves it 180 sun-seconds a tick.
        Assert.Equal(360.0, fast.ToleranceSeconds, 9);
        Assert.Equal(1.5, fast.ToleranceDegrees, 9);
    }

    private static (RecordedWorld World, SolarAudit Audit) Bind(double latitude, double longitude,
                                                                SolarEpoch epoch,
                                                                IlluminationPolicy policy,
                                                                double windowOpens)
    {
        var world = new RecordedWorld
        {
            Settings = new EpisodeSettings(SynchronousMode: true, NoRenderingMode: false,
                                           FixedDeltaSeconds: WorldDelta, Substepping: true,
                                           MaxSubstepDeltaTime: 0.01, MaxSubsteps: 10,
                                           MaxCullingDistance: 0f, DeterministicRagdolls: false,
                                           TileStreamDistance: 3000f, ActorActiveDistance: 2000f,
                                           SpectatorAsEgo: true),
        };
        world.Sun!.Latitude = latitude;
        world.Sun.Longitude = longitude;
        var declared = new DeclaredSun(epoch, policy, windowOpens);
        SolarLease lease = SolarLease.Take(world, declared);
        var audit = new SolarAudit(declared, latitude, longitude, WorldDelta);
        audit.AuditWindowOpen(lease.AtWindowOpen!.Value);
        return (world, audit);
    }

    private static void Run(RecordedWorld world, SolarAudit audit, double windowOpens, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            audit.AuditTick(tick, windowOpens + (tick * WorldDelta), TickOnce(world));
        }
    }

    private static IReadOnlyList<double> TickOnce(RecordedWorld world)
    {
        world.Tick();
        return world.ObservedSolarState();
    }
}
