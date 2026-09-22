namespace CarlaNet.CoSim;

/// <summary>
/// A co-simulation session that will not start, and the reason it will not.
/// </summary>
/// <remarks>
/// <para>Every condition this carries is one the session can only detect before it runs: a clock
/// trio that does not divide, a world that is not ticked by whoever asks it to, a network the world
/// was not built from, a population authority another component already holds. Each one produces a
/// run whose output looks ordinary and is wrong -- imagery stamped with a simulated instant the
/// poses were not computed for, or vehicles drifting a fraction of a step out of line with the
/// clock that captured them. There is no salvage after the fact, so the session refuses rather than
/// starting and warning.</para>
///
/// <para>The message names the measured values, because the useful question after a refusal is
/// always "which of the three do I change", and a refusal that names none of them cannot answer
/// it.</para>
/// </remarks>
public sealed class CoSimSessionRefusedException : InvalidOperationException
{
    public CoSimSessionRefusedException(string message) : base(message)
    {
    }

    public CoSimSessionRefusedException(string message, Exception inner) : base(message, inner)
    {
    }
}
