using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// One comparison of the sun a world reported against the sun the session declared.
/// </summary>
/// <param name="TickIndex">
/// The session tick whose world-observer snapshot was read, or <see langword="null"/> for the
/// on-demand read-back taken when the window opened, before any tick.
/// </param>
/// <param name="SimulatedSeconds">The simulated instant the world rendered.</param>
/// <param name="DeclaredCivil">That instant in civil time, from the epoch.</param>
/// <param name="DeclaredSun">
/// The date and clock the sun was declared to hold for it, in the epoch's zone. Equal to the civil
/// instant under the policies that honour the epoch; the window's opening instant under a freeze;
/// a declared hour under <c>freeze_at</c>.
/// </param>
/// <param name="Observed">What the world reported.</param>
/// <param name="Modelled">
/// Where the engine's own algorithm puts the sun at <paramref name="DeclaredSun"/>, evaluated at the
/// instant itself rather than through the engine's clock decomposition.
/// </param>
/// <param name="ClockResidualSeconds">
/// The instant the world's sun holds -- its date and clock -- minus the declared one, in seconds.
/// Signed: an advancing sun whose controller ticks before the snapshot is taken reads a tick ahead.
/// </param>
/// <param name="AngleResidualDegrees">
/// The angle between the direction the world's sun reported and the declared sun's direction, from
/// the geometric elevation and the azimuth.
/// </param>
/// <param name="CorrectedResidualDegrees">
/// The reported refraction-corrected elevation minus the declared one, where the reading carried it.
/// </param>
public sealed record SolarAuditSample(
    long? TickIndex,
    double SimulatedSeconds,
    DateTimeOffset DeclaredCivil,
    DateTime DeclaredSun,
    SolarReading Observed,
    SunPosition Modelled,
    double ClockResidualSeconds,
    double AngleResidualDegrees,
    double? CorrectedResidualDegrees)
{
    /// <summary>Where it was taken, as a report names it.</summary>
    public string Where => TickIndex is { } tick
        ? $"tick {tick} ({SimulatedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s)"
        : "window open";
}
