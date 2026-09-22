// Ported from Eclipse SUMO's reference TraCI client, tools/traci/connection.py -- the socket, the
// frame header, the status envelope, the subscription reader, simulationStep and getVersion.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net.Sockets;

namespace CarlaNet.Sumo;

/// <summary>
/// A TraCI client: one TCP socket to a running <c>sumo</c>, and the frame codec over it.
/// </summary>
/// <remarks>
/// <para>TraCI is a wire protocol, not a library. A message is a four-byte big-endian length and
/// then one command; the answer is a four-byte length, a status envelope naming the command and
/// whether SUMO accepted it, and then whatever that command returns. Nothing of SUMO's is loaded
/// into this process -- the coupling is the frame layout and the identifiers in
/// <see cref="TraCIConstants"/>, and a mismatched SUMO is reported by
/// <see cref="GetVersion"/> rather than discovered as undefined behaviour.</para>
///
/// <para><b>One thread owns a connection.</b> A frame is read strictly in order and the read
/// position is connection state, so two threads exchanging at once would interleave halves of two
/// answers. Nothing is locked, because the co-simulation bridge drives this from the one thread
/// that owns the tick; a re-entrant call is detected and rejected rather than left to corrupt the
/// stream.</para>
/// </remarks>
public sealed class TraCIConnection : IDisposable
{
    /// <summary>
    /// A response identifier is its command identifier plus this. It is how a subscription answer
    /// is matched to the subscription that asked for it.
    /// </summary>
    private const int ResponseOffset = 0x10;

    /// <summary>SUMO's result codes, from the status envelope in front of every answer.</summary>
    private const int ResultSuccess = 0x00;

    private readonly Socket _socket;
    private readonly TraCIWriter _writer = new();
    private readonly TraCIReader _reader = new();
    private readonly Dictionary<int, TraCISubscriptionResults> _subscriptions = [];
    private readonly List<(string ObjectId, int VariableId, string Message)> _failures = [];

    private byte[] _frame = new byte[4096];
    private int _busy;
    private bool _closed;

    private TraCIConnection(Socket socket)
    {
        _socket = socket;
    }

    /// <summary>
    /// Variables SUMO refused inside the last step's subscription results, one entry per variable
    /// per object.
    /// </summary>
    /// <remarks>
    /// A refusal here is per variable, not per step: the rest of the step's results are intact and
    /// the object is simply missing that one value. It is surfaced rather than printed, which is
    /// what SUMO's own client does with it, so a caller can decide whether a variable it does not
    /// use going missing matters.
    /// </remarks>
    public IReadOnlyList<(string ObjectId, int VariableId, string Message)> LastStepFailures => _failures;

