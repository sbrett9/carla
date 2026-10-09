namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The per-tick audit's model of the sun, pinned to what the engine was measured to return.
/// </summary>
/// <remarks>
/// <para>On 2026-09-21 these instants were set on a running server with <c>set_solar_epoch</c> and
/// read back with <c>get_solar_state</c> at the Arapahoe I-25 and Shahid Bahonar Port origins; the
/// readings are those returned values, transcribed from
/// <c>CarlaControl/test/test_solar_model_matches_the_engine.py</c>. They pin this port to a
/// measurement rather than to itself, and to the same measurement the Python port the capture
/// windows are placed with is pinned to, so the two cannot drift apart unnoticed.</para>
///
/// <para>Both sites, because one hides the defect the audit exists for: the gap between a sun
/// spawned at longitude/15 and the civil offset is 0.46 minutes at Arapahoe and 14.72 at Bahonar.</para>
/// </remarks>
public sealed class SolarPositionModelTests
{
    private const double ArapahoeLatitude = 39.59431;
    private const double ArapahoeLongitude = -104.88449;
    private const double BahonarLatitude = 27.15012;
    private const double BahonarLongitude = 56.18065;

    /// <summary>A hundred times inside the resolution floor; the measured worst was 6.9e-6 degrees.</summary>
    private const double AgreementLimitDegrees = SolarPositionModel.ResolutionFloorDegrees / 100.0;

    // latitude, longitude, zone, year, month, day, clock, engine elevation, engine corrected
    // elevation, engine azimuth
    public static TheoryData<double, double, double, int, int, int, double, double, double, double> EngineReadings => new()
    {
        { ArapahoeLatitude, ArapahoeLongitude, -7.0, 2026, 12, 21, 6.0, -14.22632, -14.20357, 108.83122 },
        { ArapahoeLatitude, ArapahoeLongitude, -7.0, 2026, 12, 21, 7.0, -3.61803, -3.52676, 117.70262 },
        { ArapahoeLatitude, ArapahoeLongitude, -7.0, 2026, 12, 21, 17.0, -4.43510, -4.36070, 243.02682 },
        { ArapahoeLatitude, ArapahoeLongitude, -7.0, 2026, 12, 21, 23.0, -69.73611, -69.73398, 318.58496 },
        { ArapahoeLatitude, ArapahoeLongitude, -7.0, 2026, 3, 21, 7.0, 10.39754, 10.48235, 98.36960 },
        { ArapahoeLatitude, ArapahoeLongitude, -7.0, 2026, 6, 21, 7.0, 25.63060, 25.66406, 79.88106 },
        { BahonarLatitude, BahonarLongitude, 3.5, 2026, 12, 21, 6.0, -6.99658, -6.94955, 112.79269 },
        { BahonarLatitude, BahonarLongitude, 3.5, 2026, 12, 21, 7.0, 4.98076, 5.14085, 119.56246 },
        { BahonarLatitude, BahonarLongitude, 3.5, 2026, 12, 21, 17.0, -1.58296, -1.37418, 244.34160 },
        { BahonarLatitude, BahonarLongitude, 3.5, 2026, 12, 21, 23.0, -79.47812, -79.47705, 288.25415 },
        { BahonarLatitude, BahonarLongitude, 3.5, 2026, 3, 21, 7.0, 15.10179, 15.16060, 97.63795 },
        { BahonarLatitude, BahonarLongitude, 3.5, 2026, 6, 21, 7.0, 25.91144, 25.94449, 75.65086 },
    };

    [Theory]
    [MemberData(nameof(EngineReadings))]
    public void TheModelReproducesTheEngineSReading(double latitude, double longitude, double zone,
                                                    int year, int month, int day, double clock,
                                                    double elevation, double corrected, double azimuth)
    {
        SunPosition modelled = SolarPositionModel.AtEngineClock(latitude, longitude, zone, year,
                                                                month, day, clock);

        Assert.Equal(elevation, modelled.ElevationDegrees, AgreementLimitDegrees);
        Assert.Equal(corrected, modelled.CorrectedElevationDegrees, AgreementLimitDegrees);
        Assert.Equal(azimuth, modelled.AzimuthDegrees, AgreementLimitDegrees);
    }

