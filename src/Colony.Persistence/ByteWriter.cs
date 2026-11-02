using System.Buffers.Binary;
using System.Text;

namespace Colony.Persistence;

/// <summary>A growable little-endian byte sink for section payloads and the container; every multi-byte value is written explicitly, so the bytes never depend on the host.</summary>
internal sealed class ByteWriter
{
    private byte[] _buffer;
    private int _position;

    public ByteWriter(int initialCapacity = 256)
    {
        _buffer = new byte[Math.Max(16, initialCapacity)];
    }

    public int Position => _position;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

    public byte[] ToArray() => _buffer.AsSpan(0, _position).ToArray();

    /// <summary>Starts over at position 0, keeping the buffer.</summary>
    public void Reset() => _position = 0;

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_position++] = value;
    }

    public void WriteInt32(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteInt64(long value)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    public void WriteUInt64At(int position, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(position), value);

    public void WriteUInt32At(int position, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(position), value);

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_position));
        _position += bytes.Length;
    }

    /// <summary>A UTF-8 string: 32-bit byte length then the bytes.</summary>
    public void WriteString(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        WriteInt32(length);
        Ensure(length);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position, length));
        _position += length;
    }

    public void Skip(int count)
    {
        Ensure(count);
        _buffer.AsSpan(_position, count).Clear();
        _position += count;
    }

    private void Ensure(int count)
    {
        if (_position + count <= _buffer.Length)
        {
            return;
        }

        int capacity = _buffer.Length;
        while (capacity < _position + count)
        {
            capacity *= 2;
        }

        Array.Resize(ref _buffer, capacity);
    }
}

/// <summary>A bounds-checked little-endian reader over one section's bytes; every overrun is a <see cref="SaveFormatException"/> naming the section.</summary>
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly string _section;
    private int _position;

    public ByteReader(ReadOnlySpan<byte> data, string section)
    {
        _data = data;
        _section = section;
        _position = 0;
    }

    public readonly int Remaining => _data.Length - _position;

    public readonly int Position => _position;

    public byte ReadByte()
    {
        Need(1);
        return _data[_position++];
    }

    public int ReadInt32()
    {
        Need(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    public uint ReadUInt32()
    {
        Need(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    public long ReadInt64()
    {
        Need(8);
        long value = BinaryPrimitives.ReadInt64LittleEndian(_data[_position..]);
        _position += 8;
        return value;
    }

    public ulong ReadUInt64()
    {
        Need(8);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
        _position += 8;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0)
        {
            throw new SaveFormatException($"Section '{_section}' holds a negative length ({count}).");
        }

        Need(count);
        var bytes = _data.Slice(_position, count);
        _position += count;
        return bytes;
    }

    /// <summary>A count that must fit the remaining bytes at <paramref name="elementSize"/> bytes each.</summary>
    public int ReadCount(int elementSize)
    {
        int count = ReadInt32();
        if (count < 0 || (long)count * elementSize > Remaining)
        {
            throw new SaveFormatException($"Section '{_section}' declares {count} elements of {elementSize} bytes but only {Remaining} bytes remain.");
        }

        return count;
    }

    public string ReadString()
    {
        int length = ReadInt32();
        return Encoding.UTF8.GetString(ReadBytes(length));
    }

    public readonly void ExpectEnd()
    {
        if (_position != _data.Length)
        {
            throw new SaveFormatException($"Section '{_section}' has {_data.Length - _position} unexpected trailing bytes.");
        }
    }

    private readonly void Need(int count)
    {
        if (_position + (long)count > _data.Length)
        {
            throw new SaveFormatException($"Section '{_section}' is truncated: {count} bytes needed at offset {_position}, {_data.Length - _position} remain.");
        }
    }
}
