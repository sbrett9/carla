using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim;

/// <summary>
/// The draped ground surface of one generated world, read out of its package and sampled in
/// process.
/// </summary>
/// <remarks>
/// <para>The SUMO network is flat -- no lane shape in it carries a z at all -- so a pose's height,
/// pitch and roll cannot come from SUMO. They come from the same per-cell surface the world's
/// collision heightfield was built from, which makes the seating exact rather than settled, and
/// which costs no round trip: five bilinear samples of a cached float array per vehicle per tick,
/// one for the height and four for the two gradients.</para>
///
/// <para><b>Outside the grid reads as unknown, never as the edge cell.</b> A clamped read answers
/// with the height of the nearest sandbox boundary, which for a vehicle beyond the sandbox is
/// arbitrary and looks like a measurement. The client-side sampler in <c>CarlaClient</c> answers
/// null there and this one does the same, so a bridge reading either gets the same refusal.</para>
///
/// <para><b>The grid is indexed in the CARLA frame.</b> It is built by projecting the world's
/// bounds through the georeference, which negates Y; a caller holding a SUMO position has to negate
/// Y before sampling. <see cref="SampleForSumoPosition"/> is the one that does it, and it exists
/// because the mistake has been made before -- the shipped Cursor-on-Target bridge indexes this
/// grid with a raw SUMO position and reads the mirrored row for every sample.</para>
/// </remarks>
public sealed class GroundSurface
{
    private readonly float[] _dtm;
    private readonly float[] _offset;
    private readonly double _minX;
    private readonly double _minY;
    private readonly double _cellSize;
    private readonly int _columns;
    private readonly int _rows;

    private GroundSurface(float[] dtm, float[] offset, WorldPackageManifest manifest)
    {
        _dtm = dtm;
        _offset = offset;
        _minX = manifest.GridMinXMeters;
        _minY = manifest.GridMinYMeters;
        _cellSize = manifest.GridCellSizeMeters;
        _columns = manifest.GridNumCols;
        _rows = manifest.GridNumRows;
        OriginHeightMetres = manifest.OriginHeightMeters;
    }

    /// <summary>
    /// The georeference origin's ellipsoidal height. Local z is measured from it, so a pose's z is
    /// the sampled surface less this.
    /// </summary>
    public double OriginHeightMetres { get; }

    /// <summary>The grid's cell size, which is the step the surface gradients are taken over.</summary>
    public double CellSizeMetres => _cellSize;

    /// <summary>The grid's extent in the CARLA frame, for a report that has to say what it covered.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Extent =>
        (_minX, _minY, _minX + ((_columns - 1) * _cellSize), _minY + ((_rows - 1) * _cellSize));

    /// <summary>
    /// Read a world package's surface, or refuse a world that has none.
    /// </summary>
    /// <exception cref="CoSimSessionRefusedException">
    /// The world was reconciled by a constant shift rather than a per-cell drape, so there is no
    /// surface to seat a vehicle on and no gradient to tilt it by.
    /// </exception>
    public static GroundSurface FromWorldPackage(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        WorldPackageManifest manifest = WorldPackage.ReadManifest(packagePath);
        if (!manifest.DrapeActive
            || !WorldPackage.TryReadGrids(packagePath, out float[] offset, out float[] dtm))
        {
            throw new CoSimSessionRefusedException(
                $"The world package {packagePath} carries no draped ground surface (height-align "
                + $"mode '{manifest.HeightAlignMode}'). A SUMO-driven vehicle is kinematic and takes "
                + "its height, pitch and roll from that surface, so there is nothing to seat it on.");
        }

        int expected = manifest.GridNumCols * manifest.GridNumRows;
        if (dtm.Length < expected || offset.Length < expected)
        {
            throw new CoSimSessionRefusedException(
                $"The world package {packagePath} declares a "
                + $"{manifest.GridNumCols}x{manifest.GridNumRows} grid and carries "
                + $"{Math.Min(dtm.Length, offset.Length)} cells.");
        }

        return new GroundSurface(dtm, offset, manifest);
    }

    /// <summary>
    /// Ground-surface elevation in ellipsoidal metres under a CARLA-local position, or
    /// <see langword="null"/> outside the grid.
    /// </summary>
    public double? Sample(double carlaX, double carlaY)
    {
        double fx = (carlaX - _minX) / _cellSize;
        double fy = (carlaY - _minY) / _cellSize;
        if (fx < 0.0 || fy < 0.0 || fx > _columns - 1.0 || fy > _rows - 1.0)
        {
            return null;
        }

        return Bilinear(_dtm, fx, fy) + Bilinear(_offset, fx, fy);
    }

    /// <summary>
    /// The same sample taken from a SUMO position, which is in the projected east/north frame the
    /// CARLA frame negates the northing of.
    /// </summary>
    public double? SampleForSumoPosition(double sumoX, double sumoY) => Sample(sumoX, -sumoY);

    private double Bilinear(float[] grid, double fx, double fy)
    {
        int c0 = Math.Clamp((int)Math.Floor(fx), 0, _columns - 1);
        int r0 = Math.Clamp((int)Math.Floor(fy), 0, _rows - 1);
        int c1 = Math.Min(c0 + 1, _columns - 1);
        int r1 = Math.Min(r0 + 1, _rows - 1);
        double tx = fx - c0;
        double ty = fy - r0;
        double top = grid[(r0 * _columns) + c0]
                     + ((grid[(r0 * _columns) + c1] - grid[(r0 * _columns) + c0]) * tx);
        double bottom = grid[(r1 * _columns) + c0]
                        + ((grid[(r1 * _columns) + c1] - grid[(r1 * _columns) + c0]) * tx);
        return top + ((bottom - top) * ty);
    }
}
