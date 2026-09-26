namespace CarlaNet.Recording;

/// <summary>
/// The wall-clock instant a recorder stamps each capture with, to the millisecond, strictly increasing
/// from one capture to the next.
/// </summary>
/// <remarks>
/// A capture's files are named by this instant to the millisecond, so two captures stamped in the same
/// millisecond would be written to the same two paths by two encoding workers at once, and one of them
/// would fail with the file in use by the other -- which happens whenever frames arrive in a burst, as
/// they do after any stall. A capture stamped in a millisecond already used is moved to the next one,
/// so no two captures of one recorder share a name and the names still sort in capture order. The
/// instant is the wall clock the capture was taken at and nothing more: the simulation instant a
/// capture shows is its tick, which it carries separately.
/// </remarks>
public sealed class CaptureInstantClock
{
    private long _lastMillisecond = long.MinValue;

    /// <summary>
    /// The instant to stamp a capture taken at <paramref name="utcNow"/> with: that instant truncated to
    /// the millisecond, or one millisecond past the previous capture's where that is later.
    /// </summary>
    public DateTime Next(DateTime utcNow)
    {
        long millisecond = utcNow.Ticks / TimeSpan.TicksPerMillisecond;
        if (millisecond <= _lastMillisecond)
        {
            millisecond = _lastMillisecond + 1;
        }

        _lastMillisecond = millisecond;
        return new DateTime(millisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
