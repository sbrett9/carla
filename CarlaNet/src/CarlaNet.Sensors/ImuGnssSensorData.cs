// §10.8, §10.9 — IMU and GNSS use msgpack encoding (not raw binary).
using CarlaNet.Types.Geom;

namespace CarlaNet.Sensors;

// §10.8 — IMU: msgpack [accelerometer:Vector3D, gyroscope:Vector3D, compass:float]
[MessagePackObject]
public record struct ImuSensorData(
    [property: Key(0)] Vector3D Accelerometer,
    [property: Key(1)] Vector3D Gyroscope,
    [property: Key(2)] float Compass);

// §10.9 — GNSS: msgpack [latitude:double, longitude:double, altitude:double]
[MessagePackObject]
public record struct GnssSensorData(
    [property: Key(0)] double Latitude,
    [property: Key(1)] double Longitude,
    [property: Key(2)] double Altitude);
