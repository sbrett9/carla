using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// A camera's image size and horizontal field of view, as the attributes it was spawned with say.
/// </summary>
/// <param name="ImageWidth">Picture width, pixels (<c>image_size_x</c>).</param>
/// <param name="ImageHeight">Picture height, pixels (<c>image_size_y</c>).</param>
/// <param name="HorizontalFovDegrees">Horizontal field of view, degrees (<c>fov</c>).</param>
/// <remarks>
/// Read once, when a camera is registered with a session, and never on the tick thread: a camera's
/// attributes are fixed at its spawn, so asking again could only ever give the same answer.
/// </remarks>
public readonly record struct CameraOptics(int ImageWidth, int ImageHeight, double HorizontalFovDegrees)
{
    /// <summary>The attribute names a CARLA camera blueprint publishes these under.</summary>
    public const string WidthAttribute = "image_size_x";

    /// <inheritdoc cref="WidthAttribute"/>
    public const string HeightAttribute = "image_size_y";

    /// <inheritdoc cref="WidthAttribute"/>
    public const string FovAttribute = "fov";

    /// <summary>The vertical field of view a pinhole camera of this image has, degrees.</summary>
    public double VerticalFovDegrees =>
        2.0 * Math.Atan(Math.Tan(HorizontalFovDegrees * Math.PI / 360.0) * ImageHeight / ImageWidth)
        * (180.0 / Math.PI);

    /// <summary>The focal length on the optical axis, pixels: half the width over the half-angle's tangent.</summary>
    public double FocalLengthPixels => ImageWidth / 2.0 / Math.Tan(HorizontalFovDegrees * Math.PI / 360.0);

    /// <summary>
    /// The most pixels one radian covers anywhere in the image, which is at its corners.
    /// </summary>
    /// <remarks>
    /// A rectilinear image places a ray at angle θ off the axis at <c>f tan θ</c>, so a small angle there
    /// covers <c>f / cos² θ</c> pixels along the radius: more than on the axis, by 2.56 times at the corner
    /// of a 90-degree 16:9 picture and 1.52 times at the corner of a 60-degree one. A range cap computed
    /// from the axis alone would leave out a vehicle near a corner that still covers the pixels the cap is
    /// meant to keep.
    /// </remarks>
    public double CornerPixelsPerRadian
    {
        get
        {
            double tanX = Math.Tan(HorizontalFovDegrees * Math.PI / 360.0);
            double tanY = tanX * ImageHeight / ImageWidth;
            return FocalLengthPixels * (1.0 + (tanX * tanX) + (tanY * tanY));
        }
    }

    /// <summary>
    /// The slant range beyond which a body of the given length covers fewer than the given number of
    /// pixels along its length, wherever in the picture it stands and however it is turned.
    /// </summary>
    /// <remarks>
    /// Broadside to the line of sight at the corner's scale: turned away, a body covers fewer pixels,
    /// and nearer the axis the scale is smaller, so past this range no placement of the body reaches the
    /// count. The small-angle form, <c>length × pixels-per-radian / range</c>, is exact to a part in a
    /// million at the ranges this is used at.
    /// </remarks>
    public double RangeAtWhichABodyCovers(double bodyLengthMetres, double pixels) =>
        bodyLengthMetres * CornerPixelsPerRadian / pixels;

    /// <summary>
    /// The optics a camera's attributes describe, or null where they are not a camera's: an attribute
    /// missing, not a number, or not positive, or a field of view that is not under 180 degrees.
    /// </summary>
    /// <param name="attributes">The actor's attributes, by id.</param>
    public static CameraOptics? FromAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (!attributes.TryGetValue(WidthAttribute, out string? width)
            || !attributes.TryGetValue(HeightAttribute, out string? height)
            || !attributes.TryGetValue(FovAttribute, out string? fov)
            || !int.TryParse(width, NumberStyles.Integer, CultureInfo.InvariantCulture, out int columns)
            || !int.TryParse(height, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows)
            || !double.TryParse(fov, NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees)
            || columns <= 0 || rows <= 0 || !double.IsFinite(degrees) || degrees <= 0.0 || degrees >= 180.0)
        {
            return null;
        }

        return new CameraOptics(columns, rows, degrees);
    }

    /// <summary>The optics in the report's words.</summary>
    public override string ToString() =>
        $"{ImageWidth}x{ImageHeight}, fov {HorizontalFovDegrees.ToString("0.###", CultureInfo.InvariantCulture)} deg";
}
