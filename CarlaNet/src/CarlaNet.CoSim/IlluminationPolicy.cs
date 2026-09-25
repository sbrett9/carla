using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// What a session does with the sun across its window: freeze it at the window's opening instant,
/// let the engine carry it forward, freeze it at a declared hour, or leave it alone.
/// </summary>
/// <remarks>
/// <para><b>Declared, never defaulted.</b> A frozen run and a run nobody configured write
/// byte-identical records, so a default here would make absence indistinguishable from intent.
/// <see cref="FreezeAtWindowStart"/> is the recommended value -- a window is meant to be one
/// lighting condition, and a default 1,800 s window at a real-time rate moves the sun through up to
/// 6.7 degrees of elevation -- but it is recommended, not silent.</para>
///
/// <para><b>The sun's date follows one rule.</b> The date the sun is written with advances with the
/// civil date only when the epoch's calendar advances <i>and</i> the policy lets the date move:
/// always under <see cref="IlluminationPolicyKind.Advance"/>, and under a freeze only when
/// <see cref="FreezeDateAdvances"/> says so. Otherwise it stays on the epoch's own date, so a frozen
/// week of windows keeps one seasonal sun geometry. See <see cref="SunDateAdvances"/>.</para>
/// </remarks>
public sealed class IlluminationPolicy
{
    private IlluminationPolicy(IlluminationPolicyKind kind,
                               double rate,
                               TimeSpan? freezeAt,
                               bool freezeDateAdvances,
                               bool requireSun,
                               string? note)
    {
        Kind = kind;
        Rate = rate;
        FreezeAtCivilTimeOfDay = freezeAt;
        FreezeDateAdvances = freezeDateAdvances;
        RequireSun = requireSun;
        Note = note;
    }

    /// <summary>Which of the four policies this is.</summary>
    public IlluminationPolicyKind Kind { get; }

    /// <summary>
    /// Sun-clock seconds per simulated second under <see cref="IlluminationPolicyKind.Advance"/>, and
    /// zero under every other policy, which is what the engine is told for a frozen sun.
    /// </summary>
    /// <remarks>
    /// Per simulated second, not per wall-clock second: the engine advances the clock by the world
    /// tick's delta times the rate, and under synchronous ticking that delta is the fixed one.
    /// </remarks>
    public double Rate { get; }

    /// <summary>The civil time of day the sun is held at under <see cref="IlluminationPolicyKind.FreezeAt"/>.</summary>
    public TimeSpan? FreezeAtCivilTimeOfDay { get; }

    /// <summary>
    /// Under a freeze, whether the sun's date still follows the civil date when the epoch's calendar
    /// advances. False keeps a frozen week of windows on one seasonal sun geometry.
    /// </summary>
    public bool FreezeDateAdvances { get; }

    /// <summary>
    /// Whether a world with no sun refuses the session. True unless declared otherwise; a run under
    /// lighting nobody declared is not a run anything can be concluded from.
    /// </summary>
    public bool RequireSun { get; }

    /// <summary>Why this policy, in one sentence.</summary>
    public string? Note { get; }

    /// <summary>The policy's name as the scenario contract spells it.</summary>
    public string Name => Kind switch
    {
        IlluminationPolicyKind.FreezeAtWindowStart => "freeze_at_window_start",
        IlluminationPolicyKind.Advance => "advance",
        IlluminationPolicyKind.FreezeAt => "freeze_at",
        IlluminationPolicyKind.Ignore => "ignore",
        _ => throw new InvalidOperationException($"Unknown illumination policy {Kind}."),
    };

    /// <summary>Whether the session writes the sun at all.</summary>
    public bool BindsTheSun => Kind != IlluminationPolicyKind.Ignore;

    /// <summary>Whether the engine carries the sun forward with the world tick.</summary>
    public bool Advances => Kind == IlluminationPolicyKind.Advance;

