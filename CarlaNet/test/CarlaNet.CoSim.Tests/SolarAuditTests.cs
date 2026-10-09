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
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            latitude, longitude, SolsticeEpoch(site), IlluminationPolicy.FreezeAtWindowStart(), 61_200);

        Run(world, lease, audit, 61_200, 200);

        // Nothing is written per frame under a freeze: the bind and nothing after it.
        Assert.Equal(["set_solar_epoch", "set_time_advance"], world.SolarWrites.Select(write => write.Call));

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
        (_, _, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                        IlluminationPolicy.FreezeAtWindowStart(), 61_200);

        // get_solar_state read these at 17:00 on the solstice at +03:30 on a running server: a sun
        // below the horizon either way, but by 1.58 degrees geometrically and 1.37 by the light.
        SunPosition declared = audit.AtWindowOpen!.Modelled;
        Assert.Equal(-1.58296, declared.ElevationDegrees, 1e-4);
        Assert.Equal(-1.37418, declared.CorrectedElevationDegrees, 1e-4);
    }

    [Fact]
    public void AnAdvancingWindowAtRealTimeIsWrittenForEveryFrameWithinHalfASecond()
    {
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            27.15012, 56.18065, SolsticeEpoch("Bahonar"), IlluminationPolicy.Advance(1.0), 25_200);

        Run(world, lease, audit, 25_200, 40);

        // One write per frame, each before its tick, with the engine's own advance off throughout.
        Assert.Equal(40, lease.FrameWrites);
        Assert.Equal(42, world.SolarWrites.Count);
        Assert.False(world.Sun!.Advancing);

        // The frame at 07:00:00.50 is written at 07:00:00.001: the whole second nearest the declared
        // instant less the millisecond, so the residual is the half-second less that millisecond and
        // the furthest any frame sits.
        Assert.Equal(40, audit.AuditedTicks);
        Assert.Equal(-0.499, audit.WorstClock!.ClockResidualSeconds, 6);
        Assert.Equal(10, audit.WorstClock.TickIndex);
        Assert.True(audit.WorstAngle!.AngleResidualDegrees < 0.003,
                    $"{audit.WorstAngle.AngleResidualDegrees} degrees");
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void AnAdvancingWindowHoldsAcrossTheMinuteTheEngineWouldDrop(string site, double latitude,
                                                                        double longitude)
    {
        // Two seconds before 07:01. A clock the engine carries forward reaches 07:00:59.5, where the
        // engine rounds its seconds to sixty and drops the minute, and renders the sun of 07:00:00.
        // Written by the session, the frame declared at 07:00:59.55 holds 07:01:00.001.
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            latitude, longitude, SolsticeEpoch(site), IlluminationPolicy.Advance(1.0), 25_258);
        List<SolarAuditSample> samples = [];

        for (int tick = 0; tick < 80; tick++)
        {
            double rendered = 25_258 + (tick * WorldDelta);
            lease.WriteForFrame(rendered);
            samples.Add(audit.AuditTick(tick, rendered, TickOnce(world)));
        }

        SolarAuditSample crossing = samples[31];
        Assert.Equal(new DateTime(2026, 12, 21, 7, 0, 59, 550), crossing.DeclaredSun);
        Assert.Equal((7, 1, 0), SolarPositionModel.EngineClock(crossing.Observed.SolarTimeHours));
        Assert.Equal(0.451, crossing.ClockResidualSeconds, 6);
        Assert.All(samples, sample => Assert.InRange(sample.ClockResidualSeconds, -0.5, 0.5));
        Assert.True(audit.WorstAngle!.AngleResidualDegrees < 0.003);
        Assert.Null(audit.Failure);
    }

    [Fact]
    public void AnAdvancingWindowCarriesTheDateAcrossMidnightWhenTheCalendarAdvances()
    {
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            27.15012, 56.18065, SolsticeEpoch("Bahonar"), IlluminationPolicy.Advance(1.0), 86_398);

        Run(world, lease, audit, 86_398, 80);

        // Two seconds past midnight, on the 22nd: the session wrote the date, as the engine never does.
        SolarReading last = audit.Last!.Observed;
        Assert.Equal((2026, 12, 22), (last.Year, last.Month, last.Day));
        Assert.Equal((0, 0, 2), SolarPositionModel.EngineClock(last.SolarTimeHours));
        Assert.Equal(80, audit.AuditedTicks);
    }

    [Fact]
    public void AHeldCalendarWrapsTheAdvancingClockOntoTheEpochSDate()
    {
        SolarEpoch held = SolarEpoch.Declare("2026-12-21T00:00:00+03:30", 3.5, "2026-12-20T20:30:00Z",
                                             calendarAdvances: false, dstInEffect: false, "Asia/Tehran");
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            27.15012, 56.18065, held, IlluminationPolicy.Advance(1.0), 86_398);
        List<SolarAuditSample> samples = [];

        for (int tick = 0; tick < 80; tick++)
        {
            double rendered = 86_398 + (tick * WorldDelta);
            lease.WriteForFrame(rendered);
            samples.Add(audit.AuditTick(tick, rendered, TickOnce(world)));
        }

        // The frame declared at 23:59:59.55 on the held date is nearest the following midnight, which
        // the engine holds on the following date; from midnight on, the clock is back on the 21st.
        SolarReading beforeMidnight = samples[31].Observed;
        Assert.Equal(new DateTime(2026, 12, 21, 23, 59, 59, 550), samples[31].DeclaredSun);
        Assert.Equal((2026, 12, 22, 0, 0, 0),
                     (beforeMidnight.Year, beforeMidnight.Month, beforeMidnight.Day,
                      SolarPositionModel.EngineClock(beforeMidnight.SolarTimeHours).Hour,
                      SolarPositionModel.EngineClock(beforeMidnight.SolarTimeHours).Minute,
                      SolarPositionModel.EngineClock(beforeMidnight.SolarTimeHours).Second));
        SolarReading last = samples[^1].Observed;
        Assert.Equal((2026, 12, 21), (last.Year, last.Month, last.Day));
        Assert.Equal((0, 0, 2), SolarPositionModel.EngineClock(last.SolarTimeHours));
        Assert.Null(audit.Failure);
    }

    [Fact]
    public void AnHourOfSunPerSimulatedSecondHoldsAtTheSameTolerances()
    {
        // 180 sun-seconds a tick. Written per frame, the clock is still within half a second of the
        // declared instant, so nothing about the rate needs a wider tolerance.
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            27.15012, 56.18065, SolsticeEpoch("Bahonar"), IlluminationPolicy.Advance(3600.0), 25_200);

        Run(world, lease, audit, 25_200, 40);

        Assert.Equal((0.5, 0.01), (audit.ToleranceSeconds, audit.ToleranceDegrees));
        Assert.Equal(40, audit.AuditedTicks);
        Assert.InRange(audit.WorstClock!.ClockResidualSeconds, -0.5, 0.5);
    }

    [Fact]
    public void AnEngineAdvanceAnotherClientSwitchedOnUnderAnAdvancingPolicyIsCaught()
    {
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            27.15012, 56.18065, SolsticeEpoch("Bahonar"), IlluminationPolicy.Advance(1.0), 25_200);
        Run(world, lease, audit, 25_200, 5);

        world.Sun!.WriteAdvance(true, 1.0);
        lease.WriteForFrame(25_200.25);

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditTick(5, 25_200.25, TickOnce(world)));
        Assert.Contains("the session writes the sun for every frame itself", failed.Message);
        Assert.Contains("something other than this session is driving it", failed.Message);
    }

    [Fact]
    public void AClockInTheMinuteTheEngineDropsIsNamedAsOneTheSessionDidNotWrite()
    {
        // Something moves the clock to 07:00:59.6 when the session declared 07:00:59.55 for the frame:
        // within the half-second, and lit by the sun of 07:00:00.
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            27.15012, 56.18065, SolsticeEpoch("Bahonar"), IlluminationPolicy.Advance(1.0), 25_259.55);
        lease.WriteForFrame(25_259.55);
        world.Sun!.SolarTime = 7.0 + (59.6 / 3600.0);

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditTick(0, 25_259.55, TickOnce(world)));

        Assert.Contains("GetHMSFromSolarTime", failed.Message);
        Assert.Contains("so this clock was not one it wrote", failed.Message);
        Assert.Same(failed.Sample, audit.Failure);
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void AZoneLeftAtLongitudeOverFifteenIsCaughtAtBothSites(string site, double latitude,
                                                                   double longitude)
    {
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            latitude, longitude, SolsticeEpoch(site), IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        Run(world, lease, audit, 61_200, 10);

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
        (RecordedWorld world, _, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
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
        (RecordedWorld world, _, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
                                                          IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        world.Sun!.Day += 1;

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditTick(0, 61_200, TickOnce(world)));

        Assert.Contains("a whole 1 day(s): the date is wrong", failed.Message);
    }

    [Fact]
    public void AnAdvanceAnotherClientSwitchedOnIsCaught()
    {
        (RecordedWorld world, _, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
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
        var audit = new SolarAudit(declared, 39.59431, -104.88449);

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditWindowOpen(lease.AtWindowOpen!.Value));

        Assert.Contains("not the world package's origin", failed.Message);
        Assert.Contains("window open", failed.Message);
    }

    [Fact]
    public void ASnapshotWithNoSunStopsTheRun()
    {
        (RecordedWorld world, _, SolarAudit audit) = Bind(27.15012, 56.18065, SolsticeEpoch("Bahonar"),
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
        (RecordedWorld world, SolarLease lease, SolarAudit audit) = Bind(
            27.15012, 56.18065, SolsticeEpoch("Bahonar"), IlluminationPolicy.FreezeAtWindowStart(), 61_200);
        world.ObserverCarriesCorrectedElevation = false;

        Run(world, lease, audit, 61_200, 20);

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
        var audit = new SolarAudit(declared, 27.15012, 56.18065);

        SolarAuditFailedException failed = Assert.Throws<SolarAuditFailedException>(
            () => audit.AuditWindowOpen(lease.AtWindowOpen!.Value));

        Assert.Contains("refraction-corrected elevation, which its light is rotated by", failed.Message);
    }

    [Fact]
    public void TheTolerancesAreTheFloorsAtEveryRate()
    {
        var epoch = SolsticeEpoch("Bahonar");
        var frozen = new SolarAudit(new DeclaredSun(epoch, IlluminationPolicy.FreezeAtWindowStart(), 0), 0, 0);
        var realTime = new SolarAudit(new DeclaredSun(epoch, IlluminationPolicy.Advance(1.0), 0), 0, 0);
        var fast = new SolarAudit(new DeclaredSun(epoch, IlluminationPolicy.Advance(3600.0), 0), 0, 0);

        Assert.Equal((0.5, 0.01), (frozen.ToleranceSeconds, frozen.ToleranceDegrees));
        Assert.Equal((0.5, 0.01), (realTime.ToleranceSeconds, realTime.ToleranceDegrees));
        Assert.Equal((0.5, 0.01), (fast.ToleranceSeconds, fast.ToleranceDegrees));
    }

    private static (RecordedWorld World, SolarLease Lease, SolarAudit Audit) Bind(
        double latitude, double longitude, SolarEpoch epoch, IlluminationPolicy policy, double windowOpens)
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
        var audit = new SolarAudit(declared, latitude, longitude);
        audit.AuditWindowOpen(lease.AtWindowOpen!.Value);
        return (world, lease, audit);
    }

    /// <summary>The session's loop: the sun written for each frame before its tick, then audited.</summary>
    private static void Run(RecordedWorld world, SolarLease lease, SolarAudit audit, double windowOpens,
                            int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            double rendered = windowOpens + (tick * WorldDelta);
            lease.WriteForFrame(rendered);
            audit.AuditTick(tick, rendered, TickOnce(world));
        }
    }

    private static IReadOnlyList<double> TickOnce(RecordedWorld world)
    {
        world.Tick();
        return world.ObservedSolarState();
    }
}
