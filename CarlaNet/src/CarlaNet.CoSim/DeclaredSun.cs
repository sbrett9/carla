namespace CarlaNet.CoSim;

/// <summary>
/// The sun a window declares: the instant it is bound to when the window opens, and the instant it
/// should read at any simulated second after that.
/// </summary>
/// <remarks>
/// <para>Both the write and the audit's expectation come from here. Getting them from one function
/// is what makes the audit a check rather than a tautology: its input is the epoch and the policy,
/// and its output is compared against what the engine actually did, never against what the sun
/// happens to hold.</para>
///
/// <para><b>An instant the sun holds is a date and a clock in the declared zone.</b> It is kept as a
/// <see cref="DateTime"/> of unspecified kind, because that is what the engine holds -- a
/// year, month and day and a clock in hours -- and the zone is the epoch's offset, written beside
/// it.</para>
///
/// <para><b>Every clock is written a millisecond past the whole second nearest its declared
/// instant.</b> The engine evaluates its sun at whole seconds, decomposing the clock by truncating the
/// minute and rounding the second, and it does not carry: when the seconds round up to sixty the minute
/// they belong to is lost. That happens in the last half-second of every minute -- a sun set to
/// 07:00:59.6 is computed for 07:00:00 -- and, because a whole minute is rarely exact in binary, on the
/// whole minute itself: measured by replaying the engine's arithmetic over every whole second of a day,
/// 623 of the 1,440 whole-minute clocks are evaluated as the minute before, 01:01:00 as 01:00:00.
/// Written one millisecond past the second, every one of the 86,400 decomposes as the second
/// declared. The millisecond is visible in the recorded clock and nowhere else.</para>
///
/// <para><b>Which whole second.</b> The clock written for a frame is <c>S + 1 ms</c>, and the audit
/// compares it against the frame's declared instant <c>d</c> within half a second. That holds exactly
/// when <c>S</c> is the whole second nearest <c>d - 1 ms</c>, which <see cref="WrittenAt"/> takes:
/// the residual <c>S + 1 ms - d</c> then lies in (-0.5 s, +0.5 s], at any rate. It is also the best
/// sun the engine can render, since it renders <c>S</c> and <c>S</c> is within half a second of
/// <c>d</c>. A frozen sun is declared to the whole second, so its written clock is that second plus
/// the millisecond. An advancing sun is declared to the tick, and the session writes it for every
/// frame (<see cref="SolarLease.WriteForFrame"/>) with the engine's own advance off, because a clock
/// the engine carries forward passes through the lost half-second of every minute.</para>
///
/// <para><b>The date follows the declared instant.</b> Under a policy whose date advances, a frame
/// past civil midnight is written on the next date. Under a held date the declared instant wraps onto
/// the epoch's date at midnight, and the last half-second before it is written as the following
/// midnight -- the whole second nearest the declared instant, which the engine can hold only on the
/// following date -- after which the written date returns to the held one.</para>
/// </remarks>
public sealed class DeclaredSun
{
    /// <param name="epoch">What simulated second zero means in civil time.</param>
    /// <param name="policy">What the window does with the sun.</param>
    /// <param name="windowOpensAtSimulatedSecond">
    /// The simulated instant the window opens: its first captured frame. A session that prewarms
    /// renders from earlier than this, and every prewarm frame is declared from here like any other
    /// -- under a freeze, lit by this instant's sun; under an advance, by its own instant's.
    /// </param>
    public DeclaredSun(SolarEpoch epoch, IlluminationPolicy policy, double windowOpensAtSimulatedSecond)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.BindsTheSun)
        {
            throw new ArgumentException(
                $"Under the '{policy.Name}' policy the session declares no sun.", nameof(policy));
        }

        Epoch = epoch;
        Policy = policy;
        WindowOpensAtSimulatedSecond = windowOpensAtSimulatedSecond;
        WindowOpenCivil = epoch.CivilInstantAt(windowOpensAtSimulatedSecond);
        SunAtWindowOpen = BoundInstant();
    }

    /// <summary>What simulated second zero means in civil time.</summary>
    public SolarEpoch Epoch { get; }

    /// <summary>What the window does with the sun.</summary>
    public IlluminationPolicy Policy { get; }

    /// <summary>The simulated instant the window opens: its first captured frame.</summary>
    public double WindowOpensAtSimulatedSecond { get; }

    /// <summary>The civil instant the window opens.</summary>
    public DateTimeOffset WindowOpenCivil { get; }

    /// <summary>
    /// How far past a whole second every clock is written, so the engine's decomposition falls on
    /// that second.
    /// </summary>
    public static readonly TimeSpan ClockLead = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Whether the engine's own time-of-day advance runs: never, under any policy. A frozen sun has
    /// nothing to move it, and an advancing one is written by the session for every frame.
    /// </summary>
    public const bool EngineAdvances = false;

    /// <summary>The rate the engine's own advance is set to: zero, beside <see cref="EngineAdvances"/>.</summary>
    public const double EngineRate = 0.0;

    /// <summary>
    /// The date and clock the sun is declared to hold at the window's opening, in the epoch's zone.
    /// </summary>
    public DateTime SunAtWindowOpen { get; }

    /// <summary>The date and clock written for the window's first frame.</summary>
    public DateTime WrittenAtWindowOpen => WrittenAt(WindowOpensAtSimulatedSecond);

    /// <summary>The clock written for the window's first frame, in hours.</summary>
    public double WrittenClockHours => WrittenAtWindowOpen.TimeOfDay.TotalHours;

    /// <summary>The zone the sun's clock is written in: the epoch's declared offset, in hours.</summary>
    public double TimeZoneHours => Epoch.UtcOffsetHours;

    /// <summary>
    /// The date and clock the sun should hold when the world renders a simulated instant.
    /// </summary>
    /// <remarks>
    /// Constant under a freeze. Under <see cref="IlluminationPolicyKind.Advance"/> it is the opening
    /// instant carried forward by the elapsed simulated time times the rate -- and when the policy
    /// holds the date, the clock wraps at midnight onto the same date, which is what a held date
    /// means.
    /// </remarks>
    public DateTime SunAt(double simulatedSeconds)
    {
        if (!Policy.Advances)
        {
            return SunAtWindowOpen;
        }

        double sunSeconds = (simulatedSeconds - WindowOpensAtSimulatedSecond) * Policy.Rate;
        DateTime carried = SunAtWindowOpen.AddTicks((long)Math.Round(sunSeconds * TimeSpan.TicksPerSecond));
        return Policy.SunDateAdvances(Epoch)
            ? carried
            : SunAtWindowOpen.Date + carried.TimeOfDay;
    }

    /// <summary>
    /// The date and clock the session writes for the frame the world renders at a simulated instant:
    /// the whole second nearest the declared instant less <see cref="ClockLead"/>, plus
    /// <see cref="ClockLead"/>.
    /// </summary>
    /// <remarks>
    /// Its clock sits within half a second of <see cref="SunAt"/>'s at every instant and every rate,
    /// and it decomposes in the engine as the whole second it was written past.
    /// </remarks>
    public DateTime WrittenAt(double simulatedSeconds)
    {
        DateTime declared = SunAt(simulatedSeconds);
        return new DateTime(RoundToWholeSecond((declared - ClockLead).Ticks), DateTimeKind.Unspecified)
               + ClockLead;
    }

    private DateTime BoundInstant()
    {
        if (Policy.Kind == IlluminationPolicyKind.FreezeAt)
        {
            DateTime day = Policy.SunDateAdvances(Epoch) ? WindowOpenCivil.Date : Epoch.CivilDateTime.Date;
            return DateTime.SpecifyKind(day + Policy.FreezeAtCivilTimeOfDay!.Value, DateTimeKind.Unspecified);
        }

        DateTime civil = Policy.Advances
            ? WindowOpenCivil.DateTime
            : new DateTime(RoundToWholeSecond(WindowOpenCivil.DateTime.Ticks), DateTimeKind.Unspecified);
        DateTime date = Policy.SunDateAdvances(Epoch) ? civil.Date : Epoch.CivilDateTime.Date;
        return DateTime.SpecifyKind(date + civil.TimeOfDay, DateTimeKind.Unspecified);
    }

    private static long RoundToWholeSecond(long ticks)
    {
        long whole = ticks / TimeSpan.TicksPerSecond;
        if (ticks % TimeSpan.TicksPerSecond >= TimeSpan.TicksPerSecond / 2)
        {
            whole++;
        }

        return whole * TimeSpan.TicksPerSecond;
    }
}
