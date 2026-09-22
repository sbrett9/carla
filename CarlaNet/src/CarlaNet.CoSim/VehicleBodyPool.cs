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
/// <para><b>The pool grows to demand rather than being sized in advance.</b> The alternative is to
/// spawn the full depth of every blueprint before the first tick, which for a seventeen-blueprint
/// catalogue and a render-set capacity of 128 is two thousand actors, nearly all of them parked for
/// the whole run. Growing on demand spawns the body the first time a blueprint is actually needed
/// and never again, so the spawn-once property that makes the pool safe is kept while the actor
/// count stays near what the scene holds. Each body gets its own parking slot, so a spawn is always
/// onto empty ground.</para>
///
/// <para><b>Physics and gravity are off from the moment a body exists.</b> They are written in one
/// batch immediately after the spawn and before any tick, so no body ever falls, is pushed, or
/// settles: between a spawn and the next tick the world does not advance, and the world is the only
/// thing that could move it. A pose-driven body under physics would coast, collide and roll between
/// corrections, which is the vehicle dynamics this mode exists to replace with SUMO's.</para>
///
/// <para><b>Exhaustion is a policy event.</b> Asking for a body when every one is out and the budget
/// is spent answers false and is counted. It is never an error and never a spawn attempt, because
/// the honest response to "more vehicles want rendering than this run can afford" is to render fewer
/// and record how many were declined.</para>
/// </remarks>
public sealed class VehicleBodyPool
{
    private readonly ICarlaWorld _world;
    private readonly VehicleParking _parking;
    private readonly int _maximumBodies;
    private readonly Dictionary<string, Stack<PooledBody>> _free = [];
    private readonly Dictionary<string, PooledBody> _held = [];
    private readonly List<PooledBody> _bodies = [];

    /// <param name="world">The world the bodies live in.</param>
    /// <param name="parking">Where a free body stands.</param>
    /// <param name="maximumBodies">
    /// How many actors this session may own at once, across every blueprint. A ceiling on the whole
    /// pool rather than a depth per blueprint: which bodies a scenario asks for is a property of its
    /// traffic mix, which nothing knows before the run, while the total is the budget that actually
    /// binds.
    /// </param>
    public VehicleBodyPool(ICarlaWorld world, VehicleParking parking, int maximumBodies)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBodies);
        _world = world;
        _parking = parking;
        _maximumBodies = maximumBodies;
    }

    /// <summary>Every body the pool has spawned, free or held.</summary>
    public IReadOnlyList<PooledBody> Bodies => _bodies;

    /// <summary>How many bodies are lent out right now.</summary>
    public int HeldBodies => _held.Count;

    /// <summary>How many times a vehicle was refused a body because the budget was spent.</summary>
    public long Exhaustions { get; private set; }

    /// <summary>
    /// Lend a body of the named blueprint to a vehicle, spawning one where the pool has none free
    /// and the budget allows.
    /// </summary>
    /// <returns>
    /// False where every body of that blueprint is out and the pool is at its ceiling. The vehicle
    /// is then simulated and not rendered, which is a decision, not a failure.
    /// </returns>
    public bool TryCheckOut(string vehicleId, string blueprintId, out PooledBody body)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleId);
        ArgumentException.ThrowIfNullOrEmpty(blueprintId);

        if (_held.TryGetValue(vehicleId, out body))
        {
            return true;
        }

        if (_free.TryGetValue(blueprintId, out Stack<PooledBody>? free) && free.Count > 0)
        {
            body = free.Pop();
            _held[vehicleId] = body;
            return true;
        }

        if (_bodies.Count >= _maximumBodies)
        {
            Exhaustions++;
            body = default;
            return false;
        }

        body = Spawn(blueprintId);
        _held[vehicleId] = body;
        return true;
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
        ActorId actor = _world.Spawn(blueprintId, slot);
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
