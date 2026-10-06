// Source: carla/rpc/OrbitState.h
// OrbitState MSGPACK_DEFINE_ARRAY(angle_rad, enabled, paused)
namespace CarlaNet.Types.Rpc.Orbit;

/// <summary>
/// Where an actor's orbit mover stands (<c>get_orbit_state</c>): the angle it holds the actor at, and
/// whether it is moving the actor. An actor given no orbit answers zero, disabled, not paused.
/// </summary>
/// <param name="AngleRadians">In [0, 2 pi); the angle the actor was last placed at.</param>
/// <param name="Enabled">The mover owns the actor's transform: it places the actor on the circle every tick.</param>
/// <param name="Paused">The angle does not advance; the actor is held on the circle while enabled.</param>
[MessagePackObject]
public record struct OrbitState(
    [property: Key(0)] double AngleRadians,
    [property: Key(1)] bool Enabled,
    [property: Key(2)] bool Paused);
