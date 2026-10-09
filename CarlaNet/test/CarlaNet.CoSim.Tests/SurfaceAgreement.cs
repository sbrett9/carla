using System.Globalization;
using System.Text;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// How closely each of several surfaces follows the photoreal surface, accumulated over points and split
/// by how far across the road a point lies from its reference line.
/// </summary>
internal sealed class SurfaceAgreement
{
    /// <summary>The bands of distance across the road from the reference line, metres: about a lane each.</summary>
    private static readonly double[] Bands = [3.0, 6.5, 10.0, 13.5];

    private readonly string[] _names;
    private readonly List<double[]>[] _byBand =
        [.. Enumerable.Range(0, Bands.Length + 1).Select(_ => new List<double[]>())];

    /// <param name="names">The surfaces, in the order <see cref="Add"/> is given their heights less the photoreal's.</param>
    public SurfaceAgreement(params string[] names)
    {
        _names = names;
    }

    /// <summary>
    /// Count one point <paramref name="across"/> metres from the reference line: each surface's height
    /// less the photoreal's there, in the order the surfaces were named.
    /// </summary>
    public void Add(double across, params double[] lessPhotoreal)
    {
        int band = 0;
        while (band < Bands.Length && Math.Abs(across) >= Bands[band])
        {
            band++;
        }

        _byBand[band].Add(lessPhotoreal);
    }

    /// <summary>The table: per band and over all of them, each surface's |height − photoreal| p50 / p90 and signed p50.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("    across the road    points");
        foreach (string name in _names)
        {
            text.Append(CultureInfo.InvariantCulture, $"  | {name,-8} p50   p90  signed");
        }

        text.AppendLine();
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

    private string Line(string name, List<double[]> points)
    {
        var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"    {name,-15} {points.Count,9}"));
        if (points.Count == 0)
        {
            return line.ToString();
        }

        for (int series = 0; series < _names.Length; series++)
        {
            double[] magnitude = [.. points.Select(point => Math.Abs(point[series])).Order()];
            double[] signed = [.. points.Select(point => point[series]).Order()];
            line.Append(CultureInfo.InvariantCulture,
                        $"  | {string.Empty,-8}{P(magnitude, 0.5),5:0.000} {P(magnitude, 0.9),5:0.000} {P(signed, 0.5),6:+0.000;-0.000;0.000}");
        }

        return line.ToString();
    }

    private static double P(double[] ordered, double quantile) =>
        ordered[(int)Math.Round(quantile * (ordered.Length - 1))];
}
