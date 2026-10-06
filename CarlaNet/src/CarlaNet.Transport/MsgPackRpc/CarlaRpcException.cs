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
}
