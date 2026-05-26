// Source: carla/trafficmanager/LocalizationUtils.h + LocalizationUtils.cpp
//
// Free-function helpers shared by LocalizationStage and MotionPlanStage:
//   - DeviationCrossProduct / DeviationDotProduct: heading-vs-target geometry
//     primitives used to decide steering sign and "are we on course?" gates.
//   - PushWaypoint / PopWaypoint: buffer mutators that also update the
//     TrackTraffic inverted index in lockstep.
//   - GetTargetWaypoint: pull the waypoint at `target_point_distance` metres
//     ahead in a horizon buffer.
//
// All depend on <see cref="SimpleWaypoint"/> and <see cref="TrackTraffic"/>;
// none touch the Map directly. Single-threaded by contract (LocalizationStage
// is the sole writer).
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// Free-function helpers used by LocalizationStage / MotionPlanStage. Static
/// class — every method is a pure or buffer-mutating helper. Mirrors the
/// upstream namespace-scope functions in LocalizationUtils.{h,cpp}.
/// </summary>
internal static class LocalizationUtils
{
    private const float Epsilon = Constants.Collision.EPSILON;
    private const float InvMapResolution = Constants.Map.INV_MAP_RESOLUTION;

    /// <summary>
    /// Z-component of the cross product between the vehicle's flat heading
    /// vector and the unit vector from <paramref name="referenceLocation"/>
    /// to <paramref name="targetLocation"/>. Sign tells you whether the
    /// target is to the left (+) or right (−) of the heading; magnitude is
    /// the sine of the angle.
    /// </summary>
    public static float DeviationCrossProduct(
        Location referenceLocation,
        Vector3D headingVector,
        Location targetLocation)
    {
        Vector3D nextVector = new(
            targetLocation.X - referenceLocation.X,
            targetLocation.Y - referenceLocation.Y,
            targetLocation.Z - referenceLocation.Z);
        nextVector = MakeSafeUnitVector(nextVector, Epsilon);
        return headingVector.X * nextVector.Y - headingVector.Y * nextVector.X;
    }

    /// <summary>
    /// Dot product between the vehicle's flat heading and the flat unit
    /// vector to the target. Clamped to [0, 1] — used as a "facing-forward"
    /// gate (0 means perpendicular or behind, 1 means dead ahead).
    /// </summary>
    public static float DeviationDotProduct(
        Location referenceLocation,
        Vector3D headingVector,
        Location targetLocation)
    {
        Vector3D nextVector = new(
            targetLocation.X - referenceLocation.X,
            targetLocation.Y - referenceLocation.Y,
            0f);
        nextVector = MakeSafeUnitVector(nextVector, Epsilon);

        Vector3D headingFlat = new(headingVector.X, headingVector.Y, 0f);
        headingFlat = MakeSafeUnitVector(headingFlat, Epsilon);

        float dot = nextVector.X * headingFlat.X
                  + nextVector.Y * headingFlat.Y
                  + nextVector.Z * headingFlat.Z;
        return MathF.Max(0f, MathF.Min(dot, 1f));
    }

    // Path buffer aliasing. Upstream uses `std::deque<SimpleWaypointPtr>`
    // (DataStructures.h:32). In C# we accept any `IList<SimpleWaypoint>`
    // (typically `List<SimpleWaypoint>` for O(1) indexed read in
    // GetTargetWaypoint; the O(n) RemoveAt(0) cost is negligible on the
    // ~50-entry path buffers TM uses).

    /// <summary>
    /// Append <paramref name="waypoint"/> to the back of <paramref name="buffer"/>
    /// and register the actor in <see cref="TrackTraffic"/>'s passing-vehicles
    /// index. Matches upstream's <c>PushWaypoint</c> exactly (the buffer add
    /// and the index update must happen together so the index stays
    /// consistent with the buffer contents).
    /// </summary>
    public static void PushWaypoint(
        ActorId actorId,
        TrackTraffic trackTraffic,
        IList<SimpleWaypoint> buffer,
        SimpleWaypoint waypoint)
    {
        ulong waypointId = waypoint.GetId();
        buffer.Add(waypoint);
        trackTraffic.UpdatePassingVehicle(waypointId, actorId);
    }

