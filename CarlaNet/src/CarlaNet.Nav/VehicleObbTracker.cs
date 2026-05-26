// CARLA-only workaround for DotRecast's missing oriented-bounding-box
// (OBB) obstacle support in dtCrowd. See NAV_PORT_SPEC.md Risk R1.
//
// Upstream CARLA patches DetourCrowd to carry `useObb` + `obb[12]` on
// dtCrowdAgentParams; vehicles get inserted into the crowd as zero-speed
// OBB agents and the crowd's separation step pushes walkers around them.
//
// We can't patch DotRecast (we want clean upstream tracking), so we apply
// the repulsion manually each tick: for every walker close to a vehicle OBB
// we add an inverse-square impulse to its velocity *before* calling
// `DtCrowd.Update`. The crowd's anticipate/avoidance step then folds that
// nudge into the path-following behaviour.
//
// HasVehicleNear() is the same OBB scan reused by WalkerEventStopAndCheck.
#nullable enable

using CarlaNet.Types.Geom;

namespace CarlaNet.Nav;

/// <summary>
/// Per-tick vehicle OBB registry + walker-vs-vehicle repulsion + proximity
/// scan. Lives entirely on the worker thread that owns
/// <see cref="Navigation"/>; no locking.
/// </summary>
internal sealed class VehicleObbTracker
{
    /// <summary>
    /// Oriented bounding box for one vehicle, in Unreal coords (x,y on the
    /// ground plane, z up). Yaw is in radians.
    /// </summary>
    internal readonly struct VehicleObb
    {
        public readonly ActorId Id;
        public readonly Vector3 Center;       // world-space center
        public readonly Vector3 HalfExtent;   // half-size on local x,y,z
        public readonly float CosYaw;
        public readonly float SinYaw;

        public VehicleObb(ActorId id, Vector3 center, Vector3 halfExtent, float yawRad)
        {
            Id = id;
            Center = center;
            // The CARLA patch inflates the OBB by 0.8 m on x,y so that walkers
            // bias away well before they touch the vehicle. Match upstream
            // (Navigation.cpp:546).
            const float MARGE = 0.8f;
            HalfExtent = new Vector3(halfExtent.X + MARGE, halfExtent.Y + MARGE, halfExtent.Z);
            CosYaw = MathF.Cos(yawRad);
            SinYaw = MathF.Sin(yawRad);
        }
    }

    private readonly Dictionary<ActorId, VehicleObb> _obbs = new();

    public int Count => _obbs.Count;

    /// <summary>
    /// Replace the vehicle set wholesale (upstream `UpdateVehiclesInCrowd`
    /// semantics: anything not in this snapshot disappears next tick).
    /// </summary>
    public void Update(IReadOnlyList<(ActorId Id, Location Center, Vector3D Extent, float YawDeg)> obbs)
    {
        _obbs.Clear();
        foreach (var o in obbs)
        {
            var center = new Vector3(o.Center.X, o.Center.Y, o.Center.Z);
            var half   = new Vector3(o.Extent.X, o.Extent.Y, o.Extent.Z);
            var yawRad = o.YawDeg * (MathF.PI / 180.0f);
            _obbs[o.Id] = new VehicleObb(o.Id, center, half, yawRad);
        }
    }

    /// <summary>
    /// Compute the cumulative repulsion impulse that nearby vehicle OBBs
    /// would exert on a walker at <paramref name="walkerPos"/>. Returns
    /// the velocity delta (m/s) to ADD to the agent's <c>nvel</c> before
    /// <see cref="DtCrowd.Update"/> runs.
    /// </summary>
    public Vector3 ComputeRepulsion(Vector3 walkerPos)
    {
        // Magnitudes are conservative — we want a nudge, not a teleport. The
        // crowd's own separation step does the bulk of the avoidance; this
        // just gives walkers a directional bias around vehicles that the
        // crowd otherwise can't see.
        const float MAX_INFLUENCE_RANGE = 4.0f;
        const float MAX_PUSH_SPEED      = 1.2f;   // m/s, clamps the impulse

        var accum = Vector3.Zero;

        foreach (var obb in _obbs.Values)
        {
            // 2D projection: pedestrian motion is planar.
            var dx = walkerPos.X - obb.Center.X;
            var dy = walkerPos.Y - obb.Center.Y;

            // Transform delta into the OBB's local frame (un-rotate by -yaw).
            var localX = dx * obb.CosYaw + dy * obb.SinYaw;
            var localY = -dx * obb.SinYaw + dy * obb.CosYaw;

            // Closest point on the OBB (axis-aligned in local frame).
            var clampedX = Math.Clamp(localX, -obb.HalfExtent.X, obb.HalfExtent.X);
            var clampedY = Math.Clamp(localY, -obb.HalfExtent.Y, obb.HalfExtent.Y);

            // Vector from closest point to walker (still local frame).
            var outLocalX = localX - clampedX;
            var outLocalY = localY - clampedY;
            var dist2 = outLocalX * outLocalX + outLocalY * outLocalY;

            if (dist2 >= MAX_INFLUENCE_RANGE * MAX_INFLUENCE_RANGE)
                continue;

            float pushX, pushY;
            if (dist2 < 1e-4f)
            {
                // Walker is inside the OBB — push along +localX (vehicle's
                // forward axis is arbitrary here, just need a deterministic
                // escape direction).
                pushX = obb.HalfExtent.X;
                pushY = 0;
            }
            else
            {
                var dist = MathF.Sqrt(dist2);
                pushX = outLocalX / dist;
                pushY = outLocalY / dist;
            }

            // Re-rotate to world frame.
            var worldPushX = pushX * obb.CosYaw - pushY * obb.SinYaw;
            var worldPushY = pushX * obb.SinYaw + pushY * obb.CosYaw;

            // Magnitude falls off linearly with distance (1.0 at touch,
            // 0.0 at MAX_INFLUENCE_RANGE).
            var dist1 = MathF.Sqrt(dist2);
            var falloff = 1.0f - (dist1 / MAX_INFLUENCE_RANGE);
            var mag = MAX_PUSH_SPEED * falloff;

            accum.X += worldPushX * mag;
            accum.Y += worldPushY * mag;
        }

        return accum;
    }

    /// <summary>
    /// Replacement for the CARLA-patched <c>dtCrowd::hasVehicleNear</c>.
    /// Returns true if any vehicle OBB is within <paramref name="distance"/>
    /// of <paramref name="walkerPos"/>; if <paramref name="direction"/> is
    /// non-zero, only counts vehicles in roughly that direction.
    /// </summary>
    public bool HasVehicleNear(Vector3 walkerPos, float distance, Vector3 direction)
    {
        var dist2 = distance * distance;
        var hasDir = direction.LengthSquared() > 1e-4f;
        var nDir = hasDir ? Vector3.Normalize(direction) : Vector3.Zero;

        foreach (var obb in _obbs.Values)
        {
            var dx = obb.Center.X - walkerPos.X;
            var dy = obb.Center.Y - walkerPos.Y;
            var dz = obb.Center.Z - walkerPos.Z;
            var d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > dist2)
                continue;

            if (!hasDir)
                return true;

            // Direction-cone check: dot(toVehicle, direction) > 0 ⇒ in front
            var d1 = MathF.Sqrt(d2);
            if (d1 < 1e-4f)
                return true;
            var dot = (dx * nDir.X + dy * nDir.Y + dz * nDir.Z) / d1;
            if (dot > 0.0f)
                return true;
        }

        return false;
    }

    public void Clear() => _obbs.Clear();
}
