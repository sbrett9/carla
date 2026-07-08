namespace CarlaNet.Recording;

/// <summary>
/// The EO collection platform's derived state at the instant a frame was captured — geodetic position,
/// pointing, motion, and full pinhole intrinsics — produced from the sensor-header world transform plus
/// the client-supplied <see cref="SensorPlatformOptions"/>. Emitted as a CoT air-track event and a
/// carla:sensor PNG tEXt chunk (see Docs/CAT_Research/Findings/16_Sensor_Pose_In_Recordings.md §4).
/// <para>
/// <see cref="Hae"/> is BARE-EARTH ellipsoidal WGS84 altitude — the camera's physical altitude minus the
/// height-align offset — so it shares the vehicle telemetry's datum; <see cref="AlignOffsetM"/> records
/// that offset so the physical platform altitude is recoverable (physical = Hae + AlignOffsetM).
/// </para>
/// </summary>
public sealed record SensorPose(
    string CotType,
    string Callsign,
    string Uid,
    double Lat,
    double Lon,
    double Hae,
    double AlignOffsetM,
    double AzimuthDeg,
    double ElevationDeg,
    double RollDeg,
    double CourseDeg,
    double SpeedMps,
    int Width,
    int Height,
    double Fx,
    double Fy,
    double Cx,
    double Cy,
    double HFovDeg,
    double VFovDeg,
    string SensorModel,
    string ProjectionModel,
    string Distortion);
