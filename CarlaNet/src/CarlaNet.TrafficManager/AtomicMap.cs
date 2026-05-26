// Source: carla/trafficmanager/AtomicMap.h
//
// Generic mutex-guarded `unordered_map<K,V>`. Used by `Parameters` for every
// per-actor knob.
//
// Implementation choice: a thin wrapper over <see cref="ConcurrentDictionary{TKey,TValue}"/>
// rather than a `Dictionary` + `lock`. Reads in C++ go through
// `std::scoped_lock`, but the .NET CD permits lock-free reads on
// TryGetValue / ContainsKey, which exactly matches the access pattern of
// `Parameters.GetXxx(actor_id)` called once-per-vehicle-per-stage-per-tick.
// The upstream API surface (AddEntry / Contains / GetValue / RemoveEntry)
// is kept verbatim so the Parameters port is a near-mechanical translation.
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// Thread-safe map. Drop-in replacement for the C++ <c>AtomicMap&lt;K,V&gt;</c>.
/// </summary>
internal sealed class AtomicMap<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _map = new();

    public void AddEntry(KeyValuePair<TKey, TValue> entry)
    {
        // Upstream: insert-or-overwrite. CD's indexer assignment is the same
        // semantics and is the canonical AddOrUpdate one-liner.
        _map[entry.Key] = entry.Value;
    }

    public void AddEntry(TKey key, TValue value) => _map[key] = value;

    public bool Contains(TKey key) => _map.ContainsKey(key);

    /// <summary>
    /// Throws <see cref="KeyNotFoundException"/> if the key is absent — same
    /// as <c>std::unordered_map::at</c>. Callers in <c>Parameters</c> already
    /// guard every <c>GetValue</c> with a prior <c>Contains</c>.
    /// </summary>
    public TValue GetValue(TKey key) => _map[key];

    /// <summary>
    /// Non-throwing variant; preferred internally to avoid the
    /// Contains+GetValue double-lookup pattern.
    /// </summary>
    public bool TryGetValue(TKey key, out TValue value) => _map.TryGetValue(key, out value!);

    public void RemoveEntry(TKey key) => _map.TryRemove(key, out _);

    public void Clear() => _map.Clear();

    public int Count => _map.Count;
}
