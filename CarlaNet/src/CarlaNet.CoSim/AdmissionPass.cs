using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// One admission pass of the render set: how many vehicles SUMO had -- every one of which holds a
/// place in it -- and how many took a place up and gave one up, taken once per SUMO step as the pass
/// is made.
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
/// <see cref="SimulatedTimeSeconds"/> is that frame's instant.</para>
/// </remarks>
/// <param name="WorldTick">World ticks the session had rendered when the pass was made.</param>
/// <param name="SimulatedTimeSeconds">The SUMO frame the pass decided the render set for.</param>
/// <param name="Population">
/// Vehicles SUMO had in the simulation, every one subscribed and every one holding a place in the
/// render set. A place is a body only where the vehicle's type names a measured blueprint;
/// <see cref="CoSimRunReport.VehicleTicksWithNoMeasuredBody"/> counts the difference.
/// </param>
/// <param name="NewlyAdmitted">
/// Vehicles that took up a place at this pass: the ones SUMO inserted since the pass before.
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
    /// <summary>The pass in the report's words.</summary>
    public override string ToString() =>
        $"at t={SimulatedTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s: population "
        + $"{Population}, all in the render set; this pass admitted {NewlyAdmitted} and released "
        + $"{Released}";
}
