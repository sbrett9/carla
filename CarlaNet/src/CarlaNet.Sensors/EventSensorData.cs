// §10.10, §10.11 — Collision and Obstacle detection use msgpack encoding.
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Geom;

namespace CarlaNet.Sensors;

// §10.10 — CollisionEvent: msgpack [self_actor, other_actor, normal_impulse:Vector3D]
[MessagePackObject]
public record struct CollisionSensorData(
    [property: Key(0)] Actor SelfActor,
    [property: Key(1)] Actor OtherActor,
    [property: Key(2)] Vector3D NormalImpulse);

// §10.11 — ObstacleDetectionEvent: msgpack [self_actor, other_actor, distance:float]
[MessagePackObject]
public record struct ObstacleSensorData(
    [property: Key(0)] Actor SelfActor,
    [property: Key(1)] Actor OtherActor,
    [property: Key(2)] float Distance);
