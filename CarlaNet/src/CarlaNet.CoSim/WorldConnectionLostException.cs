namespace CarlaNet.CoSim;

/// <summary>
/// A call on the CARLA world failed because the connection to it failed, carrying that failure.
/// </summary>
/// <remarks>
/// Raised by <see cref="WorldConnectionGuard"/> and never seen outside a session: the session turns it
/// into a refusal of the stage it happened in, with the connection's own failure as the refusal's
/// inner exception.
/// </remarks>
internal sealed class WorldConnectionLostException : Exception
{
    public WorldConnectionLostException(string operation, Exception failure)
        : base($"{operation}: {failure.Message}", failure)
    {
        Operation = operation;
    }

    /// <summary>The world operation whose call failed.</summary>
    public string Operation { get; }
}
