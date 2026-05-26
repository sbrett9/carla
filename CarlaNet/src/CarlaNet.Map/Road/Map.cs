// Source: carla/road/Map.h + Map.cpp (data-shell + Wave 3 topology helpers)
//
// Wave 1 deliverable: just the data shell that wraps MapData. Wave 3 will add
// the spatial-query / waypoint-generation methods (GetClosestWaypointOnRoad,
// GetWaypoint, GetNext, GetPrevious, GetLeft, GetRight, ComputeTransform,
// GenerateWaypoints, GenerateTopology, ComputeJunctionConflicts, etc.).
//
// Wave 3G additions (this commit): `ComputeTransform(Waypoint)` ported from
// Road.cpp:GetDirectedPointIn + Lane.cpp:ComputeTransform. This is the helper
// the InMemoryMap (Wave 3) needs to turn each generated dense-topology
// Waypoint POD into a world-space (Location, Rotation). Sibling agents
// querying SimpleWaypoint.Location / ForwardVector indirectly depend on it.
using System;
using CarlaNet.Map.Geom;
using CarlaNet.Map.Road.Element;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.Road;

public sealed class Map
{
    public MapData Data { get; }

    public Map(MapData data)
    {
        Data = data ?? throw new System.ArgumentNullException(nameof(data));
    }

    public CarlaNet.Types.Geom.GeoLocation GeoReference => Data.GeoReference;

    public IReadOnlyDictionary<RoadId, Road> Roads => Data.Roads;
    public IReadOnlyDictionary<JuncId, Junction> Junctions => Data.Junctions;
    public IReadOnlyDictionary<SignId, Signal> Signals => Data.Signals;
    public IReadOnlyDictionary<ContId, Controller> Controllers => Data.Controllers;

    public Junction? GetJunction(JuncId id) => Data.GetJunction(id);

    public bool IsJunction(RoadId roadId)
        => Data.Roads.TryGetValue(roadId, out var r) && r.IsJunction;

    public JuncId GetJunctionId(RoadId roadId)
        => Data.Roads.TryGetValue(roadId, out var r) ? r.JunctionId : -1;

    // ── Wave 3G: world-space transform of a Waypoint POD ───────────────────
    //
    // Port of `carla::road::Map::ComputeTransform(Waypoint)` which delegates
    // to `Lane::ComputeTransform(s)`. Lateral offset accumulation walks the
    // lanes between the waypoint's lane and lane 0 of the same section,
    // summing per-lane widths along the road's `t` axis. Then we apply the
    // road-level `laneOffset` and the Unreal Y-axis flip.
    /// <summary>
    /// Computes the world-space (Location, Rotation) of a road-graph
    /// <see cref="Waypoint"/>. Ported from Road.cpp / Lane.cpp; matches the
    /// upstream `Map::ComputeTransform` bit-for-bit modulo float order-of-ops.
    /// </summary>
    public Transform ComputeTransform(Waypoint w)
    {
        if (!Data.Roads.TryGetValue(w.RoadId, out var road))
            throw new ArgumentException($"unknown road id {w.RoadId}", nameof(w));

        var section = FindSection(road, w.S, w.SectionId);
        if (section == null)
            throw new ArgumentException($"unknown section {w.SectionId} in road {w.RoadId}", nameof(w));

        var lane = section.GetLane(w.LaneId)
            ?? throw new ArgumentException($"unknown lane {w.LaneId} in section {w.SectionId} of road {w.RoadId}", nameof(w));

        return ComputeLaneTransform(road, section, lane, w.S);
    }

    private static LaneSection? FindSection(Road road, double s, SectionId sectionId)
    {
        // Prefer the explicit section id (Waypoint records carry it); fall back
        // to the s-range scan if the id is stale.
        if (road.LaneSectionsById.TryGetValue(sectionId, out var byId))
            return byId;
        LaneSection? candidate = null;
        foreach (var sec in road.LaneSections)
        {
            if (sec.S <= s) candidate = sec;
            else break;
        }
        return candidate;
    }

