namespace CarlaNet.CoSim;

/// <summary>
/// What a render-set policy is told at the start of each admission pass: the session's state and the
/// cameras it has.
/// </summary>
/// <param name="SimulatedTimeSeconds">
/// The SUMO frame the pass decides the render set for: one step ahead of the last rendered frame.
/// </param>
/// <param name="SumoStepSeconds">
/// SUMO's step, which is how long the render set the pass decides is held, and so how far a vehicle or
/// a camera can move before the next pass looks again.
/// </param>
/// <param name="Seed">
/// The scenario's SUMO seed, which a ranking under a capacity is drawn from: two runs of one seed see
/// the same vehicles in the same states, and rank them the same.
/// </param>
/// <param name="LongestBodyMetres">
/// The longest body the catalogue measures: the length a camera's range cap is computed for, so that no
/// body the session can render covers the threshold's pixels beyond it.
/// </param>
/// <param name="BodyReachMetres">
/// The farthest any measured body reaches from the point SUMO reports a vehicle at, the centre of its
/// front bumper. A vehicle whose reported point is farther than this from a footprint has no part of its
/// body in it.
/// </param>
/// <param name="Cameras">
/// Every registered camera whose pose the last rendered frame's snapshot holds, in actor order. Empty
/// where none is registered, or where the session has no world to read one from.
/// </param>
/// <param name="GroundHeight">
/// The height of the world's ground surface above the georeference origin -- CARLA-local z -- under a
/// CARLA-frame position, or null outside the world's grid.
/// </param>
/// <remarks>
/// The camera poses are the last rendered frame's and the vehicles' the frame one step ahead, because
/// that is what the session holds when it decides: the render set a pass decides is drawn from that
/// last frame up to the frame the pass read.
/// </remarks>
public sealed record RenderSetPass(
    double SimulatedTimeSeconds,
    double SumoStepSeconds,
    long Seed,
    double LongestBodyMetres,
    double BodyReachMetres,
    IReadOnlyList<CameraView> Cameras,
    Func<double, double, double?> GroundHeight);
