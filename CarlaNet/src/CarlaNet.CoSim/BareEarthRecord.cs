namespace CarlaNet.CoSim;

/// <summary>
/// The bare-earth reference record a CARLA server holds for its loaded world, as
/// <c>get_bare_earth_reference</c> and its two grid calls answer it.
/// </summary>
/// <remarks>
/// Published by the client that generated the world (<c>CarlaClient.SetBareEarthReferenceAsync</c>),
/// and by a world restored from its level when the level loads, so that a client which did not build
/// the world can recover what it was built on. The world package's manifest and grid entry are
/// written from the same values in the same build, which is what makes the two comparable.
/// </remarks>
/// <param name="OffsetMetres">
/// The constant surface shift, where the surface was shifted by one amount; 0 under a drape, where
/// the grids are authoritative instead.
/// </param>
/// <param name="DrapeActive">Whether the surface was conformed cell by cell.</param>
/// <param name="MinXMetres">The grid's corner cell, CARLA frame.</param>
/// <param name="MinYMetres">Likewise.</param>
/// <param name="CellSizeMetres">The grid's spacing.</param>
/// <param name="Columns">Cells along X.</param>
/// <param name="Rows">Cells along Y.</param>
/// <param name="OffsetGrid">
/// Per-cell surface shift, row-major, metres; empty unless <paramref name="DrapeActive"/>.
/// </param>
/// <param name="GroundGrid">
/// Per-cell bare-earth ground height, row-major, ellipsoidal metres; empty unless
/// <paramref name="DrapeActive"/>.
/// </param>
public sealed record BareEarthRecord(
    double OffsetMetres,
    bool DrapeActive,
    double MinXMetres,
    double MinYMetres,
    double CellSizeMetres,
    int Columns,
    int Rows,
    float[] OffsetGrid,
    float[] GroundGrid);
