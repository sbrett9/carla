namespace CarlaNet.CoSim;

/// <summary>
/// Which of the sun's two elevations a declared elevation means: the one every record states a
/// window's sun by, with the other always carried beside it.
/// </summary>
/// <remarks>
/// <para>The engine computes both. The <b>geometric</b> elevation is where the sun is, with no
/// atmosphere; it is what an external ephemeris reproduces and what <c>get_solar_state</c> has always
/// reported first. The <b>refraction-corrected</b> elevation is where its light appears to come from,
/// and it is what <c>ACesiumSunSky</c> rotates the directional light by -- so it is the sun the
/// imagery was actually rendered under. Near the horizon they differ by up to 0.28 degrees, which is
/// 5 to 25 per cent of the elevation in exactly the low-sun windows a twilight capture is made of,
/// so a declaration that does not say which it means is ambiguous where it matters most.</para>
///
/// <para><b>Declarations are made against the corrected elevation</b>, because imagery is what the
/// corpus is for; the geometric one is recorded beside it under its own name, so nothing that
/// reproduces the sun from an ephemeris loses it. This was ruled for the capture work pending the
/// plan owner's confirmation (<c>11_Time_And_Illumination.md</c> open question 7), and reversing it
/// is this one constant: every record names the kind it used, so records written under either
/// ruling stay readable.</para>
/// </remarks>
public static class DeclaredSunElevation
{
    /// <summary>The elevation a declaration means.</summary>
    public const SolarElevationKind Kind = SolarElevationKind.RefractionCorrected;

    /// <summary>The kind as a record writes it.</summary>
    public static string Name => Kind == SolarElevationKind.RefractionCorrected
        ? "refraction_corrected"
        : "geometric";

    /// <summary>The declared elevation of a sun.</summary>
    public static double Of(SunPosition sun) => Kind == SolarElevationKind.RefractionCorrected
        ? sun.CorrectedElevationDegrees
        : sun.ElevationDegrees;
}
