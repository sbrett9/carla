using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// One vehicle's state at one SUMO step, as the playback bridge reads it: SUMO's own frame and
/// units, plus where along its lane the vehicle is.
/// </summary>
/// <param name="Id">SUMO's vehicle id, stable for the vehicle's whole life in the simulation.</param>
/// <param name="X">Projected easting in metres, at the <b>centre of the front bumper</b>.</param>
/// <param name="Y">Projected northing in metres, at the same reference point as <paramref name="X"/>.</param>
/// <param name="HeadingDegrees">Degrees clockwise from north.</param>
/// <param name="SpeedMetresPerSecond">Speed along the lane.</param>
/// <param name="EdgeId">The edge the vehicle is on; one inside a junction has a leading colon.</param>
/// <param name="LaneId">The lane, as <c>&lt;edge&gt;_&lt;index&gt;</c>.</param>
/// <param name="LanePositionMetres">
/// How far the front bumper is along that lane, from the lane's start. This is the parameter the
/// lane polyline is evaluated at, which is what keeps an interpolated pose on the lane through a
/// turn instead of on the chord across it.
/// </param>
/// <param name="TypeId">The vehicle type id the scenario's <c>vType</c> declared.</param>
/// <param name="Signals">SUMO's signal word for this step.</param>
/// <param name="LateralOffsetMetres">
/// How far the vehicle is to the left of <paramref name="LaneId"/>'s centre line, which is where
/// SUMO's reported position is. Zero but during a lane change SUMO spreads over time
/// (<c>lanechange.duration</c>): then it grows steadily towards the lane being moved to, jumps by a
/// lane width when SUMO starts reporting that lane, past halfway, and returns to zero.
/// </param>
/// <remarks>
/// Separate from <see cref="SumoVehicleState"/> rather than an extension of it. That record is the
/// TraCI client's fixed seven-variable read, differenced against SUMO's own client to establish it;
/// lane position and lateral offset are variables only a bridge that interpolates needs, and every
/// subscribed variable is charged to every SUMO step for every subscribed vehicle. Putting them in the
/// client's state record would charge them to every reader.
/// </remarks>
public readonly record struct CoSimVehicleFrame(
    string Id,
    double X,
    double Y,
    double HeadingDegrees,
    double SpeedMetresPerSecond,
    string EdgeId,
    string LaneId,
    double LanePositionMetres,
    string TypeId,
    SumoVehicleSignals Signals,
    double LateralOffsetMetres = 0.0)
{
    /// <summary>Whether the vehicle is inside a junction, which SUMO marks by the edge's name.</summary>
    public bool IsOnInternalEdge => EdgeId.Length > 0 && EdgeId[0] == ':';

    /// <summary>The frame a TraCI client state plus a lane position and lateral offset make.</summary>
    public static CoSimVehicleFrame From(in SumoVehicleState state, double lanePositionMetres,
                                         double lateralOffsetMetres = 0.0) =>
        new(state.Id, state.X, state.Y, state.HeadingDegrees, state.SpeedMetresPerSecond,
            state.EdgeId, state.LaneId, lanePositionMetres, state.TypeId, state.Signals,
            lateralOffsetMetres);
}
