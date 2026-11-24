using System;
using System.Buffers.Binary;
using System.Text;

namespace ZeEngine.Networking;

/// <summary>
/// Builds a message payload. Everything is little-endian. The writer grows as needed, and
/// <see cref="Reset"/> lets you reuse one instance.
/// </summary>
public sealed class NetWriter
{
    /// <summary>Longest string, in UTF-8 bytes, that <see cref="Write(string)"/> accepts.</summary>
    public const int MaxStringBytes = ushort.MaxValue;

    private byte[] _buffer;
    private int _length;

    public NetWriter(int capacity = 64)
    {
        _buffer = new byte[Math.Max(capacity, 16)];
    }

    public int Length => _length;

    public void Reset() => _length = 0;

    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => AsSpan().ToArray();

    private Span<byte> Reserve(int count)
    {
        int needed = _length + count;
        if (needed > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));

        Span<byte> span = _buffer.AsSpan(_length, count);
        _length = needed;
        return span;
    }

    public void Write(byte value) => Reserve(1)[0] = value;
    public void Write(bool value) => Write(value ? (byte)1 : (byte)0);
    public void Write(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);
    public void Write(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
    public void Write(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);
    public void Write(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
    public void Write(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);
    public void Write(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);

    /// <summary>Writes a length-prefixed UTF-8 string. null is sent as an empty string.</summary>
    public void Write(string value)
    {
        value ??= string.Empty;

        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxStringBytes)
            throw new ArgumentException($"String is {byteCount} bytes in UTF-8; the maximum is {MaxStringBytes}.");

        Write((ushort)byteCount);
        Encoding.UTF8.GetBytes(value, Reserve(byteCount));
    }
}
