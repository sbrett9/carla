namespace CarlaNet.CoSim;

/// <summary>
/// The OpenDRIVE road a body was on, and how its seat was shared between that road's profile and the
/// ground surface.
/// </summary>
/// <param name="RoadId">The OpenDRIVE road the body's origin is on.</param>
/// <param name="S">Where along that road's reference line the body's origin projects.</param>
/// <param name="SurfaceZMetres">
/// The road's profile there, above the georeference origin: the height of every lane of the road at
/// that s, flat across.
/// </param>
/// <param name="SlopeAlongHeading">
/// The profile's rise per metre travelled along the body's own heading: its slope at
/// <paramref name="S"/>, signed by which way the heading runs along the road.
/// </param>
/// <param name="DepartureFromGroundMetres">
/// The profile's height less the ground surface's, at the road's reference line at
/// <paramref name="S"/> -- where the profile is defined, so a lane's distance across a cambered road
/// does not enter into it. Within a few centimetres where the road is at grade; metres on a bridge deck
/// and on a road spanning the ground beneath one.
/// </param>
/// <param name="GroundWeight">
/// How much of the body's seat the ground surface gave it: one at grade, where the seat is the ground's
/// exactly -- height, pitch, roll and climb; zero on a structure, where height and pitch are the
/// profile's and the roll is none; blended between. It follows <paramref name="DepartureFromGroundMetres"/>
/// but is held to fall no faster along the road than <see cref="PoseConverter.WeightChangeMetres"/>
/// allows, made to meet the carriageways a junction connector joins at its ends, and carried across the
/// connector section a merge absorbed, so it can differ from what the departure alone would give.
/// </param>
public readonly record struct RoadSeat(
    uint RoadId,
    double S,
    double SurfaceZMetres,
    double SlopeAlongHeading,
    double DepartureFromGroundMetres = 0.0,
    double GroundWeight = 1.0);

/// <summary>Why a body was seated on the ground surface with no road under it.</summary>
public enum GroundSeatReason
{
    /// <summary>It was not: the body is on a road, and the road's departure from the ground decided its seat.</summary>
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

/// <summary>Where on its road a body's origin is: what <see cref="RoadSurface"/> answers per tick.</summary>
/// <param name="Road">The road.</param>
/// <param name="S">Where along its reference line the origin projects; off either end beyond the road.</param>
/// <param name="SurfaceZMetres">The profile's height at <paramref name="S"/>.</param>
/// <param name="ProfileSlope">The profile's slope along +s at <paramref name="S"/>.</param>
/// <param name="AlongPerMetre">
/// How far s advances per metre travelled along the heading: plus one along +s on the reference line,
/// minus one against it, more than one on the inside of a curve.
/// </param>
internal readonly record struct RoadPlace(
    RoadProfile Road,
    double S,
    double SurfaceZMetres,
    double ProfileSlope,
    double AlongPerMetre)
{
    /// <summary>The OpenDRIVE road id.</summary>
    public uint RoadId => Road.Id;

    /// <summary>The profile's rise per metre travelled along the heading.</summary>
    public double SlopeAlongHeading => ProfileSlope * AlongPerMetre;
}
