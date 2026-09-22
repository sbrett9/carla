namespace CarlaNet.CoSim;

/// <summary>What a ghost session is pointed at, and what it is allowed to do.</summary>
/// <param name="ScenarioPath">The scenario's SUMO configuration.</param>
/// <param name="WorldPackagePath">
/// The world package: the ground surface the poses are seated on, and the road network they are
/// interpolated along.
/// </param>
/// <param name="CataloguePath">The measured vehicle catalogue.</param>
/// <param name="WorldKey">
/// What identifies the world to every component that could claim its population -- a server address
/// and the map it has loaded.
/// </param>
/// <param name="RenderSet">Which vehicles hold a place in the render set, and how many may.</param>
/// <remarks>
/// The five that decide what a session <i>is</i> are constructor parameters and the rest are
/// settable. A session is orchestrated from Python, where an object initialiser is not expressible
/// and an init-only property cannot be written at all, so an options object that can only be built
/// with one is an options object the orchestrator cannot build.
/// </remarks>
public sealed record GhostSessionOptions(
    string ScenarioPath,
    string WorldPackagePath,
    string CataloguePath,
    string WorldKey,
    IRenderSetPolicy RenderSet)
{
    /// <summary>The CARLA world's fixed delta.</summary>
    public double WorldDeltaSeconds { get; set; } = 0.05;

    /// <summary>Frames per simulated second a recorder would emit.</summary>
    public double CaptureRateHz { get; set; } = 2.0;

    /// <summary>
    /// Whether the CARLA world advances only on a tick cue. False refuses the session, and the
    /// refusal is not a formality: measured on this fork, a camera spawned into an asynchronous
    /// world delivers no frames at all.
    /// </summary>
    public bool WorldIsSynchronous { get; set; } = true;

    /// <summary>
    /// Advance the CARLA world by one tick, answering false where the tick did not produce a frame.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a client, because the only thing this stage needs from CARLA is the
    /// advance of the world clock: the poses are computed from the world package and the catalogue,
    /// and none of them is applied. A ghost run with no CARLA at all supplies one that counts.
    /// </remarks>
    public Func<bool>? TickWorld { get; set; }

    /// <summary>Who to name if something else has already claimed the world's population.</summary>
    public string Holder { get; set; } = "CarlaNet.CoSim ghost session";

    /// <summary>
    /// Height of the actor origin above the contact surface per blueprint, where it has been
    /// measured by settling a body on level ground rather than taken from its bounding box.
    /// </summary>
    public IReadOnlyDictionary<string, double>? MeasuredSeatHeights { get; set; }

    /// <summary>
    /// A SUMO step length to force, overriding what the scenario authored.
    /// </summary>
    /// <remarks>
    /// Behaviour-changing, and recorded in the report for that reason. Measured on the shipped port
    /// scenario: moving from a one-second step to a tenth left the demand identical -- same
    /// insertions, same routes -- and cut mean time loss per vehicle by 62%, which is most of what
    /// the run is capturing truth about.
    /// </remarks>
    public double? SumoStepOverrideSeconds { get; set; }

    /// <summary>Simulated second to fast-forward SUMO to before the first world tick.</summary>
    public double WarmUpToSimulatedSecond { get; set; }

    /// <summary>Where each computed pose goes. The ghost's whole output.</summary>
    public Action<GhostPoseRecord>? OnPose { get; set; }

    /// <summary>Where a completed render-set interval goes.</summary>
    public Action<RenderedVehicleInterval>? OnRelease { get; set; }

    /// <summary>Where SUMO's own console output goes.</summary>
    public Action<string>? SumoOutput { get; set; }
}
