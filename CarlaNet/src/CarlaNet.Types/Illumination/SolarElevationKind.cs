namespace CarlaNet.Types.Illumination;

/// <summary>Which of the sun's two elevations is meant.</summary>
public enum SolarElevationKind
{
    /// <summary>Where the sun is, with no atmosphere.</summary>
    Geometric,

    /// <summary>Where its light comes from, atmospheric refraction applied: what the light is rotated by.</summary>
    RefractionCorrected,
}

/// <summary>The names a record writes a <see cref="SolarElevationKind"/> by.</summary>
public static class SolarElevationKinds
{
    /// <summary><c>refraction_corrected</c> or <c>geometric</c>.</summary>
    public static string Name(SolarElevationKind kind) => kind == SolarElevationKind.RefractionCorrected
        ? "refraction_corrected"
        : "geometric";
}
