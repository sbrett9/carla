using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// The incoming half of the frame codec: the types a subscription actually carries, and what
/// happens at the edges of a frame.
/// </summary>
/// <remarks>
/// The bytes here are written by <see cref="TraCIWriter"/>, so these are round trips rather than
/// independent checks of the decode -- what establishes the decode is
/// <see cref="ReferenceClientAgreementTests"/>, which differences it against SUMO's own client on a
/// live simulation. What these establish is that every type the bridge reads survives the trip, and
/// that a frame read past its end or at the wrong offset fails rather than returning something.
/// </remarks>
public class TraCIReaderTests
{
    [Fact]
    public void ADoubleSurvivesTheRoundTrip()
    {
        TraCIValue value = RoundTrip(TraCIVariables.Speed, writer => writer.WriteDouble(13.89));
        Assert.Equal(TraCIValueKind.Double, value.Kind);
        Assert.Equal(13.89, value.AsDouble);
    }

    [Fact]
    public void AnIntegerSurvivesTheRoundTrip()
    {
        TraCIValue value = RoundTrip(TraCIVariables.Signals, writer => writer.WriteInt(10));
        Assert.Equal(TraCIValueKind.Integer, value.Kind);
        Assert.Equal(10, value.AsInt);
        Assert.Equal(SumoVehicleSignals.BlinkerLeft | SumoVehicleSignals.BrakeLight,
                     (SumoVehicleSignals)value.AsInt);
    }

    [Fact]
    public void AStringSurvivesTheRoundTrip()
    {
        TraCIValue value = RoundTrip(TraCIVariables.LaneId, writer => writer.WriteString(":junction_0_0"));
        Assert.Equal(TraCIValueKind.String, value.Kind);
        Assert.Equal(":junction_0_0", value.AsString);
    }

    [Fact]
    public void AStringListSurvivesTheRoundTrip()
    {
        TraCIValue value = RoundTrip(TraCIConstants.TRACI_ID_LIST,
                                     writer => writer.WriteStringList(["first", "second"]));
        Assert.Equal(TraCIValueKind.StringList, value.Kind);
        Assert.Equal(["first", "second"], value.AsStringList);
    }

    /// <summary>
    /// A string's length is in bytes, not characters. A vehicle type or an edge id carrying a
    /// non-ASCII character is the case where counting characters reads short and then mis-decodes
    /// everything after it in the frame.
    /// </summary>
    [Fact]
    public void AStringIsMeasuredInBytesNotCharacters()
    {
        TraCIValue value = RoundTrip(TraCIVariables.TypeId, writer => writer.WriteString("Straße_1"));
        Assert.Equal("Straße_1", value.AsString);
    }

    [Fact]
    public void ACompoundOfTypedMembersSurvivesTheRoundTrip()
    {
        TraCIValue value = RoundTrip(TraCIConstants.VAR_PARAMETER_WITH_KEY, writer =>
        {
            writer.WriteCompoundHeader(2);
            writer.WriteString("has.dwell");
            writer.WriteString("true");
        });

        Assert.Equal(TraCIValueKind.Compound, value.Kind);
        Assert.Equal(2, value.AsCompound.Count);
        Assert.Equal("has.dwell", value.AsCompound[0].AsString);
        Assert.Equal("true", value.AsCompound[1].AsString);
    }

    /// <summary>
    /// The accessor is the type check. Reading a string as a number is a reader asking for the
    /// wrong variable, and it says so rather than parsing whatever the string held.
    /// </summary>
    [Fact]
    public void ReadingAValueAsTheWrongTypeSaysSo()
    {
        TraCIValue value = RoundTrip(TraCIVariables.RoadId, writer => writer.WriteString("42"));
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => value.AsDouble);
        Assert.Contains("String", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Double", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A frame read past its end is fatal, not recoverable. There is no position to resume from:
    /// the bytes that would have said where the next value starts are the ones that are missing.
    /// </summary>
    [Fact]
    public void ReadingPastTheEndOfAFrameIsFatal()
    {
        TraCIReader reader = new();
        reader.Reset([0x00, 0x00, 0x00], 3);
        Assert.Throws<FatalTraCIError>(() => reader.ReadDouble());
    }

    /// <summary>
    /// A type byte the client has no decoder for is fatal for the same reason: its width is part of
    /// what is unknown, so the read position cannot be advanced past it.
    /// </summary>
    [Fact]
    public void AnUnknownValueTypeIsFatal()
    {
        TraCIReader reader = new();
        reader.Reset([0x7E, 0x00, 0x00, 0x00, 0x00], 5);
        FatalTraCIError failure = Assert.Throws<FatalTraCIError>(
            () => reader.ReadTypedValue(TraCIVariables.Speed));
        Assert.Contains("0x7e", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A count that could not possibly fit in what is left of the frame says the frame is being read
    /// at the wrong offset, and is caught there rather than after allocating for it.
    /// </summary>
    [Fact]
    public void AnImpossibleElementCountIsFatal()
    {
        TraCIReader reader = new();
        reader.Reset([TraCIConstants.TYPE_STRINGLIST, 0x7F, 0xFF, 0xFF, 0xFF], 5);
        Assert.Throws<FatalTraCIError>(() => reader.ReadTypedValue(TraCIConstants.TRACI_ID_LIST));
    }

    /// <summary>
    /// Write a typed value the way a command would and read it back the way a subscription result
    /// is read, skipping the frame and command headers in between.
    /// </summary>
    private static TraCIValue RoundTrip(int variableId, Action<TraCIWriter> write)
    {
        TraCIWriter writer = new();
        writer.BeginBareCommand(TraCIConstants.CMD_GETVERSION);
        write(writer);
        byte[] frame = writer.Finish().ToArray();

        // The frame's four-byte length and the command's two-byte header are in front of the value.
        const int HeaderLength = 6;
        TraCIReader reader = new();
        reader.Reset(frame[HeaderLength..], frame.Length - HeaderLength);
        return reader.ReadTypedValue(variableId);
    }
}
