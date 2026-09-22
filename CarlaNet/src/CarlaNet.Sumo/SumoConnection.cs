// The session shape is SUMO's own: tools/traci/main.py's start() launches `sumo --remote-port N`,
// connects, asks CMD_GETVERSION, and closes the connection to end the process.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CarlaNet.Sumo;

/// <summary>
/// One SUMO session: the <c>sumo</c> process, the TraCI connection to it, and the domains read
/// through it.
/// </summary>
/// <remarks>
/// <para><c>sumo</c> runs as a child process and is spoken to over a socket. Nothing of SUMO's is
/// loaded into this one, so a microsimulation that asserts or dies surfaces here as an exception a
/// caller can act on rather than taking the process down with it, and a second consumer is another
/// connection rather than a second simulation.</para>
///
/// <para>Unlike a binding with a static API, there is nothing process-global here: several sessions
/// can be open at once, each with its own <c>sumo</c>, its own port and its own state.</para>
/// </remarks>
public sealed class SumoConnection : IDisposable
{
    /// <summary>How long to give <c>sumo</c> to exit on its own after the connection closes.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    private readonly TraCIConnection _traci;
    private readonly Process? _process;
    private bool _disposed;

    private SumoConnection(TraCIConnection traci,
                           Process? process,
                           int apiVersion,
                           string serverVersion,
                           IReadOnlyList<int> subscribedVariables)
    {
        _traci = traci;
        _process = process;
        ApiVersion = apiVersion;
        ServerVersion = serverVersion;
        Simulation = new SumoSimulationDomain(traci);
        Vehicles = new SumoVehicleDomain(traci, subscribedVariables);
        StepLength = Simulation.StepLength;
    }

    /// <summary>The TraCI protocol version the server answered the handshake with.</summary>
    /// <remarks>
    /// <see cref="TraCIConstants.TRACI_VERSION"/> is the one the constant table was translated from.
    /// A server answering a different number speaks a protocol this client was not written for, and
    /// this is the only moment that is knowable.
    /// </remarks>
    public int ApiVersion { get; }

    /// <summary>The server's own version string, for example <c>SUMO 1.27.0</c>.</summary>
    public string ServerVersion { get; }

    /// <summary>
    /// Seconds of simulated time in one <see cref="Step"/>, read once at open because a simulation's
    /// step length does not change.
    /// </summary>
    public double StepLength { get; }

    /// <summary>The clock, and the step's departures and arrivals.</summary>
    public SumoSimulationDomain Simulation { get; }

    /// <summary>The vehicles, and the subscription their state is read through.</summary>
    public SumoVehicleDomain Vehicles { get; }

    /// <summary>The connection underneath, for a domain this class does not wrap.</summary>
    public TraCIConnection TraCI => _traci;

    /// <summary>Simulated seconds since the configuration's begin.</summary>
    public double Time => Simulation.Time;

    /// <summary>
    /// Start <c>sumo</c> on a configuration and connect to it.
    /// </summary>
    /// <param name="installation">The SUMO whose <c>sumo</c> binary is launched.</param>
    /// <param name="configurationPath">A <c>.sumocfg</c>. SUMO resolves the network and route files
    /// it names relative to its own directory, so it may sit anywhere.</param>
    /// <param name="options">How to launch and connect; the defaults suit a scenario run.</param>
    public static SumoConnection Start(SumoInstallation installation,
                                       string configurationPath,
                                       SumoLaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        options ??= new SumoLaunchOptions();

        int port = options.Port ?? FreePort();
        ProcessStartInfo start = new(installation.Sumo)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = options.Output is not null,
            RedirectStandardError = options.Output is not null,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(configurationPath);
        start.ArgumentList.Add("--remote-port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string argument in options.ExtraArguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start)
                          ?? throw new FatalTraCIError($"Could not start {installation.Sumo}.");

        if (options.Output is not null)
        {
            process.OutputDataReceived += (_, line) => Report(options.Output, line.Data);
            process.ErrorDataReceived += (_, line) => Report(options.Output, line.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            return Attach(process, "127.0.0.1", port, options);
        }
        catch
        {
            KillQuietly(process);
            throw;
        }
    }

    /// <summary>
    /// Connect to a <c>sumo</c> that is already listening, started by something else. Closing this
    /// connection ends that simulation, but the process is not this session's to wait on or kill.
    /// </summary>
    public static SumoConnection Attach(string host, int port, SumoLaunchOptions? options = null) =>
        Attach(null, host, port, options ?? new SumoLaunchOptions());

    /// <summary>
    /// Advance the simulation by one step, or to a given simulated second.
    /// </summary>
    /// <param name="targetTime">
    /// The simulated second to advance to, or zero for exactly one step. A time at or before the
    /// current one does nothing, which is how a caller can be sure a step happened only by reading
    /// the clock back.
    /// </param>
    /// <remarks>
    /// Subscription results for the new state arrive with this answer, so reading them afterwards
    /// costs only the decode.
    /// </remarks>
    public void Step(double targetTime = 0.0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _traci.SimulationStep(targetTime);
    }

    /// <summary>
    /// Close the connection, which ends the simulation, and wait for the <c>sumo</c> this session
    /// started to exit.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _traci.Dispose();

        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.WaitForExit(ShutdownGrace))
            {
                // SUMO exits when its last client disconnects. One that has not after the socket
                // closed is wedged, and leaving it running would hold the port and the output files.
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or SystemException)
        {
            // Already gone, which is what this wanted.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static SumoConnection Attach(Process? process, string host, int port, SumoLaunchOptions options)
    {
        TraCIConnection traci = TraCIConnection.Connect(host, port, options.ConnectTimeout,
                                                        options.ReceiveTimeout);
        try
        {
            (int apiVersion, string serverVersion) = traci.GetVersion();
            return new SumoConnection(traci, process, apiVersion, serverVersion,
                                      options.SubscribedVehicleVariables);
        }
        catch
        {
            traci.Dispose();
            throw;
        }
    }

    private static void Report(Action<string> output, string? line)
    {
        if (line is not null)
        {
            output(line);
        }
    }

    /// <summary>
    /// A port nothing is listening on, found the way SUMO's own client finds one: bind to port zero,
    /// read what the operating system assigned, and release it.
    /// </summary>
    private static int FreePort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or SystemException)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }
}
