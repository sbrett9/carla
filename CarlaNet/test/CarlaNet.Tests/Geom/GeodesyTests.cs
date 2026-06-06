// Tests for CarlaNet.Types.Geom.Geodesy — the WGS84 ellipsoidal local<->geo transform
// used for Cesium-coherent georeferenced telemetry (Track B), plus the legacy spherical
// Web-Mercator port (CARLA GNSS parity) and the spherical-vs-ellipsoid residual it removes.
using CarlaNet.Types.Geom;

namespace CarlaNet.Tests.Geom;

public class GeodesyTests
{
    // Wrigley Field home plate — the pinned origin used throughout the digital-twin work.
    private static readonly GeoLocation Origin = new(41.94813, -87.65593, 149.0);

    // ── ECEF round-trip ──────────────────────────────────────────────────────
    [Fact]
    public void Ecef_Geodetic_RoundTrip()
    {
        var (x, y, z) = Geodesy.GeodeticToEcef(Origin);
        var g = Geodesy.EcefToGeodetic(x, y, z);
        Assert.Equal(Origin.Latitude, g.Latitude, 9);
        Assert.Equal(Origin.Longitude, g.Longitude, 9);
        Assert.Equal(Origin.Altitude, g.Altitude, 6);
    }

    // ── Origin maps to origin ────────────────────────────────────────────────
    [Fact]
    public void CarlaLocal_Origin_Maps_To_Origin()
    {
        var g = Geodesy.CarlaLocalToGeodetic(Origin, 0.0, 0.0, 0.0);
        Assert.Equal(Origin.Latitude, g.Latitude, 9);
        Assert.Equal(Origin.Longitude, g.Longitude, 9);
        Assert.Equal(Origin.Altitude, g.Altitude, 6);
    }

    // ── Cardinal directions: signs + axis convention (+X East, -Y North, +Z Up) ──
    [Fact]
    public void CarlaLocal_Cardinal_Directions()
    {
        var east = Geodesy.CarlaLocalToGeodetic(Origin, 1000.0, 0.0, 0.0);
        Assert.True(east.Longitude > Origin.Longitude);          // +X -> east -> larger lon
        Assert.Equal(Origin.Latitude, east.Latitude, 4);         // negligible lat change

        var north = Geodesy.CarlaLocalToGeodetic(Origin, 0.0, -1000.0, 0.0);
        Assert.True(north.Latitude > Origin.Latitude);           // -Y -> north -> larger lat

        var up = Geodesy.CarlaLocalToGeodetic(Origin, 0.0, 0.0, 50.0);
        Assert.Equal(Origin.Altitude + 50.0, up.Altitude, 3);    // +Z -> altitude
    }

    // ── Displacement magnitude is metric-accurate ────────────────────────────
    [Fact]
    public void CarlaLocal_Displacement_Is_Metric()
    {
        // 1000 m north (y = -1000), 0 east
        var p = Geodesy.CarlaLocalToGeodetic(Origin, 0.0, -1000.0, 0.0);
        var (e, n, u) = Geodesy.GeodeticToEnu(Origin, p);
        Assert.Equal(0.0, e, 1);       // decimetre precision
        Assert.Equal(1000.0, n, 1);
        Assert.Equal(0.0, u, 1);
    }

    // ── Full round-trip CARLA-local -> geo -> CARLA-local ─────────────────────
    [Fact]
    public void CarlaLocal_Geo_RoundTrip()
    {
        var local = new Location(558.0f, -345.0f, 12.0f);    // NE-corner-ish point on the Wrigleyville patch
        var geo = Geodesy.CarlaLocalToGeodetic(Origin, local);
        var back = Geodesy.GeodeticToCarlaLocal(Origin, geo);
        Assert.Equal(local.X, back.X, 2);    // cm precision (float round-trip)
        Assert.Equal(local.Y, back.Y, 2);
        Assert.Equal(local.Z, back.Z, 2);
    }

    // ── The whole point: spherical Mercator drifts from the ellipsoid ─────────
    [Fact]
    public void SphericalMercator_Residual_Vs_Ellipsoid()
    {
        // ~660 m from origin (NE corner of the Wrigleyville patch): 558 m east, 345 m north.
        double x = 558.0, y = -345.0;
        var ellipsoid = Geodesy.CarlaLocalToGeodetic(Origin, x, y, 0.0);
        var spherical = Geodesy.SphericalMercatorLocalToGeodetic(Origin, x, y, 0.0);

        // Distance between the two predicted positions, in metres (measured in the ellipsoidal ENU frame).
        var (de, dn, _) = Geodesy.GeodeticToEnu(ellipsoid, spherical);
        double residual = Math.Sqrt(de * de + dn * dn);

        // Matches the offline analysis (~1.1 m at the NE corner): real, deterministic, and removed
        // by using the ellipsoidal transform for truth output.
        Assert.InRange(residual, 0.3, 3.0);
    }

    [Fact]
    public void SphericalMercator_Origin_Maps_To_Origin()
    {
        var g = Geodesy.SphericalMercatorLocalToGeodetic(Origin, 0.0, 0.0, 0.0);
        Assert.Equal(Origin.Latitude, g.Latitude, 6);
        Assert.Equal(Origin.Longitude, g.Longitude, 6);
    }

    // ── Error grows with distance (origin-proximity mitigation rationale) ─────
    [Fact]
    public void SphericalMercator_Residual_Grows_With_Distance()
    {
        double Resid(double east, double north)
        {
            var ell = Geodesy.EnuToGeodetic(Origin, east, north, 0.0);
            var sph = Geodesy.SphericalMercatorLocalToGeodetic(Origin, east, -north, 0.0);
            var (de, dn, _) = Geodesy.GeodeticToEnu(ell, sph);
            return Math.Sqrt(de * de + dn * dn);
        }
        double near = Resid(100.0, 100.0);
        double far = Resid(600.0, 600.0);
        Assert.True(far > near, $"expected residual to grow with distance: near={near:F3} far={far:F3}");
    }
}
