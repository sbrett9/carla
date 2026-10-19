using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// How a running session stopped when it refused to go on: the stage, which side failed, the last
/// frame whose truth still holds, and what was wrong.
/// </summary>
/// <param name="Stage">
/// <see cref="CoSimSessionStage.PreRoll"/> for a tick of the prewarm, <see cref="CoSimSessionStage.Window"/>
/// from the window's opening on.
/// </param>
/// <param name="Cause">Which side failed, or <see cref="CoSimStopCause.None"/>.</param>
/// <param name="LastCompleteSeconds">
/// The simulated instant of the last tick the session completed -- written, rendered, observed and
/// audited -- or <see langword="null"/> where it completed none. A frame after it is either one the
/// world never produced or one the session could not vouch for, such as a frame whose sun disagreed.
/// </param>
/// <param name="CompleteTicks">How many ticks the session completed before it stopped.</param>
/// <param name="Message">The refusal's message, for a person.</param>
/// <remarks>
/// The session's own record of the termination a truth record closes with. The step record that
/// would carry it is not built, so the report carries it in the meantime, beside the counts a run
/// with a clean end has.
/// </remarks>
public sealed record CoSimRunStop(
    CoSimSessionStage Stage,
    CoSimStopCause Cause,
    double? LastCompleteSeconds,
    long CompleteTicks,
    string Message)
{
    /// <summary>The stop in one line, in the report's words.</summary>
    public override string ToString() =>
        $"{Cause.Code()} at {Stage}; last complete frame "
        + (LastCompleteSeconds is { } last
            ? $"t={last.ToString("0.###", CultureInfo.InvariantCulture)} s, tick {CompleteTicks - 1}"
            : "none")
        + $": {Message}";
}
