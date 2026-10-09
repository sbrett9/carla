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
    CoSimRunStop? Stopped)
{
    /// <summary>
    /// The vehicles SUMO had removed at the last SUMO frame read, released by the session's end because the
    /// frame of their last step, which would have drawn them, was never rendered: each with the body that
    /// drew it, its own reason, and ending at the instant of that frame, the first that did not draw it. In
    /// the order of their ids. The other vehicles still in the render set are released by the end too, and
    /// are in none of these.
    /// </summary>
    public IReadOnlyList<RenderedVehicleInterval> Released { get; init; } = [];
}
