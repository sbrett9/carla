using System.Buffers.Binary;
using System.Text;
using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// The collisions SUMO reports decode field by field, as SUMO's own client reads them, and not as the
/// compound their header declares.
/// </summary>
/// <remarks>
/// The bytes are laid out as SUMO 1.27.0's server writes them (<c>TraCIServerAPI_Simulation.cpp</c>,
/// <c>VAR_COLLISIONS</c>): a compound type byte, a member count of one plus four per collision, a typed
/// integer holding the number of collisions, then nine typed fields for each. The count is wrong by
/// five per collision, which is why a generic decode stops part-way. That the layout is SUMO's is
/// established live, against a real collision, in the co-simulation session's tests.
/// </remarks>
public sealed class SumoCollisionDecodingTests
{
    [Fact]
    public void TwoCollisionsDecodeFieldByField()
    {
        byte[] frame = Collisions(
            ("goer", "turner", "truck", "car", 21.5, 27.78, "collision", "approach_0", 55.25),
            ("a", "b", "t1", "t2", 0.0, 3.0, "junction", ":centre_0_0", 1.5));

        IReadOnlyList<SumoCollision> decoded = SumoSimulationDomain.ReadCollisions(Reader(frame));

        Assert.Equal(
            [
                new SumoCollision("goer", "turner", "truck", "car", 21.5, 27.78, "collision", "approach_0", 55.25),
                new SumoCollision("a", "b", "t1", "t2", 0.0, 3.0, "junction", ":centre_0_0", 1.5),
            ],
            decoded);
    }

    [Fact]
    public void NoCollisionsDecodeToNone()
    {
        Assert.Empty(SumoSimulationDomain.ReadCollisions(Reader(Collisions())));
    }

    [Fact]
    public void AReadingOfTheDeclaredCountStopsPartWayThroughTheFirstCollision()
    {
        // What the generic decoder would have done with the same bytes: five members read, of the ten
        // that follow, and the frame left mid-collision.
        byte[] frame = Collisions(("goer", "turner", "truck", "car", 21.5, 27.78, "collision", "approach_0", 55.25));
        TraCIReader reader = Reader(frame);

        TraCIValue generic = reader.ReadTypedValue(TraCIConstants.VAR_COLLISIONS);

        Assert.Equal(5, generic.AsCompound.Count);
        Assert.True(reader.HasMore);
    }

    [Fact]
    public void AValueThatIsNotACompoundIsRefusedAsUnreadable()
    {
        byte[] frame = [TraCIConstants.TYPE_INTEGER, 0, 0, 0, 0];

        Assert.Throws<FatalTraCIError>(() => SumoSimulationDomain.ReadCollisions(Reader(frame)));
    }

    [Fact]
    public void AFieldOfTheWrongTypeIsRefusedAsUnreadable()
    {
        // A well-formed frame with the collider's speed written as an integer: every value in it reads,
        // and only the kind of the fifth field says the collision is being read at the wrong offset.
        byte[] frame = Collisions(colliderSpeedAsInteger: true,
                                  ("goer", "turner", "truck", "car", 21.0, 27.78, "collision", "approach_0", 55.25));

        Assert.Throws<FatalTraCIError>(() => SumoSimulationDomain.ReadCollisions(Reader(frame)));
    }

    private static TraCIReader Reader(byte[] frame)
    {
        var reader = new TraCIReader();
        reader.Reset(frame, frame.Length);
        return reader;
    }

    /// <summary>The value as SUMO's server writes it, from its type byte on.</summary>
    private static byte[] Collisions(
        params (string Collider, string Victim, string ColliderType, string VictimType, double ColliderSpeed,
                double VictimSpeed, string Kind, string Lane, double Position)[] collisions) =>
        Collisions(colliderSpeedAsInteger: false, collisions);

    /// <summary>The same, optionally with each collider's speed written as a typed integer.</summary>
    private static byte[] Collisions(
        bool colliderSpeedAsInteger,
        params (string Collider, string Victim, string ColliderType, string VictimType, double ColliderSpeed,
                double VictimSpeed, string Kind, string Lane, double Position)[] collisions)
    {
        var bytes = new List<byte> { TraCIConstants.TYPE_COMPOUND };
        Int(bytes, 1 + (collisions.Length * 4));
        bytes.Add(TraCIConstants.TYPE_INTEGER);
        Int(bytes, collisions.Length);
        foreach (var c in collisions)
        {
            String(bytes, c.Collider);
            String(bytes, c.Victim);
            String(bytes, c.ColliderType);
            String(bytes, c.VictimType);
            if (colliderSpeedAsInteger)
            {
                bytes.Add(TraCIConstants.TYPE_INTEGER);
                Int(bytes, (int)c.ColliderSpeed);
            }
            else
            {
                Double(bytes, c.ColliderSpeed);
            }

            Double(bytes, c.VictimSpeed);
            String(bytes, c.Kind);
            String(bytes, c.Lane);
            Double(bytes, c.Position);
        }

        return [.. bytes];
    }

    private static void Int(List<byte> bytes, int value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void String(List<byte> bytes, string value)
    {
        bytes.Add(TraCIConstants.TYPE_STRING);
        byte[] text = Encoding.UTF8.GetBytes(value);
        Int(bytes, text.Length);
        bytes.AddRange(text);
    }

    private static void Double(List<byte> bytes, double value)
    {
        bytes.Add(TraCIConstants.TYPE_DOUBLE);
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        bytes.AddRange(buffer);
    }
}
