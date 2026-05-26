// Integrator-owned glue: a static, process-wide lazy factory that hands out
// a single WalkerNavigation per CarlaClient instance. Lives in CarlaNet.Nav
// (not on CarlaClient itself) because Nav already references Transport — the
// opposite reference (Transport → Nav) would create a project cycle.
//
// Usage from the Python shim:
//
//     from CarlaNet.Nav import NavigationFactory
//     wn = NavigationFactory.GetOrCreate(client._inner)
//     wn.Start(walker_id, walker_loc)
//
// First call:
//   - Pulls the navmesh bytes via CarlaClient.GetNavigationMeshAsync()
//   - Constructs a Navigation, calls LoadMesh
//   - Wraps it in a WalkerNavigation and caches it keyed by the CarlaClient.
//
// Subsequent calls reuse the cache. The fetch is synchronous (.GetAwaiter
// pattern matches the rest of CarlaNet); fetch failures bubble out as an
// InvalidOperationException so the Python shim can catch and fall back to
// its no-op stub.
#nullable enable

using System.Runtime.CompilerServices;
using CarlaNet.Transport;
using Microsoft.Extensions.Logging;

namespace CarlaNet.Nav;

/// <summary>
/// Process-wide factory for <see cref="WalkerNavigation"/> instances. One
/// entry per <see cref="CarlaClient"/>. The cache uses object-reference
/// identity so a fresh CarlaClient (new connection) gets a fresh Navigation
/// — no stale-navmesh hazards if the user reconnects to a different map.
/// </summary>
public static class NavigationFactory
{
    // ConditionalWeakTable so a disposed CarlaClient's WalkerNavigation
    // becomes eligible for GC. Locked manually for the create path because
    // CWT lacks an atomic compare-and-create primitive.
    private static readonly ConditionalWeakTable<CarlaClient, WalkerNavigation> _cache = new();
    private static readonly object _gate = new();

    /// <summary>
    /// Returns the cached <see cref="WalkerNavigation"/> for
    /// <paramref name="client"/>, creating it (fetching the navmesh + parsing
    /// it) on first call. Thread-safe; the create path uses double-checked
    /// locking so concurrent first-callers don't issue duplicate RPCs.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the navmesh RPC returned empty bytes or threw. The Python
    /// shim catches this and falls back to a no-op WalkerNavigation stub.
    /// </exception>
    public static WalkerNavigation GetOrCreate(CarlaClient client, ILogger? logger = null)
    {
        if (client is null)
            throw new ArgumentNullException(nameof(client));

        if (_cache.TryGetValue(client, out var cached))
            return cached;

        lock (_gate)
        {
            if (_cache.TryGetValue(client, out cached))
                return cached;

            byte[] blob;
            try
            {
                blob = client.GetNavigationMeshAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "WalkerNavigation: get_navigation_mesh RPC failed; "
                    + "is the server running and is the current map cooked with a Nav/ folder?",
                    ex);
            }
            if (blob is null || blob.Length < 40)
            {
                throw new InvalidOperationException(
                    $"WalkerNavigation: get_navigation_mesh returned "
                    + $"{(blob is null ? "null" : blob.Length + " bytes")}; "
                    + "this map has no usable navmesh.");
            }

            var nav = new Navigation(logger);
            try
            {
                nav.LoadMesh(blob);
            }
            catch (Exception ex)
            {
                nav.Dispose();
                throw new InvalidOperationException(
                    "WalkerNavigation: failed to parse navmesh blob "
                    + $"({blob.Length} bytes): {ex.Message}", ex);
            }

            var walkerNav = new WalkerNavigation(nav);
            _cache.Add(client, walkerNav);
            return walkerNav;
        }
    }

    /// <summary>
    /// True if a <see cref="WalkerNavigation"/> has already been created for
    /// <paramref name="client"/>. Lets the TM worker decide whether to skip
    /// the per-tick walker step without forcing a navmesh fetch.
    /// </summary>
    public static bool TryGet(CarlaClient client, out WalkerNavigation? walkerNavigation)
    {
        if (client is null) { walkerNavigation = null; return false; }
        if (_cache.TryGetValue(client, out var found))
        {
            walkerNavigation = found;
            return true;
        }
        walkerNavigation = null;
        return false;
    }
}
