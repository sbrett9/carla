namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A <c>CesiumSunSky</c> that holds a date, a clock and a zone, and does with them what the engine
/// does: the epoch setter's validation and wrap, and the time-of-day controller's advance, midnight
/// carried onto the date.
/// </summary>
/// <remarks>
/// <para>It starts where an unconfigured sun starts -- <c>ACesiumSunSky</c>'s class defaults, 13:00
/// on 2019-09-21 in a zone of -5 hours -- because that, or whatever the previous session left, is
/// what a real session finds.</para>
///
/// <para>What it cannot establish is what the engine's renderer does with the sun it holds; the
/// angles it reports are computed from what it holds, never measured.</para>
/// </remarks>
internal sealed class SimulatedSun
{
    /// <summary>The sun's clock, hours in [0, 24).</summary>
    public double SolarTime { get; set; } = 13.0;

    public int Year { get; set; } = 2019;

    public int Month { get; set; } = 9;

    public int Day { get; set; } = 21;

    /// <summary>The zone the clock is read in, hours from UTC.</summary>
    public double TimeZone { get; set; } = -5.0;

    /// <summary>The georeference origin the sun is computed for.</summary>
    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>Whether the time-of-day controller advances the clock with the tick.</summary>
    public bool Advancing { get; set; }

    /// <summary>Sun-clock seconds per simulated second.</summary>
    public double Rate { get; set; } = 1.0;

    /// <summary>
    /// Set to have the epoch setter leave the zone alone, as the setter that preceded the civil-time
    /// one did: the clock is then local mean solar time at whatever zone the sun already had.
    /// </summary>
    public bool IgnoresTimeZoneWrites { get; set; }

    /// <summary>Set to have the sun refuse every epoch, as a sun given an impossible date does.</summary>
    public bool RefusesEpochs { get; set; }

    /// <summary>The packed values <c>get_solar_state</c> answers with, corrected elevation last.</summary>
    public IReadOnlyList<double> Read() =>
    [
        SolarTime, Year, Month, Day, TimeZone, Latitude, Longitude, 0.0, 0.0,
        Advancing ? 1.0 : 0.0, Rate, 0.0,
    ];

    /// <summary><c>UCesiumHeightSampler::SetSolarEpoch</c>: validate, then set everything at once.</summary>
    public bool WriteEpoch(int year, int month, int day, double hours, double utcOffsetHours)
    {
        if (RefusesEpochs || year < 1 || year > 9999 || month < 1 || month > 12 || day < 1
            || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        Year = year;
        Month = month;
        Day = day;
        SolarTime = ((hours % 24.0) + 24.0) % 24.0;
        if (!IgnoresTimeZoneWrites)
        {
            TimeZone = Math.Clamp(utcOffsetHours, -12.0, 14.0);
        }

        return true;
    }

    /// <summary><c>UCesiumHeightSampler::SetTimeAdvance</c>.</summary>
    public bool WriteAdvance(bool advancing, double rate)
    {
        Advancing = advancing;
        Rate = rate;
        return true;
    }

    /// <summary>
    /// <c>ACesiumTimeOfDayController::Tick</c>: carry the clock forward by the tick's delta times
    /// the rate, whole days off the clock and onto the calendar.
    /// </summary>
    public void Tick(double deltaSeconds)
    {
        if (!Advancing)
        {
            return;
        }

        double advanced = SolarTime + (deltaSeconds * Rate / 3600.0);
        double wholeDays = Math.Floor(advanced / 24.0);
        SolarTime = advanced - (wholeDays * 24.0);
        if (wholeDays != 0.0)
        {
            DateTime rolled = new DateTime(Year, Month, Day).AddDays(wholeDays);
            Year = rolled.Year;
            Month = rolled.Month;
            Day = rolled.Day;
        }
    }
}
