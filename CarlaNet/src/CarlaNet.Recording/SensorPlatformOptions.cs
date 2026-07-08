namespace CarlaNet.Recording;

/// <summary>
/// Client-supplied configuration for recording the EO collection platform (the airborne camera) as a CoT
/// air track. The simulation server has no concept of the airframe a sensor is notionally mounted on, so
/// platform identity and optics are attributed here, at collection-config time (see
/// Docs/CAT_Research/Findings/16_Sensor_Pose_In_Recordings.md §6). The recorder combines these with the
/// per-frame sensor-header pose to produce a <see cref="SensorPose"/>.
/// </summary>
/// <param name="HFovDeg">Camera horizontal field of view, degrees (from the camera blueprint).</param>
/// <param name="CotType">Fully-resolved CoT air-track type, e.g. "a-f-A-M-F-Q". Use
/// <see cref="ResolveCotType"/> to build it from an airframe alias + affiliation.</param>
/// <param name="Callsign">Platform callsign for the CoT contact.</param>
/// <param name="Uid">Stable CoT track uid, e.g. "CARLA-SENSOR-&lt;camera id&gt;".</param>
/// <param name="SensorModel">Sensor/camera model string for the CoT sensor element, e.g. "sensor.camera.rgb".</param>
/// <param name="Distortion">Lens-distortion descriptor: "none" at CARLA defaults, or the serialized raw
/// CARLA lens parameters (which are a non-standard model, not Brown-Conrady) when non-default.</param>
public sealed record SensorPlatformOptions(
    double HFovDeg,
    string CotType,
    string Callsign,
    string Uid,
    string SensorModel = "sensor.camera.rgb",
    string Distortion = "none")
{
    /// <summary>
    /// Resolve an airframe alias (or a raw CoT type string) plus an affiliation into a CoT air-track type.
    /// CoT type letters verified against the MITRE CoTtypes.xml catalog: the military drone/RPV/UAV leaf is
    /// uppercase "-Q" (fixed "a-.-A-M-F-Q", rotary "a-.-A-M-H-Q"); manned military air is "a-.-A-M-F" /
    /// "a-.-A-M-H". A string already beginning with "a-" is treated as a raw CoT type and returned verbatim.
    /// </summary>
    public static string ResolveCotType(string typeOrAlias, string affiliation)
    {
        if (!string.IsNullOrWhiteSpace(typeOrAlias) &&
            typeOrAlias.StartsWith("a-", StringComparison.Ordinal))
            return typeOrAlias;

        string aff = string.IsNullOrWhiteSpace(affiliation)
            ? "f"
            : affiliation.Trim().Substring(0, 1).ToLowerInvariant();

        string tail = (typeOrAlias ?? "").Trim().ToLowerInvariant() switch
        {
            "uas-fixed"     => "A-M-F-Q",   // Air/Mil/Fixed/Drone,RPV,UAV
            "uas-rotary"    => "A-M-H-Q",   // Air/Mil/Rotor/Drone,RPV,UAV
            "manned-fixed"  => "A-M-F",     // Air/Mil/Fixed
            "manned-rotary" => "A-M-H",     // Air/Mil/Rotor
            _               => "A-M-F-Q",   // default: fixed-wing UAS
        };
        return $"a-{aff}-{tail}";
    }
}