    /// <summary>
    /// Whether every frame is lit by the sun of its own declared civil instant -- true under
    /// <see cref="IlluminationPolicyKind.Advance"/> and <see cref="IlluminationPolicyKind.FreezeAtWindowStart"/>,
    /// false under a declared hour or no binding at all.
    /// </summary>
    public bool HonoursTheEpoch =>
        Kind is IlluminationPolicyKind.Advance or IlluminationPolicyKind.FreezeAtWindowStart;

    /// <summary>
    /// Freeze the sun at the civil instant the window opens. The recommended policy.
    /// </summary>
    public static IlluminationPolicy FreezeAtWindowStart(bool freezeDateAdvances = false,
                                                         bool requireSun = true,
                                                         string? note = null) =>
        new(IlluminationPolicyKind.FreezeAtWindowStart, 0.0, null, freezeDateAdvances, requireSun, note);

    /// <summary>
    /// Start the sun at the civil instant the window opens and let the engine carry it forward.
    /// </summary>
    /// <param name="rate">Sun-clock seconds per simulated second; 1.0 keeps the sun on civil time.</param>
    /// <exception cref="CoSimSessionRefusedException">The rate is not a positive number.</exception>
    public static IlluminationPolicy Advance(double rate, bool requireSun = true, string? note = null)
    {
        if (!double.IsFinite(rate) || rate <= 0.0)
        {
            throw new CoSimSessionRefusedException(
                $"An advancing sun at a rate of {rate} sun-seconds per simulated second is not an "
                + "advancing sun. Declare a positive rate, or freeze it.");
        }

        return new IlluminationPolicy(IlluminationPolicyKind.Advance, rate, null, false, requireSun, note);
    }

    /// <summary>
    /// Hold the sun at a declared civil time of day, whatever instant the window opens at.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">The time is not a time of day.</exception>
    public static IlluminationPolicy FreezeAt(TimeSpan civilTimeOfDay,
                                              bool freezeDateAdvances = false,
                                              bool requireSun = true,
                                              string? note = null)
    {
        if (civilTimeOfDay < TimeSpan.Zero || civilTimeOfDay >= TimeSpan.FromDays(1))
        {
            throw new CoSimSessionRefusedException(
                $"A sun frozen at {civilTimeOfDay} is not frozen at a time of day; it must lie in "
                + "[00:00:00, 24:00:00).");
        }

        return new IlluminationPolicy(IlluminationPolicyKind.FreezeAt, 0.0, civilTimeOfDay,
                                      freezeDateAdvances, requireSun, note);
    }

    /// <summary>
    /// Leave the sun alone. The run's lighting is whatever the world held and is recorded as not
    /// honouring any epoch; the escape hatch for diagnostics and for a world with no sun.
    /// </summary>
    public static IlluminationPolicy Ignore(string? note = null) =>
        new(IlluminationPolicyKind.Ignore, 0.0, null, false, false, note);

    /// <summary>
    /// Whether the sun's date moves with the civil date under this policy and epoch: the epoch's
    /// calendar advances, and the policy advances or lets a frozen sun's date follow it.
    /// </summary>
    public bool SunDateAdvances(SolarEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        return epoch.CalendarAdvances && (Advances || FreezeDateAdvances);
    }

    /// <summary>The policy in one line, as a run report states it.</summary>
    public override string ToString() => Kind switch
    {
        IlluminationPolicyKind.Advance =>
            $"{Name} at {Rate.ToString("0.######", CultureInfo.InvariantCulture)} sun-s per simulated s",
        IlluminationPolicyKind.FreezeAt =>
            $"{Name} {FreezeAtCivilTimeOfDay!.Value.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture)} civil"
            + (FreezeDateAdvances ? ", date follows the calendar" : ", date held"),
        IlluminationPolicyKind.FreezeAtWindowStart =>
            Name + (FreezeDateAdvances ? ", date follows the calendar" : ", date held"),
        _ => Name,
    };
}