    /// <summary>
    /// Implementation shared by <see cref="ComputeTransform"/>; the lane is
    /// resolved by the caller. Mirrors <c>Lane::ComputeTransform(s)</c>.
    /// </summary>
    private static Transform ComputeLaneTransform(Road road, LaneSection section, Lane lane, double s)
    {
        var clampedS = Math.Clamp(s, 0.0, road.Length);

        // Accumulate lateral t-offset and tangent contribution of the lanes
        // strictly between lane 0 and the target lane (the target lane itself
        // contributes its half-width).
        float laneTOffset = 0f;
        float laneTangent = 0f;

        if (lane.Id != 0)
        {
            var widthSum = ComputeTotalLaneWidth(section, lane.Id, clampedS);
            laneTOffset = (float)widthSum.Distance;
            laneTangent = (float)widthSum.Tangent;
        }

        // Pull the road-level laneOffset tangent contribution at s.
        float laneOffsetTangent = 0f;
        var laneOffset = GetActive<RoadInfoLaneOffset>(road.Info, clampedS);
        if (laneOffset != null)
            laneOffsetTangent = (float)laneOffset.Polynomial.Tangent(clampedS);
        laneTangent -= laneOffsetTangent;

        // GetDirectedPointIn — geometry + lane-offset lateral shift + elevation.
        var dp = GetDirectedPointIn(road, clampedS);
        dp.ApplyLateralOffset(laneTOffset);

        // Adjust tangent for the per-lane tangent accumulation.
        dp.Tangent -= laneTangent;

        // Unreal Y-axis hack (carla applies y *= -1 and tangent *= -1 here).
        dp.Location = new Location(dp.Location.X, -dp.Location.Y, dp.Location.Z);
        dp.Tangent = -dp.Tangent;

        float pitchDeg = ToDegrees((float)dp.Pitch);
        float yawDeg = ToDegrees((float)dp.Tangent);
        var rot = new Rotation(pitchDeg, yawDeg, 0f);

        // For lanes whose IsPositiveDirection() is false (right-side, lane_id<0
        // in RHT), Carla flips the heading by 180°. The Lane.cs check is
        // `IsPositiveDirection => Id <= 0`; we need the opposite branch to flip.
        if (!lane.IsPositiveDirection)
        {
            rot.Yaw += 180f;
            rot.Pitch = 360f - rot.Pitch;
        }

        return new Transform(dp.Location, rot);
    }

    private readonly record struct WidthAndTangent(double Distance, double Tangent);

    // Mirror of Lane.cpp:ComputeTotalLaneWidth. Walks lanes from |id|=1 toward
    // the target on the correct side of the centerline (sign depends on
    // lane_id direction). For each lane that isn't the target, accumulate the
    // full width; for the target lane add half (so we land at the lane's
    // centerline, not its outer edge).
    private static WidthAndTangent ComputeTotalLaneWidth(LaneSection section, LaneId targetId, double s)
    {
        bool negative = targetId < 0;
        double dist = 0.0;
        double tangent = 0.0;

        // The iteration order matches upstream:
        //   right side (negative ids) — walk from lane -1 down to targetId
        //   left  side (positive ids) — walk from lane +1 up to targetId
        // The signed lane_ids in our SortedDictionary already iterate ascending
        // (most-negative → 0 → most-positive), so we filter and walk in the
        // appropriate direction.
        IEnumerable<Lane> walk;
        if (negative)
        {
            // lanes with id in [-1 .. targetId] iterated from -1 downward.
            var list = new List<Lane>();
            foreach (var kv in section.Lanes)
                if (kv.Key < 0) list.Add(kv.Value);
            // ascending by key (most-negative first) → reverse so we go -1, -2, …
            list.Reverse();
            walk = list;
        }
        else
        {
            // lanes with id in [+1 .. targetId] iterated +1 upward (already ascending).
            var list = new List<Lane>();
            foreach (var kv in section.Lanes)
                if (kv.Key > 0) list.Add(kv.Value);
            walk = list;
        }

        foreach (var lane in walk)
        {
            var info = GetActive<RoadInfoLaneWidth>(lane.Info, s);
            if (info == null)
            {
                // LaneBorder fallback (lane has border instead of width records).
                var border = GetActive<RoadInfoLaneBorder>(lane.Info, s);
                if (border == null) continue;
                double bd = border.Polynomial.Evaluate(s);
                double bt = border.Polynomial.Tangent(s);
                if (lane.Id != targetId)
                {
                    dist += negative ? bd : -bd;
                    tangent += negative ? bt : -bt;
                }
                else
                {
                    bd *= 0.5;
                    dist += negative ? bd : -bd;
                    tangent += (negative ? bt : -bt) * 0.5;
                    break;
                }
                continue;
            }

            double current = info.Polynomial.Evaluate(s);
            double curTan = info.Polynomial.Tangent(s);
            if (lane.Id != targetId)
            {
                dist += negative ? current : -current;
                tangent += negative ? curTan : -curTan;
            }
            else
            {
                current *= 0.5;
                dist += negative ? current : -current;
                tangent += (negative ? curTan : -curTan) * 0.5;
                break;
            }
        }
        return new WidthAndTangent(dist, tangent);
    }

