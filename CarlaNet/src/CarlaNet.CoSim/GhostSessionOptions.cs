using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>What a ghost session is pointed at, and what it is allowed to do.</summary>
public sealed record GhostSessionOptions
{
    /// <summary>The scenario's SUMO configuration.</summary>
    public required string ScenarioPath { get; init; }

    /// <summary>
    /// The world package: the ground surface the poses are seated on, and the road network they are
    /// interpolated along.
    /// </summary>
    public required string WorldPackagePath { get; init; }

    /// <summary>The measured vehicle catalogue.</summary>
    public required string CataloguePath { get; init; }

    /// <summary>Which vehicles hold a place in the render set, and how many may.</summary>
    public required IRenderSetPolicy RenderSet { get; init; }

    /// <summary>The CARLA world's fixed delta.</summary>
    public double WorldDeltaSeconds { get; init; } = 0.05;

    /// <summary>Frames per simulated second a recorder would emit.</summary>
    public double CaptureRateHz { get; init; } = 2.0;

    /// <summary>
    /// Whether the CARLA world advances only on a tick cue. False refuses the session, and the
    /// refusal is not a formality: measured on this fork, a camera spawned into an asynchronous
    /// world delivers no frames at all.
    /// </summary>
    public bool WorldIsSynchronous { get; init; } = true;

    /// <summary>
    /// Advance the CARLA world by one tick, answering false where the tick did not produce a frame.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a client, because the only thing this stage needs from CARLA is the
    /// advance of the world clock: the poses are computed from the world package and the catalogue,
    /// and none of them is applied. A ghost run with no CARLA at all supplies one that counts.
    /// </remarks>
    public Func<bool>? TickWorld { get; init; }

    /// <summary>Who to name if something else has already claimed the world's population.</summary>
    public string Holder { get; init; } = "CarlaNet.CoSim ghost session";

    /// <summary>
    /// What identifies the world to every component that could claim it -- a server address and the
    /// map it has loaded.
    /// </summary>
    public required string WorldKey { get; init; }

    /// <summary>
    /// Height of the actor origin above the contact surface per blueprint, where it has been
    /// measured by settling a body on level ground rather than taken from its bounding box.
    /// </summary>
    public IReadOnlyDictionary<string, double>? MeasuredSeatHeights { get; init; }

    /// <summary>
    /// A SUMO step length to force, overriding what the scenario authored.
    /// </summary>
    /// <remarks>
    /// Behaviour-changing, and recorded in the report for that reason. Measured on the shipped port
    /// scenario: moving from a one-second step to a tenth left the demand identical -- same
    /// insertions, same routes -- and cut mean time loss per vehicle by 62%, which is most of what
    /// the run is capturing truth about.
    /// </remarks>
    public double? SumoStepOverrideSeconds { get; init; }

    /// <summary>Simulated second to fast-forward SUMO to before the first world tick.</summary>
    public double WarmUpToSimulatedSecond { get; init; }

    /// <summary>Where each computed pose goes. The ghost's whole output.</summary>
    public Action<GhostPoseRecord>? OnPose { get; init; }

    /// <summary>Where a completed render-set interval goes.</summary>
    public Action<RenderedVehicleInterval>? OnRelease { get; init; }

    /// <summary>Where SUMO's own console output goes.</summary>
    public Action<string>? SumoOutput { get; init; }
}