    /// <summary>Whether the connection has been closed, by <see cref="Close"/> or by SUMO.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Connect to a TraCI server that is already listening.
    /// </summary>
    /// <param name="host">The host <c>sumo</c> is listening on.</param>
    /// <param name="port">The port given to <c>sumo --remote-port</c>.</param>
    /// <param name="connectTimeout">
    /// How long to keep retrying the connect. <c>sumo</c> opens its listening socket only after it
    /// has parsed and loaded the network, which for a city-sized one is seconds, so a single
    /// attempt fails for a reason that is not an error.
    /// </param>
    /// <param name="receiveTimeout">
    /// How long to wait for an answer, or <see cref="Timeout.InfiniteTimeSpan"/> to wait forever,
    /// which is what SUMO's own client does. A step's answer takes as long as the step, so a finite
    /// value here has to be larger than the slowest step the scenario will produce.
    /// </param>
    public static TraCIConnection Connect(string host,
                                          int port,
                                          TimeSpan connectTimeout,
                                          TimeSpan? receiveTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        DateTime deadline = DateTime.UtcNow + connectTimeout;
        SocketException? last = null;
        while (true)
        {
            Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Nagle's algorithm would hold a small command back waiting for another to coalesce
                // with, and TraCI is a request-response protocol where the next command depends on
                // this one's answer, so there is never another to wait for.
                socket.NoDelay = true;
                socket.Connect(host, port);
                if (receiveTimeout is { } timeout && timeout != Timeout.InfiniteTimeSpan)
                {
                    socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                }

                return new TraCIConnection(socket);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
                if (DateTime.UtcNow >= deadline)
                {
                    throw new FatalTraCIError(
                        $"Could not connect to a TraCI server at {host}:{port} within "
                        + $"{connectTimeout.TotalSeconds:0.#} s.", exception);
                }

                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// Ask SUMO which protocol it speaks and which build it is.
    /// </summary>
    /// <remarks>
    /// This is the check a linked binding cannot make. The identifiers in
    /// <see cref="TraCIConstants"/> are pinned to one SUMO release, and a server from a different
    /// one may answer the same commands with a different frame layout. Asking makes a mismatch a
    /// reportable fact at the moment of connection instead of a value that decodes into something
    /// plausible later.
    /// </remarks>
    public (int ApiVersion, string ServerVersion) GetVersion()
    {
        _writer.BeginBareCommand(TraCIConstants.CMD_GETVERSION);
        TraCIReader reader = Exchange(TraCIConstants.CMD_GETVERSION);
        reader.ReadLength();
        int response = reader.ReadUnsignedByte();
        if (response != TraCIConstants.CMD_GETVERSION)
        {
            throw new FatalTraCIError(
                $"SUMO answered the version handshake with response 0x{response:x2} rather than "
                + $"0x{TraCIConstants.CMD_GETVERSION:x2}.");
        }

        return (reader.ReadInt(), reader.ReadString());
    }

    /// <summary>
    /// Advance the simulation and collect what every subscription delivered with it.
    /// </summary>
    /// <param name="targetTime">
    /// The simulated second to advance to, or zero for exactly one step. A time at or before the
    /// current one does nothing.
    /// </param>
    /// <returns>How many subscription responses the step carried.</returns>
    /// <remarks>
    /// SUMO fills subscription results as part of advancing, so they arrive in this answer and
    /// reading them afterwards costs only the decode. It also means the subscribed set is charged
    /// to the step whether or not anything reads it: measured at 388 vehicles on Arapahoe, 3.73 ms
    /// with nothing subscribed against 9.26 ms subscribed and unread.
    /// </remarks>
    public int SimulationStep(double targetTime = 0.0)
    {
        _writer.BeginBareCommand(TraCIConstants.CMD_SIMSTEP);
        _writer.WriteRawDouble(targetTime);
        TraCIReader reader = Exchange(TraCIConstants.CMD_SIMSTEP);

        foreach (TraCISubscriptionResults results in _subscriptions.Values)
        {
            results.Reset();
        }

        _failures.Clear();

        int responses = reader.ReadInt();
        for (int index = 0; index < responses; index++)
        {
            ReadSubscription(reader);
        }

        return responses;
    }

    /// <summary>
    /// Read one variable of one object directly, without a subscription.
    /// </summary>
    /// <param name="getCommandId">The domain's get command, such as
    /// <see cref="TraCIConstants.CMD_GET_VEHICLE_VARIABLE"/>.</param>
    /// <param name="variableId">The variable within that domain.</param>
    /// <param name="objectId">Which object, or the empty string for a domain-wide variable.</param>
    /// <remarks>
    /// One round trip per variable per object. Correct, and the wrong shape for a step loop: the
    /// same seven variables read this way for every vehicle measured 145.1 ms per step at 388
    /// vehicles against 10.4 ms by subscription.
    /// </remarks>
    public TraCIValue GetVariable(int getCommandId, int variableId, string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        TraCIVariables.EnsureDecodable(getCommandId, variableId);

        _writer.BeginCommand(getCommandId, variableId, objectId);
        TraCIReader reader = Exchange(getCommandId);
        reader.ReadLength();
        int response = reader.ReadUnsignedByte();
        int returnedVariable = reader.ReadUnsignedByte();
        string returnedObject = reader.ReadString();
        if (response - getCommandId != ResponseOffset
            || returnedVariable != variableId
            || returnedObject != objectId)
        {
            throw new FatalTraCIError(
                $"SUMO answered get 0x{getCommandId:x2}/0x{variableId:x2} for '{objectId}' with "
                + $"0x{response:x2}/0x{returnedVariable:x2} for '{returnedObject}'.");
        }

        return reader.ReadTypedValue(variableId);
    }

    /// <summary>
    /// Begin a command that changes something, returning the writer its payload is appended to.
    /// The command goes out when <see cref="SendPreparedCommand"/> is called.
    /// </summary>
    /// <remarks>
    /// Two calls rather than one because a set command's payload varies in shape from a single byte
    /// to a compound of seven mixed values, and building it through the writer directly is what
    /// keeps that out of an argument list or an object allocated per call.
    /// </remarks>
    internal TraCIWriter BeginSetCommand(int setCommandId, int variableId, string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        _writer.BeginCommand(setCommandId, variableId, objectId);
        return _writer;
    }

    /// <summary>
    /// Send the command begun by <see cref="BeginSetCommand"/> and check SUMO accepted it. A set
    /// command returns nothing beyond its status.
    /// </summary>
    internal void SendPreparedCommand(int setCommandId) => Exchange(setCommandId);

    /// <summary>
    /// Have SUMO deliver these variables of this object with every step from now on.
    /// </summary>
    /// <param name="subscribeCommandId">The domain's subscribe command, such as
    /// <see cref="TraCIConstants.CMD_SUBSCRIBE_VEHICLE_VARIABLE"/>.</param>
    /// <param name="objectId">The object to subscribe.</param>
    /// <param name="variableIds">The variables wanted. Subscribing none removes the subscription.</param>
    /// <param name="begin">First simulated second the subscription applies to.</param>
    /// <param name="end">Last simulated second it applies to.</param>
    public void Subscribe(int subscribeCommandId,
                          string objectId,
                          IReadOnlyList<int> variableIds,
                          double begin = TraCIConstants.INVALID_DOUBLE_VALUE,
                          double end = TraCIConstants.INVALID_DOUBLE_VALUE)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(variableIds);

        int getCommandId = subscribeCommandId - TraCIVariables.SubscribeOffset;
        foreach (int variableId in variableIds)
        {
            TraCIVariables.EnsureDecodable(getCommandId, variableId);
        }

        _writer.BeginSubscription(subscribeCommandId, begin, end, objectId);
        _writer.WriteRawUnsignedByte((byte)variableIds.Count);
        foreach (int variableId in variableIds)
        {
            _writer.WriteRawUnsignedByte((byte)variableId);
        }

        TraCIReader reader = Exchange(subscribeCommandId);
        if (variableIds.Count == 0)
        {
            // Removing a subscription is answered by the status alone; there are no results to
            // carry, so there is no subscription block behind it.
            return;
        }

        (string answeredObject, int response) = ReadSubscription(reader);
        if (response - subscribeCommandId != ResponseOffset || answeredObject != objectId)
        {
            throw new FatalTraCIError(
                $"SUMO answered subscribe 0x{subscribeCommandId:x2} for '{objectId}' with "
                + $"0x{response:x2} for '{answeredObject}'.");
        }
    }

    /// <summary>
    /// Stop delivering an object's variables.
    /// </summary>
    /// <remarks>
    /// SUMO drops a subscription together with the object it is on, so unsubscribing an object it
    /// has already removed is refused with "The subscription to remove was not found" -- and SUMO
    /// writes a line to its own console for each one. A caller releasing a vehicle that has arrived
    /// should forget it locally instead; see
    /// <see cref="SumoVehicleSubscription.Release(string)"/>.
    /// </remarks>
    public void Unsubscribe(int subscribeCommandId, string objectId) =>
        Subscribe(subscribeCommandId, objectId, []);

    /// <summary>
    /// The store a domain's subscription results land in, created on first use.
    /// </summary>
    /// <param name="subscribeResponseId">The domain's subscription response identifier, such as
    /// <see cref="TraCIConstants.RESPONSE_SUBSCRIBE_VEHICLE_VARIABLE"/>.</param>
    public TraCISubscriptionResults SubscriptionResults(int subscribeResponseId)
    {
        if (!_subscriptions.TryGetValue(subscribeResponseId, out TraCISubscriptionResults? results))
        {
            results = new TraCISubscriptionResults();
            _subscriptions[subscribeResponseId] = results;
        }

        return results;
    }

    /// <summary>
    /// Tell SUMO the session is over and close the socket. SUMO exits when its last client closes.
    /// </summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            _writer.BeginBareCommand(TraCIConstants.CMD_CLOSE);
            Exchange(TraCIConstants.CMD_CLOSE);
        }
        catch (Exception exception) when (exception is FatalTraCIError or TraCIException or SocketException)
        {
            // SUMO having gone already is the outcome this method wants. Anything it says on the
            // way out changes nothing, and raising here would mask whatever sent the caller here.
        }
        finally
        {
            _closed = true;
            _socket.Close();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
        _socket.Dispose();
    }

