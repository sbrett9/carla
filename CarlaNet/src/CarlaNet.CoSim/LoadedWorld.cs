namespace CarlaNet.CoSim;

/// <summary>
/// What a CARLA server says about the world it has loaded: where the world's local origin is pinned
/// on the globe, the road network it serves, and the bare-earth reference record published for it.
/// </summary>
/// <param name="OriginLatitude">The georeference origin, degrees (<c>get_cesium_origin</c>).</param>
/// <param name="OriginLongitude">Likewise.</param>
/// <param name="OriginHeightMetres">The origin's ellipsoidal height, which local z is measured from.</param>
/// <param name="OpenDrive">
/// The OpenDRIVE the server serves for the loaded map (<c>get_map_data</c>): the network a world
/// generated at runtime was built from, or the one a world restored from its level carries as an
/// asset.
/// </param>
/// <param name="BareEarth">
/// The bare-earth reference record, or <see langword="null"/> where the world carries none -- any
/// map loaded rather than generated.
/// </param>
public sealed record LoadedWorld(
    double OriginLatitude,
    double OriginLongitude,
    double OriginHeightMetres,
    string OpenDrive,
    BareEarthRecord? BareEarth);
