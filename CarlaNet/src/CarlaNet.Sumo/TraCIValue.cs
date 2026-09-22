namespace CarlaNet.Sumo;

/// <summary>
/// One value read off the TraCI wire, together with the type the wire said it was.
/// </summary>
/// <remarks>
/// <para>TraCI prefixes every value with a type byte, so a decoded value already knows what it is.
/// This type keeps that knowledge instead of flattening everything to a string and re-parsing it at
/// the point of use: <see cref="AsDouble"/> on a value that arrived as a string throws, naming both
/// kinds, rather than returning a number parsed out of whatever the string happened to look
/// like.</para>
///
/// <para>Numbers and positions live in the struct's own fields, so reading a subscribed speed or
/// position allocates nothing. Strings and lists are references, which they would be
/// anyway.</para>
/// </remarks>
public readonly struct TraCIValue
{
    private readonly double _first;
    private readonly double _second;
    private readonly double _third;
    private readonly object? _reference;

    private TraCIValue(TraCIValueKind kind, double first, double second, double third, object? reference)
    {
        Kind = kind;
        _first = first;
        _second = second;
        _third = third;
        _reference = reference;
    }

    /// <summary>What this value is, as the type byte in front of it declared.</summary>
    public TraCIValueKind Kind { get; }

    /// <summary>A double, an integer or a byte value, whichever arrived.</summary>
    public static TraCIValue Number(TraCIValueKind kind, double value) => new(kind, value, 0, 0, null);

    /// <summary>A string or a list, whichever arrived.</summary>
    public static TraCIValue Reference(TraCIValueKind kind, object value) => new(kind, 0, 0, 0, value);

    /// <summary>A position, projected or geographic, in two or three components.</summary>
    public static TraCIValue Position(TraCIValueKind kind, double first, double second, double third) =>
        new(kind, first, second, third, null);

    /// <summary>
    /// The value as a double. Accepts anything numeric -- SUMO answers some variables with an
    /// integer where a reader expects a quantity, and widening one is lossless.
    /// </summary>
    public double AsDouble => Kind switch
    {
        TraCIValueKind.Double or TraCIValueKind.Integer
            or TraCIValueKind.Byte or TraCIValueKind.UnsignedByte => _first,
        _ => throw Mismatch(nameof(AsDouble), TraCIValueKind.Double),
    };

    /// <summary>
    /// The value as a 32-bit integer. A double is not accepted: narrowing one silently is how a
    /// wrongly decoded frame stops looking wrong.
    /// </summary>
    public int AsInt => Kind switch
    {
        TraCIValueKind.Integer or TraCIValueKind.Byte or TraCIValueKind.UnsignedByte => (int)_first,
        _ => throw Mismatch(nameof(AsInt), TraCIValueKind.Integer),
    };

    /// <summary>The value as a string.</summary>
    public string AsString => Kind == TraCIValueKind.String
        ? (string)_reference!
        : throw Mismatch(nameof(AsString), TraCIValueKind.String);

    /// <summary>The value as a list of strings.</summary>
    public IReadOnlyList<string> AsStringList => Kind == TraCIValueKind.StringList
        ? (string[])_reference!
        : throw Mismatch(nameof(AsStringList), TraCIValueKind.StringList);

    /// <summary>The value as a list of doubles.</summary>
    public IReadOnlyList<double> AsDoubleList => Kind == TraCIValueKind.DoubleList
        ? (double[])_reference!
        : throw Mismatch(nameof(AsDoubleList), TraCIValueKind.DoubleList);

    /// <summary>The members of a compound value, each carrying its own kind.</summary>
    public IReadOnlyList<TraCIValue> AsCompound => Kind == TraCIValueKind.Compound
        ? (TraCIValue[])_reference!
        : throw Mismatch(nameof(AsCompound), TraCIValueKind.Compound);

    /// <summary>The points of a polygon, in the order SUMO wrote them.</summary>
    public IReadOnlyList<(double X, double Y)> AsPolygon => Kind == TraCIValueKind.Polygon
        ? ((double X, double Y)[])_reference!
        : throw Mismatch(nameof(AsPolygon), TraCIValueKind.Polygon);

    /// <summary>The value as red, green, blue and alpha bytes.</summary>
    public (byte Red, byte Green, byte Blue, byte Alpha) AsColor => Kind == TraCIValueKind.Color
        ? (((byte, byte, byte, byte))_reference!)
        : throw Mismatch(nameof(AsColor), TraCIValueKind.Color);

    /// <summary>
    /// The value as a position. The third component is zero for a two-dimensional one. A projected
    /// position is metres east and north; a geographic one is degrees of longitude then latitude,
    /// which is SUMO's order and the reverse of how a latitude and longitude are usually spoken.
    /// </summary>
    public (double First, double Second, double Third) AsPosition => Kind switch
    {
        TraCIValueKind.Position2D or TraCIValueKind.Position3D
            or TraCIValueKind.GeoPosition2D or TraCIValueKind.GeoPosition3D => (_first, _second, _third),
        _ => throw Mismatch(nameof(AsPosition), TraCIValueKind.Position2D),
    };

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        TraCIValueKind.None => "none",
        TraCIValueKind.String or TraCIValueKind.StringList or TraCIValueKind.DoubleList
            or TraCIValueKind.Compound or TraCIValueKind.Polygon or TraCIValueKind.Color
            => $"{Kind}({_reference})",
        TraCIValueKind.Position2D or TraCIValueKind.GeoPosition2D => $"{Kind}({_first}, {_second})",
        TraCIValueKind.Position3D or TraCIValueKind.GeoPosition3D
            => $"{Kind}({_first}, {_second}, {_third})",
        _ => $"{Kind}({_first})",
    };

    private InvalidOperationException Mismatch(string accessor, TraCIValueKind expected) =>
        new($"A TraCI value of kind {Kind} was read through {accessor}, which reads {expected}. "
            + "The type came off the wire with the value, so this is a reader asking for the wrong "
            + "variable, not a value that could be converted.");
}
