using CarlaNet.Types.Geom;

namespace CarlaNet.CoSim;

/// <summary>
/// Where a pooled body stands while no SUMO vehicle is borrowing it.
/// </summary>
/// <param name="OriginX">CARLA-local easting of the first slot, metres.</param>
/// <param name="OriginY">CARLA-local northing negated of the first slot, metres.</param>
/// <param name="Z">CARLA-local height of every slot, metres.</param>
/// <param name="SpacingMetres">Distance between slots, along and across.</param>
/// <param name="SlotsPerRow">How many slots before the next row.</param>
/// <remarks>
/// <para><b>One slot per body, and each body keeps its own.</b> CARLA refuses a spawn whose point is
/// occupied, with no queue and no retry, so a pool that parked every body on one point could spawn
/// exactly one. Giving each body its own slot makes every spawn a spawn onto empty ground, which is
/// what lets the pool grow to meet demand instead of being sized in advance by a guess.</para>
///
/// <para><b>Out of sight by where it is, not by what it looks like.</b> A parked body is below the
/// ground surface and beyond the sandbox the network was clipped to, so no camera pointed at the
/// scene can see it and nothing on the road can hit it. It costs one row in the world-observer
/// snapshot per tick and nothing else. No opacity is written and none is needed.</para>
/// </remarks>
public readonly record struct VehicleParking(
    double OriginX,
    double OriginY,
    double Z,
    double SpacingMetres,
    int SlotsPerRow)
{
    /// <summary>How far beyond the ground surface the first slot sits.</summary>
    private const double StandOffMetres = 200.0;

    /// <summary>How far below the ground surface's own frame the slots sit.</summary>
    private const double DepthMetres = 300.0;

    /// <summary>Slot spacing: wider than the longest body in the shipped catalogue.</summary>
    private const double DefaultSpacingMetres = 20.0;

    /// <summary>Slots in a row before the next one starts.</summary>
    private const int DefaultSlotsPerRow = 16;

    /// <summary>
    /// A parking area outside and below a world's ground surface, in that world's own frame.
    /// </summary>
    /// <remarks>
    /// Taken from the surface rather than configured, because the surface is what defines where the
    /// scene is: it is built from the same bounds the SUMO network was clipped to, so a point beyond
    /// it is a point no vehicle in the simulation can reach and no camera aimed at the road can
    /// frame.
    /// </remarks>
    public static VehicleParking BeyondTheSurface(GroundSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        (_, _, double maxX, double maxY) = surface.Extent;
        return new VehicleParking(maxX + StandOffMetres, maxY + StandOffMetres, -DepthMetres,
                                  DefaultSpacingMetres, DefaultSlotsPerRow);
    }

    /// <summary>The transform of one slot. Bodies face the same way; nothing looks at them.</summary>
    public Transform Slot(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        int row = index / SlotsPerRow;
        int column = index % SlotsPerRow;
        return new Transform(
            new Location((float)(OriginX + (column * SpacingMetres)),
                         (float)(OriginY + (row * SpacingMetres)),
                         (float)Z),
            new Rotation(0f, 0f, 0f));
    }
}
