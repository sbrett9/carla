namespace CarlaNet.Transport.MsgPackRpc;

/// <summary>An error the server answered a call with, in the server's words.</summary>
public sealed class CarlaRpcException(string message) : Exception(message)
{
    /// <summary>
    /// Whether the server answered that it binds no function of that name, which is what a server
    /// built before the call existed answers. rpclib, which the CARLA server is built on, says
    /// "server could not find function"; CarlaNet's own RPC server, which stands in for it in tests,
    /// says "unknown method".
    /// </summary>
    public bool NamesNoSuchFunction =>
        Message.Contains("could not find function", StringComparison.Ordinal)
        || Message.Contains("unknown method", StringComparison.Ordinal);

    /// <summary>
    /// Whether the server answered that it binds the function with another number of arguments, which is
    /// what a server built before the call gained an argument answers. rpclib says the function "was
    /// called with an invalid number of arguments"; CarlaNet's own RPC server says "wrong argument count".
    /// </summary>
    public bool NamesWrongArgumentCount =>
        Message.Contains("invalid number of arguments", StringComparison.Ordinal)
        || Message.Contains("wrong argument count", StringComparison.Ordinal);
}
