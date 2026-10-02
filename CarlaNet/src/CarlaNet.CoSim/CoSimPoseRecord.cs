using CarlaNet.Sumo;
using CarlaNet.Types.Rpc.Lighting;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// One pose the bridge computed for one vehicle, at one world tick.
/// </summary>
/// <param name="TickIndex">Ticks since the session's first, counting from zero.</param>
/// <param name="SimulatedTimeSeconds">The rendered instant, which is what a frame would be stamped with.</param>
/// <param name="IsCaptureTick">Whether a recorder would have emitted a frame on this tick.</param>
/// <param name="Actor">
/// The pooled body the pose was written to, or zero where none was: a session with no CARLA
/// attached, or one whose pool had no body left for this vehicle's blueprint.
/// </param>
/// <param name="Pose">The transform and velocity that went into the batch.</param>
/// <param name="Case">How the sub-step pose was produced.</param>
/// <param name="SumoX">Where SUMO said the front bumper was, for a reader checking the conversion.</param>
/// <param name="SumoY">The same, northing.</param>
/// <param name="SumoHeadingDegrees">And the heading SUMO reported, clockwise from north.</param>
/// <param name="Signals">
/// SUMO's signal word for the vehicle at the SUMO frame this tick is rendered from -- the raw word, so a
/// reader can tell what the simulation said from what the rendering made of it.
/// </param>
/// <param name="Lamps">
/// The lamps the body holds after this tick's batch: <see cref="Signals"/> mapped bit by bit, and the
/// headlights the sun asserts; <see cref="VehicleLightStateFlags.None"/> where the session drives no lamps.
/// </param>
/// <param name="LaneId">
/// The lane the interpolation put the front bumper on at this tick, which named the road the body was
/// seated on; empty where the vehicle was on no lane.
/// </param>
/// <param name="LanePositionMetres">How far along <paramref name="LaneId"/> the front bumper was.</param>
public readonly record struct CoSimPoseRecord(
    long TickIndex,
    double SimulatedTimeSeconds,
    bool IsCaptureTick,
    ActorId Actor,
    VehiclePose Pose,
    LaneInterpolationCase Case,
    double SumoX,
    double SumoY,
    double SumoHeadingDegrees,
    SumoVehicleSignals Signals = SumoVehicleSignals.None,
    VehicleLightStateFlags Lamps = VehicleLightStateFlags.None,
    string LaneId = "",
    double LanePositionMetres = 0.0);
