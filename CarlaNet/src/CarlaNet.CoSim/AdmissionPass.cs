using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// One admission pass of the render set: how many vehicles SUMO had, how many the policy admitted a
/// place to, how many were rendered and how many the capacity shed -- the shedding ledger's row, taken
/// once per SUMO step as the pass is made.
/// </summary>
/// <remarks>
/// <para>Published as it happens (<see cref="CoSimRunReport.LastAdmissionPass"/>, and to
/// <see cref="SumoDriveSessionOptions.OnAdmissionPass"/>), so a monitor shows the population, the
/// eligible, the admitted and the shed while the run goes, and a writer can keep every row. It is
/// immutable and replaced whole, so a reader between two advances reads one pass, never half of two;
/// from Python it is one object read off the report and a handful of integers read off it.</para>
///
/// <para>The pass decides the render set for the SUMO frame the session has just read, which is one
/// step ahead of the frame the world last rendered (the lookahead), so
/// <see cref="SimulatedTimeSeconds"/> is that frame's instant.</para>
/// </remarks>
/// <param name="WorldTick">World ticks the session had rendered when the pass was made.</param>
/// <param name="SimulatedTimeSeconds">The SUMO frame the pass decided the render set for.</param>
/// <param name="Population">
/// Vehicles SUMO had in the simulation: every one carries the position subscription the render set is
/// decided from.
/// </param>
/// <param name="Subscribed">Of those, the ones inside the subscription margin, delivering full state.</param>
/// <param name="Eligible">
/// Of those, the ones the render-set predicate -- the circle, or a camera's footprint with its margin --
/// admitted a place to, and the rendered ones the release lag held (<see cref="Held"/>).
/// </param>
/// <param name="Admitted">
/// The vehicles holding a place in the render set after the pass: the eligible, up to the capacity.
/// A place in the render set is a body only where the vehicle's type names a measured blueprint and
/// the pool has one to lend; <see cref="CoSimRunReport.VehicleTicksWithNoMeasuredBody"/> and
/// <see cref="CoSimRunReport.PoseDeclinesForNoBody"/> count the difference.
/// </param>
/// <param name="Shed">The eligible the capacity declined: <see cref="Eligible"/> less <see cref="Admitted"/>.</param>
/// <param name="Capacity">The render-set capacity in force.</param>
/// <param name="NewlyAdmitted">Vehicles that took up a place at this pass.</param>
/// <param name="Released">Vehicles that gave one up at this pass, for any reason.</param>
/// <param name="TotalAdmissions">Admissions since the session started; a vehicle admitted again counts again.</param>
/// <param name="TotalCapacityDeclines">Vehicles shed since the session started, counted per pass.</param>
public sealed record AdmissionPass(
    long WorldTick,
    double SimulatedTimeSeconds,
    int Population,
    int Subscribed,
    int Eligible,
    int Admitted,
    int Shed,
    int Capacity,
    int NewlyAdmitted,
    int Released,
    long TotalAdmissions,
    long TotalCapacityDeclines)
{
    /// <summary>
    /// The rule the pass decided by: the registered cameras' footprints, or the circle where no camera
    /// was registered or the session runs the circle alone.
    /// </summary>
    /// <remarks>
    /// Set rather than positional, as are <see cref="Cameras"/> and <see cref="Held"/>, so a pass built
    /// from the twelve counts alone -- as a reader's test builds one -- still builds, and is a circle's.
    /// </remarks>
    public RenderSetRule Rule { get; init; } = RenderSetRule.Circle;

    /// <summary>How many cameras' footprints the pass decided from; zero under the circle.</summary>
    public int Cameras { get; init; }

    /// <summary>
    /// Of the eligible, the rendered vehicles the release lag held after they stopped passing the
    /// predicate; always zero under the circle, which has no lag.
    /// </summary>
    public int Held { get; init; }

    /// <summary>The pass in the report's words.</summary>
    public override string ToString() =>
        $"at t={SimulatedTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s: population "
        + $"{Population}, subscribed {Subscribed}, eligible {Eligible}, admitted {Admitted}, shed {Shed}, "
        + $"cap {Capacity}; this pass admitted {NewlyAdmitted} and released {Released}; "
        + (Rule == RenderSetRule.Cameras
            ? $"by {Cameras} camera footprint(s), {Held} held by the release lag"
            : "by the circle");
}
