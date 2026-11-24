using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ZeEngine.Networking;

/// <summary>
/// Reads a message payload written by <see cref="NetWriter"/>. Reading past the end throws
/// <see cref="InvalidDataException"/>; <see cref="NetworkManager"/> treats that as a malformed
/// message and disconnects the sender, so a bad packet can never crash the game.
/// </summary>
public sealed class NetReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;

    public NetReader(byte[] data) : this(data, 0, data?.Length ?? 0) { }

    public NetReader(byte[] data, int offset, int count)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        if (offset < 0 || count < 0 || offset + count > data.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        _position = offset;
        _end = offset + count;
    }

    /// <summary>Bytes not read yet.</summary>
    public int Remaining => _end - _position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
            throw new InvalidDataException("The message ended before all its fields were read.");

        ReadOnlySpan<byte> span = _data.AsSpan(_position, count);
        _position += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];
    public bool ReadBoolean() => ReadByte() != 0;
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public string ReadString()
    {
        int byteCount = ReadUInt16();
        return Encoding.UTF8.GetString(Take(byteCount));
    }
}
