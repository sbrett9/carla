// WGS84 ellipsoidal geodesy for CARLA <-> Cesium georeferenced telemetry ("Track B").
//
// CarlaNet ports the geo ORIGIN parser (CarlaNet.Map) but NOT a local->geo transform.
// CARLA's runtime carla::geom::GeoLocation::Transform uses a SPHERICAL Web-Mercator
// (R = 6378137), which drifts from the WGS84 ellipsoid that Cesium uses — up to ~1.1 m
// across a city-sized patch, growing with distance from the origin and with latitude.
// For accurate, Cesium-coherent truth telemetry we convert through a local ENU tangent
// plane -> ECEF -> WGS84 geodetic; this matches Cesium's own ENU/ECEF model to well under
// 1 mm over a city-scale patch (and to sub-cm vs the +proj=tmerc placement netconvert uses).
//
// The legacy spherical Web-Mercator path is also provided, both for parity with CARLA's
// GNSS sensor output and so the spherical-vs-ellipsoid residual can be measured directly.
//
// CARLA world-frame convention (see carla/geom/GeoLocation.cpp): local metres, +X = East,
// -Y = North (i.e. +Y = South), +Z = Up. All angles in degrees; all distances in metres
// (CARLA API units — NOT UE centimetres).

namespace CarlaNet.Types.Geom;

public static class Geodesy
{
    // ---- WGS84 ellipsoid ----
    public const double WGS84_A = 6378137.0;                       // semi-major axis (m)
    public const double WGS84_F = 1.0 / 298.257223563;            // flattening
    public static readonly double WGS84_E2 = WGS84_F * (2.0 - WGS84_F); // first eccentricity squared
    public const double SphericalRadius = 6378137.0;              // CARLA spherical Mercator radius

    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    // ---- ECEF <-> geodetic (WGS84) ----

    public static (double X, double Y, double Z) GeodeticToEcef(GeoLocation g)
    {
        double lat = g.Latitude * Deg2Rad, lon = g.Longitude * Deg2Rad, h = g.Altitude;
        double sLat = Math.Sin(lat), cLat = Math.Cos(lat);
        double sLon = Math.Sin(lon), cLon = Math.Cos(lon);
        double n = WGS84_A / Math.Sqrt(1.0 - WGS84_E2 * sLat * sLat);
        return ((n + h) * cLat * cLon,
                (n + h) * cLat * sLon,
                (n * (1.0 - WGS84_E2) + h) * sLat);
    }

    /// <summary>ECEF (m) -> geodetic via Bowring's closed-form method (sub-mm accuracy).</summary>
    public static GeoLocation EcefToGeodetic(double x, double y, double z)
    {
        double a = WGS84_A, e2 = WGS84_E2;
        double b = a * Math.Sqrt(1.0 - e2);
        double ep2 = (a * a - b * b) / (b * b);
        double p = Math.Sqrt(x * x + y * y);
        double lon = Math.Atan2(y, x);
        double theta = Math.Atan2(z * a, p * b);
        double sT = Math.Sin(theta), cT = Math.Cos(theta);
        double lat = Math.Atan2(z + ep2 * b * sT * sT * sT,
                                p - e2 * a * cT * cT * cT);
        double sLat = Math.Sin(lat);
        double n = a / Math.Sqrt(1.0 - e2 * sLat * sLat);
        double h = (p / Math.Cos(lat)) - n;
        return new GeoLocation(lat * Rad2Deg, lon * Rad2Deg, h);
    }

    // ---- ENU <-> geodetic (tangent plane at an origin) ----

