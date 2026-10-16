namespace CarlaNet.CoSim;

/// <summary>
/// The bare-earth reference record a CARLA server holds for its loaded world, as
/// <c>get_bare_earth_reference</c> answers it, with the digests <c>get_bare_earth_digest</c> answers
/// for its two grids in place of the grids themselves.
/// </summary>
/// <remarks>
/// <para>Published by the client that generated the world (<c>CarlaClient.SetBareEarthReferenceAsync</c>),
/// and by a world restored from its level when the level loads, so that a client which did not build
/// the world can recover what it was built on. The world package's manifest and grid entry are
/// written from the same values in the same build, which is what makes the two comparable.</para>
///
/// <para>The grids travel as digests because they are the size of the drape: 7,611,381 floats each
/// on the Bahonar world, whose two grid fetches took 146 s and 153 s. The server digests each grid
/// when the record is set, over exactly the bytes <c>bareearth.bin</c> holds for it
/// (<c>WorldPackage.HashGrid</c>), so equal digests are equal grids, bit for bit.</para>
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
/// <param name="OffsetGridSha1">
/// SHA-1 of the per-cell surface shift grid, lowercase hexadecimal; empty unless
/// <paramref name="DrapeActive"/>, and empty under a drape where the server publishes no digest.
/// </param>
/// <param name="GroundGridSha1">
/// Likewise for the per-cell bare-earth ground height grid.
/// </param>
public sealed record BareEarthRecord(
    double OffsetMetres,
    bool DrapeActive,
    double MinXMetres,
    double MinYMetres,
    double CellSizeMetres,
    int Columns,
    int Rows,
    string OffsetGridSha1,
    string GroundGridSha1);
