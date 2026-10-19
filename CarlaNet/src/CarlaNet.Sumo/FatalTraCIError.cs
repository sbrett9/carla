// Ported from Eclipse SUMO's reference TraCI client, tools/traci/exceptions.py.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// The connection cannot be used any further.
/// </summary>
/// <remarks>
/// Raised where the socket closed under the client, where SUMO answered a command with a response
/// for a different one, or where a frame held a value the decoder could not place. The last of
/// these matters most: a value read with the wrong width leaves the read position inside the next
/// value, so every byte after it is misread as something plausible. There is no recovering the
/// stream from that, and reporting it as fatal is the only honest answer.
///
/// <para>Recoverable refusals -- SUMO saying no to a command it understood -- are
/// <see cref="TraCIException"/>.</para>
/// </remarks>
public sealed class FatalTraCIError : Exception
{
    /// <param name="message">What went wrong, and where in the exchange.</param>
    public FatalTraCIError(string message)
        : base(message)
    {
    }

    /// <param name="message">What went wrong, and where in the exchange.</param>
    /// <param name="innerException">The socket or decode failure underneath it.</param>
    public FatalTraCIError(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
