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

    /// <summary>
    /// What <c>sumo-gui</c> is given beyond <c>sumo</c>'s arguments, so that it serves a client the way
    /// <c>sumo</c> does (<see cref="SumoLaunchOptions.Gui"/>).
    /// </summary>
    /// <remarks>
    /// <para>Each is read from SUMO's own source at the pinned release, not assumed from the name:</para>
    /// <list type="bullet">
    /// <item><c>--start</c>. The GUI runs a simulation only once its play button is pressed, and a
    /// TraCI server inside it answers nothing until then (<c>docs/web/docs/TraCI/index.md</c>), so
    /// without it the connection's first command waits on a person.</item>
    /// <item><c>--quit-on-end</c>. When the client closes the connection the simulation ends, and the
    /// GUI then asks in a modal dialog whether to close its views and stays open
    /// (<c>GUIApplicationWindow::handleEvent_SimulationEnded</c>) -- a process that never exits, which
    /// <see cref="Dispose"/> would kill after its grace. With it the GUI exits as <c>sumo</c> does. It
    /// cannot close the GUI in the middle of a run: while a client is connected SUMO overrides every
    /// ending state but the connection's own closing to "running" (<c>MSNet::adaptToState</c>). And a
    /// configuration the GUI cannot load exits it at once rather than leaving a window with an error
    /// in it and the connection waiting out its timeout.</item>
    /// <item><c>--delay 0</c>. The GUI sleeps this long between steps, and a gui-settings file the
    /// configuration names can raise it from zero (<c>GUISettingsHandler::getDelay</c>); given on the
    /// command line it wins. The pace of a run belongs to whoever steps it, not to the window.</item>
    /// <item><c>--message-log stdout --error-log stderr</c>. A GUI build removes the console from
    /// SUMO's message handlers before it loads anything ("within gui-based applications, nothing is
    /// reported to the console", <c>GUILoadThread::run</c>) and writes to its own message window
    /// instead, so without these <see cref="SumoLaunchOptions.Output"/> would receive nothing: no
    /// warnings to count, and no last words to quote when SUMO fails. These put the console back
    /// exactly as <c>sumo</c> has it -- warnings and errors on stderr, messages on stdout only when
    /// verbose (<c>MsgHandler::initOutputOptions</c>) -- and the window keeps its own copy.</item>
    /// </list>
    /// <para>Not given: <c>--game</c>, which replaces the view with SUMO's traffic-light game, and
    /// <c>--window-size</c> and <c>--window-pos</c>, which the GUI otherwise restores from where its
    /// last window was.</para>
    /// </remarks>
    internal static readonly IReadOnlyList<string> GuiArguments =
    [
        "--start",
        "--quit-on-end",
        "--delay", "0",
        "--message-log", "stdout",
        "--error-log", "stderr",
    ];

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
        ProcessId = process?.Id;
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

    /// <summary>
    /// The operating system's id for the <c>sumo</c> this session started, or <see langword="null"/>
    /// for one it attached to.
    /// </summary>
    /// <remarks>
    /// What an operator needs to find the process when a run reports that SUMO stopped answering.
    /// </remarks>
    public int? ProcessId { get; }

    /// <summary>Simulated seconds since the configuration's begin.</summary>
    public double Time => Simulation.Time;

    /// <summary>
    /// Start <c>sumo</c> on a configuration and connect to it.
    /// </summary>
    /// <param name="installation">The SUMO whose <c>sumo</c> binary -- or <c>sumo-gui</c>, under
    /// <see cref="SumoLaunchOptions.Gui"/> -- is launched.</param>
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
        (string executable, IReadOnlyList<string> arguments) =
            CommandLine(installation, configurationPath, port, options);
        // CreateNoWindow suppresses a console window only; a graphical sumo-gui still opens its own.
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = options.Output is not null,
            RedirectStandardError = options.Output is not null,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start)
                      ?? throw new FatalTraCIError($"Could not start {executable}.");
        }
        catch (System.ComponentModel.Win32Exception failed)
        {
            // The operating system would not run the file -- it is not there, not executable, or not a
            // program for this platform. That is SUMO failing to start, and is reported as such rather
            // than as an exception nobody calling a launch has reason to expect.
            throw new FatalTraCIError($"Could not start {executable}: {failed.Message}", failed);
        }

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
    /// Wait up to <paramref name="bound"/> for the <c>sumo</c> this session started to exit, and then
    /// until every line it wrote has been handed to <see cref="SumoLaunchOptions.Output"/>.
    /// </summary>
    /// <returns>Whether it exited; true for a <c>sumo</c> this session did not start.</returns>
    /// <remarks>
    /// SUMO says why it closed the connection on its console a moment before the socket closes, and
    /// that line reaches <see cref="SumoLaunchOptions.Output"/> from another thread, so a caller that
    /// quotes SUMO after the connection failed waits here first rather than racing the pipe.
    /// </remarks>
    public bool WaitForExit(TimeSpan bound)
    {
        if (_process is null || _disposed)
        {
            return true;
        }

        try
        {
            if (!_process.WaitForExit(bound))
            {
                return false;
            }

            _process.WaitForExit();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or SystemException)
        {
            return true;
        }
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
            // A SUMO that stopped answering is hung, and waiting for it to notice the socket close is
            // waiting on the thing that already failed to happen.
            if (_process.WaitForExit(_traci.StoppedAnswering ? TimeSpan.Zero : ShutdownGrace))
            {
                // Exited; the wait with no bound returns once the last of its output has been handed
                // to Output, so nothing it said on the way out is lost.
                _process.WaitForExit();
            }
            else
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

    /// <summary>
    /// The executable <see cref="Start"/> launches and the arguments it gives it, in order.
    /// </summary>
    /// <remarks>
    /// <c>sumo-gui</c> is given <c>sumo</c>'s arguments unchanged and <see cref="GuiArguments"/> after
    /// them, so the configuration, the port and every caller's override reach it exactly as they would
    /// reach <c>sumo</c>. The caller's own arguments come last in both, as the overrides they are.
    /// </remarks>
    internal static (string Executable, IReadOnlyList<string> Arguments) CommandLine(
        SumoInstallation installation,
        string configurationPath,
        int port,
        SumoLaunchOptions options)
    {
        List<string> arguments =
        [
            "-c", configurationPath,
            "--remote-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        if (options.Gui)
        {
            arguments.AddRange(GuiArguments);
        }

        arguments.AddRange(options.ExtraArguments);
        return (options.Gui ? installation.SumoGui : installation.Sumo, arguments);
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

    /// <summary>How long a launch that failed waits for its sumo to exit before disposing it.</summary>
    private const int ConsoleDrainMilliseconds = 5000;

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            // Its console lines arrive on a reader thread a moment after they are written, so a sumo
            // that printed why it stopped and exited could be disposed before they did, and the launch
            // refused with no reason. Once the timed wait sees the exit, the untimed one returns only
            // when the redirected output has all been handed over.
            if (process.WaitForExit(ConsoleDrainMilliseconds))
            {
                process.WaitForExit();
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
