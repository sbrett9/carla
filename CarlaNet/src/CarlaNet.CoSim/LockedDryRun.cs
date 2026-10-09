using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// What a compile lock records of the compiler's SUMO-only run of the scenario (the compiler's check
/// 59, <c>carlacontrol.SumoDryRun</c>): whether it ran; where it did, the SUMO release that ran it, the
/// span it ran over and its counts; where it did not, the reason the compiler recorded.
/// </summary>
/// <remarks>
/// The compiler runs the files it is about to write in SUMO alone, over the scenario's whole span, and
/// refuses a scenario in which a vehicle the supervision plan names never enters the simulation --
/// discarded after waiting <c>max-depart-delay</c> at its entrance, or still waiting when the run ends.
/// <c>compile_scenario.py --skip-dry-run</c> skips that for quick iteration on a draft, and the lock then
/// says so (<c>ran: false</c>) with the reason. A session refuses a scenario whose lock says so, or whose
/// lock has no <c>dry_run</c> block at all -- one written before the compiler ran the check -- unless the
/// run accepts it (<see cref="SumoDriveSessionOptions.AcceptSkippedDryRun"/>): the fault the compile
/// would have found stops a run only once SUMO drops the vehicle, hours of rendering in.
/// </remarks>
/// <param name="Ran">Whether the compiler ran the scenario in SUMO alone before writing it.</param>
/// <param name="Reason">Why it did not, as the compiler recorded it; null where it ran.</param>
/// <param name="SumoRelease">The release of SUMO that ran it: <c>1.27.0</c>.</param>
/// <param name="EndSeconds">The simulated second the run ended at: the scenario's end.</param>
/// <param name="VehiclesLoaded">Vehicles SUMO loaded from the route file.</param>
/// <param name="VehiclesInserted">Vehicles SUMO inserted into the simulation.</param>
/// <param name="VehiclesDiscarded">Vehicles SUMO discarded after they waited <c>max-depart-delay</c>.</param>
/// <param name="VehiclesWaitingAtEnd">Vehicles still waiting to be inserted when the run ended.</param>
/// <param name="PlannedTotal">Vehicles the supervision plan names.</param>
/// <param name="PlannedInserted">Of those, the ones SUMO inserted; the compiler refuses a scenario where it is fewer.</param>
/// <param name="Collisions">Collisions SUMO registered over the run.</param>
public sealed record LockedDryRun(bool Ran, string? Reason, string? SumoRelease, double? EndSeconds,
                                  long? VehiclesLoaded, long? VehiclesInserted, long? VehiclesDiscarded,
                                  long? VehiclesWaitingAtEnd, long? PlannedTotal, long? PlannedInserted,
                                  long? Collisions)
{
    /// <summary>What the lock records, in the report's words.</summary>
    public override string ToString()
    {
        if (!Ran)
        {
            return "not run: " + (string.IsNullOrEmpty(Reason) ? "the lock records no reason" : Reason);
        }

        return $"ran with SUMO {Shown(SumoRelease)} over "
               + (EndSeconds is { } end ? end.ToString("0.###", CultureInfo.InvariantCulture) + " s" : "(not recorded)")
               + $": {Shown(VehiclesLoaded)} vehicles loaded, {Shown(VehiclesInserted)} inserted, "
               + $"{Shown(VehiclesDiscarded)} discarded, {Shown(VehiclesWaitingAtEnd)} waiting at the end; "
               + $"{Shown(PlannedInserted)} of {Shown(PlannedTotal)} planned vehicles inserted; "
               + $"{Shown(Collisions)} collisions";
    }

    private static string Shown(string? value) => string.IsNullOrEmpty(value) ? "(not recorded)" : value;

    private static string Shown(long? value) =>
        value is { } count ? count.ToString(CultureInfo.InvariantCulture) : "(not recorded)";
}
