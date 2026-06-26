namespace CarlaNet.Recording;

/// <summary>
/// One vehicle's truth telemetry at an instant — the field set of Docs/CAT_Research/Findings/
/// 09_Telemetry_CoT_Contract. Heights are ellipsoidal WGS84 (HAE). This is the single source of truth
/// consumed by both the CoT-XML sidecar (recorder) and the Python get_vehicle_telemetry shim.
/// </summary>
public sealed record VehicleTelemetry(
    uint Id,
    string TypeId,
    string BaseType,
    string SpecialType,
    string Color,
    string RoleName,
    double Lat,
    double Lon,
    double Hae,
    double HaeDtm,
    double SpeedMps,
    double CourseDeg,
    double Vx,
    double Vy,
    double Vz,
    double LengthM,
    double WidthM,
    double HeightM);
