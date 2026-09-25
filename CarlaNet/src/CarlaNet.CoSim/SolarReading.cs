using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// The sun as a world reports it: the clock, date and zone it holds, where it is, and whether the
/// engine is carrying it forward.
/// </summary>
/// <param name="SolarTimeHours">The sun's clock, hours in [0, 24) in its own time zone.</param>
/// <param name="Year">The sun's calendar year.</param>
/// <param name="Month">The sun's calendar month.</param>
/// <param name="Day">The sun's calendar day.</param>
/// <param name="TimeZoneHours">The zone the clock is read in, hours from UTC.</param>
/// <param name="LatitudeDegrees">The latitude the sun is computed for: the georeference origin.</param>
/// <param name="LongitudeDegrees">The longitude the sun is computed for.</param>
/// <param name="ElevationDegrees">
/// The <b>geometric</b> elevation above the horizon -- where the sun is, with no atmosphere.
/// </param>
/// <param name="AzimuthDegrees">Degrees clockwise from north.</param>
/// <param name="Advancing">Whether the engine advances the clock with the world tick.</param>
/// <param name="Rate">Sun-clock seconds per simulated second when it does.</param>
/// <param name="CorrectedElevationDegrees">
/// The elevation with atmospheric refraction applied, which is what the sun's directional light is
/// actually rotated by, or <see langword="null"/> where the source did not carry it. The two differ
/// by up to a few tenths of a degree near the horizon, which is a large fraction of a low sun.
/// </param>
/// <remarks>
/// The layout is the one <c>get_solar_state</c> packs its values in, and the world-observer header
/// carries the same values in the same order: eleven through <see cref="Rate"/>, and the corrected
/// elevation twelfth where the header is wide enough to hold it.
/// </remarks>
public readonly record struct SolarReading(
    double SolarTimeHours,
    int Year,
    int Month,
    int Day,
    double TimeZoneHours,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationDegrees,
    double AzimuthDegrees,
    bool Advancing,
    double Rate,
    double? CorrectedElevationDegrees)
{
    /// <summary>The values a reading needs to be one: everything up to and including the rate.</summary>
    public const int RequiredValues = 11;

    /// <summary>
    /// A reading from the packed values, or <see langword="null"/> where there are too few of them
    /// to be one -- which is how a world with no sun answers.
    /// </summary>
    public static SolarReading? From(IReadOnlyList<double>? values)
    {
        if (values is null || values.Count < RequiredValues)
        {
            return null;
        }

        return new SolarReading(
            values[0], (int)values[1], (int)values[2], (int)values[3], values[4], values[5],
            values[6], values[7], values[8], values[9] != 0.0, values[10],
            values.Count > RequiredValues ? values[11] : null);
    }

    /// <summary>
    /// The instant the sun holds, as a date and a clock in its own zone, or <see langword="null"/>
    /// where its date is not a calendar date -- the state a clamped date setter leaves it in.
    /// </summary>
    public DateTime? LocalInstant
    {
        get
        {
            if (Year < 1 || Year > 9999 || Month < 1 || Month > 12 || Day < 1
                || Day > DateTime.DaysInMonth(Year, Month) || !double.IsFinite(SolarTimeHours))
            {
                return null;
            }

            return new DateTime(Year, Month, Day, 0, 0, 0, DateTimeKind.Unspecified)
                .AddTicks((long)Math.Round(SolarTimeHours * TimeSpan.TicksPerHour));
        }
    }

    /// <summary>The reading in one line: the instant and zone it holds, and where the sun is.</summary>
    public override string ToString()
    {
        string instant = LocalInstant is { } local
            ? local.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : $"{Year:0000}-{Month:00}-{Day:00} {SolarTimeHours:0.######} h";
        string corrected = CorrectedElevationDegrees is { } refracted
            ? $", {refracted:0.####} deg refraction-corrected"
            : string.Empty;
        return $"{instant} at UTC{SolarEpoch.FormatOffset(TimeSpan.FromHours(TimeZoneHours))} "
               + $"({TimeZoneHours:0.######} h), {LatitudeDegrees:0.#####}, {LongitudeDegrees:0.#####}; "
               + $"elevation {ElevationDegrees:0.####} deg geometric{corrected}, azimuth "
               + $"{AzimuthDegrees:0.####} deg; "
               + (Advancing ? $"advancing at {Rate:0.######}" : $"not advancing (rate {Rate:0.######})");
    }
}
