namespace CarlaNet.CoSim;

/// <summary>
/// How far a co-simulation session had got when it refused, named by what it had taken by then.
/// </summary>
/// <remarks>
/// <para>A caller maps a refusal onto its own outcome from this and nothing else. The message says
/// what was wrong; the stage says what the session had started, written and held, which is what the
/// caller's record has to state about the world afterwards. Every refusal gives back everything the
/// session took, whatever its stage, so the stage never means something was left changed.</para>
///
/// <para>The stages follow the session's own start sequence, in order, and then its window.</para>
/// </remarks>
public enum CoSimSessionStage
{
    /// <summary>
    /// Refused on what the session was given, before it started or wrote anything: its declarations,
    /// the world package, the catalogue, the scenario's configuration, network and compile lock, the
    /// SUMO installation and its release, and whether the world package is the world the server has
    /// loaded -- which is read, not written, and whose connection may fail while it is read. SUMO has not
    /// been started, and nothing on the server has been written.
    /// </summary>
    /// <remarks>
    /// The stage of every refusal raised outside a session as well, by a check or a declaration used
    /// on its own: none of them has started anything.
    /// </remarks>
    Validation = 0,

    /// <summary>
    /// Refused after SUMO was started on the scenario and the world's clock and rendering layers were
    /// taken, and before the population lease: SUMO could not load the scenario, the world would not
    /// hold synchronous mode at the delta asked of it, the SUMO step, the world's delta and the capture
    /// rate do not divide, or the connection to the server failed while the clock or the layers were
    /// written. SUMO has been stopped and the world's clock and layers given back, or named on the
    /// refusal where only the unreachable server could hold them.
    /// </summary>
    Launch = 1,

    /// <summary>
    /// Refused because something else holds the world's population, which the refusal names. SUMO has
    /// been stopped and the world's clock and layers given back.
    /// </summary>
    Authority = 2,

    /// <summary>
    /// Refused after the population lease was taken and before the window opened. From
    /// <see cref="SumoDriveSession.Start"/>: SUMO failing or not answering during its fast-forward or the
    /// step of lookahead after it, the world's sun refusing the binding, reading back other than it was
    /// written, or disagreeing with the declaration at the window's opening instant, or the connection to
    /// the server failing while the sun is bound -- and the lease,
    /// the sun, the bodies, the layers, the clock and SUMO have all been given back. From
    /// <see cref="SumoDriveSession.Advance"/>: any of the <see cref="Window"/> refusals raised on a tick
    /// of the prewarm, rendered before the window-open instant -- and the caller disposes the session,
    /// which gives everything back. Either way no frame of the window was rendered.
    /// </summary>
    PreRoll = 3,

    /// <summary>
    /// Raised by <see cref="SumoDriveSession.Advance"/> on a tick at or after the window's opening
    /// instant: the world produced no frame for a tick, the connection to the server failed or left a
    /// call such as the tick cue unanswered, the sun disagreed with its declaration on a tick, was absent
    /// from its snapshot or refused the write for a frame, the server offered no blueprint for a body the
    /// pool needed, or SUMO failed, died or stopped answering mid-run. The frames already rendered were
    /// rendered; the caller disposes the session, which gives back everything it can reach.
    /// </summary>
    Window = 4,
}