    /// <summary>
    /// Put the command the writer holds on the socket, read the answer, and check the status
    /// envelope in front of it. The reader is left positioned at whatever the command returns.
    /// </summary>
    private TraCIReader Exchange(int commandId)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A second TraCI exchange started while one was in flight. A connection's read "
                + "position is connection state, so it belongs to exactly one thread; give each "
                + "thread its own connection.");
        }

        try
        {
            Send(_writer.Finish().Span);
            ReadFrame();
            CheckStatus(commandId);
            return _reader;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Send(ReadOnlySpan<byte> message)
    {
        try
        {
            int sent = 0;
            while (sent < message.Length)
            {
                sent += _socket.Send(message[sent..]);
            }
        }
        catch (SocketException exception)
        {
            _closed = true;
            throw new FatalTraCIError("The TraCI socket failed while sending a command.", exception);
        }
    }

    private void ReadFrame()
    {
        Span<byte> prefix = stackalloc byte[4];
        ReceiveExactly(prefix);
        int total = BinaryPrimitives.ReadInt32BigEndian(prefix);
        int length = total - 4;
        if (length < 0)
        {
            throw new FatalTraCIError($"A TraCI frame declared a total length of {total} bytes.");
        }

        if (_frame.Length < length)
        {
            _frame = new byte[Math.Max(length, _frame.Length * 2)];
        }

        ReceiveExactly(_frame.AsSpan(0, length));
        _reader.Reset(_frame, length);
    }

    private void ReceiveExactly(Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int received;
            try
            {
                received = _socket.Receive(destination[read..]);
            }
            catch (SocketException exception)
            {
                _closed = true;
                throw new FatalTraCIError("The TraCI socket failed while reading an answer.", exception);
            }

            if (received == 0)
            {
                _closed = true;
                throw new FatalTraCIError(
                    "SUMO closed the TraCI connection. A microsimulation that ends, crashes or is "
                    + "killed looks exactly like this from here.");
            }

            read += received;
        }
    }

    /// <summary>
    /// Read the status TraCI puts in front of every answer: which command it is answering, whether
    /// SUMO accepted it, and what SUMO has to say about it.
    /// </summary>
    private void CheckStatus(int commandId)
    {
        _reader.ReadUnsignedByte();                       // The status block's own length.
        int answered = _reader.ReadUnsignedByte();
        int result = _reader.ReadUnsignedByte();
        string description = _reader.ReadString();

        if (result != ResultSuccess || description.Length > 0)
        {
            throw new TraCIException(description.Length > 0 ? description : $"result 0x{result:x2}",
                                     answered, result);
        }

        if (answered != commandId)
        {
            throw new FatalTraCIError(
                $"SUMO answered command 0x{commandId:x2} with a status for 0x{answered:x2}. The "
                + "frame stream is out of step and nothing further can be read from it.");
        }
    }

    /// <summary>
    /// Read one subscription block: which object, and each variable it carried.
    /// </summary>
    private (string ObjectId, int Response) ReadSubscription(TraCIReader reader)
    {
        reader.ReadLength();
        int response = reader.ReadUnsignedByte();

        // A variable subscription answers about the object it names. A context subscription answers
        // about every object near it, and its block carries an extra domain byte and an object
        // count. The two ranges are SUMO's own, from connection.py:238-242.
        bool isVariableSubscription =
            (response >= TraCIConstants.RESPONSE_SUBSCRIBE_INDUCTIONLOOP_VARIABLE
             && response <= TraCIConstants.RESPONSE_SUBSCRIBE_BUSSTOP_VARIABLE)
            || (response >= TraCIConstants.RESPONSE_SUBSCRIBE_PARKINGAREA_VARIABLE
                && response <= TraCIConstants.RESPONSE_SUBSCRIBE_OVERHEADWIRE_VARIABLE);

        string objectId = reader.ReadString();
        if (!isVariableSubscription)
        {
            throw new FatalTraCIError(
                $"SUMO sent a context subscription response (0x{response:x2}) for '{objectId}'. This "
                + "client subscribes objects individually and has no decoder for one, and the block "
                + "cannot be skipped because its length is not written where the reader is.");
        }

        if (!_subscriptions.TryGetValue(response, out TraCISubscriptionResults? results))
        {
            throw new FatalTraCIError(
                $"SUMO sent subscription response 0x{response:x2} for '{objectId}', which nothing in "
                + "this client subscribed to.");
        }

        int variables = reader.ReadUnsignedByte();
        for (int index = 0; index < variables; index++)
        {
            int variableId = reader.ReadUnsignedByte();
            int status = reader.ReadUnsignedByte();
            if (status != ResultSuccess)
            {
                _failures.Add((objectId, variableId, reader.ReadTypedValue(variableId).AsString));
                continue;
            }

            results.Add(objectId, variableId, reader.ReadTypedValue(variableId));
        }

        return (objectId, response);
    }
}
