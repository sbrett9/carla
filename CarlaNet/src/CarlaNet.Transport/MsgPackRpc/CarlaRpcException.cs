namespace CarlaNet.Transport.MsgPackRpc;

public sealed class CarlaRpcException(string message) : Exception(message);
