// §5.1 — Builder-style helper for declaring a complete set of RPC handlers
// against a single MsgPackRpcServer. Has no functional independence from
// MsgPackRpcServer; exists purely so TM-specific server-side classes
// (TrafficManagerServer in CarlaNet.TrafficManager) can assemble their
// handler set in a more fluent style and unit tests can introspect the
// registered set.
namespace CarlaNet.Transport.MsgPackRpc.Server;

/// <summary>
/// Fluent helper around <see cref="MsgPackRpcServer"/>. Each Bind* call
/// forwards to the underlying server's RegisterHandler / RegisterVoidHandler
/// and additionally records the method name for diagnostics.
/// </summary>
public sealed class MsgPackRpcHandlerRegistry
{
    private readonly MsgPackRpcServer _server;
    private readonly List<string> _names = new();

    public MsgPackRpcHandlerRegistry(MsgPackRpcServer server)
    {
        _server = server;
    }

    /// <summary>All method names registered through this registry, in order.</summary>
    public IReadOnlyList<string> RegisteredMethods => _names;

    public MsgPackRpcHandlerRegistry Bind(string name, Action handler)
    { _server.RegisterVoidHandler(name, handler); _names.Add(name); return this; }

    public MsgPackRpcHandlerRegistry Bind<TArg>(string name, Action<TArg> handler)
    { _server.RegisterVoidHandler<TArg>(name, handler); _names.Add(name); return this; }

    public MsgPackRpcHandlerRegistry Bind<TArg1, TArg2>(string name, Action<TArg1, TArg2> handler)
    { _server.RegisterVoidHandler<TArg1, TArg2>(name, handler); _names.Add(name); return this; }

    public MsgPackRpcHandlerRegistry Bind<TArg1, TArg2, TArg3>(string name, Action<TArg1, TArg2, TArg3> handler)
    { _server.RegisterVoidHandler<TArg1, TArg2, TArg3>(name, handler); _names.Add(name); return this; }

    public MsgPackRpcHandlerRegistry BindFunc<TResult>(string name, Func<TResult> handler)
    { _server.RegisterHandler<TResult>(name, handler); _names.Add(name); return this; }

    public MsgPackRpcHandlerRegistry BindFunc<TArg, TResult>(string name, Func<TArg, TResult> handler)
    { _server.RegisterHandler<TArg, TResult>(name, handler); _names.Add(name); return this; }

    public MsgPackRpcHandlerRegistry BindFunc<TArg1, TArg2, TResult>(string name, Func<TArg1, TArg2, TResult> handler)
    { _server.RegisterHandler<TArg1, TArg2, TResult>(name, handler); _names.Add(name); return this; }

    public MsgPackRpcHandlerRegistry BindFunc<TArg1, TArg2, TArg3, TResult>(string name, Func<TArg1, TArg2, TArg3, TResult> handler)
    { _server.RegisterHandler<TArg1, TArg2, TArg3, TResult>(name, handler); _names.Add(name); return this; }
}
