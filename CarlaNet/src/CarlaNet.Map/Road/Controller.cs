// Source: carla/road/Controller.h
//
// Aggregates signals (typically traffic-light heads) that operate as a unit at
// a junction. MapBuilder populates Junctions/Signals after parsing.
namespace CarlaNet.Map.Road;

public sealed class Controller
{
    public ContId Id { get; }
    public string Name { get; }
    public uint Sequence { get; }

    /// <summary>Junctions this controller is bound to. Filled by MapBuilder.</summary>
    public SortedSet<JuncId> Junctions { get; } = new();

    /// <summary>Signals owned by this controller. Filled by MapBuilder.</summary>
    public SortedSet<SignId> Signals { get; } = new();

    public Controller(ContId id, string name, uint sequence)
    {
        Id = id;
        Name = name;
        Sequence = sequence;
    }
}
