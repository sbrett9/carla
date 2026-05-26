// Source: carla/trafficmanager/AtomicActorSet.h
//
// Mutex-guarded `map<ActorId, ActorPtr>` wrapper used by TrafficManagerLocal
// for the registered-vehicle pool and (via Parameters) for the
// per-vehicle collision-ignore sets. In C# we keep the SAME public surface
// (Insert / Remove / GetList / Contains / Size / State / Clear / Destroy)
// so callers map 1:1 with upstream.
//
// Implementation: a `ConcurrentDictionary<ActorId, Actor>` for lock-free
// reads (the stages call Contains in their hot path) plus an
// `Interlocked.Increment`-driven state counter for the cache-invalidation
// signal that ALSM uses.
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// Thread-safe set of registered vehicles. The C++ class also keeps the
/// <see cref="Actor"/> payload (so <c>GetList()</c> can return live actor
/// pointers); we preserve that semantics with a dictionary.
/// </summary>
internal sealed class AtomicActorSet
{
    private readonly ConcurrentDictionary<ActorId, Actor> _actorSet = new();

    // state_counter (int in C++) — bumped on every mutation. Stages compare
    // GetState() across ticks to know if the registered-vehicle list has
    // changed and they need to re-allocate frames.
    private int _stateCounter;

    public AtomicActorSet() { }

    public IReadOnlyList<Actor> GetList()
    {
        // Snapshot — ConcurrentDictionary's enumerator is safe but does not
        // see a frozen view. For TM use the cost is negligible (~50 entries).
        var list = new List<Actor>(_actorSet.Count);
        foreach (var kv in _actorSet)
            list.Add(kv.Value);
        return list;
    }

    public IReadOnlyList<ActorId> GetIDList()
    {
        var list = new List<ActorId>(_actorSet.Count);
        foreach (var kv in _actorSet)
            list.Add(kv.Key);
        return list;
    }

    public void Insert(IEnumerable<Actor> actors)
    {
        bool changed = false;
        foreach (var actor in actors)
        {
            // Upstream uses `insert({id, actor})` which is a no-op on
            // collision; TryAdd matches that. Bump the counter only if any
            // entry actually went in.
            if (_actorSet.TryAdd(actor.Id, actor))
                changed = true;
        }
        if (changed)
            Interlocked.Increment(ref _stateCounter);
    }

    public void Remove(IEnumerable<ActorId> actorIds)
    {
        bool changed = false;
        foreach (var id in actorIds)
        {
            if (_actorSet.TryRemove(id, out _))
                changed = true;
        }
        if (changed)
            Interlocked.Increment(ref _stateCounter);
    }

    /// <summary>
    /// Removes the actor from the set. Upstream additionally calls
    /// <c>actor.Destroy()</c> (an RPC to the server). The Wave-1 port has
    /// no actor handle, so this is a remove-only — Wave 3+ ALSM will call
    /// <c>CarlaClient.DestroyActor(id)</c> separately. State counter still
    /// bumps so caches invalidate.
    /// </summary>
    public bool Destroy(ActorId actorId)
    {
        if (_actorSet.TryRemove(actorId, out _))
        {
            Interlocked.Increment(ref _stateCounter);
            return true;
        }
        return false;
    }

    public int GetState() => Volatile.Read(ref _stateCounter);

    public bool Contains(ActorId id) => _actorSet.ContainsKey(id);

    public int Size => _actorSet.Count;

    /// <summary>
    /// True when the set holds no actors. Upstream does not expose an
    /// explicit <c>Empty</c> helper — callers use <c>Size() == 0</c> — but
    /// the C# port surfaces it for clarity at TM call sites.
    /// </summary>
    public bool Empty => _actorSet.IsEmpty;

    public void Clear()
    {
        // Match upstream: Clear() does NOT bump the state counter. (The C++
        // implementation also omits the increment — see AtomicActorSet.h:107.)
        _actorSet.Clear();
    }
}
