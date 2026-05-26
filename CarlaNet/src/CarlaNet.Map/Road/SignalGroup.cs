// Source: carla/road/SignalGroup.h (no upstream file in this build — slot reserved
// by Wave 1 spec). We model the implicit grouping by reusing Controller's signal
// set; this file exists only to host related enums / records as Wave 2 needs them.
namespace CarlaNet.Map.Road;

/// <summary>
/// Reserved placeholder for future signal-group aggregation. Upstream lacks a
/// dedicated SignalGroup.h in this revision (groups are flattened into
/// <see cref="Controller"/>'s signal set); kept as an empty namespace marker so
/// Wave 2 has a slot if OpenDRIVE's signalGroup is later needed.
/// </summary>
public static class SignalGroupReserved
{
    public const string Note = "Reserved — see Controller.Signals for grouping.";
}
