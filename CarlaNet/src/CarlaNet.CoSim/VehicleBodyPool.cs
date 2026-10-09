using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// The CARLA bodies a session lends to SUMO vehicles: spawned once each, checked out on admission,
/// checked back in on release, and destroyed only when the session ends.
/// </summary>
/// <remarks>
/// <para><b>Why a pool at all.</b> Measured on the shipped port scenario, a busy hour holds at most
/// 131 vehicles at once and the authored week inserts 68,880 of them. Spawning one actor per vehicle
/// is 68,880 spawns, every one of which can fail because CARLA refuses a spawn whose point is
/// occupied -- there is no queue and no retry, the call simply answers a collision. A vehicle that
/// borrows a body that already exists never meets that failure.</para>
///
/// <para><b>The pool grows to demand, and has no ceiling.</b> Every vehicle SUMO has is rendered, so
/// the pool comes to hold as many bodies of each blueprint as the scenario ever has vehicles of it at
/// once: a scenario heavier than the machine is comfortable with makes the run slower, never thinner.
/// A body is spawned only when every body of its blueprint is lent out, so each one is spawned once
/// and reused for the rest of the run -- the spawn-once property that makes the pool safe -- and the
/// actor count stays at the most the scene ever held. Each body gets its own parking slot, so a spawn
/// is always onto empty ground.</para>
///
/// <para><b>Physics and gravity are off from the moment a body exists.</b> They are written in one
/// batch immediately after the spawn and before any tick, so no body ever falls, is pushed, or
/// settles: between a spawn and the next tick the world does not advance, and the world is the only
/// thing that could move it. A pose-driven body under physics would coast, collide and roll between
/// corrections, which is the vehicle dynamics this mode exists to replace with SUMO's.</para>
///
/// <para><b>Every body is spawned under the role <see cref="RoleName"/>.</b> <c>role_name</c> is
/// provenance, naming the authority that drives an actor: <c>autopilot</c> for traffic-manager
/// traffic, <c>scenario</c> for storyboard entities, <c>sumo</c> for SUMO-driven ones (doc 04 D4.9).
/// Left to the blueprint's default, every body read <c>autopilot</c>, and the truth record named the
/// traffic manager as the driver of every SUMO vehicle. An attribute is fixed for the actor's life,
/// and SUMO drives a body for all of its, so the role holds whichever vehicle the body carries.</para>
/// </remarks>
public sealed class VehicleBodyPool
{
    /// <summary>The role every body is spawned under: SUMO drives it.</summary>
    public const string RoleName = "sumo";

    private readonly ICarlaWorld _world;
    private readonly VehicleParking _parking;
    private readonly Dictionary<string, Stack<PooledBody>> _free = [];
    private readonly Dictionary<string, PooledBody> _held = [];
    private readonly List<PooledBody> _bodies = [];

    /// <param name="world">The world the bodies live in.</param>
    /// <param name="parking">Where a free body stands.</param>
    public VehicleBodyPool(ICarlaWorld world, VehicleParking parking)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _parking = parking;
    }

    /// <summary>Every body the pool has spawned, free or held.</summary>
    public IReadOnlyList<PooledBody> Bodies => _bodies;

    /// <summary>How many bodies are lent out right now.</summary>
    public int HeldBodies => _held.Count;

    /// <summary>
    /// The bodies lent out right now, by the vehicle holding each: the render set as the world will
    /// draw it once the next batch is applied. A body not in here is parked.
    /// </summary>
    public IReadOnlyDictionary<string, PooledBody> Held => _held;

    /// <summary>
    /// Lend a body of the named blueprint to a vehicle: the one it already holds, a free one of that
    /// blueprint, or one spawned for it where every body of that blueprint is out.
    /// </summary>
    public PooledBody CheckOut(string vehicleId, string blueprintId)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        ArgumentException.ThrowIfNullOrEmpty(blueprintId);

        if (_held.TryGetValue(vehicleId, out PooledBody body))
        {
            return body;
        }

        body = _free.TryGetValue(blueprintId, out Stack<PooledBody>? free) && free.Count > 0
            ? free.Pop()
            : Spawn(blueprintId);
        _held[vehicleId] = body;
        return body;
    }

    /// <summary>The body a vehicle holds, if it holds one.</summary>
    public bool TryGetHeld(string vehicleId, out PooledBody body) =>
        _held.TryGetValue(vehicleId, out body);

    /// <summary>
    /// Take a body back from a vehicle, answering the body so the caller can park it.
    /// </summary>
    /// <remarks>
    /// The body is free for the next vehicle from here, and the caller is the one that writes it
    /// back to its slot: parking is a pose like any other and belongs in the same batch as the rest
    /// of the tick's poses rather than in a round trip of its own.
    /// </remarks>
    public bool TryCheckIn(string vehicleId, out PooledBody body)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        if (!_held.Remove(vehicleId, out body))
        {
            return false;
        }

        Free(body.BlueprintId).Push(body);
        return true;
    }

    /// <summary>
    /// Destroy every body, in one batch, and forget them.
    /// </summary>
    /// <remarks>
    /// The one moment a pooled actor is destroyed. A session that left its bodies behind would leave
    /// the operator's world with a car park of parked vehicles beyond the sandbox, which the next
    /// session would inherit and which nothing would explain.
    /// </remarks>
    public IReadOnlyList<CommandResponse> DestroyAll()
    {
        if (_bodies.Count == 0)
        {
            return [];
        }

        var commands = new List<Command>(_bodies.Count);
        foreach (PooledBody body in _bodies)
        {
            commands.Add(new DestroyActorCommand(body.Actor));
        }

        _bodies.Clear();
        _free.Clear();
        _held.Clear();
        return _world.ApplyBatch(commands);
    }

    private PooledBody Spawn(string blueprintId)
    {
        Transform slot = _parking.Slot(_bodies.Count);
        ActorId actor = _world.Spawn(blueprintId, slot, RoleName);
        var body = new PooledBody(actor, blueprintId, slot);
        _bodies.Add(body);

        // Before the world advances a tick, and therefore before anything can act on the body:
        // nothing in this mode drives it but a written pose.
        _world.ApplyBatch([
            new SetSimulatePhysicsCommand(actor, false),
            new SetEnableGravityCommand(actor, false),
        ]);
        return body;
    }

    private Stack<PooledBody> Free(string blueprintId)
    {
        if (!_free.TryGetValue(blueprintId, out Stack<PooledBody>? free))
        {
            free = new Stack<PooledBody>();
            _free[blueprintId] = free;
        }

        return free;
    }
}
