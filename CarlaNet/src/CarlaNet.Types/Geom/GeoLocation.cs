// Source: carla/geom/GeoLocation.h — MSGPACK_DEFINE_ARRAY(latitude, longitude, altitude)
namespace CarlaNet.Types.Geom;

[MessagePackObject]
public record struct GeoLocation(
    [property: Key(0)] double Latitude,
    [property: Key(1)] double Longitude,
    [property: Key(2)] double Altitude)
{
    [IgnoreMember] public double latitude => Latitude;
    [IgnoreMember] public double longitude => Longitude;
    [IgnoreMember] public double altitude => Altitude;
}
