namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Binding the world's sun to a window's declared instant, confirming the world took it, and giving
/// back the sun the world was found with.
/// </summary>
/// <remarks>
/// The sun a session finds is never a known state: a loaded world keeps the previous session's, and
/// one nobody configured holds the class-default date. So every test here starts from a sun that is
/// wrong in some way, and most of them are about what happens when the binding does not take.
/// </remarks>
public sealed class SolarLeaseTests
{
    /// <summary>The sizing scenario's epoch: t = 0 is midnight at the port, at +03:30.</summary>
    internal static SolarEpoch PortEpoch(bool calendarAdvances = true) => SolarEpoch.Declare(
        "2026-03-21T00:00:00+03:30", 3.5, "2026-03-20T20:30:00Z", calendarAdvances,
        dstInEffect: false, "Asia/Tehran");

    [Fact]
    public void TheSunIsBoundToTheWindowSOpeningInstantAndFoundAgainAsItWas()
    {
        // A sun left advancing fast by whoever used the world last, on the class-default date.
        var world = new RecordedWorld();
        SimulatedSun sun = world.Sun!;
        sun.Advancing = true;
        sun.Rate = 60.0;

        var declared = new DeclaredSun(PortEpoch(), IlluminationPolicy.FreezeAtWindowStart(), 25_200);
        using (SolarLease lease = SolarLease.Take(world, declared))
        {
            // 07:00 on day 0 at +03:30, frozen: the clock is civil time because the zone is the
            // civil offset, and nothing moves it. Written a millisecond past the second, where the
            // engine's decomposition lands on the second declared.
            Assert.Equal((2026, 3, 21), (sun.Year, sun.Month, sun.Day));
            Assert.Equal(7.0 + (0.001 / 3600.0), sun.SolarTime, 12);
            Assert.Equal((7, 0, 0), SolarPositionModel.EngineClock(sun.SolarTime));
            Assert.Equal(3.5, sun.TimeZone);
            Assert.False(sun.Advancing);
            Assert.Equal(0.0, sun.Rate);

            // What the session would otherwise have captured under, kept for the record.
            SolarReading found = lease.AsFound!.Value;
            Assert.Equal((13.0, 2019, 9, 21, -5.0, true, 60.0),
                         (found.SolarTimeHours, found.Year, found.Month, found.Day, found.TimeZoneHours,
                          found.Advancing, found.Rate));
            Assert.Equal(2026, lease.AtWindowOpen!.Value.Year);
        }

        // Given back exactly, advancing flag and rate included.
        Assert.Equal((13.0, 2019, 9, 21, -5.0, true, 60.0),
                     (sun.SolarTime, sun.Year, sun.Month, sun.Day, sun.TimeZone, sun.Advancing, sun.Rate));
    }

    [Fact]
    public void TheAdvanceSettingIsWrittenAfterTheClockAndBeforeAnyTick()
    {
        var world = new RecordedWorld();
        var declared = new DeclaredSun(PortEpoch(), IlluminationPolicy.Advance(1.0), 25_200);

        using SolarLease lease = SolarLease.Take(world, declared);

        // Enabled first, an advancing sun would carry a clock that was about to be replaced.
        Assert.Equal(["set_solar_epoch", "set_time_advance"], world.SolarWrites.Select(write => write.Call));
        Assert.All(world.SolarWrites, write => Assert.Equal(0, write.AtTick));
        Assert.True(world.Sun!.Advancing);
        Assert.Equal(1.0, world.Sun.Rate);
    }

    [Fact]
    public void AFrozenSunIsDeclaredToTheWholeSecondAndAnAdvancingOneExactly()
    {
        // 07:00:00.6 is declared as 07:00:01, the second the engine would round it to anyway.
        var frozen = new RecordedWorld();
        using (SolarLease.Take(frozen, new DeclaredSun(PortEpoch(), IlluminationPolicy.FreezeAtWindowStart(),
                                                       25_200.6)))
        {
            Assert.Equal(7.0 + (1.001 / 3600.0), frozen.Sun!.SolarTime, 12);
        }

        var advancing = new RecordedWorld();
        using (SolarLease.Take(advancing, new DeclaredSun(PortEpoch(), IlluminationPolicy.Advance(1.0),
                                                          25_200.6)))
        {
            Assert.Equal(7.0 + (0.6 / 3600.0), advancing.Sun!.SolarTime, 12);
        }
    }

    [Fact]
    public void EveryFrozenSecondOfADayIsWrittenWhereTheEngineDecomposesItAsThatSecond()
    {
        // Written at the whole second itself, the engine evaluates 623 of the day's 1,440 whole
        // minutes as the minute before: 01:01:00 as 01:00:00. A millisecond past it, none.
        var epoch = SolarEpoch.Declare("2026-03-21T00:00:00+03:30", 3.5, "2026-03-20T20:30:00Z",
                                       calendarAdvances: false, dstInEffect: false);
        int exactlyOnTheSecond = 0;
        for (int second = 0; second < 86_400; second++)
        {
            var declared = new DeclaredSun(epoch, IlluminationPolicy.FreezeAtWindowStart(), second);
            (int hours, int minutes, int seconds) = (second / 3600, second / 60 % 60, second % 60);
            Assert.Equal((hours, minutes, seconds), SolarPositionModel.EngineClock(declared.WrittenClockHours));
            if (SolarPositionModel.EngineClock(declared.SunAtWindowOpen.TimeOfDay.TotalHours)
                != (hours, minutes, seconds))
            {
                exactlyOnTheSecond++;
            }
        }

        Assert.Equal(623, exactlyOnTheSecond);
    }

