namespace CarlaNet.CoSim;

/// <summary>
/// The solar-position algorithm the engine evaluates, evaluated without the engine.
/// </summary>
/// <remarks>
/// <para><c>USunPositionFunctionLibrary::GetSunPosition</c>
/// (<c>Engine/Plugins/Runtime/SunPosition/Source/SunPosition/Private/SunPosition.cpp</c>) is the
/// NOAA solar-position spreadsheet. <c>ACesiumSunSky::UpdateSun_Implementation</c> calls it with the
/// georeference origin, its own time zone and date, and the hour, minute and second it decomposes
/// its clock into, and stores the elevation, the refraction-corrected elevation and the azimuth it
/// answers with. This is a port of both halves, precision included: the engine takes latitude,
/// longitude and zone as single-precision floats, converts the latitude to radians in single
/// precision, and hands its angles back as floats offset by 180 degrees. Measured against a running
/// server on 2026-09-21 at two sites and twelve instants, the Python port of the same algorithm
/// agreed to 6.9e-6 degrees, three orders of magnitude inside <see cref="ResolutionFloorDegrees"/>.</para>
///
/// <para><b>The clock decomposition is the engine's, defect included.</b> The engine rounds its clock
/// to whole seconds, and a clock in the last half-second of a minute rounds to sixty seconds without
/// carrying the minute: 07:00:59.6 is evaluated as 07:00:00, a minute early, which is up to 0.25
/// degrees of hour angle. <see cref="AtEngineClock"/> reproduces that, because a comparison against
/// the engine has to be a comparison against what the engine computes. <see cref="AtInstant"/>
/// evaluates the same algorithm at the instant itself, so the difference between the two is what the
/// engine's decomposition costs at that instant -- a property of the engine given a clock, computed
/// rather than observed.</para>
/// </remarks>
public static class SolarPositionModel
{
    /// <summary>
    /// The finest agreement with the engine that is resolvable: the engine's whole-second clock is
    /// worth up to 0.0042 degrees of elevation, and its angles are single-precision floats near 180
    /// degrees, worth about 1.6e-5 degrees. Their sum, rounded up.
    /// </summary>
    public const double ResolutionFloorDegrees = 0.01;

    private const float SinglePrecisionPi = 3.1415926535897932f;

    /// <summary>
    /// The hour, minute and second <c>ACesiumSunSky::GetHMSFromSolarTime</c> decomposes a clock
    /// into -- including the minute it drops when the seconds round up to sixty.
    /// </summary>
    public static (int Hour, int Minute, int Second) EngineClock(double solarTimeHours)
    {
        int hour = (int)Math.Truncate(solarTimeHours) % 24;
        int minute = (int)Math.Truncate((solarTimeHours - hour) * 60.0) % 60;

        // The engine subtracts Minute / 60 in integer arithmetic, which is zero, so this is the
        // seconds into the hour rounded and taken modulo sixty.
        int second = (int)Math.Floor(((solarTimeHours - hour) * 3600.0) + 0.5) % 60;
        return (hour, minute, second);
    }

    /// <summary>
    /// What the engine's sun reports for a clock reading: the clock decomposed as the engine
    /// decomposes it, then evaluated.
    /// </summary>
    public static SunPosition AtEngineClock(double latitude, double longitude, double timeZoneHours,
                                            int year, int month, int day, double solarTimeHours)
    {
        (int hour, int minute, int second) = EngineClock(solarTimeHours);
        return At(latitude, longitude, timeZoneHours, year, month, day, hour, minute, second);
    }

    /// <summary>
    /// The sun at a date and whole-second clock in a zone, as the engine computes it; for a date or
    /// time that does not exist, the sentinel the engine is left holding: -180 degrees of elevation.
    /// </summary>
    public static SunPosition At(double latitude, double longitude, double timeZoneHours,
                                 int year, int month, int day, int hour, int minute, int second)
    {
        if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1
            || day > DateTime.DaysInMonth(year, month) || hour < 0 || hour > 23 || minute < 0
            || minute > 59 || second < 0 || second > 59)
        {
            // GetSunPosition returns early leaving its output at zero, and the sun then subtracts 180.
            return new SunPosition(-180.0, -180.0, 0.0);
        }

