using System.Collections.Concurrent;

namespace CarlaNet.Transport.MsgPackRpc;

/// <summary>
/// The type a value handed to the RPC layer as <c>object</c> is serialized as.
/// </summary>
/// <remarks>
/// <para>MessagePack resolves a formatter from the type it is given, and the RPC layer holds its
/// arguments and results as <c>object</c>, so the type has to come from the value. Its runtime type
/// is right for everything except a list: a list's runtime type is whatever the caller happened to
/// build, and a collection expression handed to an <see cref="IReadOnlyList{T}"/> is a
/// compiler-synthesized list no resolver knows. Serialized by its runtime type, the call throws --
/// inside the RPC layer, several frames from the code that chose the collection.</para>
///
/// <para>So a list that is not an array is written as the <see cref="IReadOnlyList{T}"/> of its
/// element type: a msgpack array of its elements, which is byte for byte what an array or a
/// <see cref="List{T}"/> already produced, and no caller has to know which it passed. An array keeps
/// its own type, because one of them is not an array on the wire: a <c>byte[]</c> is a binary
/// blob.</para>
/// </remarks>
internal static class MsgPackWireType
{
    private static readonly ConcurrentDictionary<Type, Type> Resolved = new();

    /// <summary>The type to serialize <paramref name="value"/> as.</summary>
    public static Type Of(object value) => Resolved.GetOrAdd(value.GetType(), Resolve);

    private static Type Resolve(Type runtime)
    {
        if (runtime.IsArray)
        {
            return runtime;
        }

        foreach (Type contract in runtime.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                return contract;
            }
        }

        return runtime;
    }
}
