namespace CarlaNet.CoSim;

/// <summary>
/// Where a sun is: both elevations and the azimuth, in degrees.
/// </summary>
/// <param name="ElevationDegrees">Geometric elevation above the horizon: where the sun is.</param>
/// <param name="CorrectedElevationDegrees">
/// Elevation with atmospheric refraction applied: where its light appears to come from, and what
/// the engine rotates the sun's directional light by.
/// </param>
/// <param name="AzimuthDegrees">Degrees clockwise from north.</param>
public readonly record struct SunPosition(
    double ElevationDegrees,
    double CorrectedElevationDegrees,
    double AzimuthDegrees);
