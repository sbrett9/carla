namespace CarlaNet.CoSim;

/// <summary>
/// How a session ended, as every observer is told of it.
/// </summary>
/// <param name="LastFrameSeconds">
/// TraCI's clock for the last SUMO frame the session read, one step ahead of the last frame rendered.
/// </param>
/// <param name="LastRenderedSeconds">
/// The simulated instant of the last frame whose truth holds, or null where none was rendered.
/// </param>
/// <param name="LastRenderedFrame">That frame's number, or null.</param>
/// <param name="ScenarioFinished">
/// Whether SUMO had nothing left to simulate: the last advance answered false. Otherwise the caller
/// stopped advancing -- the window it wanted had closed -- or the run stopped (<paramref name="Stopped"/>).
/// </param>
/// <param name="Stopped">
/// How the run stopped, where SUMO, the CARLA server or the world's sun failed part-way; null for a run
/// that did not stop that way.
/// </param>
public sealed record SessionEndRecord(
    double LastFrameSeconds,
    double? LastRenderedSeconds,
    ulong? LastRenderedFrame,
    bool ScenarioFinished,
    CoSimRunStop? Stopped);
