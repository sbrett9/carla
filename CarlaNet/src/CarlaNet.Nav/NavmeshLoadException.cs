// Thrown when Navigation.LoadMesh fails to parse the navmesh blob coming
// from the CARLA server (`get_navigation_mesh` / `get_cache_file`).
//
// Wraps the underlying DotRecast IOException so callers don't have to take a
// hard reference on DotRecast.* to catch the failure.
namespace CarlaNet.Nav;

public sealed class NavmeshLoadException : Exception
{
    public NavmeshLoadException(string message) : base(message) { }
    public NavmeshLoadException(string message, Exception inner) : base(message, inner) { }
}
