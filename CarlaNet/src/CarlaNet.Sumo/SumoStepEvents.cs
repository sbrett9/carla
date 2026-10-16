// The event lists of Eclipse SUMO's reference TraCI client, tools/traci/_simulation.py --
// getDepartedIDList, getArrivedIDList, getStopStartingVehiclesIDList, getStopEndingVehiclesIDList,
// getParkingStartingVehiclesIDList, getParkingEndingVehiclesIDList, getStartingTeleportIDList and
// getEmergencyStoppingVehiclesIDList.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2011-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

using System.Globalization;

namespace CarlaNet.Sumo;

/// <summary>
/// What SUMO reported happening to its vehicles in one step, list by list, and the clock of that step.
/// </summary>
/// <param name="TimeSeconds">
/// TraCI's clock for the step: the simulated second SUMO reports once it has advanced, read from the
/// same answer as the lists. Every event here is stamped with it and with nothing else.
/// </param>
/// <param name="Departed">The vehicles SUMO inserted.</param>
/// <param name="Arrived">
/// The vehicles SUMO removed, whether they reached their destination or were taken out by SUMO for
/// another reason.
/// </param>
/// <param name="StopsStarted">
/// The vehicles that reached a stop and halted at it -- a stop on the lane and a parking stay alike.
/// </param>
/// <param name="StopsEnded">The vehicles that left a stop.</param>
/// <param name="ParkingStarted">
/// The vehicles that left their lane to park. A parking stop lists its vehicle here and in
/// <see cref="StopsStarted"/> on the same step.
/// </param>
/// <param name="ParkingEnded">
/// The vehicles that came back onto their lane from parking. Measured on SUMO 1.27.0, a parking stop
/// lists its vehicle here one step before it lists it in <see cref="StopsEnded"/>: the vehicle is put
/// back on the lane on one step and resumes its route on the next.
/// </param>
/// <param name="TeleportsStarted">
/// The vehicles SUMO took off the network to move them further along their route. A session refuses a
/// scenario that lets SUMO teleport unless the operator accepted it, so on an accepted run this is the
/// record of each jump, and on a refused one it is empty.
/// </param>
/// <param name="EmergencyStops">
/// The vehicles SUMO stopped dead at the end of their lane because they could not brake in time
/// (<c>MSVehicle::executeMove</c>): a deceleration no vehicle can make, which reaches the imagery and
/// the truth's kinematics alike.
/// </param>
/// <remarks>
/// <para><b>The instant of an event is the bridge's clock, never one of SUMO's own stamps.</b> SUMO
/// does a step's work at the step's opening second and only then advances its clock, so a stamp SUMO
/// keeps for itself -- a vehicle's departure time, a stop's arrival and departure -- is one step before
/// the clock TraCI reports alongside the list that announced it. Measured on SUMO 1.27.0 at a 1 s and a
/// 0.05 s step: a stop listed as started at TraCI's 11.0 s carries an arrival of 10.0 s, and a vehicle
/// listed as departed at 1.0 s a departure of 0.0 s. The frame that renders the event is the one at
/// <see cref="TimeSeconds"/>.</para>
///
/// <para>Each list covers the step just taken and nothing before it. SUMO collects these as its vehicles
/// change state and clears them once every client has moved on to the next step
/// (<c>TraCIServer::processCommands</c>), so a list read on a step is that step's whether it was read by
/// subscription or by a direct get.</para>
/// </remarks>
public sealed record SumoStepEvents(
    double TimeSeconds,
    IReadOnlyList<string> Departed,
    IReadOnlyList<string> Arrived,
    IReadOnlyList<string> StopsStarted,
    IReadOnlyList<string> StopsEnded,
    IReadOnlyList<string> ParkingStarted,
    IReadOnlyList<string> ParkingEnded,
    IReadOnlyList<string> TeleportsStarted,
    IReadOnlyList<string> EmergencyStops)
{
    /// <summary>A step at <paramref name="timeSeconds"/> in which nothing happened to any vehicle.</summary>
    public static SumoStepEvents None(double timeSeconds) =>
        new(timeSeconds, [], [], [], [], [], [], [], []);

    /// <summary>How many entries the lists hold between them.</summary>
    public int Count =>
        Departed.Count + Arrived.Count + StopsStarted.Count + StopsEnded.Count + ParkingStarted.Count
        + ParkingEnded.Count + TeleportsStarted.Count + EmergencyStops.Count;

    /// <summary>The step in one line: its clock, and each list that has anything in it.</summary>
    public override string ToString()
    {
        var lists = new List<string>();
        Add(lists, "departed", Departed);
        Add(lists, "arrived", Arrived);
        Add(lists, "stop started", StopsStarted);
        Add(lists, "stop ended", StopsEnded);
        Add(lists, "parking started", ParkingStarted);
        Add(lists, "parking ended", ParkingEnded);
        Add(lists, "teleport started", TeleportsStarted);
        Add(lists, "emergency stop", EmergencyStops);
        string at = $"t={TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";
        return lists.Count == 0 ? $"{at}: nothing" : $"{at}: {string.Join("; ", lists)}";
    }

    private static void Add(List<string> lists, string name, IReadOnlyList<string> vehicles)
    {
        if (vehicles.Count > 0)
        {
            lists.Add($"{name} {string.Join(", ", vehicles.Select(vehicle => $"'{vehicle}'"))}");
        }
    }
}
