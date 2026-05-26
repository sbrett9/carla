// Source: carla/road/JuncRoadSegment.h
//
// Lightweight (road_id, section_id) pair used by junction topology queries.
// Upstream defines it as a small POD inside MapBuilder; we surface it as a
// public record-struct so InMemoryMap (Wave 3) can use it directly.
namespace CarlaNet.Map.Road;

public readonly record struct JuncRoadSegment(RoadId RoadId, SectionId SectionId);
