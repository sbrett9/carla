using System.Globalization;
using System.Text;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// How closely the road profile and the ground surface each follow the photoreal surface, accumulated
/// over points and split by how far across the road a point lies from its reference line.
/// </summary>
internal sealed class SurfaceAgreement
{
    /// <summary>The bands of distance across the road from the reference line, metres: about a lane each.</summary>
    private static readonly double[] Bands = [3.0, 6.5, 10.0, 13.5];

    private readonly List<(double Profile, double Ground)>[] _byBand =
        [.. Enumerable.Range(0, Bands.Length + 1).Select(_ => new List<(double, double)>())];

    /// <summary>
    /// Count one point: the profile's height less the photoreal's, and the ground's less the photoreal's,
    /// at a point <paramref name="across"/> metres from the reference line.
    /// </summary>
    public void Add(double across, double profileLessPhotoreal, double groundLessPhotoreal)
    {
        int band = 0;
        while (band < Bands.Length && Math.Abs(across) >= Bands[band])
        {
            band++;
        }

        _byBand[band].Add((profileLessPhotoreal, groundLessPhotoreal));
    }

    /// <summary>The table, one line per band and one for all of them.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine("    across the road    points  |profile-photoreal| p50/p90   |ground-photoreal| p50/p90   signed p50 profile/ground   ground nearer");
        for (int band = 0; band <= Bands.Length; band++)
        {
            string name = band == 0 ? $"< {Bands[0]:0.#} m"
                : band == Bands.Length ? $">= {Bands[^1]:0.#} m"
                : $"{Bands[band - 1]:0.#}-{Bands[band]:0.#} m";
            text.AppendLine(Line(name, _byBand[band]));
        }

        text.Append(Line("all", [.. _byBand.SelectMany(points => points)]));
        return text.ToString();
    }

    private static string Line(string name, List<(double Profile, double Ground)> points)
    {
        if (points.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"    {name,-15} {0,9}");
        }

        double[] profile = [.. points.Select(point => Math.Abs(point.Profile)).Order()];
        double[] ground = [.. points.Select(point => Math.Abs(point.Ground)).Order()];
        double[] signedProfile = [.. points.Select(point => point.Profile).Order()];
        double[] signedGround = [.. points.Select(point => point.Ground).Order()];
        double nearer = points.Count(point => Math.Abs(point.Ground) < Math.Abs(point.Profile)) / (double)points.Count;
        return string.Create(CultureInfo.InvariantCulture,
            $"    {name,-15} {points.Count,9}  {P(profile, 0.5),8:0.000} {P(profile, 0.9),7:0.000}          {P(ground, 0.5),8:0.000} {P(ground, 0.9),7:0.000}          {P(signedProfile, 0.5),8:+0.000;-0.000} {P(signedGround, 0.5),8:+0.000;-0.000}          {nearer,6:0.0%}");
    }

    private static double P(double[] ordered, double quantile) =>
        ordered[(int)Math.Round(quantile * (ordered.Length - 1))];
}