    /// <summary>
    /// Remove the front (default) or back waypoint from the buffer and
    /// de-register the actor from <see cref="TrackTraffic"/>'s passing-
    /// vehicles index for that waypoint. <paramref name="frontOrBack"/>
    /// matches upstream's <c>front_or_back</c> arg semantics: true ⇒ front.
    /// </summary>
    public static void PopWaypoint(
        ActorId actorId,
        TrackTraffic trackTraffic,
        IList<SimpleWaypoint> buffer,
        bool frontOrBack = true)
    {
        if (buffer.Count == 0) return;
        int idx = frontOrBack ? 0 : buffer.Count - 1;
        ulong removedId = buffer[idx].GetId();
        buffer.RemoveAt(idx);
        trackTraffic.RemovePassingVehicle(removedId, actorId);
    }

    /// <summary>
    /// Pair returned by <see cref="GetTargetWaypoint"/>: the selected
    /// waypoint and its index inside the buffer. Mirrors upstream's
    /// <c>TargetWPInfo = std::pair&lt;SimpleWaypointPtr,uint64_t&gt;</c>.
    /// </summary>
    public readonly record struct TargetWPInfo(SimpleWaypoint Waypoint, ulong Index);

    /// <summary>
    /// Walk a horizon buffer to find the waypoint at
    /// <paramref name="targetPointDistance"/> metres ahead of the buffer's
    /// front. Mirrors upstream's <c>GetTargetWaypoint</c> in
    /// LocalizationUtils.cpp:59–94.
    /// </summary>
    /// <remarks>
    /// The upstream implementation has a subtle quirk: the "scan backward"
    /// branch is unreachable because it's gated on `mScanForward == false`
    /// only when `front->DistanceSquared(target=front) &gt;= dist²` — but
    /// at i=startPosn that distance is 0 so the gate fails. We preserve the
    /// same control flow for byte-for-byte parity; the practical effect is
    /// that we always scan forward from `startPosn`.
    /// </remarks>
    public static TargetWPInfo GetTargetWaypoint(
        IReadOnlyList<SimpleWaypoint> waypointBuffer,
        float targetPointDistance)
    {
        SimpleWaypoint targetWaypoint = waypointBuffer[0];
        SimpleWaypoint bufferFront = waypointBuffer[0];

        ulong startPosn = (ulong)MathF.Abs(targetPointDistance * InvMapResolution);
        ulong index = startPosn;

        if (startPosn < (ulong)waypointBuffer.Count)
        {
            float targetPointDistPower = targetPointDistance * targetPointDistance;

            bool scanForward = false;
            if (bufferFront.DistanceSquared(targetWaypoint) < targetPointDistPower)
                scanForward = true;

            if (scanForward)
            {
                for (ulong i = startPosn;
                    i < (ulong)waypointBuffer.Count
                    && bufferFront.DistanceSquared(targetWaypoint) < targetPointDistPower;
                    i++)
                {
                    targetWaypoint = waypointBuffer[(int)i];
                    index = i;
                }
            }
            else
            {
                for (ulong i = startPosn;
                    bufferFront.DistanceSquared(targetWaypoint) > targetPointDistPower;
                    i--)
                {
                    targetWaypoint = waypointBuffer[(int)i];
                    index = i;
                    if (i == 0) break; // guard against uint underflow
                }
            }
        }
        else
        {
            targetWaypoint = waypointBuffer[waypointBuffer.Count - 1];
            index = (ulong)(waypointBuffer.Count - 1);
        }

        return new TargetWPInfo(targetWaypoint, index);
    }

    // ── Internal vector math (mirrors cg::Vector3D::MakeSafeUnitVector) ────

    /// <summary>
    /// Returns <paramref name="v"/> normalised to unit length, leaving it
    /// untouched if its length is &lt;= <paramref name="epsilon"/> (the
    /// upstream <c>MakeSafeUnitVector</c> behaviour — see Vector3D.h:76).
    /// </summary>
    private static Vector3D MakeSafeUnitVector(Vector3D v, float epsilon)
    {
        float length = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        float k = (length > MathF.Max(epsilon, 0f)) ? (1f / length) : 1f;
        return new Vector3D(v.X * k, v.Y * k, v.Z * k);
    }
}