    [Fact]
    public void TheEngineSClockDropsTheMinuteItShouldCarry()
    {
        // 07:00:59.6 rounds to sixty seconds, is taken modulo sixty, and the minute is not carried.
        Assert.Equal((7, 0, 0), SolarPositionModel.EngineClock(7.0 + (59.6 / 3600.0)));
        Assert.Equal((7, 0, 59), SolarPositionModel.EngineClock(7.0 + (59.4 / 3600.0)));

        // And on the whole minute itself, when the minute is a hair short of whole in binary: 07:01
        // truncates to minute 0 and its seconds round to sixty, and 07:02 to minute 1. The whole
        // hour is exact in binary and is not affected.
        Assert.Equal((7, 0, 0), SolarPositionModel.EngineClock(7.0 + (60.0 / 3600.0)));
        Assert.Equal((7, 1, 0), SolarPositionModel.EngineClock(7.0 + (120.0 / 3600.0)));
        Assert.Equal((8, 0, 0), SolarPositionModel.EngineClock(8.0));

        // A minute of hour angle is a quarter of a degree, and near the horizon most of that is
        // elevation: what the engine renders at 07:00:59.6 is the sun of 07:00:00.
        var instant = new DateTime(2026, 12, 21, 7, 0, 59, 600, DateTimeKind.Unspecified);
        SunPosition engine = SolarPositionModel.AtEngineClock(BahonarLatitude, BahonarLongitude, 3.5,
                                                              2026, 12, 21, 7.0 + (59.6 / 3600.0));
        SunPosition exact = SolarPositionModel.AtInstant(BahonarLatitude, BahonarLongitude, 3.5, instant);
        double error = SolarPositionModel.SeparationDegrees(engine.ElevationDegrees, engine.AzimuthDegrees,
                                                            exact.ElevationDegrees, exact.AzimuthDegrees);
        Assert.InRange(error, 0.18, 0.26);
    }

    [Fact]
    public void AnInstantOnAWholeSecondIsEvaluatedAsTheEngineEvaluatesIt()
    {
        var instant = new DateTime(2026, 12, 21, 17, 0, 0, DateTimeKind.Unspecified);
        SunPosition engine = SolarPositionModel.AtEngineClock(BahonarLatitude, BahonarLongitude, 3.5,
                                                              2026, 12, 21, 17.0);
        SunPosition exact = SolarPositionModel.AtInstant(BahonarLatitude, BahonarLongitude, 3.5, instant);

        Assert.Equal(engine, exact);
    }

    [Fact]
    public void ADateThatDoesNotExistLeavesTheEngineSSentinel()
    {
        SunPosition sentinel = SolarPositionModel.AtEngineClock(BahonarLatitude, BahonarLongitude, 3.5,
                                                                2026, 2, 31, 12.0);

        Assert.Equal(-180.0, sentinel.ElevationDegrees);
        Assert.Equal(0.0, sentinel.AzimuthDegrees);
    }

    [Fact]
    public void TheSeparationOfTwoSunsIsTheAngleBetweenTheirDirections()
    {
        Assert.Equal(0.0, SolarPositionModel.SeparationDegrees(30, 120, 30, 120), 12);
        Assert.Equal(1.0, SolarPositionModel.SeparationDegrees(30, 120, 31, 120), 9);

        // Near the zenith a large difference in azimuth is a small difference in direction.
        Assert.True(SolarPositionModel.SeparationDegrees(89.99, 0, 89.99, 180) < 0.03);
    }

    [Fact]
    public void TheSpawnedZoneFlipsATerminatorWindowAtBahonarAndMovesNothingAtArapahoe()
    {
        // The civil-time defect the epoch setter removes, at both sites: a sun left at longitude/15
        // renders local mean solar time rather than civil time.
        SunPosition bahonarCivil = SolarPositionModel.AtEngineClock(
            BahonarLatitude, BahonarLongitude, 3.5, 2026, 12, 21, 17.0);
        SunPosition bahonarSpawned = SolarPositionModel.AtEngineClock(
            BahonarLatitude, BahonarLongitude, BahonarLongitude / 15.0, 2026, 12, 21, 17.0);
        Assert.True(bahonarCivil.ElevationDegrees < 0.0 && bahonarSpawned.ElevationDegrees > 0.0);

        SunPosition arapahoeCivil = SolarPositionModel.AtEngineClock(
            ArapahoeLatitude, ArapahoeLongitude, -7.0, 2026, 12, 21, 17.0);
        SunPosition arapahoeSpawned = SolarPositionModel.AtEngineClock(
            ArapahoeLatitude, ArapahoeLongitude, ArapahoeLongitude / 15.0, 2026, 12, 21, 17.0);
        Assert.True(Math.Abs(arapahoeCivil.ElevationDegrees - arapahoeSpawned.ElevationDegrees) < 0.2);
    }
}
