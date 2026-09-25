// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Application;
using SharpProspero.Platform;
using System;
using System.Buffers.Binary;

namespace ProsperoMultiTools.Shell;

/// <summary>How the escalation handshake ended.</summary>
internal enum UnjailOutcome
{
    /// <summary>The daemon accepted the request and widened the file view.</summary>
    Applied,

    /// <summary>The daemon was not reachable at all (socket connect / send / receive failed).</summary>
    DaemonUnreachable,

    /// <summary>The daemon answered but reported a non-zero outcome word.</summary>
    DaemonRefused,

    /// <summary>The daemon closed the connection before enough of a reply was in hand to read it.</summary>
    Malformed,
}

/// <summary>The outcome the escalation handshake ended with.</summary>
internal readonly record struct UnjailResult(UnjailOutcome Outcome, string Reason)
{
    /// <summary>True when the daemon accepted the request.</summary>
    public bool Applied => Outcome == UnjailOutcome.Applied;
}

internal static class UnjailRequest
{
    private const int DaemonPort = 9069;
    private const int CommandSize = 0xA10;
    private const uint Magic = 0xDEADBEEF;
    private const int EscalationCommand = 5;
    private const int ReceiveTimeoutMicroseconds = 3_000_000;

    /// <summary>
    /// Asks the escalation daemon to widen the file view for this process. Returns the outcome of
    /// the handshake so a caller can carry the state forward and decide whether paths that only the
    /// widened view reaches are worth probing.
    /// </summary>
    public static UnjailResult Request()
    {
        try
        {
            using var conn = TcpConnection.Connect(SocketAddress.Loopback(DaemonPort));

            Span<byte> request = stackalloc byte[CommandSize];
            request.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(request, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(request.Slice(4), EscalationCommand);
            BinaryPrimitives.WriteInt32LittleEndian(request.Slice(8), ProcessInfo.Id);

            conn.SendAll(request);

            conn.SetReceiveTimeout(ReceiveTimeoutMicroseconds);

            Span<byte> reply = stackalloc byte[CommandSize];
            int total = 0;
            while (total < CommandSize)
            {
                int n = conn.Receive(reply.Slice(total));
                if (n <= 0)
                    break;
                total += n;
            }

            if (total < 16)
                return new UnjailResult(UnjailOutcome.Malformed,
                    "The daemon closed the connection before answering.");

            int outcome = BinaryPrimitives.ReadInt32LittleEndian(reply.Slice(0x0C));
            if (outcome != 0)
                return new UnjailResult(UnjailOutcome.DaemonRefused,
                    "The daemon refused the request.");

            return new UnjailResult(UnjailOutcome.Applied, string.Empty);
        }
        catch (Exception error)
        {
            return new UnjailResult(UnjailOutcome.DaemonUnreachable, error.Message);
        }
    }
}
