using System.Buffers.Binary;
using System.Text;
using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// A vehicle's stops decode field by field, as SUMO's own client reads them, and not as the compound
/// their header declares.
/// </summary>
/// <remarks>
/// The bytes are laid out as SUMO 1.27.0's server writes them (<c>TraCIServer::wrapNextStopDataVector</c>,
/// for <c>VAR_NEXT_STOPS2</c>): a compound type byte, a member count of one plus four per stop, a typed
/// integer holding the number of stops, then sixteen typed fields for each. The count is wrong by twelve
/// per stop, which is why a generic decode stops part-way. That the layout is SUMO's is established
/// against a real SUMO in <see cref="SumoSimulationSubscriptionTests"/>.
/// </remarks>
public sealed class SumoStopDecodingTests
{
    private const double Invalid = TraCIConstants.INVALID_DOUBLE_VALUE;

    [Fact]
    public void TwoStopsDecodeFieldByFieldWithSumoSInvalidTimesAsNone()
    {
        byte[] frame = Stops(
            new Stop("ahead_0", 59.8, 60.0, "", 0, 3.0, Invalid, Invalid, 10.0, 13.0, "", "", "", "", "", 0.0),
            new Stop("turn_east_0", 49.8, 50.0, "lot_3", 65, 4.0, 3600.0, 12.5, 14.0, Invalid,
                     "s", "j", "unload", "trip_7", "line_2", 1.5));

        IReadOnlyList<SumoStop> decoded = SumoVehicleDomain.ReadStops(Reader(frame));

        Assert.Equal(
            [
                new SumoStop("ahead_0", 59.8, 60.0, "", SumoStopFlags.None, 3.0, null, null, 10.0, 13.0,
                             "", "", "", "", "", 0.0),
                new SumoStop("turn_east_0", 49.8, 50.0, "lot_3", SumoStopFlags.Parking | SumoStopFlags.ParkingArea,
                             4.0, 3600.0, 12.5, 14.0, null, "s", "j", "unload", "trip_7", "line_2", 1.5),
            ],
            decoded);
        Assert.False(decoded[0].IsParking);
        Assert.True(decoded[1].IsParking);
    }

    [Fact]
    public void NoStopsDecodeToNone()
    {
        Assert.Empty(SumoVehicleDomain.ReadStops(Reader(Stops())));
    }

    [Fact]
    public void AReadingOfTheDeclaredCountStopsPartWayThroughTheFirstStop()
    {
        // What the generic decoder would have done with the same bytes: five members read, of the
        // seventeen that follow, and the frame left mid-stop.
        byte[] frame = Stops(new Stop("ahead_0", 59.8, 60.0, "", 0, 3.0, Invalid, Invalid, 10.0, 13.0, "", "", "", "", "", 0.0));
        TraCIReader reader = Reader(frame);

        TraCIValue generic = reader.ReadTypedValue(TraCIConstants.VAR_NEXT_STOPS2);

        Assert.Equal(5, generic.AsCompound.Count);
        Assert.True(reader.HasMore);
    }

    [Fact]
    public void AValueThatIsNotACompoundIsRefusedAsUnreadable()
    {
        byte[] frame = [TraCIConstants.TYPE_INTEGER, 0, 0, 0, 0];

        Assert.Throws<FatalTraCIError>(() => SumoVehicleDomain.ReadStops(Reader(frame)));
    }

    [Fact]
    public void AFieldOfTheWrongTypeIsRefusedAsUnreadable()
    {
        // A well-formed frame with the flags written as a double: every value in it reads, and only the
        // kind of the fourth field says the stop is being read at the wrong offset.
        byte[] frame = Stops(flagsAsDouble: true,
                             new Stop("ahead_0", 59.8, 60.0, "", 0, 3.0, Invalid, Invalid, 10.0, 13.0, "", "", "", "", "", 0.0));

        Assert.Throws<FatalTraCIError>(() => SumoVehicleDomain.ReadStops(Reader(frame)));
    }

    [Fact]
    public void TheFlagsAreSumoSOwnStopBits()
    {
        Assert.Equal(1, (int)SumoStopFlags.Parking);
        Assert.Equal(2, (int)SumoStopFlags.Triggered);
        Assert.Equal(4, (int)SumoStopFlags.ContainerTriggered);
        Assert.Equal(8, (int)SumoStopFlags.BusStop);
        Assert.Equal(16, (int)SumoStopFlags.ContainerStop);
        Assert.Equal(32, (int)SumoStopFlags.ChargingStation);
        Assert.Equal(64, (int)SumoStopFlags.ParkingArea);
        Assert.Equal(128, (int)SumoStopFlags.OverheadWire);
    }

    [Fact]
    public void AStopSaysInOneLineWhereItIsAndTheTimesSumoHolds()
    {
        var stop = new SumoStop("ahead_0", 59.8, 60.0, "", SumoStopFlags.None, 3.0, null, null, 10.0, 13.0,
                                "", "", "", "", "", 0.0);

        Assert.Equal("ahead_0 at 60 m; duration 3 s, arrival 10 s, departure 13 s", stop.ToString());
    }

    private static TraCIReader Reader(byte[] frame)
    {
        var reader = new TraCIReader();
        reader.Reset(frame, frame.Length);
        return reader;
    }

    /// <summary>
    /// One stop's sixteen fields, named as SUMO's client names them. <see cref="Stops(Stop[])"/> writes
    /// them in the server's order, which is not this one.
    /// </summary>
    private sealed record Stop(
        string Lane, double StartPos, double EndPos, string StoppingPlace, int Flags, double Duration,
        double Until, double IntendedArrival, double Arrival, double Depart, string Split, string Join,
        string ActType, string TripId, string Line, double Speed);

    private static byte[] Stops(params Stop[] stops) => Stops(flagsAsDouble: false, stops);

    /// <summary>The value as SUMO's server writes it, from its type byte on.</summary>
    private static byte[] Stops(bool flagsAsDouble, params Stop[] stops)
    {
        var bytes = new List<byte> { TraCIConstants.TYPE_COMPOUND };
        Int(bytes, 1 + (stops.Length * 4));
        bytes.Add(TraCIConstants.TYPE_INTEGER);
        Int(bytes, stops.Length);
        foreach (Stop stop in stops)
        {
            String(bytes, stop.Lane);
            Double(bytes, stop.EndPos);
            String(bytes, stop.StoppingPlace);
            if (flagsAsDouble)
            {
                Double(bytes, stop.Flags);
            }
            else
            {
                bytes.Add(TraCIConstants.TYPE_INTEGER);
                Int(bytes, stop.Flags);
            }

            Double(bytes, stop.Duration);
            Double(bytes, stop.Until);
            Double(bytes, stop.StartPos);
            Double(bytes, stop.IntendedArrival);
            Double(bytes, stop.Arrival);
            Double(bytes, stop.Depart);
            String(bytes, stop.Split);
            String(bytes, stop.Join);
            String(bytes, stop.ActType);
            String(bytes, stop.TripId);
            String(bytes, stop.Line);
            Double(bytes, stop.Speed);
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