    /// <summary>Local East/North/Up metres (relative to origin) -> WGS84 geodetic.</summary>
    public static GeoLocation EnuToGeodetic(GeoLocation origin, double east, double north, double up)
    {
        var (x0, y0, z0) = GeodeticToEcef(origin);
        double lat0 = origin.Latitude * Deg2Rad, lon0 = origin.Longitude * Deg2Rad;
        double sLat = Math.Sin(lat0), cLat = Math.Cos(lat0);
        double sLon = Math.Sin(lon0), cLon = Math.Cos(lon0);
        // ENU -> ECEF delta (transpose of the ECEF->ENU rotation)
        double dx = -sLon * east - sLat * cLon * north + cLat * cLon * up;
        double dy = cLon * east - sLat * sLon * north + cLat * sLon * up;
        double dz = cLat * north + sLat * up;
        return EcefToGeodetic(x0 + dx, y0 + dy, z0 + dz);
    }

    /// <summary>WGS84 geodetic -> local East/North/Up metres (relative to origin).</summary>
    public static (double East, double North, double Up) GeodeticToEnu(GeoLocation origin, GeoLocation target)
    {
        var (x0, y0, z0) = GeodeticToEcef(origin);
        var (x, y, z) = GeodeticToEcef(target);
        double dx = x - x0, dy = y - y0, dz = z - z0;
        double lat0 = origin.Latitude * Deg2Rad, lon0 = origin.Longitude * Deg2Rad;
        double sLat = Math.Sin(lat0), cLat = Math.Cos(lat0);
        double sLon = Math.Sin(lon0), cLon = Math.Cos(lon0);
        return (-sLon * dx + cLon * dy,
                -sLat * cLon * dx - sLat * sLon * dy + cLat * dz,
                cLat * cLon * dx + cLat * sLon * dy + sLat * dz);
    }

    // ---- CARLA local frame conveniences (the Track-B telemetry API) ----
    // CARLA local metres: +X = East, -Y = North, +Z = Up. Matches carla/geom/GeoLocation.cpp.

    /// <summary>CARLA local position (x,y,z metres) at a georeference origin -> accurate WGS84 geodetic.</summary>
    public static GeoLocation CarlaLocalToGeodetic(GeoLocation origin, double x, double y, double z)
        => EnuToGeodetic(origin, x, -y, z);

    /// <summary>CARLA local <see cref="Location"/> (metres) at a georeference origin -> accurate WGS84 geodetic.</summary>
    public static GeoLocation CarlaLocalToGeodetic(GeoLocation origin, Location local)
        => CarlaLocalToGeodetic(origin, local.X, local.Y, local.Z);

    /// <summary>WGS84 geodetic -> CARLA local <see cref="Location"/> (metres) at a georeference origin.</summary>
    public static Location GeodeticToCarlaLocal(GeoLocation origin, GeoLocation target)
    {
        var (east, north, up) = GeodeticToEnu(origin, target);
        return new Location((float)east, (float)(-north), (float)up);
    }

    // ---- Legacy: CARLA's spherical Web-Mercator (parity with the GNSS sensor) ----

    /// <summary>
    /// Ports carla::geom::GeoLocation::Transform — spherical Web-Mercator. Reproduces what
    /// CARLA's GNSS sensor reports for a CARLA local position; accurate to the SPHERE, not the
    /// ellipsoid. Prefer <see cref="CarlaLocalToGeodetic(GeoLocation,double,double,double)"/>
    /// for Cesium-coherent truth. Provided for parity and residual measurement.
    /// </summary>
    public static GeoLocation SphericalMercatorLocalToGeodetic(GeoLocation origin, double x, double y, double z)
    {
        double scale = Math.Cos(origin.Latitude * Deg2Rad);
        double mx = scale * SphericalRadius * origin.Longitude * Deg2Rad;
        double my = scale * SphericalRadius * Math.Log(Math.Tan((90.0 + origin.Latitude) * Math.PI / 360.0));
        mx += x;
        my += -y; // CARLA flips Y so that +north = -location.y
        double lon = mx * Rad2Deg / (SphericalRadius * scale);
        double lat = 360.0 * Math.Atan(Math.Exp(my / (SphericalRadius * scale))) / Math.PI - 90.0;
        return new GeoLocation(lat, lon, origin.Altitude + z);
    }
}
