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
/// <para><b>A frozen sun is written to the whole second.</b> The engine evaluates the sun at whole
/// seconds of its clock and rounds anything finer, except that a clock in the last half-second of a
/// minute rounds up to sixty and loses the minute it should have carried: a sun set to 07:00:59.6
/// is computed for 07:00:00. Writing the frozen clock already rounded means the engine evaluates
/// exactly what was written. An advancing sun is written exactly, because the engine moves it off
/// any whole second on the first tick regardless.</para>
/// </remarks>
public sealed class DeclaredSun
{
    /// <param name="epoch">What simulated second zero means in civil time.</param>
    /// <param name="policy">What the window does with the sun.</param>
    /// <param name="windowOpensAtSimulatedSecond">
    /// The simulated instant of the first frame the session renders: where SUMO was fast-forwarded
    /// to. With one SUMO process per window, that is the instant the window opens.
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

    /// <summary>The simulated instant of the window's first rendered frame.</summary>
    public double WindowOpensAtSimulatedSecond { get; }

    /// <summary>The civil instant of the window's first rendered frame.</summary>
    public DateTimeOffset WindowOpenCivil { get; }

    /// <summary>
    /// The date and clock the sun is written with at the window's opening, in the epoch's zone.
    /// </summary>
    public DateTime SunAtWindowOpen { get; }

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
