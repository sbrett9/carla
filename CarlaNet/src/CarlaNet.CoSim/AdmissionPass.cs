using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// One admission pass of the render set: how many vehicles SUMO had, how many took a place up and gave
/// one up, and -- where an optional limit is in force -- how many the limit left out, taken once per
/// SUMO step as the pass is made.
/// </summary>
/// <remarks>
/// <para>Published as it happens (<see cref="CoSimRunReport.LastAdmissionPass"/>, and to
/// <see cref="SumoDriveSessionOptions.OnAdmissionPass"/>), so a monitor shows the population while the
/// run goes, and a writer can keep every row. It is immutable and replaced whole, so a reader between
/// two advances reads one pass, never half of two; from Python it is one object read off the report
/// and a handful of integers read off it.</para>
///
/// <para>The pass decides the render set for the SUMO frame the session has just read, which is one
/// step ahead of the frame the world last rendered (the lookahead), so
/// <see cref="SimulatedTimeSeconds"/> is that frame's instant. A vehicle SUMO inserted is drawn from
/// that instant on, at the position SUMO first reported, and on no frame before it.</para>
///
/// <para><b>With no limit, the default,</b> every vehicle SUMO has holds a place: <see cref="Eligible"/>
/// and <see cref="Admitted"/> equal <see cref="Population"/> and nothing is shed. <b>Under an optional
/// limit</b> -- a circle, the cameras' footprints, a capacity -- the difference between the population
/// and the admitted is the vehicles the limit left without a body, simulated by SUMO and in no
/// frame and no truth record: <see cref="Population"/> less <see cref="Eligible"/> outside the circle or
/// the cameras' reach, and <see cref="Shed"/> declined for the capacity.</para>
///
/// <para>The limit's figures are set rather than positional, so a pass built from the six counts alone
/// -- as a reader's test builds one -- still builds, and is a pass with no limit.</para>
/// </remarks>
/// <param name="WorldTick">World ticks the session had rendered when the pass was made.</param>
/// <param name="SimulatedTimeSeconds">The SUMO frame the pass decided the render set for.</param>
/// <param name="Population">
/// Vehicles SUMO had in the simulation, every one subscribed. With no limit every one holds a place in
/// the render set; a place is a body only where the vehicle's type names a measured blueprint, and
/// <see cref="CoSimRunReport.VehicleTicksWithNoMeasuredBody"/> counts the difference.
/// </param>
/// <param name="NewlyAdmitted">
/// Vehicles that took up a place at this pass: with no limit, the ones SUMO inserted since the pass
/// before, each drawn from <see cref="SimulatedTimeSeconds"/>, the frame SUMO first reports it in.
/// </param>
/// <param name="Released">Vehicles that gave one up at this pass, for any reason.</param>
/// <param name="TotalAdmissions">Admissions since the session started.</param>
public sealed record AdmissionPass(
    long WorldTick,
    double SimulatedTimeSeconds,
    int Population,
    int NewlyAdmitted,
    int Released,
    long TotalAdmissions)
{
    private readonly int? _eligible;
    private readonly int? _admitted;

    /// <summary>
    /// The vehicles the render-set predicate admitted a place to, and the rendered ones the release lag
    /// held (<see cref="Held"/>). With no limit, the whole population.
    /// </summary>
    public int Eligible
    {
        get => _eligible ?? Population;
        init => _eligible = value;
    }

    /// <summary>
    /// The vehicles holding a place in the render set after the pass: the eligible, up to the capacity.
    /// With no limit, the whole population.
    /// </summary>
    public int Admitted
    {
        get => _admitted ?? Population;
        init => _admitted = value;
    }

    /// <summary>The eligible a capacity declined: <see cref="Eligible"/> less <see cref="Admitted"/>.</summary>
    public int Shed { get; init; }

    /// <summary>
    /// Of the eligible, the rendered vehicles the release lag held after they stopped passing the
    /// predicate; zero under every rule but the cameras'.
    /// </summary>
    public int Held { get; init; }

    /// <summary>The capacity in force, or null for none.</summary>
    public int? Capacity { get; init; }

    /// <summary>
    /// The rule the pass decided by: every vehicle, the circle, or the registered cameras' footprints.
    /// </summary>
    public RenderSetRule Rule { get; init; } = RenderSetRule.Every;

    /// <summary>How many cameras' footprints the pass decided from; zero under any other rule.</summary>
    public int Cameras { get; init; }

    /// <summary>Whether a limit could leave a vehicle out at this pass.</summary>
    public bool Limited { get; init; }

    /// <summary>The pass in the report's words.</summary>
    public override string ToString()
    {
        string at = $"at t={SimulatedTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s: population "
                    + $"{Population}";
        string moved = $"this pass admitted {NewlyAdmitted} and released {Released}";
        if (!Limited)
        {
            return $"{at}, all in the render set; {moved}";
        }

        string by = Rule switch
        {
            RenderSetRule.Cameras => $"by {Cameras} camera footprint(s), {Held} held by the release lag",
            RenderSetRule.Circle => "by the circle",
            _ => "every vehicle",
        };
        return $"{at}, eligible {Eligible}, drawn {Admitted}, shed {Shed}"
               + (Capacity is { } capacity ? $", capacity {capacity}" : string.Empty)
               + $"; {Population - Admitted} without a body; {moved}; {by}";
    }
}
