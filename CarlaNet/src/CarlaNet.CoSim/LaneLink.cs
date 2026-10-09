namespace CarlaNet.CoSim;

/// <summary>
/// Where one lane leads: the junction connector a vehicle crosses, where there is one, and the lane
/// it arrives on.
/// </summary>
/// <param name="ViaLaneId">
/// The internal lane inside the junction, or an empty string where the two lanes meet directly.
/// </param>
/// <param name="ToLaneId">The lane the vehicle ends up on.</param>
/// <remarks>
/// SUMO writes a crossing as two connections: one from the approach to the far lane naming the
/// connector as its <c>via</c>, and one from the connector itself to that same far lane. So walking
/// a route means stepping to the connector and letting its own connection carry on, never stepping
/// straight to the far lane -- which would skip the length and the shape of the turn, and the turn
/// is the whole reason a route is walked at all.
/// </remarks>
public readonly record struct LaneLink(string ViaLaneId, string ToLaneId)
{
    /// <summary>The lane a vehicle reaches next: the connector where there is one.</summary>
    public string NextLaneId => ViaLaneId.Length > 0 ? ViaLaneId : ToLaneId;
}
