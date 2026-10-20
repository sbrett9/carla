using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim;

/// <summary>
/// Refuses a world package whose road network is not in the world's own frame.
/// </summary>
internal static class NetworkFrameCheck
{
    /// <summary>
    /// Refuse a network that is not in the world's frame.
    /// </summary>
    /// <remarks>
    /// <para>Three things make the frame conversion a sign on the northing and no offset at all, and
    /// all three are checkable before a single pose is computed: the network's projection has to be
    /// the world's own georeference string, its normalisation offset has to be zero, and its extent
    /// has to lie inside the ground surface the world carries.</para>
    ///
    /// <para>A network built at a different origin converts to a position several hundred metres
    /// away that is inside the sandbox and looks entirely ordinary, which is why this is a refusal
    /// rather than a warning.</para>
    ///
    /// <para>This compares the package's network with the package. That the scenario drives the
    /// same network is <see cref="ScenarioNetworkCheck"/>'s, settled before SUMO was started: two
    /// networks can agree on all three of these and still be different graphs.</para>
    /// </remarks>
    internal static void Require(WorldPackageManifest manifest,
                                 SumoRoadNetwork network,
                                 SumoDriveSessionOptions options)
    {
        if (network.NetOffset != (0.0, 0.0))
        {
            throw new CoSimSessionRefusedException(
                $"The network in {options.WorldPackagePath} was normalized by "
                + $"{network.NetOffset}, so its coordinates are not the world's. Build it with "
                + "normalization disabled.");
        }

        if (network.Projection != manifest.GeoReferenceString)
        {
            throw new CoSimSessionRefusedException(
                $"The network projects as '{network.Projection}' and the world it is packaged with "
                + $"as '{manifest.GeoReferenceString}'. A vehicle converted through the wrong one of "
                + "those ends up somewhere inside the sandbox and looks entirely ordinary.");
        }

        (double minX, double minY, double maxX, double maxY) = network.ConvBoundary;
        (double gridMinX, double gridMinY, double gridMaxX, double gridMaxY) =
            (manifest.GridMinXMeters, manifest.GridMinYMeters,
             manifest.GridMinXMeters + ((manifest.GridNumCols - 1) * manifest.GridCellSizeMeters),
             manifest.GridMinYMeters + ((manifest.GridNumRows - 1) * manifest.GridCellSizeMeters));

        // The network's extent is in the projected frame and the grid's is in the CARLA frame, so
        // the northings swap places as well as sign. The tolerance is one grid cell: the surface is
        // built from the same bounds the network was clipped to and the two agree to within
        // rounding -- measured on the shipped Arapahoe world, the network overhangs the grid by
        // 0.2 mm at one edge -- so a stricter test refuses every world for a fraction of a cell. A
        // vehicle that genuinely stands off the end of the surface is counted at the tick it
        // happens, which is where an overhang of any size shows up.
        double slack = manifest.GridCellSizeMeters;
        if (minX < gridMinX - slack || maxX > gridMaxX + slack
            || -maxY < gridMinY - slack || -minY > gridMaxY + slack)
        {
            throw new CoSimSessionRefusedException(
                $"The network spans ({minX:0.0}, {minY:0.0}) to ({maxX:0.0}, {maxY:0.0}) and the "
                + $"world's ground surface covers ({gridMinX:0.0}, {gridMinY:0.0}) to "
                + $"({gridMaxX:0.0}, {gridMaxY:0.0}) in the CARLA frame. Part of the network has no "
                + "ground under it, so vehicles there would have no height.");
        }
    }
}
