// Source: carla/road/Signal.h
namespace CarlaNet.Map.Road;

public enum SignalOrientation
{
    Positive,
    Negative,
    Both,
}

/// <summary>Cross-reference to another signal that this signal depends on.</summary>
public sealed class SignalDependency
{
    public string DependencyId { get; }
    public string Type { get; }

    public SignalDependency(string dependencyId, string type)
    {
        DependencyId = dependencyId;
        Type = type;
    }
}

/// <summary>
/// A traffic signal / sign anchored to a road. Mutable: Wave 2's MapBuilder fills
/// the `Controllers` set and `Transform` after the geometry is built.
/// </summary>
public sealed class Signal
{
    public RoadId RoadId { get; }
    public SignId SignalId { get; }
    public double S { get; }
    public double T { get; }
    public string Name { get; }
    public string DynamicRaw { get; }
    public string OrientationRaw { get; }
    public double ZOffset { get; }
    public string Country { get; }
    public string Type { get; }
    public string Subtype { get; }
    public double Value { get; }
    public string Unit { get; }
    public double Height { get; }
    public double Width { get; }
    public string Text { get; }
    public double HOffset { get; }
    public double Pitch { get; }
    public double Roll { get; }

    /// <summary>Other signals this signal depends on (e.g. shared pole).</summary>
    public List<SignalDependency> Dependencies { get; } = new();

    /// <summary>World-space transform — populated by MapBuilder once geometry is resolved.</summary>
    public Transform Transform { get; set; }

    /// <summary>Set by MapBuilder. Controllers that own this signal.</summary>
    public SortedSet<ContId> Controllers { get; } = new();

    /// <summary>If true, signal position uses world (inertial) coords rather than road (s/t).</summary>
    public bool UsingInertialPosition { get; set; }

    public Signal(
        RoadId roadId,
        SignId signalId,
        double s,
        double t,
        string name,
        string dynamic,
        string orientation,
        double zOffset,
        string country,
        string type,
        string subtype,
        double value,
        string unit,
        double height,
        double width,
        string text,
        double hOffset,
        double pitch,
        double roll)
    {
        RoadId = roadId;
        SignalId = signalId;
        S = s;
        T = t;
        Name = name;
        DynamicRaw = dynamic;
        OrientationRaw = orientation;
        ZOffset = zOffset;
        Country = country;
        Type = type;
        Subtype = subtype;
        Value = value;
        Unit = unit;
        Height = height;
        Width = width;
        Text = text;
        HOffset = hOffset;
        Pitch = pitch;
        Roll = roll;
    }

    /// <summary>Returns true iff the upstream "dynamic" string equals "yes".</summary>
    public bool IsDynamic => DynamicRaw == "yes";

    public SignalOrientation Orientation => OrientationRaw switch
    {
        "+" => SignalOrientation.Positive,
        "-" => SignalOrientation.Negative,
        _   => SignalOrientation.Both,
    };
}
