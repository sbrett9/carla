// Source: carla/rpc/WeatherParameters.h
// MSGPACK_DEFINE_ARRAY(cloudiness, precipitation, precipitation_deposits, wind_intensity,
//   sun_azimuth_angle, sun_altitude_angle, fog_density, fog_distance, fog_falloff,
//   wetness, scattering_intensity, mie_scattering_scale, rayleigh_scattering_scale, dust_storm)
namespace CarlaNet.Types.Rpc.Environment;

[MessagePackObject]
public record struct WeatherParameters(
    [property: Key(0)]  float Cloudiness,
    [property: Key(1)]  float Precipitation,
    [property: Key(2)]  float PrecipitationDeposits,
    [property: Key(3)]  float WindIntensity,
    [property: Key(4)]  float SunAzimuthAngle,
    [property: Key(5)]  float SunAltitudeAngle,
    [property: Key(6)]  float FogDensity,
    [property: Key(7)]  float FogDistance,
    [property: Key(8)]  float FogFalloff,
    [property: Key(9)]  float Wetness,
    [property: Key(10)] float ScatteringIntensity,
    [property: Key(11)] float MieScatteringScale,
    [property: Key(12)] float RayleighScatteringScale,
    [property: Key(13)] float DustStorm);
