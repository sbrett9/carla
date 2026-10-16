// Ported from Eclipse SUMO's reference TraCI client, tools/traci/_vehicle.py -- the StopData class
// (_vehicle.py:37-71) and _readStopData (:103-127), the reader of getStops.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2011-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

using System.Globalization;

namespace CarlaNet.Sumo;

/// <summary>
/// One stop of one vehicle, as TraCI's <c>getStops</c> reports it: one it has made, or one still ahead
/// of it.
/// </summary>
/// <param name="LaneId">The lane the stop is on.</param>
/// <param name="StartPositionMetres">Where along the lane the stop's extent begins.</param>
/// <param name="EndPositionMetres">Where along the lane the stop's extent ends, which is where the front bumper halts.</param>
/// <param name="StoppingPlaceId">
/// The bus stop, container stop, charging station, parking area or overhead-wire segment the stop is
/// at, or empty for a stop on the lane itself.
/// </param>
/// <param name="Flags">What kind of stop it is.</param>
/// <param name="DurationSeconds">
/// For a stop already made, the duration the route declared, or null where it declared none; for one
/// still ahead or under way, the time SUMO has the vehicle stand there, which once it is under way is
/// what is left of it. A negative duration is SUMO's way of saying a parked vehicle never comes back.
/// </param>
/// <param name="UntilSeconds">The simulated second the route said the stop lasts until, or null where it said none.</param>
/// <param name="IntendedArrivalSeconds">
/// The simulated second the route said the vehicle should arrive at the stop, its <c>arrival</c>
/// attribute, or null where it said none.
/// </param>
/// <param name="ArrivalSeconds">
/// SUMO's own stamp for when the stop began, or null for one not yet reached. One step before the TraCI
/// clock of the step that listed the vehicle as starting it (<see cref="SumoStepEvents"/>).
/// </param>
/// <param name="DepartureSeconds">
/// SUMO's own stamp for when the stop ended, or null for one not yet left. One step before the TraCI clock
/// of the step that listed the vehicle as ending it.
/// </param>
/// <param name="Split">The train a vehicle splits off from at the stop, or empty.</param>
/// <param name="Join">The train it joins at the stop, or empty.</param>
/// <param name="ActType">What the route says the vehicle is doing there, or empty.</param>
/// <param name="TripId">The trip the stop belongs to, or empty.</param>
/// <param name="Line">The line the stop belongs to, or empty.</param>
/// <param name="SpeedMetresPerSecond">
/// For a waypoint, the speed the vehicle passes it at rather than halting; zero for a stop it halts at.
/// </param>
/// <remarks>
/// <para><b>Declared, committed and SUMO's stamp of the commitment, side by side.</b> SUMO keeps what
/// the route declared (<see cref="DurationSeconds"/>, <see cref="UntilSeconds"/>,
/// <see cref="IntendedArrivalSeconds"/>) apart from what happened (<see cref="ArrivalSeconds"/>,
/// <see cref="DepartureSeconds"/>). A stop declared with a duration declares a length, not an instant,
/// so its declared start is legitimately absent. What happened is SUMO's own stamp, which is not the
/// instant a bridge records: the instant is TraCI's clock on the step the event was listed in, and the
/// stamp here is one step before it.</para>
///
/// <para>SUMO reports every time in seconds and writes its invalid value, <c>-2^30</c>, for one that is
/// not set; those are null here, so an unset time is never read as a very early one.</para>
/// </remarks>
public sealed record SumoStop(
    string LaneId,
    double StartPositionMetres,
    double EndPositionMetres,
    string StoppingPlaceId,
    SumoStopFlags Flags,
    double? DurationSeconds,
    double? UntilSeconds,
    double? IntendedArrivalSeconds,
    double? ArrivalSeconds,
    double? DepartureSeconds,
    string Split,
    string Join,
    string ActType,
    string TripId,
    string Line,
    double SpeedMetresPerSecond)
{
    /// <summary>Whether the vehicle left its lane for the stay.</summary>
    public bool IsParking => (Flags & SumoStopFlags.Parking) != 0;

    /// <summary>A time SUMO reported, or null where it wrote its invalid value.</summary>
    internal static double? Seconds(double reported) =>
        reported == TraCIConstants.INVALID_DOUBLE_VALUE ? null : reported;

    /// <summary>The stop in one line: where, what kind, and the times SUMO holds for it.</summary>
    public override string ToString()
    {
        var times = new List<string>();
        Add(times, "duration", DurationSeconds);
        Add(times, "until", UntilSeconds);
        Add(times, "intended arrival", IntendedArrivalSeconds);
        Add(times, "arrival", ArrivalSeconds);
        Add(times, "departure", DepartureSeconds);
        return $"{LaneId} at {EndPositionMetres.ToString("0.##", CultureInfo.InvariantCulture)} m"
               + (StoppingPlaceId.Length > 0 ? $" ({StoppingPlaceId})" : string.Empty)
               + (Flags != SumoStopFlags.None ? $", {Flags}" : string.Empty)
               + (times.Count > 0 ? $"; {string.Join(", ", times)}" : string.Empty);
    }

    private static void Add(List<string> times, string name, double? seconds)
    {
        if (seconds is { } value)
        {
            times.Add($"{name} {value.ToString("0.###", CultureInfo.InvariantCulture)} s");
        }
    }
}
