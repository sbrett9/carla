namespace CarlaNet.Types.Illumination;

/// <summary>
/// The illumination band a sun elevation falls in: <c>11_Time_And_Illumination.md</c> §4.4's six, from
/// the highest sun down.
/// </summary>
/// <remarks>
/// <para>A coarse stratification key computed from the sun elevation, so a report and a corpus can
/// speak in regimes rather than degrees. Below the horizon the edges are the standard twilight
/// boundaries; above it <see cref="Golden"/> separates the low sun from the day. None of them is known
/// to be the right cut point for an electro-optical detector, which is why a band is always traceable
/// to the edges it was cut by (<see cref="IlluminationBands.Table"/>).</para>
///
/// <para>It is derived context, never a label (<c>06_Truth_And_Annotation.md</c> D6.21): nothing that
/// reads or writes supervision branches on it.</para>
/// </remarks>
public enum IlluminationBand
{
    /// <summary>Above +6 degrees.</summary>
    Day,

    /// <summary>Above 0, up to +6 degrees.</summary>
    Golden,

    /// <summary>Above -6, up to 0 degrees.</summary>
    CivilTwilight,

    /// <summary>Above -12, up to -6 degrees.</summary>
    NauticalTwilight,

    /// <summary>Above -18, up to -12 degrees.</summary>
    AstronomicalTwilight,

    /// <summary>-18 degrees and below.</summary>
    Night,
}

/// <summary>One band of the table and the elevation it holds everything above.</summary>
/// <param name="Band">The band.</param>
/// <param name="Name">Its name, as every record writes it.</param>
/// <param name="AboveDegrees">
/// The band holds every elevation above this, up to and including the next band's edge. Null for
/// <see cref="IlluminationBand.Night"/>, which holds everything at and below the last edge.
/// </param>
public readonly record struct IlluminationBandEdge(IlluminationBand Band, string Name, double? AboveDegrees);

/// <summary>
/// The one definition of the illumination bands, and the function from a sun elevation to its band.
/// </summary>
/// <remarks>
/// <para>Each band holds its upper edge: <c>golden</c> is (0, 6], <c>civil_twilight</c> (-6, 0], and so
/// on down, and <c>night</c> is -18 and below.</para>
///
/// <para><b>One table, every reader.</b> The recorder writes each capture's band from it, beside the
/// capture's sun. The scenario compiler's illumination-label statistic buckets by it and the annotation
/// vocabulary's closed core spells its <c>illumination_band</c> terms from it, both reaching it through
/// <c>carlanet</c> (<c>carlacontrol.IlluminationBand</c>), so a band in a capture's truth, a band a
/// statistic counts and a band a vocabulary names are one table and cannot come to disagree.</para>
///
/// <para><b>Which elevation.</b> The edges are elevations doc 11 declares, and a declared elevation
/// means the refraction-corrected one: the elevation the sun's light is rotated by, and so the sun the
/// imagery was rendered under (doc 11 open question 7). <see cref="Elevation"/> says so. It is the
/// constant the co-simulation's <c>DeclaredSunElevation.Kind</c> also states, and a test holds the two
/// together, so reversing that ruling moves the bands with the declarations.</para>
///
/// <para>A band is defined for a sun's elevation, -90 to +90 degrees. Anything else -- the -180
/// degrees the engine reports for a sun it could not compute, or a value that is not a number -- has
/// no band, rather than reading as <c>night</c>.</para>
/// </remarks>
public static class IlluminationBands
{
    /// <summary>Where the bands are defined.</summary>
    public const string Source = "11_Time_And_Illumination.md §4.4";

    /// <summary>The elevation the edges are stated against.</summary>
    public const SolarElevationKind Elevation = SolarElevationKind.RefractionCorrected;

    /// <summary>Every band and its edge, from the highest sun down.</summary>
    public static IReadOnlyList<IlluminationBandEdge> Table { get; } =
    [
        new(IlluminationBand.Day, "day", 6.0),
        new(IlluminationBand.Golden, "golden", 0.0),
        new(IlluminationBand.CivilTwilight, "civil_twilight", -6.0),
        new(IlluminationBand.NauticalTwilight, "nautical_twilight", -12.0),
        new(IlluminationBand.AstronomicalTwilight, "astronomical_twilight", -18.0),
        new(IlluminationBand.Night, "night", null),
    ];

    /// <summary>Every band's name, from the highest sun down.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Table.Select(edge => edge.Name)];

    /// <summary>The band of a sun elevation.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The elevation is not a sun's: outside -90 to +90
    /// degrees, or not a number.</exception>
    public static IlluminationBand Of(double elevationDegrees) =>
        TryOf(elevationDegrees, out IlluminationBand band)
            ? band
            : throw new ArgumentOutOfRangeException(
                nameof(elevationDegrees), elevationDegrees,
                "a band is defined for a sun's elevation, -90 to +90 degrees");

    /// <summary>The band of a sun elevation, or false where the elevation is not a sun's.</summary>
    public static bool TryOf(double elevationDegrees, out IlluminationBand band)
    {
        band = IlluminationBand.Night;
        if (!(elevationDegrees >= -90.0 && elevationDegrees <= 90.0))
        {
            return false;
        }

        foreach (IlluminationBandEdge edge in Table)
        {
            if (edge.AboveDegrees is not { } floor || elevationDegrees > floor)
            {
                band = edge.Band;
                break;
            }
        }

        return true;
    }

    /// <summary>A band's name, as every record writes it.</summary>
    public static string Name(IlluminationBand band)
    {
        foreach (IlluminationBandEdge edge in Table)
        {
            if (edge.Band == band)
            {
                return edge.Name;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(band), band, "not one of the six bands");
    }

    /// <summary>The name of a sun elevation's band.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The elevation is not a sun's.</exception>
    public static string NameOf(double elevationDegrees) => Name(Of(elevationDegrees));
}