        var instant = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return Evaluate(latitude, longitude, timeZoneHours, instant);
    }

    /// <summary>
    /// The sun at an instant in a zone, fractional seconds and all: the same algorithm with no
    /// clock decomposition in front of it.
    /// </summary>
    public static SunPosition AtInstant(double latitude, double longitude, double timeZoneHours,
                                        DateTime localInstant) =>
        Evaluate(latitude, longitude, timeZoneHours, localInstant);

    /// <summary>
    /// The angle between two suns, in degrees: how far apart the two directions the light would
    /// come from are, whatever the azimuth does near the zenith.
    /// </summary>
    public static double SeparationDegrees(double elevationA, double azimuthA,
                                           double elevationB, double azimuthB)
    {
        (double ax, double ay, double az) = Direction(elevationA, azimuthA);
        (double bx, double by, double bz) = Direction(elevationB, azimuthB);
        double crossX = (ay * bz) - (az * by);
        double crossY = (az * bx) - (ax * bz);
        double crossZ = (ax * by) - (ay * bx);
        double cross = Math.Sqrt((crossX * crossX) + (crossY * crossY) + (crossZ * crossZ));
        double dot = (ax * bx) + (ay * by) + (az * bz);
        return Degrees(Math.Atan2(cross, dot));
    }

    private static SunPosition Evaluate(double latitude, double longitude, double timeZoneHours,
                                        DateTime local)
    {
        // The engine's inputs are single precision, and it converts the latitude in single
        // precision too.
        float latitudeSingle = (float)latitude;
        float longitudeSingle = (float)longitude;
        float timeOffset = (float)timeZoneHours;
        double latitudeRad = latitudeSingle * (SinglePrecisionPi / 180f);

        double julianDay = 1721425.5 + (double)(local.Ticks / TimeSpan.TicksPerDay)
                           + local.TimeOfDay.TotalDays;
        double julianCentury = (julianDay - 2451545.0) / 36525.0;

        double geomMeanLongSunDeg =
            (280.46646 + (julianCentury * (36000.76983 + (julianCentury * 0.0003032)))) % 360.0;
        double geomMeanLongSunRad = Radians(geomMeanLongSunDeg);
        double geomMeanAnomSunDeg = 357.52911 + (julianCentury * (35999.05029 - (0.0001537 * julianCentury)));
        double geomMeanAnomSunRad = Radians(geomMeanAnomSunDeg);
        double eccentEarthOrbit = 0.016708634 - (julianCentury * (0.000042037 + (0.0000001267 * julianCentury)));

        double sunEqOfCtr =
            (Math.Sin(geomMeanAnomSunRad) * (1.914602 - (julianCentury * (0.004817 + (0.000014 * julianCentury)))))
            + (Math.Sin(2.0 * geomMeanAnomSunRad) * (0.019993 - (0.000101 * julianCentury)))
            + (Math.Sin(3.0 * geomMeanAnomSunRad) * 0.000289);
        double sunTrueLongDeg = geomMeanLongSunDeg + sunEqOfCtr;
        double sunAppLongDeg = sunTrueLongDeg - 0.00569
                               - (0.00478 * Math.Sin(Radians(125.04 - (1934.136 * julianCentury))));
        double sunAppLongRad = Radians(sunAppLongDeg);

        double meanObliqEclipticDeg = 23.0 + ((26.0 + ((21.448 - (julianCentury
            * (46.815 + (julianCentury * (0.00059 - (julianCentury * 0.001813)))))) / 60.0)) / 60.0);
        double obliqCorrDeg = meanObliqEclipticDeg
                              + (0.00256 * Math.Cos(Radians(125.04 - (1934.136 * julianCentury))));
        double obliqCorrRad = Radians(obliqCorrDeg);
        double sunDeclinRad = Asin(Math.Sin(obliqCorrRad) * Math.Sin(sunAppLongRad));

        double varY = Math.Pow(Math.Tan(obliqCorrRad / 2.0), 2.0);
        double eqOfTimeMinutes = 4.0 * Degrees(
            (varY * Math.Sin(2.0 * geomMeanLongSunRad))
            - (2.0 * eccentEarthOrbit * Math.Sin(geomMeanAnomSunRad))
            + (4.0 * eccentEarthOrbit * varY * Math.Sin(geomMeanAnomSunRad) * Math.Cos(2.0 * geomMeanLongSunRad))
            - (0.5 * varY * varY * Math.Sin(4.0 * geomMeanLongSunRad))
            - (1.25 * eccentEarthOrbit * eccentEarthOrbit * Math.Sin(2.0 * geomMeanAnomSunRad)));

        double trueSolarTimeMinutes = (local.TimeOfDay.TotalMinutes + eqOfTimeMinutes
                                       + (4.0 * longitudeSingle) - (60.0 * timeOffset)) % 1440.0;
        double hourAngleDeg = trueSolarTimeMinutes < 0
            ? (trueSolarTimeMinutes / 4.0) + 180
            : (trueSolarTimeMinutes / 4.0) - 180.0;
        double hourAngleRad = Radians(hourAngleDeg);

        double solarZenithAngleRad = Acos(
            (Math.Sin(latitudeRad) * Math.Sin(sunDeclinRad))
            + (Math.Cos(latitudeRad) * Math.Cos(sunDeclinRad) * Math.Cos(hourAngleRad)));
        double solarElevationAngleDeg = 90.0 - Degrees(solarZenithAngleRad);
        double tanOfSolarElevationAngle = Math.Tan(Radians(solarElevationAngleDeg));

        // The refraction terms as the engine writes them, operator precedence included.
        double refractionDeg = 0.0;
        if (solarElevationAngleDeg <= 85.0)
        {
            if (solarElevationAngleDeg > 5.0)
            {
                refractionDeg = (58.1 / tanOfSolarElevationAngle)
                                - (0.07 / Math.Pow(tanOfSolarElevationAngle, 3))
                                + (0.000086 / Math.Pow(tanOfSolarElevationAngle, 5) / 3600.0);
            }
            else if (solarElevationAngleDeg > -0.575)
            {
                refractionDeg = 1735.0 + (solarElevationAngleDeg * (-518.2 + (solarElevationAngleDeg
                    * (103.4 + (solarElevationAngleDeg * (-12.79 + (solarElevationAngleDeg * 0.711)))))));
            }
            else
            {
                refractionDeg = -20.772 / tanOfSolarElevationAngle;
            }

            refractionDeg /= 3600.0;
        }

        double correctedDeg = solarElevationAngleDeg + refractionDeg;
        double azimuthBase = Degrees(Acos(
            ((Math.Sin(latitudeRad) * Math.Cos(solarZenithAngleRad)) - Math.Sin(sunDeclinRad))
            / (Math.Cos(latitudeRad) * Math.Sin(solarZenithAngleRad))));
        double azimuthDeg = hourAngleDeg > 0.0
            ? (azimuthBase + 180.0) % 360.0
            : (540.0 - azimuthBase) % 360.0;

        // Handed back as single-precision floats offset by 180, and the sun subtracts the 180 again
        // in single precision.
        float elevation = (float)(180.0f + solarElevationAngleDeg) - 180.0f;
        float corrected = (float)(180.0f + correctedDeg) - 180.0f;
        return new SunPosition(elevation, corrected, (float)azimuthDeg);
    }

    private static (double East, double North, double Up) Direction(double elevationDeg, double azimuthDeg)
    {
        double elevation = Radians(elevationDeg);
        double azimuth = Radians(azimuthDeg);
        return (Math.Cos(elevation) * Math.Sin(azimuth), Math.Cos(elevation) * Math.Cos(azimuth),
                Math.Sin(elevation));
    }

    private static double Radians(double degrees) => degrees * (Math.PI / 180.0);

    private static double Degrees(double radians) => radians * (180.0 / Math.PI);

    private static double Asin(double value) => Math.Asin(Math.Clamp(value, -1.0, 1.0));

    private static double Acos(double value) => Math.Acos(Math.Clamp(value, -1.0, 1.0));
}
