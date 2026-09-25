namespace CarlaNet.CoSim;

/// <summary>
/// The sun a world reported is not the sun the session declared, so the run stops.
/// </summary>
/// <remarks>
/// Never a warning and never a correction. Every frame after the disagreement would carry a sun
/// nothing declared -- an inherited one, a clock another client moved, a wrapped date -- and a frame
/// rendered under the wrong sun is not recoverable by any later process. Rewriting the sun instead
/// would hide which of those it was.
/// </remarks>
public sealed class SolarAuditFailedException : CoSimSessionRefusedException
{
    public SolarAuditFailedException(string message, SolarAuditSample? sample) : base(message)
    {
        Sample = sample;
    }

    /// <summary>The comparison that failed, or null where there was no sun to compare.</summary>
    public SolarAuditSample? Sample { get; }
}
