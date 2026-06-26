using CarlaNet.Transport;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Actors;

namespace CarlaNet.Recording;

/// <summary>
/// Produces per-vehicle truth telemetry from the live world state — the single source of truth for the
/// CoT sidecar and the Python get_vehicle_telemetry shim. Reuses the existing .NET machinery: the
/// world-observer snapshot cache (transforms/velocities, zero-RPC), <see cref="Geodesy"/> for the
/// local->geodetic transform, and the height-align/drape state cached on <see cref="CarlaClient"/>.
/// `hae` is the BARE-EARTH ellipsoidal-WGS84 altitude: the per-vehicle physical altitude with the
/// photoreal-seating bias removed (a constant offset in 'area'/'origin' modes, or the per-cell drape
/// offset in 'drape' mode), matching the documented telemetry contract.
/// </summary>
public sealed class VehicleTelemetryService
{
    private readonly CarlaClient _client;

    // Per-actor description + bounding box are static, so cache them and RPC only for newly-seen ids.
    private readonly Dictionary<ActorId, Actor> _meta = new();

    // Parsed drape grids, re-parsed only when the underlying cached byte[] reference changes.
    private float[]? _offGrid, _dtmGrid;
    private byte[]? _offRef, _dtmRef;
    private int _nc, _nr;
    private double _minx, _miny, _cell;

    public VehicleTelemetryService(CarlaClient client) => _client = client;

    /// <summary>The world's georeference origin (lat, lon, height_m). Cache it and pass it in to avoid
    /// the per-call RPC.</summary>
    public GeoLocation GetOrigin() => _client.GetCesiumOriginAsync().GetAwaiter().GetResult();

    public IReadOnlyList<VehicleTelemetry> Compute(GeoLocation origin)
    {
        IReadOnlyList<ActorId> ids = _client.GetCachedActorIds();

        // Refresh descriptions only for actors we have not seen (RPC once per new actor, not per call).
        List<ActorId>? unknown = null;
        foreach (var id in ids)
            if (!_meta.ContainsKey(id))
                (unknown ??= new List<ActorId>()).Add(id);
        if (unknown is { Count: > 0 })
        {
            var fetched = _client.GetActorsByIdAsync(unknown).GetAwaiter().GetResult();
            foreach (var a in fetched) _meta[a.Id] = a;
        }

        bool drape = _client.LastDrapeActive;
        if (drape) EnsureDrapeGrids();
        double scalarOffset = _client.LastHeightAlignOffset;
        var dtmSamples = _client.LastGroundDtmSamples;

        var outp = new List<VehicleTelemetry>(ids.Count);
        foreach (var id in ids)
        {
            if (!_meta.TryGetValue(id, out var meta)) continue;
            string typeId = meta.Description.Id;
            if (!typeId.StartsWith("vehicle.", StringComparison.Ordinal)) continue;

            var snap = _client.GetActorSnapshot(id);
            if (snap is null) continue;
            var loc = snap.Transform.Location;
            var vel = snap.Velocity;

            var geo = Geodesy.CarlaLocalToGeodetic(origin, loc.X, loc.Y, loc.Z);
            double physicalHae = geo.Altitude;

            double hae, haeDtm;
            if (drape && _offGrid is not null && _dtmGrid is not null)
            {
                hae = physicalHae - Sample(_offGrid, loc.X, loc.Y);
                haeDtm = Sample(_dtmGrid, loc.X, loc.Y);
            }
            else
            {
                hae = physicalHae - scalarOffset;
                haeDtm = NearestDtm(dtmSamples, geo.Latitude, geo.Longitude);
            }

            double vx = vel.X, vy = vel.Y, vz = vel.Z;
            double speed = Math.Sqrt(vx * vx + vy * vy);
            double course;
            if (speed >= 0.5)
                course = Mod360(RadToDeg(Math.Atan2(vx, -vy)));        // course over ground, true north
            else
            {
                double yaw = DegToRad(snap.Transform.Rotation.Yaw);    // ~stopped: fall back to heading
                course = Mod360(RadToDeg(Math.Atan2(Math.Cos(yaw), -Math.Sin(yaw))));
            }

            var attrs = meta.Description.Attributes;
            string baseType = Attr(attrs, "base_type", "");
            if (baseType.Length == 0)
                baseType = Attr(attrs, "number_of_wheels", "4") == "2" ? "motorcycle" : "car";
            var ext = meta.BoundingBox.Extent;

            outp.Add(new VehicleTelemetry(
                id, typeId, baseType, Attr(attrs, "special_type", ""),
                Attr(attrs, "color", ""), Attr(attrs, "role_name", ""),
                geo.Latitude, geo.Longitude, hae, haeDtm,
                speed, course, vx, vy, vz,
                2.0 * ext.X, 2.0 * ext.Y, 2.0 * ext.Z));
        }
        return outp;
    }

