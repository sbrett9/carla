using CarlaNet.Sumo;

namespace CarlaNet.Sumo.Tests;

/// <summary>
/// The end-to-end check: start <c>sumo</c>, connect over TraCI, step it, close it.
/// </summary>
/// <remarks>
/// These run a real microsimulation on the minimal network in <c>Fixtures</c>. Nothing is faked --
/// a client for a wire protocol can be made to pass any test against a fake server, because the
/// fake and the client would be two readings of the same guess.
/// </remarks>
public class SumoConnectionTests
{
    /// <summary>
    /// The acceptance check. A version comes back, one step advances the clock by exactly one step
    /// length, and the connection closes cleanly.
    /// </summary>
    /// <remarks>
    /// The clock is asserted against <see cref="SumoConnection.StepLength"/> read from the server
    /// rather than against the 0.05 s the fixture declares, so this says "the step advanced time by
    /// a step" and not "the fixture still says what it used to say".
    /// </remarks>
    [RequiresSumoFact]
    public void AnEmptySimulationHandshakesStepsAndCloses()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.EmptySimulation);

        Assert.True(sumo.ApiVersion > 0);
        Assert.False(string.IsNullOrWhiteSpace(sumo.ServerVersion));
        Assert.True(sumo.StepLength > 0);

        double before = sumo.Time;
        sumo.Step();
        double after = sumo.Time;

        Assert.Equal(before + sumo.StepLength, after, precision: 9);
    }

    /// <summary>
    /// The protocol version is a thing to ask for, and a linked binding cannot ask. A server
    /// speaking a version other than the one the constant table was translated from is reportable
    /// at the moment of connection rather than discovered later as a value that decoded oddly.
    /// </summary>
    [RequiresSumoFact]
    public void TheServerReportsTheProtocolVersionTheClientWasWrittenFor()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.EmptySimulation);

        Assert.Equal(TraCIConstants.TRACI_VERSION, sumo.ApiVersion);
        Assert.Contains("SUMO", sumo.ServerVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version mismatch has to be visible, not merely survivable. The client cannot make SUMO
    /// answer a different number, so what is asserted is that the number is carried out of the
    /// handshake intact and compared -- which is what the test above then does with it.
    /// </summary>
    [RequiresSumoFact]
    public void TheHandshakeCarriesTheServersOwnBuildString()
    {
        SumoInstallation installation = SumoInstallation.LocateOrThrow();
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.EmptySimulation);

        Assert.NotNull(installation.Release);
        Assert.Contains(installation.Release!, sumo.ServerVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stepping to a time rather than by a step, which is what a bridge running SUMO at a
    /// different rate from the world clock does.
    /// </summary>
    [RequiresSumoFact]
    public void SteppingToATimeAdvancesToThatTime()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.EmptySimulation);

        sumo.Step(1.0);

        Assert.Equal(1.0, sumo.Time, precision: 9);
    }

    /// <summary>
    /// A hundred steps, to show the frame stream stays in step with itself. A codec that is off by
    /// a byte somewhere survives one exchange surprisingly often and never survives a hundred.
    /// </summary>
    [RequiresSumoFact]
    public void AHundredStepsStayInStep()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);

        double start = sumo.Time;
        for (int step = 0; step < 100; step++)
        {
            sumo.Step();
        }

        Assert.Equal(start + (100 * sumo.StepLength), sumo.Time, precision: 6);
    }

    /// <summary>
    /// SUMO refusing a command is recoverable and the connection survives it. Asking about a
    /// vehicle that is not there is the everyday case: a bridge reads a vehicle it saw last step
    /// and SUMO has since removed it.
    /// </summary>
    [RequiresSumoFact]
    public void AskingAboutAVehicleThatIsNotThereIsRecoverable()
    {
        using SumoConnection sumo = SumoFixtures.Open(SumoFixtures.TwoVehicles);
        sumo.Step();

        TraCIException refusal = Assert.Throws<TraCIException>(() => sumo.Vehicles.Speed("no_such_vehicle"));
        Assert.Equal(TraCIConstants.CMD_GET_VEHICLE_VARIABLE, refusal.CommandId);
        Assert.NotEqual(0, refusal.ResultCode);

        // The point of the distinction: the connection is still usable afterwards.
        double before = sumo.Time;
        sumo.Step();
        Assert.Equal(before + sumo.StepLength, sumo.Time, precision: 9);
    }

    /// <summary>
    /// Nothing listening on the port is a connect that fails, not one that hangs. The wait is short
    /// so a machine with no SUMO does not sit on this for half a minute.
    /// </summary>
    [Fact]
    public void ConnectingToNothingFails()
    {
        FatalTraCIError failure = Assert.Throws<FatalTraCIError>(
            () => TraCIConnection.Connect("127.0.0.1", UnusedPort(), TimeSpan.FromMilliseconds(200)));

        Assert.Contains("127.0.0.1", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>sumo</c> that exits instead of listening -- here because it was handed a network file
    /// where a configuration should be -- fails at the connect rather than leaving the session
    /// waiting on a process that has already gone.
    /// </summary>
    [RequiresSumoFact]
    public void AConfigurationSumoRejectsFailsAtTheConnect()
    {
        Assert.Throws<FatalTraCIError>(() => SumoFixtures.Open(
            SumoFixtures.NotAConfiguration,
            new SumoLaunchOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                Output = _ => { },
            }));
    }

    /// <summary>
    /// A closed connection stays closed, and says so rather than reading a socket that is gone.
    /// </summary>
    [RequiresSumoFact]
    public void SteppingAClosedConnectionSaysSo()
    {
        SumoConnection sumo = SumoFixtures.Open(SumoFixtures.EmptySimulation);
        sumo.Step();
        sumo.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sumo.Step());
    }

    /// <summary>
    /// Closing twice is what a <c>using</c> around an explicit close does, and it does nothing the
    /// second time.
    /// </summary>
    [RequiresSumoFact]
    public void ClosingTwiceIsHarmless()
    {
        SumoConnection sumo = SumoFixtures.Open(SumoFixtures.EmptySimulation);
        sumo.Dispose();
        sumo.Dispose();
    }

    /// <summary>
    /// Two sessions at once, which a binding holding one static connection could not do. Each has
    /// its own <c>sumo</c>, its own port and its own clock, and stepping one does not move the
    /// other.
    /// </summary>
    [RequiresSumoFact]
    public void TwoSessionsAreIndependent()
    {
        using SumoConnection first = SumoFixtures.Open(SumoFixtures.EmptySimulation);
        using SumoConnection second = SumoFixtures.Open(SumoFixtures.EmptySimulation);

        first.Step();
        first.Step();
        second.Step();

        Assert.Equal(2 * first.StepLength, first.Time, precision: 9);
        Assert.Equal(second.StepLength, second.Time, precision: 9);
    }

    private static int UnusedPort()
    {
        using System.Net.Sockets.Socket probe = new(System.Net.Sockets.AddressFamily.InterNetwork,
                                                    System.Net.Sockets.SocketType.Stream,
                                                    System.Net.Sockets.ProtocolType.Tcp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