    /// <summary>
    /// Port of <c>Road::GetDirectedPointIn(s)</c>: evaluate the active
    /// geometry primitive at <paramref name="s"/>, then apply the road's
    /// lane-offset lateral shift and elevation profile.
    /// </summary>
    public static DirectedPoint GetDirectedPointIn(Road road, double s)
    {
        var clampedS = Math.Clamp(s, 0.0, road.Length);
        var geomInfo = GetActive<RoadInfoGeometry>(road.Info, clampedS)
            ?? throw new InvalidOperationException($"no geometry record at s={clampedS} on road {road.Id}");

        float offset = 0f;
        var laneOffset = GetActive<RoadInfoLaneOffset>(road.Info, clampedS);
        if (laneOffset != null)
            offset = (float)laneOffset.Polynomial.Evaluate(clampedS);

        var dp = geomInfo.Geometry.PosFromDist(clampedS - geomInfo.Distance);
        // Note: upstream applies a NEGATIVE offset here (Unreal Y axis hack
        // comment in Road.cpp:196). Preserve it exactly.
        dp.ApplyLateralOffset(-offset);

        var elev = GetActive<RoadInfoElevation>(road.Info, s);
        if (elev != null)
        {
            float z = (float)elev.Polynomial.Evaluate(s);
            dp.Location = new Location(dp.Location.X, dp.Location.Y, z);
            dp.Pitch = elev.Polynomial.Tangent(s);
        }
        return dp;
    }

    /// <summary>
    /// Port of <c>Road::GetDirectedPointInNoLaneOffset(s)</c>. Used by the
    /// signal-transform helper.
    /// </summary>
    public static DirectedPoint GetDirectedPointInNoLaneOffset(Road road, double s)
    {
        var clampedS = Math.Clamp(s, 0.0, road.Length);
        var geomInfo = GetActive<RoadInfoGeometry>(road.Info, clampedS)
            ?? throw new InvalidOperationException($"no geometry record at s={clampedS} on road {road.Id}");

        var dp = geomInfo.Geometry.PosFromDist(clampedS - geomInfo.Distance);

        var elev = GetActive<RoadInfoElevation>(road.Info, s);
        if (elev != null)
        {
            float z = (float)elev.Polynomial.Evaluate(s);
            dp.Location = new Location(dp.Location.X, dp.Location.Y, z);
            dp.Pitch = elev.Polynomial.Tangent(s);
        }
        return dp;
    }

    // Helper: first RoadInfo of T whose Distance <= s (the upstream
    // `_info.GetInfo<T>(s)` shape). RoadElementSet.GetReverseSubset yields
    // records in descending-s order; the first match of type T is the active
    // one at s.
    private static T? GetActive<T>(RoadElementSet<RoadInfo> set, double s) where T : RoadInfo
    {
        foreach (var info in set.GetReverseSubset(s))
            if (info is T t) return t;
        return null;
    }

    private static float ToDegrees(float rad) => rad * (180f / MathF.PI);
}
