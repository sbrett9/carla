namespace CarlaNet.CoSim;

/// <summary>
/// A co-simulation session that will not start, or will not go on, and the reason.
/// </summary>
/// <remarks>
/// <para>Most conditions this carries are ones the session can only detect before it runs: a clock
/// trio that does not divide, a world that is not ticked by whoever asks it to, a network the world
/// was not built from, a population authority another component already holds. Each one produces a
/// run whose output looks ordinary and is wrong -- imagery stamped with a simulated instant the
/// poses were not computed for, or vehicles drifting a fraction of a step out of line with the
/// clock that captured them. There is no salvage after the fact, so the session refuses rather than
/// starting and warning. The rest are the same kind of condition found while the window runs, and
/// they stop it for the same reason.</para>
///
/// <para>The message names the measured values, because the useful question after a refusal is
/// always "which of the three do I change", and a refusal that names none of them cannot answer
/// it.</para>
///
/// <para><see cref="Stage"/> says how far the session had got, so a caller can map a refusal onto
/// its own outcome without reading the message. The session assigns it as the refusal leaves it;
/// a refusal raised by a check or a declaration used on its own is
/// <see cref="CoSimSessionStage.Validation"/>, because nothing had been started.</para>
/// </remarks>
public class CoSimSessionRefusedException : InvalidOperationException
{
    public CoSimSessionRefusedException(string message) : base(message)
    {
    }

    public CoSimSessionRefusedException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <param name="stage">How far the session had got.</param>
    /// <param name="message">What was wrong, with the measured values.</param>
    /// <param name="inner">The failure underneath it, where there was one.</param>
    public CoSimSessionRefusedException(CoSimSessionStage stage, string message, Exception? inner = null)
        : base(message, inner)
    {
        Stage = stage;
    }

    /// <summary>How far the session had got when it refused.</summary>
    public CoSimSessionStage Stage { get; internal set; } = CoSimSessionStage.Validation;

    /// <summary>
    /// <see cref="Stage"/>'s name -- <c>Validation</c>, <c>Launch</c>, <c>Authority</c>,
    /// <c>PreRoll</c> or <c>Window</c> -- for a caller that compares text rather than the enum.
    /// </summary>
    public string StageName => Stage.ToString();
}
