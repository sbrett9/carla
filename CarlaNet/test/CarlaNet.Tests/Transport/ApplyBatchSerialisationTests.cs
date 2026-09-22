// A batch must serialise whatever IReadOnlyList it was built as.
//
// MessagePack resolves a formatter from the argument's RUNTIME type, not from the parameter's
// declared one. A collection expression produces a compiler-generated read-only wrapper with no
// registered formatter, so a batch built with `[..]` threw at serialisation while the identical
// batch in an array succeeded - and it threw inside the RPC layer, several frames from the caller
// that chose the collection type, so the stack did not name the cause.
//
// Not hypothetical: the co-simulation bridge's first live run died on it at the first vehicle it
// tried to spawn. CarlaClient converts at the RPC boundary so no caller has to know.
using CarlaNet.Types.Rpc.Commands;
using MessagePack;

namespace CarlaNet.Tests.Transport;

public class ApplyBatchSerialisationTests
{
    private static Command[] Batch() =>
    [
        new ApplyTransformCommand(11u, new Transform()),
        new ApplyTransformCommand(12u, new Transform()),
    ];

    /// <summary>The batch as the wrapper a collection expression produces, not as an array.</summary>
    private static IReadOnlyList<Command> AsCollectionExpression()
    {
        Command[] batch = Batch();
        IReadOnlyList<Command> wrapped = [batch[0], batch[1]];
        return wrapped;
    }

    /// <summary>What the RPC layer does: serialise by the argument's runtime type.</summary>
    private static void SerialiseAsRpcWould(object argument) =>
        MessagePackSerializer.Serialize(argument.GetType(), argument);

    [Fact]
    public void A_Collection_Expression_Is_Neither_An_Array_Nor_A_List()
    {
        // The premise. If this stops holding, the hazard below stops existing and so does this file.
        IReadOnlyList<Command> wrapped = AsCollectionExpression();
        Assert.IsNotType<Command[]>(wrapped);
        Assert.IsNotType<List<Command>>(wrapped);
    }

    [Fact]
    public void The_Wrapper_Has_No_Formatter_Which_Is_The_Hazard()
    {
        // Serialising the wrapper by its runtime type is what the RPC layer used to do, and it
        // throws. Asserting it keeps the reason for the conversion visible: without this, someone
        // removing the conversion would see every other test still pass.
        Assert.ThrowsAny<MessagePackSerializationException>(
            () => SerialiseAsRpcWould(AsCollectionExpression()));
    }

    [Fact]
    public void The_Conversion_Makes_Both_Shapes_Serialisable()
    {
        // What CarlaClient's conversion produces, for each shape a caller might pass. An argument
        // that is already an array must come through unchanged rather than being copied.
        IReadOnlyList<Command> wrapped = AsCollectionExpression();
        Command[] fromWrapper = wrapped as Command[] ?? [.. wrapped];
        Assert.Equal(2, fromWrapper.Length);
        SerialiseAsRpcWould(fromWrapper);

        Command[] alreadyAnArray = Batch();
        Command[] fromArray = alreadyAnArray as Command[] ?? [.. alreadyAnArray];
        Assert.Same(alreadyAnArray, fromArray);
        SerialiseAsRpcWould(fromArray);
    }
}
