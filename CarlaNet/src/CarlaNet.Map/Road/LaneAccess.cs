// Source: carla/road/LaneValidity.h
//
// (named "LaneValidity" upstream — renamed to LaneValidityRange to match the
// "range of valid lane ids" semantics; "LaneAccess" file slot is reused per the
// Wave 1 spec but the type name reflects upstream more clearly.)
namespace CarlaNet.Map.Road;

/// <summary>Closed range of lane IDs for which a signal/object is valid.</summary>
public readonly record struct LaneValidity(LaneId FromLane, LaneId ToLane);
