namespace CarlaNet.Sumo;

/// <summary>
/// What a <see cref="TraCIValue"/> holds, as the type byte that preceded it on the wire said.
/// </summary>
/// <remarks>
/// These are not TraCI's type codes renamed: <see cref="TraCIConstants"/> holds those. This is the
/// shape the decoded value takes in memory, which is a smaller set -- a geographic position and a
/// projected one arrive under different type codes and both decode to two doubles.
/// </remarks>
public enum TraCIValueKind
{
    /// <summary>Nothing was decoded. The default, and never a value SUMO sent.</summary>
    None = 0,

    /// <summary>A 32-bit signed integer.</summary>
    Integer,

    /// <summary>A signed byte.</summary>
    Byte,

    /// <summary>An unsigned byte.</summary>
    UnsignedByte,

    /// <summary>A double.</summary>
    Double,

    /// <summary>A UTF-8 string.</summary>
    String,

    /// <summary>A list of UTF-8 strings.</summary>
    StringList,

    /// <summary>A list of doubles.</summary>
    DoubleList,

    /// <summary>A projected position in metres: easting and northing.</summary>
    Position2D,

    /// <summary>A projected position in metres: easting, northing and height.</summary>
    Position3D,

    /// <summary>A geographic position in degrees: longitude and latitude, in that order.</summary>
    GeoPosition2D,

    /// <summary>A geographic position in degrees: longitude, latitude and altitude in metres.</summary>
    GeoPosition3D,

    /// <summary>Red, green, blue and alpha, each a byte.</summary>
    Color,

    /// <summary>A sequence of two-dimensional points.</summary>
    Polygon,

    /// <summary>A fixed-length sequence of further values, each carrying its own type.</summary>
    Compound,
}
