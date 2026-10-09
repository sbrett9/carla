namespace CarlaNet.CoSim;

/// <summary>
/// Something that follows a run from inside its session: every SUMO frame the session reads, every
/// world frame it renders, and the session's end. Registered on
/// <see cref="SumoDriveSessionOptions.StepObservers"/>.
/// </summary>
/// <remarks>
/// <para><b>The seam a truth writer is built on, so that it does not have to be built into the
/// loop.</b> Each call comes at one fixed point in the session's advance, from the thread that
/// advances it, carrying what the session already holds at that point; nothing is read from SUMO or
/// the world to make it. An observer reads and writes its own records, and nothing it does changes what
/// the session does.</para>
///
/// <para><b>When each is called.</b></para>
/// <list type="bullet">
/// <item><see cref="OnSumoStep"/> once for every SUMO frame the session reads: the frame SUMO was
/// fast-forwarded to and the step of lookahead after it while the session starts, then one per advance.
/// It comes once the frame's population, render set, collisions and insertion queue are settled, so it
/// sees what the report sees, and it is one SUMO step ahead of the frames rendered so far -- the
/// lookahead every pose is interpolated inside.</item>
/// <item><see cref="OnFrameRendered"/> once for every world tick whose frame the session completed: its
/// render set recorded, its sun audited, its poses compared. A tick that stops the run is not
/// completed, and is not reported.</item>
/// <item><see cref="OnSessionEnded"/> once, when the session is disposed -- after a run that stopped as
/// after one that did not -- once the rendered intervals are closed and the report completed, and before
/// SUMO is stopped, so a question put to SUMO then is still answered. A start that is refused has no
/// session to dispose and tells nothing more: its caller holds the refusal.</item>
/// </list>
///
/// <para><b>Who it is for.</b> A world truth track reads every vehicle's state at each SUMO frame and
/// each rendered frame's illumination; a run manifest writes the events, the rendered frames and the
/// end; an interval binder takes a committed onset from an event at the clock it carries, asks SUMO for a
/// vehicle's departure or completed stops when an event says one has changed
/// (<see cref="SumoStepRecord.Vehicles"/>), and closes what is still open at the end, telling a window
/// that closed from a scenario that finished (<see cref="SessionEndRecord.ScenarioFinished"/>).</para>
///
/// <para><b>What an observer must not do.</b> Block: every call is on the tick thread, and under a
/// synchronous world the server waits on it. Keep a live collection past the call: what a record lends
/// from the session -- the vehicles' states, the render set's ids -- is refilled at the next frame, and
/// is to be copied where it is kept. An exception it throws leaves the advance or the start that called
/// it, as one thrown from any of the options' callbacks does; at the session's end it is kept with the
/// other give-back failures, and nothing else is skipped because of it.</para>
/// </remarks>
public interface ISumoStepObserver
{
    /// <summary>A SUMO frame has been read, and the session has made what it makes of it.</summary>
    void OnSumoStep(SumoStepRecord step);

    /// <summary>A world frame has been rendered and completed.</summary>
    void OnFrameRendered(RenderedFrameRecord frame);

    /// <summary>The session is ending.</summary>
    void OnSessionEnded(SessionEndRecord end);
}