    private static string Attr(IReadOnlyList<ActorAttributeValue> attrs, string id, string dflt)
    {
        foreach (var a in attrs) if (a.Id == id) return a.Value;
        return dflt;
    }

    private void EnsureDrapeGrids()
    {
        _nc = _client.LastDrapeNumCols;
        _nr = _client.LastDrapeNumRows;
        _minx = _client.LastDrapeMinX;
        _miny = _client.LastDrapeMinY;
        _cell = _client.LastDrapeCellSize;
        var offBytes = _client.LastDrapedOffsetBytes;
        var dtmBytes = _client.LastDrapedDtmBytes;
        if (!ReferenceEquals(offBytes, _offRef)) { _offGrid = ToFloats(offBytes); _offRef = offBytes; }
        if (!ReferenceEquals(dtmBytes, _dtmRef)) { _dtmGrid = ToFloats(dtmBytes); _dtmRef = dtmBytes; }
    }

    private static float[] ToFloats(byte[] b)
    {
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);   // row-major float32, little-endian host
        return f;
    }

    // Faithful port of the Python _drape_surf: samples a grid using the SAME per-cell triangulation as
    // Chaos::FHeightField, so the reported ground matches the physics surface the vehicle rests on
    // (bilinear would disagree on steep cells). Edge-clamped, O(1).
    private double Sample(float[] grid, double x, double y)
    {
        double fc = Math.Clamp((x - _minx) / _cell, 0.0, _nc - 1.0);
        double fr = Math.Clamp((y - _miny) / _cell, 0.0, _nr - 1.0);
        int c0 = (int)fc, r0 = (int)fr;
        int c1 = Math.Min(c0 + 1, _nc - 1), r1 = Math.Min(r0 + 1, _nr - 1);
        double tx = fc - c0, ty = fr - r0;
        double v00 = grid[r0 * _nc + c0], v01 = grid[r0 * _nc + c1];
        double v10 = grid[r1 * _nc + c0], v11 = grid[r1 * _nc + c1];
        return ty <= tx
            ? v00 + (v01 - v00) * tx + (v11 - v01) * ty    // lower-right triangle (v00, v01, v11)
            : v00 + (v11 - v10) * tx + (v10 - v00) * ty;    // upper-left triangle (v00, v11, v10)
    }

    private static double NearestDtm(IReadOnlyList<GeoLocation> table, double lat, double lon)
    {
        if (table is null || table.Count == 0) return double.NaN;
        double coslat = Math.Cos(DegToRad(lat));
        double best = double.PositiveInfinity, bestAlt = double.NaN;
        for (int i = 0; i < table.Count; i++)
        {
            double dx = (table[i].Longitude - lon) * coslat;
            double dy = table[i].Latitude - lat;
            double d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; bestAlt = table[i].Altitude; }
        }
        return bestAlt;
    }

    private static double Mod360(double d) => ((d % 360.0) + 360.0) % 360.0;
    private static double RadToDeg(double r) => r * 180.0 / Math.PI;
    private static double DegToRad(double d) => d * Math.PI / 180.0;
}
