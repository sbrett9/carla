namespace CarlaNet.CoSim;

/// <summary>
/// Which side failed when a running session stopped, named so a caller can record it without reading
/// the message or the exception underneath.
/// </summary>
/// <remarks>
/// <para>Every one of these stops the run rather than degrading it. A world that keeps ticking after
/// SUMO has gone renders a timeline nothing simulated; a SUMO that keeps stepping after the world has
/// gone produces truth no frame shows; a frame lit by a sun nothing declared is unrecoverable. Each is
/// a run that cannot produce honest truth, and a short honest run is worth more than a long one with a
/// silently wrong span in it.</para>
///
/// <para><see cref="Code"/>'s text is the one the co-simulation runtime's failure paths use for the
/// run's termination.</para>
/// </remarks>
public enum CoSimStopCause
{
    /// <summary>
    /// Not a failure of either side: a check or a declaration refused before anything ran, or the
    /// refusal was raised outside a session.
    /// </summary>
    None = 0,

    /// <summary>
    /// SUMO can no longer be relied on: it closed the connection, died, stopped answering within the
    /// session's bound, sent an answer that could not be read, or refused a command the session needed.
    /// </summary>
    SumoConnectionLost = 1,

    /// <summary>
    /// The connection to the CARLA server failed, or a call on it went unanswered past the client's
    /// timeout -- a tick cue that never returned among them.
    /// </summary>
    WorldConnectionLost = 2,

    /// <summary>
    /// The CARLA server answered the tick cue and the frame it named never reached the world observer
    /// within the client's timeout.
    /// </summary>
    WorldTickTimeout = 3,

    /// <summary>
    /// The world's sun disagreed with its declaration, was absent from a tick's snapshot after one was
    /// bound, or refused the sun written for a frame.
    /// </summary>
    SolarStateDisagreement = 4,

    /// <summary>
    /// The server offers no blueprint for a body the pool had to spawn, so the catalogue was measured
    /// against another content build.
    /// </summary>
    MissingBlueprint = 5,
}
