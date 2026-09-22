namespace CarlaNet.Sumo;

/// <summary>
/// How <see cref="SumoConnection.Start"/> launches <c>sumo</c> and connects to it.
/// </summary>
public sealed record SumoLaunchOptions
{
    /// <summary>Command-line options appended after the configuration, overriding what it declares.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// The port to tell <c>sumo</c> to listen on, or <see langword="null"/> to take one the
    /// operating system says is free. A fixed port is worth asking for only when something outside
    /// this process has to find the server too.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>
    /// How long to keep trying to connect. <c>sumo</c> opens its listening socket only after
    /// parsing and loading the network, which for a city-sized one takes seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait for an answer, or <see langword="null"/> to wait indefinitely, which is what
    /// SUMO's own client does. A step's answer takes as long as the step, so any finite value has to
    /// exceed the slowest step the scenario will produce.
    /// </summary>
    public TimeSpan? ReceiveTimeout { get; init; }

    /// <summary>
    /// Where SUMO's own console output goes, line by line, or <see langword="null"/> to let it
    /// inherit this process's console.
    /// </summary>
    /// <remarks>
    /// Worth setting for anything long-running. SUMO writes a line per refused command, and a
    /// bridge that unsubscribes arrived vehicles instead of releasing them locally produces one per
    /// arrival -- several a second on a real scenario, which buries everything else it has to say.
    /// </remarks>
    public Action<string>? Output { get; init; }

    /// <summary>
    /// The vehicle variables the session subscribes vehicles to. Every one is charged to every step
    /// for every subscribed vehicle, so this is a budget rather than a preference.
    /// </summary>
    public IReadOnlyList<int> SubscribedVehicleVariables { get; init; } = TraCIVariables.VehicleState;
}
