using CarlaNet.Transport;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Recording;

/// <summary>
/// Produces per-vehicle truth telemetry from the live world state — the single source of truth for the
/// CoT sidecar and the Python get_vehicle_telemetry shim. Reuses the existing .NET machinery: the
/// world-observer snapshot cache (transforms/velocities, zero-RPC), <see cref="Geodesy"/> for the
/// local->geodetic transform, and the height-align/drape state cached on <see cref="CarlaClient"/>.
/// `hae` is the BARE-EARTH ellipsoidal-WGS84 altitude: the per-vehicle physical altitude with the
/// photoreal-seating bias removed (a constant offset in 'area'/'origin' modes, or the per-cell drape
/// offset in 'drape' mode), matching the documented telemetry contract.
///
/// During a SUMO drive the world's vehicle actors are a pool of bodies, each lent to a SUMO vehicle
/// while it is drawn and parked out of sight between loans. The session names each body to the
/// server as it lends it and gives it back, and every world-observer snapshot carries what it named
/// (<see cref="ObservedRenderSet"/>), so the truth here -- the live pull of any process as much as a
/// recorder beside the session -- leaves out a body parked on the frame it describes and names a lent
/// one by its SUMO vehicle. An actor no session named is reported as it always was.
///
/// `base_type` and `special_type` are the vehicle catalogue's for the vehicle's blueprint wherever
/// this connection adopted a catalogue that curates them (<see cref="CarlaClient.CatalogueBaseTypes"/>,
/// <see cref="CarlaClient.CatalogueSpecialTypes"/>), which a SUMO drive session does when it starts:
/// the class's base type, and its kind, an empty one where the class curates none, whatever the
/// blueprint declares. A blueprint no adopted catalogue curates keeps what it declares itself, with
/// the base type taken from its wheel count where it declares none (doc 06 D6.18).
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

    /// <summary>Truth for every vehicle as of the newest world-observer frame.</summary>
    public IReadOnlyList<VehicleTelemetry> Compute(GeoLocation origin) => Compute(origin, null, out _);

    /// <summary>
    /// Truth for every vehicle as of <paramref name="frame"/>, for pairing with something produced at
    /// that frame: a camera image carries its frame in its header, and the snapshot of that frame is
    /// what its pixels show. Null asks for the newest frame instead. <paramref name="telemetryFrame"/>
    /// is the frame the records actually describe: the one asked for whenever the client still holds
    /// it, otherwise the nearest it does hold, so a caller can record what it got rather than assume.
    /// </summary>
    public IReadOnlyList<VehicleTelemetry> Compute(GeoLocation origin, ulong? frame, out ulong telemetryFrame)
        => Compute(origin, frame, out telemetryFrame, out _);

    /// <summary>
    /// Truth as <see cref="Compute(GeoLocation, ulong?, out ulong)"/> answers it, and the render set
    /// the records' own frame carried: <see cref="ObservedRenderSet.None"/> where it carried none, so
    /// the records are every vehicle actor, and otherwise the set they were cut to and named from.
    /// </summary>
    /// <remarks>
    /// Where no frame is asked for and the newest snapshot carries a render set -- a SUMO drive -- the
    /// records are read from that newest retained frame rather than the actor cache, so a body's pose
    /// and its naming always come from one frame: a body is lent or given back between two ticks, and
    /// the cache is refreshed in place while it is read.
    /// </remarks>
    public IReadOnlyList<VehicleTelemetry> Compute(GeoLocation origin, ulong? frame, out ulong telemetryFrame,
                                                   out ObservedRenderSet renderSet)
        => Compute(origin, frame, out telemetryFrame, out renderSet, out _, out _);

    /// <summary>
    /// Truth as <see cref="Compute(GeoLocation, ulong?, out ulong, out ObservedRenderSet)"/> answers it,
    /// and the supervision the records' own frame carried, read in the same read as its actors and its
    /// render set, so a body's supervision is always the one it carried for the vehicle it drew on that
    /// frame.
    /// </summary>
    /// <param name="fromSnapshot">
    /// Whether the records were read from a held snapshot -- the one <paramref name="telemetryFrame"/>
    /// names -- rather than the actor cache, which a client holding no snapshot yet answers from and
    /// which carries no supervision.
    /// </param>
    public IReadOnlyList<VehicleTelemetry> Compute(GeoLocation origin, ulong? frame, out ulong telemetryFrame,
                                                   out ObservedRenderSet renderSet,
                                                   out ObservedSupervision supervision, out bool fromSnapshot)
    {
        IReadOnlyDictionary<ActorId, ActorSnapshot>? atFrame = null;
        telemetryFrame = 0;
        renderSet = ObservedRenderSet.None;
        supervision = ObservedSupervision.None;
        if (frame.HasValue)
            atFrame = _client.GetSnapshotFrame(frame.Value, out telemetryFrame, out renderSet, out supervision);
        else if (!_client.GetCachedRenderSet().IsEmpty)
            atFrame = _client.GetSnapshotFrame(_client.LatestObservedFrame, out telemetryFrame, out renderSet,
                                               out supervision);
        fromSnapshot = atFrame is not null;
        if (atFrame is null)
            telemetryFrame = _client.LatestObservedFrame;
        IReadOnlyList<ActorId> ids = atFrame is not null ? atFrame.Keys.ToArray() : _client.GetCachedActorIds();

        // Refresh descriptions only for actors we have not seen (RPC once per new actor, not per call).
        // A parked body is never reported, so its description waits until it is lent.
        List<ActorId>? unknown = null;
        foreach (var id in ids)
            if (!_meta.ContainsKey(id) && !renderSet.IsParked(id))
                (unknown ??= new List<ActorId>()).Add(id);
        if (unknown is { Count: > 0 })
        {
            var fetched = _client.GetActorsByIdAsync(unknown).GetAwaiter().GetResult();
            foreach (var a in fetched) _meta[a.Id] = a;
        }

        // Recover the surface shift for a world this client did not build (a reconnect, or a world
        // opened rather than generated). Without it the height-align state below reads as "no shift"
        // and the photoreal-referenced height would be reported as bare-earth truth. Idempotent and
        // queried at most once per world, so it is safe in this per-tick path.
        _client.EnsureBareEarthReference();

        bool drape = _client.LastDrapeActive;
        if (drape) EnsureDrapeGrids();
        var dtmSamples = _client.LastGroundDtmSamples;

        // Read once, so every record of this frame takes its kinds from the same tables.
        IReadOnlyDictionary<string, string> curatedBaseTypes = _client.CatalogueBaseTypes;
        IReadOnlyDictionary<string, string> curatedKinds = _client.CatalogueSpecialTypes;

        var outp = new List<VehicleTelemetry>(ids.Count);
        foreach (var id in ids)
        {
            // A body a SUMO drive's pool had parked on this frame stands out of sight below the
            // ground, drawn for nobody: not a vehicle in the scene, so not reported.
            if (renderSet.IsParked(id)) continue;
            if (!_meta.TryGetValue(id, out var meta)) continue;
            string typeId = meta.Description.Id;
            if (!typeId.StartsWith("vehicle.", StringComparison.Ordinal)) continue;

            // Truth telemetry describes vehicles that are actually in the scene. Boundary-aware
            // staging traffic spawns a vehicle transparent out in the entry ring and dissolves it in
            // as it crosses into the interior, so one that has never been fully opaque has not
            // arrived yet and is deliberately not reported — a half-dissolved car is not something a
            // sensor should be told is there. Vehicles nobody fades are established from the start,
            // so this gate is inert unless staging traffic is running.
            if (!_client.IsActorEstablished(id)) continue;

            ActorSnapshot? snap = atFrame is not null
                ? (atFrame.TryGetValue(id, out var held) ? held : null)
                : _client.GetActorSnapshot(id);
            if (snap is null) continue;
            var loc = snap.Transform.Location;
            var vel = snap.Velocity;

            var geo = Geodesy.CarlaLocalToGeodetic(origin, loc.X, loc.Y, loc.Z);
            double physicalHae = geo.Altitude;

            double hae = physicalHae - OffsetAt(loc.X, loc.Y);
            double haeDtm = (drape && _dtmGrid is not null)
                ? Sample(_dtmGrid, loc.X, loc.Y)
                : NearestDtm(dtmSamples, geo.Latitude, geo.Longitude);

            double vx = vel.X, vy = vel.Y, vz = vel.Z;
            double speed = Math.Sqrt(vx * vx + vy * vy);
            double yaw = DegToRad(snap.Transform.Rotation.Yaw);
            double heading = Mod360(RadToDeg(Math.Atan2(Math.Cos(yaw), -Math.Sin(yaw))));  // true north
            double course = speed >= 0.5
                ? Mod360(RadToDeg(Math.Atan2(vx, -vy)))                // course over ground, true north
                : heading;                                             // ~stopped: fall back to heading

            var attrs = meta.Description.Attributes;
            // The base type the catalogue curates for this blueprint, and only for a blueprint it does
            // not curate the one the blueprint declares, or failing that its wheel count's.
            if (!curatedBaseTypes.TryGetValue(typeId, out string? baseType))
            {
                baseType = Attr(attrs, "base_type", "");
                if (baseType.Length == 0)
                    baseType = Attr(attrs, "number_of_wheels", "4") == "2" ? "motorcycle" : "car";
            }
            // The kind the catalogue curates for this blueprint, an empty one included, and only for
            // a blueprint it does not curate the kind the blueprint declares.
            string specialType = curatedKinds.TryGetValue(typeId, out string? curated)
                ? curated
                : Attr(attrs, "special_type", "");
            var ext = meta.BoundingBox.Extent;

            outp.Add(new VehicleTelemetry(
                id, typeId, baseType, specialType,
                Attr(attrs, "color", ""), Attr(attrs, "role_name", ""),
                geo.Latitude, geo.Longitude, hae, haeDtm,
                speed, course, vx, vy, vz,
                2.0 * ext.X, 2.0 * ext.Y, 2.0 * ext.Z)
            {
                HeadingDeg = heading,
                Opacity = _client.GetActorOpacity(id),
                // Carried alongside the truth so anything measuring against the imagery — occlusion,
                // a projected bounding box — works from the same pose this record was built from.
                ActorTransform = snap.Transform,
                BoundingBox = meta.BoundingBox,
                // The SUMO vehicle a lent body was drawn for on this frame, so the record names the
                // vehicle rather than the body, which carries a succession of them over a run.
                Rendered = renderSet.TryGetLent(id, out var lent)
                    ? new RenderedVehicle(id, lent.VehicleId, lent.VehicleTypeId, lent.AdmittedFrame)
                    : null,
            });
        }
        // Drop cached descriptions for actors no longer present so this cache tracks the live world too
        // (ids come from the world-observer snapshot, which now evicts destroyed actors).
        if (_meta.Count > ids.Count)
        {
            var live = new HashSet<ActorId>(ids);
            foreach (var key in _meta.Keys.ToList())
                if (!live.Contains(key)) _meta.Remove(key);
        }
        return outp;
    }

    /// <summary>
    /// The height-align offset (metres) applied at a horizontal position — the amount added to bare-earth
    /// terrain to seat road/ground on the photoreal imagery. A function of (x, y) only, independent of
    /// altitude, so it is well-defined for an airborne camera as well as a ground vehicle: 0 in 'none'
    /// mode, the scalar offset in 'area'/'origin', and the per-cell drape sample (edge-clamped) in 'drape'.
    /// Subtract it from a physical HAE to get the bare-earth HAE.
    /// </summary>
    public double OffsetAt(double x, double y)
    {
        if (_client.LastDrapeActive)
        {
            EnsureDrapeGrids();
            if (_offGrid is not null) return Sample(_offGrid, x, y);
        }
        return _client.LastHeightAlignOffset;
    }

    /// <summary>
    /// Derive the collection platform's per-frame state from the sensor-header world transform and the
    /// client-supplied platform options. <paramref name="prevTf"/> and <paramref name="dtSeconds"/> (the
    /// previous processed frame's transform and the sim-time gap to it) yield course/speed over ground;
    /// pass null/0 for the first frame. Pinhole intrinsics are derived from the horizontal FOV and the
    /// frame size (centered principal point, square pixels).
    /// </summary>
    public SensorPose ComputeSensorPose(GeoLocation origin, Transform tf, Transform? prevTf,
                                        double dtSeconds, SensorPlatformOptions opt, int width, int height)
    {
        var loc = tf.Location;
        var geo = Geodesy.CarlaLocalToGeodetic(origin, loc.X, loc.Y, loc.Z);
        double offset = OffsetAt(loc.X, loc.Y);
        double hae = geo.Altitude - offset;

        // Boresight pointing. CARLA yaw: +X=East, -Y=North; pitch is +up (so -90 = nadir).
        double yaw = DegToRad(tf.Rotation.Yaw);
        double az = Mod360(RadToDeg(Math.Atan2(Math.Cos(yaw), -Math.Sin(yaw))));
        double el = tf.Rotation.Pitch;
        double roll = tf.Rotation.Roll;

        // Platform course/speed over ground from the pose delta (the sensor header carries no velocity).
        double course = az, speed = 0.0;
        if (prevTf is Transform p && dtSeconds > 1e-6)
        {
            double dx = loc.X - p.Location.X, dy = loc.Y - p.Location.Y;
            speed = Math.Sqrt(dx * dx + dy * dy) / dtSeconds;
            if (speed >= 0.5) course = Mod360(RadToDeg(Math.Atan2(dx, -dy)));  // over ground, true north
        }

        // Pinhole intrinsics from horizontal FOV + frame size. hfov/2 in radians = HFovDeg * PI/360.
        double fx = width / (2.0 * Math.Tan(opt.HFovDeg * Math.PI / 360.0));
        double fy = fx;                                   // square pixels
        double cx = width / 2.0, cy = height / 2.0;       // centered principal point
        double vfov = RadToDeg(2.0 * Math.Atan(height / (2.0 * fx)));

        return new SensorPose(
            opt.CotType, opt.Callsign, opt.Uid,
            geo.Latitude, geo.Longitude, hae, offset,
            az, el, roll, course, speed,
            width, height, fx, fy, cx, cy, opt.HFovDeg, vfov,
            opt.SensorModel, "pinhole", opt.Distortion);
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