    [Fact]
    public void AFrozenWeekKeepsTheEpochSDateUnlessItsDateIsDeclaredToFollow()
    {
        // Day 4's morning shift. A freeze that let the seasonal angle drift across a week would be
        // a freeze in name only, so the sun stays on 21 March unless the policy says otherwise.
        var held = new RecordedWorld();
        using (SolarLease.Take(held, new DeclaredSun(PortEpoch(), IlluminationPolicy.FreezeAtWindowStart(),
                                                     370_800)))
        {
            Assert.Equal((2026, 3, 21), (held.Sun!.Year, held.Sun.Month, held.Sun.Day));
            Assert.Equal((7, 0, 0), SolarPositionModel.EngineClock(held.Sun.SolarTime));
        }

        var following = new RecordedWorld();
        using (SolarLease.Take(following, new DeclaredSun(
                   PortEpoch(), IlluminationPolicy.FreezeAtWindowStart(freezeDateAdvances: true), 370_800)))
        {
            Assert.Equal((2026, 3, 25), (following.Sun!.Year, following.Sun.Month, following.Sun.Day));
        }

        // And an epoch whose calendar is held keeps its date whatever the policy says.
        var pinned = new RecordedWorld();
        using (SolarLease.Take(pinned, new DeclaredSun(
                   PortEpoch(calendarAdvances: false), IlluminationPolicy.Advance(1.0), 370_800)))
        {
            Assert.Equal((2026, 3, 21), (pinned.Sun!.Year, pinned.Sun.Month, pinned.Sun.Day));
        }
    }

    [Fact]
    public void ASunFrozenAtADeclaredHourIgnoresTheWindowSOwnClock()
    {
        var world = new RecordedWorld();
        var declared = new DeclaredSun(PortEpoch(), IlluminationPolicy.FreezeAt(TimeSpan.FromHours(15)),
                                       82_800);

        using SolarLease lease = SolarLease.Take(world, declared);

        // The night shift, lit as though it were mid-afternoon: deliberate, and the record says so.
        Assert.Equal((15, 0, 0), SolarPositionModel.EngineClock(world.Sun!.SolarTime));
        Assert.Equal("2026-03-21T23:00:00+03:30", SolarEpoch.FormatCivil(declared.WindowOpenCivil));
    }

    [Fact]
    public void AWorldWithNoSunIsRefusedUnlessTheRunDeclaredItNeedsNone()
    {
        var sunless = new RecordedWorld { Sun = null };
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarLease.Take(sunless, new DeclaredSun(PortEpoch(),
                                                           IlluminationPolicy.FreezeAtWindowStart(), 0)));
        Assert.Contains("reports no sun", refused.Message);
        Assert.Empty(sunless.SolarWrites);

        using SolarLease allowed = SolarLease.Take(sunless, new DeclaredSun(
            PortEpoch(), IlluminationPolicy.FreezeAtWindowStart(requireSun: false), 0));
        Assert.True(allowed.NoSun);
        Assert.Empty(sunless.SolarWrites);
    }

    [Fact]
    public void ASunThatKeepsItsLongitudeZoneIsRefusedAndGivenBack()
    {
        // The server as it was before the epoch setter set the zone: the clock is written and the
        // zone stays at longitude/15, so the clock is local mean solar time -- 14 minutes 43 seconds
        // from civil time at the port, which at the terminator is a sun on the wrong side of the
        // horizon.
        var world = new RecordedWorld();
        SimulatedSun sun = world.Sun!;
        sun.TimeZone = 56.18065 / 15.0;
        sun.IgnoresTimeZoneWrites = true;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarLease.Take(world, new DeclaredSun(PortEpoch(),
                                                         IlluminationPolicy.FreezeAtWindowStart(), 61_200)));

        Assert.Contains("time zone 3.745377 h, written 3.5 h", refused.Message);
        Assert.Contains("local mean solar time", refused.Message);

        // And the sun it was found with is back.
        Assert.Equal((13.0, 2019, 9, 21), (sun.SolarTime, sun.Year, sun.Month, sun.Day));
    }

    [Fact]
    public void AnEpochTheWorldRefusesLeavesTheSunUntouched()
    {
        var world = new RecordedWorld();
        world.Sun!.RefusesEpochs = true;

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarLease.Take(world, new DeclaredSun(PortEpoch(),
                                                         IlluminationPolicy.FreezeAtWindowStart(), 0)));

        Assert.Contains("refused the epoch", refused.Message);
        Assert.Equal(["set_solar_epoch"], world.SolarWrites.Select(write => write.Call));
        Assert.Equal(2019, world.Sun.Year);
    }

    [Fact]
    public void GivingTheSunBackTwiceDoesNothingTheSecondTime()
    {
        var world = new RecordedWorld();
        SolarLease lease = SolarLease.Take(world, new DeclaredSun(PortEpoch(),
                                                                  IlluminationPolicy.FreezeAtWindowStart(), 0));

        lease.Dispose();
        int writes = world.SolarWrites.Count;
        lease.Dispose();

        Assert.True(lease.IsReleased);
        Assert.Equal(writes, world.SolarWrites.Count);
    }

    [Fact]
    public void ThePolicyThatDeclaresNoSunCannotBeBound()
    {
        Assert.Throws<ArgumentException>(
            () => new DeclaredSun(PortEpoch(), IlluminationPolicy.Ignore(), 0));
    }
}
