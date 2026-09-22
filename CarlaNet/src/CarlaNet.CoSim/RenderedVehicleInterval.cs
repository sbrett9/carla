using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// The span of simulated time one SUMO vehicle held a rendered actor for.
/// </summary>
/// <param name="VehicleId">SUMO's vehicle id.</param>
/// <param name="Actor">
/// The pooled body that rendered it, or zero where it held none -- a vehicle the render set admitted
/// and the pool had no body left for, or a run with no CARLA attached.
/// </param>
/// <param name="AdmittedAtSeconds">Simulated second the vehicle entered the render set.</param>
/// <param name="ReleasedAtSeconds">Simulated second it left, or the session's end.</param>
/// <param name="ReleaseReason">Why it left: the policy, the capacity, or SUMO removing it.</param>
/// <remarks>
/// <para>These two instants are what lets anyone later reconcile "SUMO simulated sixty-eight
/// thousand vehicles" against "the collect shows N tracks", and they are the only honest account of
/// a track that starts or stops mid-scene. They are recorded facts about the run rather than visual
/// transitions, and they cost nothing to emit because the bridge computes both anyway.</para>
///
/// <para>The actor is on the record for the same reason. A pooled body carries a succession of
/// vehicles over a run, so an actor id on its own names nothing; an actor id with the interval it
/// was lent over names exactly one vehicle, which is what a consumer holding a track in the imagery
/// has to resolve against.</para>
/// </remarks>
public readonly record struct RenderedVehicleInterval(
    string VehicleId,
    ActorId Actor,
    double AdmittedAtSeconds,
    double ReleasedAtSeconds,
    RenderSetReleaseReason ReleaseReason);
