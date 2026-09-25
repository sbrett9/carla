namespace CarlaNet.CoSim;

/// <summary>Which of the sun's two elevations is meant.</summary>
public enum SolarElevationKind
{
    /// <summary>Where the sun is, with no atmosphere.</summary>
    Geometric,

    /// <summary>Where its light comes from, atmospheric refraction applied: what the light is rotated by.</summary>
    RefractionCorrected,
}
