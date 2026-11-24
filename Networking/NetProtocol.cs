using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ZeEngine.Networking;

/// <summary>
/// A message that can be sent through <see cref="NetworkManager"/>. Implement it on a small class
/// with a public parameterless constructor, then register it with
/// <see cref="NetworkManager.Register{T}(byte)"/>.
/// </summary>
public interface INetMessage
{
    /// <summary>Writes this message's fields. Called on the game thread when sending.</summary>
    void Write(NetWriter writer);

    /// <summary>Reads this message's fields, in the same order they were written.</summary>
    void Read(NetReader reader);
}

/// <summary>Why a peer went away, or why a connection attempt failed.</summary>
public enum NetDisconnectReason : byte
{
    Unknown = 0,
    /// <summary>The other side closed the connection on purpose (for example the host quit).</summary>
    Shutdown = 1,
    /// <summary>The host removed this player.</summary>
    Kicked = 2,
    /// <summary>The host has no free slots.</summary>
    ServerFull = 3,
    /// <summary>The other side runs a different game or protocol version.</summary>
    VersionMismatch = 4,
    /// <summary>Nothing was heard from the other side for too long.</summary>
    Timeout = 5,
    /// <summary>The connection dropped without a goodbye.</summary>
    ConnectionLost = 6,
    /// <summary>The other side sent data that could not be understood.</summary>
    ProtocolError = 7,
    /// <summary>Messages were queued faster than the connection could send them.</summary>
    SendOverflow = 8,
    /// <summary>The host could not be reached (refused, unreachable, or timed out).</summary>
    ConnectFailed = 9,
}

/// <summary>
/// Wire format. Every message on the TCP stream is a frame:
///   [ushort payloadLength][byte type][payload bytes...]   (little-endian)
/// Types 0-15 are reserved for the engine; game messages use 16-255.
/// </summary>
internal static class NetProtocol
{
    public const ushort Version = 1;

    public const int HeaderSize = 3;
    public const int MaxPayload = 16 * 1024;

    // Engine messages.
    public const byte MsgHello = 0;       // client -> host: ushort version, string gameId, string playerName
    public const byte MsgWelcome = 1;     // host -> client: int assignedPeerId, string hostName
    public const byte MsgReject = 2;      // host -> client: byte reason
    public const byte MsgPing = 3;        // both ways, empty
    public const byte MsgDisconnect = 4;  // both ways: byte reason

    public const byte FirstUserMessage = 16;

    public static byte[] BuildFrame(byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new ArgumentException($"Payload is {payload.Length} bytes; the maximum is {MaxPayload}.");

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);
        frame[2] = type;
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// Reads one frame. Throws <see cref="EndOfStreamException"/> when the stream closes and
    /// <see cref="InvalidDataException"/> when the header claims an oversized payload.
    /// </summary>
    public static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(
        Stream stream, byte[] header, CancellationToken token)
    {
        await stream.ReadExactlyAsync(header, 0, HeaderSize, token).ConfigureAwait(false);

        int length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        byte type = header[2];

        if (length > MaxPayload)
            throw new InvalidDataException($"Frame claims {length} payload bytes; the maximum is {MaxPayload}.");

        if (length == 0) return (type, Array.Empty<byte>());

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, 0, length, token).ConfigureAwait(false);
        return (type, payload);
    }
}
