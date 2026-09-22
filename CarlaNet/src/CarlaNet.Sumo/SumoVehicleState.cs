namespace CarlaNet.Sumo;

/// <summary>
/// One vehicle's state at one SUMO step, in SUMO's own frame and units.
/// </summary>
/// <param name="Id">SUMO's vehicle id, stable for the vehicle's whole life in the simulation.</param>
/// <param name="X">Projected easting in metres, at the <b>centre of the front bumper</b> -- not the
/// centre of the body, which is what a renderer positions.</param>
/// <param name="Y">Projected northing in metres, at the same reference point as <paramref name="X"/>.</param>
/// <param name="HeadingDegrees">Degrees <b>clockwise from north</b>, which is neither CARLA's yaw
/// convention nor a mathematical bearing.</param>
/// <param name="SpeedMetresPerSecond">Speed along the lane. This is SUMO's true speed for the
/// vehicle whether or not anything is rendering it.</param>
/// <param name="EdgeId">The edge the vehicle is on. Internal edges inside a junction are named with
/// a leading colon.</param>
/// <param name="LaneId">The lane the vehicle is on, as <c>&lt;edge&gt;_&lt;index&gt;</c>.</param>
/// <param name="TypeId">The vehicle type id, which is what a scenario's <c>vType</c> declared.</param>
/// <param name="Signals">SUMO's signal word for this step.</param>
/// <remarks>
/// A record struct of plain values, copied out of the decoded frame at the point of reading. The
/// frame buffer is reused by the next exchange, so nothing here may point into it.
/// </remarks>
public readonly record struct SumoVehicleState(
    string Id,
    double X,
    double Y,
    double HeadingDegrees,
    double SpeedMetresPerSecond,
    string EdgeId,
    string LaneId,
    string TypeId,
    SumoVehicleSignals Signals);
