using CarlaNet.Recording;

namespace CarlaNet.CoSim;

/// <summary>
/// One world frame the session rendered and completed, as every observer is told of it.
/// </summary>
/// <param name="Frame">
/// The frame the tick produced: the world's own frame number, or for a session with no world, the
/// session's count of its ticks. What a recorder keys a capture by.
/// </param>
/// <param name="TickIndex">Ticks since the session's first, counting from zero.</param>
/// <param name="SimulatedTimeSeconds">The simulated instant the frame rendered.</param>
/// <param name="IsCaptureTick">Whether a recorder emits a frame on this tick.</param>
/// <param name="InWindow">
/// Whether the frame is the capture window's -- at or after its opening instant -- rather than the
/// prewarm's. A sun frozen at the window's start corresponds to nothing before it.
/// </param>
/// <param name="RenderSet">
/// The bodies the frame drew and the SUMO vehicle each drew, as a recorder lists them beside its
/// capture; null where the session renders no world.
/// </param>
/// <param name="Illumination">
/// What the frame's illumination was declared to be and the sun audit's residual on its tick, as a
/// recorder writes it beside its capture; null where the session renders no world.
/// </param>
/// <remarks>
/// Taken from the session's own per-frame stores as the tick completes, so it is the same render set
/// and the same declaration a recorder asking by this frame is answered with.
/// </remarks>
public sealed record RenderedFrameRecord(
    ulong Frame,
    long TickIndex,
    double SimulatedTimeSeconds,
    bool IsCaptureTick,
    bool InWindow,
    RenderSet? RenderSet,
    IlluminationDeclaration? Illumination)
{
    /// <summary>
    /// The geometric elevation of the sun the world reported on the frame's tick, degrees: the
    /// achieved sun, read from the same snapshot as the <c>_solar</c> block a recorder writes beside
    /// the frame's capture, rather than the one declared for it. Null where the session audits no sun.
    /// </summary>
    public double? SunElevationDegrees { get; init; }

    /// <summary>
    /// The refraction-corrected elevation the world reported on the same tick, degrees: the elevation
    /// the sun's light is rotated by, which the illumination bands are cut from. Null where the session
    /// audits no sun, or the world's reading carries only the geometric one.
    /// </summary>
    public double? SunCorrectedElevationDegrees { get; init; }
}
