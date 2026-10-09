// Ported from Eclipse SUMO's reference TraCI client, tools/traci/exceptions.py.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// SUMO refused a command, and the connection is still usable.
/// </summary>
/// <remarks>
/// TraCI answers every command with a status: a result code, and a description SUMO wrote. A
/// non-zero code is this exception, carrying both, and the connection survives it -- the frame was
/// read to its end, so the next command starts clean.
///
/// <para>The distinction from <see cref="FatalTraCIError"/> is the whole reason there are two
/// types. This one is routine and often expected: asking about a vehicle that has arrived, or
/// unsubscribing from one SUMO has already removed. A caller can catch it, decide the answer does
/// not matter, and carry on. <see cref="FatalTraCIError"/> means the socket or the frame stream is
/// no longer trustworthy and nothing further can be read from it.</para>
/// </remarks>
public sealed class TraCIException : Exception
{
    /// <param name="message">SUMO's own description of what it refused.</param>
    /// <param name="commandId">The TraCI command the status answered.</param>
    /// <param name="resultCode">SUMO's result code: 0x01 not implemented, 0xFF error.</param>
    public TraCIException(string message, int commandId, int resultCode)
        : base(message)
    {
        CommandId = commandId;
        ResultCode = resultCode;
    }

    /// <param name="message">What went wrong.</param>
    public TraCIException(string message)
        : base(message)
    {
    }

    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The failure underneath it.</param>
    public TraCIException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The TraCI command identifier the refused status answered, or <c>-1</c> where this was raised
    /// without one. <see cref="TraCIConstants"/> names them.
    /// </summary>
    public int CommandId { get; } = -1;

    /// <summary>
    /// SUMO's result code. <c>0x01</c> is "not implemented" and <c>0xFF</c> is "error"; <c>0x00</c>
    /// is success and never reaches here. <c>-1</c> where this was raised without a status.
    /// </summary>
    public int ResultCode { get; } = -1;
}
