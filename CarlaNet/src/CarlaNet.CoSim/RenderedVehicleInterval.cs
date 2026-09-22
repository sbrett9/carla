namespace CarlaNet.CoSim;

/// <summary>
/// The span of simulated time one SUMO vehicle held a rendered actor for.
/// </summary>
/// <param name="VehicleId">SUMO's vehicle id.</param>
/// <param name="AdmittedAtSeconds">Simulated second the vehicle entered the render set.</param>
/// <param name="ReleasedAtSeconds">Simulated second it left, or the session's end.</param>
/// <param name="ReleaseReason">Why it left: the policy, the capacity, or SUMO removing it.</param>
/// <remarks>
/// These two instants are what lets anyone later reconcile "SUMO simulated sixty-eight thousand
/// vehicles" against "the collect shows N tracks", and they are the only honest account of a track
/// that starts or stops mid-scene. They are recorded facts about the run rather than visual
/// transitions, and they cost nothing to emit because the bridge computes both anyway.
/// </remarks>
public readonly record struct RenderedVehicleInterval(
    string VehicleId,
    double AdmittedAtSeconds,
    double ReleasedAtSeconds,
    RenderSetReleaseReason ReleaseReason);
