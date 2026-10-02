namespace CarlaNet.CoSim;

/// <summary>
/// Where on the OpenDRIVE road network a body was seated, and how its height and tilt were taken
/// from it.
/// </summary>
/// <param name="RoadId">The OpenDRIVE road whose profile the height came from.</param>
/// <param name="S">Where along that road's reference line the body's origin projects.</param>
/// <param name="SurfaceZMetres">
/// The road's height there above the georeference origin: the profile at <paramref name="S"/>, which
/// is the height of every lane of the road at that s.
/// </param>
/// <param name="SlopeAlongHeading">
/// The rise per metre travelled along the body's own heading: the profile's slope at
/// <paramref name="S"/>, signed by which way the heading runs along the road.
/// </param>
/// <param name="DepartureFromGroundMetres">
/// The road's height less the ground surface's under the same point. Within a few tenths of a metre
/// where the road is at grade; metres on a bridge deck and on a road spanning the ground beneath one.
/// </param>
/// <param name="RollWeight">
/// How much of the ground surface's cross-slope the body was rolled by: one at grade, zero on a
/// structure, and blended between by <see cref="DepartureFromGroundMetres"/>.
/// </param>
public readonly record struct RoadSeat(
    uint RoadId,
    double S,
    double SurfaceZMetres,
    double SlopeAlongHeading,
    double DepartureFromGroundMetres = 0.0,
    double RollWeight = 1.0);

/// <summary>Why a body was seated on the ground surface rather than on the road profile.</summary>
public enum GroundSeatReason
{
    /// <summary>It was not: the body sits on its road's profile.</summary>
    None,

    /// <summary>
    /// SUMO puts the vehicle on no lane: parked at a stop off the carriageway, or pulling into or out of
    /// one. Nothing says which road, if any, it stands on.
    /// </summary>
    NoLane,

    /// <summary>The vehicle's lane is on an edge no OpenDRIVE road was found for.</summary>
    NoRoad,

    /// <summary>
    /// The position lies further from the lane's road than a vehicle on one of its lanes can, so the
    /// road's profile is not the surface under it.
    /// </summary>
    OffTheRoad,
}
