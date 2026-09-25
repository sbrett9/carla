using System.Globalization;
using System.Text;
using System.Xml;

namespace CarlaNet.Recording;

/// <summary>
/// What a frame's sun was declared to be, and how far the world's sun was from it on that frame's
/// tick: the declaration a capture's illumination is traceable to.
/// </summary>
/// <remarks>
/// <para>The <c>_solar</c> block beside it records what the sun <i>was</i>, read from the world. This
/// records what the run <i>said it should be</i> -- the scenario's epoch, the illumination policy,
/// the civil instant of the frame and the sun declared for it -- and the audit's residual between
/// the two. Before it existed a frozen run and a run nobody configured wrote identical frames.</para>
///
/// <para><b>Which elevation a declaration means.</b> Both are carried, named for what they are: the
/// geometric elevation, which an external ephemeris reproduces, and the refraction-corrected one,
/// which the sun's light is rotated by and so the frame was rendered under.
/// <see cref="DeclaredElevationKind"/> names the one a window's elevation is declared against.</para>
/// </remarks>
/// <param name="Policy">The illumination policy's name: <c>freeze_at_window_start</c>, <c>advance</c>,
/// <c>freeze_at</c> or <c>ignore</c>.</param>
/// <param name="EpochHonoured">Whether the frame was lit by the sun of its own declared civil instant.</param>
/// <param name="Audited">Whether the world's sun was compared against the declaration on this frame's tick.</param>
public sealed record IlluminationDeclaration(string Policy, bool EpochHonoured, bool Audited)
{
    /// <summary>Sun-clock seconds per simulated second, under an advancing policy.</summary>
    public double? Rate { get; init; }

    /// <summary>The civil time of day the sun is held at, under <c>freeze_at</c>.</summary>
    public string? FreezeAtCivilTime { get; init; }

    /// <summary>SHA-256 of the scenario epoch, so a frame separated from its run still names it.</summary>
    public string? EpochDigest { get; init; }

    /// <summary>The civil instant simulated second zero means, with its offset.</summary>
    public string? EpochCivil { get; init; }

    /// <summary>The declared UTC offset, hours.</summary>
    public double? UtcOffsetHours { get; init; }

    /// <summary>This frame's simulated instant as a civil time, with its offset.</summary>
    public string? DeclaredCivil { get; init; }

    /// <summary>The same instant in UTC: the join key for anything outside this pipeline.</summary>
    public string? DeclaredUtc { get; init; }

    /// <summary>
    /// The date and clock the sun was declared to hold for this frame, with its offset. The civil
    /// instant under the policies that honour the epoch; the window's opening instant under a freeze.
    /// </summary>
    public string? SunDeclared { get; init; }

    /// <summary>The geometric elevation of the declared sun, degrees.</summary>
    public double? SunElevationDeclaredDegrees { get; init; }

    /// <summary>The refraction-corrected elevation of the declared sun, degrees.</summary>
    public double? SunCorrectedElevationDeclaredDegrees { get; init; }

    /// <summary>
    /// Which of the two elevations a declared window elevation means: <c>refraction_corrected</c> or
    /// <c>geometric</c>.
    /// </summary>
    public string? DeclaredElevationKind { get; init; }

    /// <summary>The world's sun's instant minus the declared one, seconds.</summary>
    public double? ResidualClockSeconds { get; init; }

    /// <summary>The angle between the world's sun and the declared sun, degrees.</summary>
    public double? ResidualDegrees { get; init; }

    /// <summary>The world's corrected elevation minus the declared one, where it was carried.</summary>
    public double? ResidualCorrectedDegrees { get; init; }

    /// <summary>The PNG tEXt chunk carrying the declaration, so a still names it even apart from its sidecar.</summary>
    public IEnumerable<(string Keyword, string Text)> PngTextChunks()
    {
        yield return ("carla:illumination", ToJson());
    }

    /// <summary>Compact JSON of the declaration (ASCII, safe as a PNG tEXt value).</summary>
    public string ToJson()
    {
        var json = new StringBuilder("{");
        json.Append("\"policy\":\"").Append(Escape(Policy)).Append('"');
        json.Append(",\"epoch_honoured\":").Append(EpochHonoured ? "true" : "false");
        json.Append(",\"audited\":").Append(Audited ? "true" : "false");
        foreach ((string name, string value, bool quoted) in Fields())
        {
            json.Append(",\"").Append(name).Append("\":");
            json.Append(quoted ? "\"" + Escape(value) + "\"" : value);
        }

        return json.Append('}').ToString();
    }

    /// <summary>Write the <c>_illumination</c> element of a sidecar.</summary>
    public void WriteElement(XmlWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartElement("_illumination");
        writer.WriteAttributeString("policy", Policy);
        writer.WriteAttributeString("epoch_honoured", EpochHonoured ? "true" : "false");
        writer.WriteAttributeString("audited", Audited ? "true" : "false");
        foreach ((string name, string value, _) in Fields())
        {
            writer.WriteAttributeString(name, value);
        }

        writer.WriteEndElement();
    }

    private IEnumerable<(string Name, string Value, bool Quoted)> Fields()
    {
        if (Rate is { } rate) yield return ("rate", F(rate, "0.######"), false);
        if (FreezeAtCivilTime is { } freezeAt) yield return ("freeze_at_civil_time", freezeAt, true);
        if (EpochDigest is { } digest) yield return ("epoch_digest", digest, true);
        if (EpochCivil is { } epoch) yield return ("epoch_civil", epoch, true);
        if (UtcOffsetHours is { } offset) yield return ("utc_offset_hours", F(offset, "0.##"), false);
        if (DeclaredCivil is { } civil) yield return ("declared_civil", civil, true);
        if (DeclaredUtc is { } utc) yield return ("declared_utc", utc, true);
        if (SunDeclared is { } sun) yield return ("sun_declared", sun, true);
        if (SunElevationDeclaredDegrees is { } elevation)
            yield return ("sun_elevation_declared_deg", F(elevation, "0.####"), false);
        if (SunCorrectedElevationDeclaredDegrees is { } corrected)
            yield return ("sun_corrected_elevation_declared_deg", F(corrected, "0.####"), false);
        if (DeclaredElevationKind is { } kind) yield return ("declared_elevation", kind, true);
        if (ResidualClockSeconds is { } clock) yield return ("residual_clock_s", F(clock, "0.######"), false);
        if (ResidualDegrees is { } angle) yield return ("residual_deg", F(angle, "0.######"), false);
        if (ResidualCorrectedDegrees is { } refracted)
            yield return ("residual_corrected_deg", F(refracted, "0.######"), false);
    }

    private static string F(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
