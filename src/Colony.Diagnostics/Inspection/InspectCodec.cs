using System.Buffers.Binary;
using System.Text;

namespace Colony.Diagnostics.Inspection;

/// <summary>
/// The compact binary form of the inspection protocol (spec section 10.2), version <see cref="ProtocolVersion"/>.
/// A value is a tag byte (<see cref="InspectValueKind"/>) then its payload: nothing for Null; one byte for
/// Boolean; a zig-zag LEB128 varint for Whole; 8 little-endian bytes (IEEE 754) for Number; a varint length then
/// UTF-8 for Text; a varint length then the bytes for Bytes; a varint count then the items for List; a varint
/// count then (UTF-8 key, value) pairs for Map. On a stream every message is one frame: a 4-byte little-endian
/// length, then one encoded value. A connection opens with the 8-byte <see cref="Magic"/> and a 2-byte version
/// from the client; the first message must be <c>hello</c> carrying the token.
/// </summary>
public static class InspectCodec
{
    /// <summary>Bumped on any incompatible change of the encoding or of a request/response shape.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Largest frame either side accepts (64 MiB): a snapshot of a large World can be several MiB.</summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    private const int MaxDepth = 64;

    /// <summary>Connection preamble: "CLNYINSP".</summary>
    public static ReadOnlySpan<byte> Magic => "CLNYINSP"u8;

    public static byte[] Encode(InspectValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        Write(stream, value, 0);
        return stream.ToArray();
    }

    public static InspectValue Decode(ReadOnlySpan<byte> bytes)
    {
        int position = 0;
        var value = Read(bytes, ref position, 0);
        if (position != bytes.Length)
        {
            throw new FormatException($"{bytes.Length - position} trailing byte(s) after the value.");
        }

        return value;
    }

    /// <summary>Writes one length-prefixed frame.</summary>
    public static void WriteFrame(Stream stream, InspectValue value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = Encode(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
        stream.Flush();
    }

    /// <summary>Reads one frame; null on a clean end of stream before the length.</summary>
    public static InspectValue? ReadFrame(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> length = stackalloc byte[4];
        int read = ReadFully(stream, length);
        if (read == 0)
        {
            return null;
        }

        if (read < 4)
        {
            throw new EndOfStreamException("The stream ended inside a frame header.");
        }

        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 0 || size > MaxFrameBytes)
        {
            throw new FormatException($"Frame of {size} bytes is outside 0..{MaxFrameBytes}.");
        }

        byte[] payload = new byte[size];
        if (ReadFully(stream, payload) != size)
        {
            throw new EndOfStreamException("The stream ended inside a frame.");
        }

        return Decode(payload);
    }

    /// <summary>Writes the connection preamble (client side).</summary>
    public static void WritePreamble(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> preamble = stackalloc byte[10];
        Magic.CopyTo(preamble);
        BinaryPrimitives.WriteUInt16LittleEndian(preamble[8..], ProtocolVersion);
        stream.Write(preamble);
        stream.Flush();
    }

    /// <summary>Reads and checks the connection preamble (server side); returns the client's protocol version.</summary>
    public static int ReadPreamble(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> preamble = stackalloc byte[10];
        if (ReadFully(stream, preamble) != preamble.Length || !preamble[..8].SequenceEqual(Magic))
        {
            throw new FormatException("Not an inspection protocol connection (bad preamble).");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(preamble[8..]);
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static void Write(Stream stream, InspectValue value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"Values nest deeper than {MaxDepth} levels.");
        }

        stream.WriteByte((byte)value.Kind);
        switch (value.Kind)
        {
            case InspectValueKind.Null:
                break;
            case InspectValueKind.Boolean:
                stream.WriteByte(value.AsBoolean ? (byte)1 : (byte)0);
                break;
            case InspectValueKind.Whole:
                long whole = value.AsWhole;
                WriteVarint(stream, (ulong)((whole << 1) ^ (whole >> 63)));
                break;
            case InspectValueKind.Number:
                Span<byte> number = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(number, value.AsNumber);
                stream.Write(number);
                break;
            case InspectValueKind.Text:
                WriteText(stream, value.AsText);
                break;
            case InspectValueKind.Bytes:
                WriteVarint(stream, (ulong)value.AsBytes.Length);
                stream.Write(value.AsBytes);
                break;
            case InspectValueKind.List:
                WriteVarint(stream, (ulong)value.Items.Count);
                foreach (var item in value.Items)
                {
                    Write(stream, item, depth + 1);
                }

                break;
            default:
                WriteVarint(stream, (ulong)value.Entries.Count);
                foreach (var entry in value.Entries)
                {
                    WriteText(stream, entry.Key);
                    Write(stream, entry.Value, depth + 1);
                }

                break;
        }
    }

    private static void WriteText(Stream stream, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        WriteVarint(stream, (ulong)utf8.Length);
        stream.Write(utf8);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static InspectValue Read(ReadOnlySpan<byte> bytes, ref int position, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"Values nest deeper than {MaxDepth} levels.");
        }

        var kind = (InspectValueKind)Take(bytes, ref position, 1)[0];
        switch (kind)
        {
            case InspectValueKind.Null:
                return InspectValue.Null;
            case InspectValueKind.Boolean:
                return InspectValue.From(Take(bytes, ref position, 1)[0] != 0);
            case InspectValueKind.Whole:
                ulong zigzag = ReadVarint(bytes, ref position);
                return InspectValue.From((long)(zigzag >> 1) ^ -(long)(zigzag & 1));
            case InspectValueKind.Number:
                return InspectValue.From(BinaryPrimitives.ReadDoubleLittleEndian(Take(bytes, ref position, 8)));
            case InspectValueKind.Text:
                return InspectValue.From(ReadText(bytes, ref position));
            case InspectValueKind.Bytes:
                return InspectValue.From(Take(bytes, ref position, ReadLength(bytes, ref position)));
            case InspectValueKind.List:
                int count = ReadLength(bytes, ref position);
                var items = new InspectValue[count];
                for (int i = 0; i < count; i++)
                {
                    items[i] = Read(bytes, ref position, depth + 1);
                }

                return InspectValue.List(items);
            case InspectValueKind.Map:
                int entries = ReadLength(bytes, ref position);
                var map = new KeyValuePair<string, InspectValue>[entries];
                for (int i = 0; i < entries; i++)
                {
                    string key = ReadText(bytes, ref position);
                    map[i] = new(key, Read(bytes, ref position, depth + 1));
                }

                return InspectValue.Map(map);
            default:
                throw new FormatException($"Unknown value tag {(byte)kind} at byte {position - 1}.");
        }
    }

    private static string ReadText(ReadOnlySpan<byte> bytes, ref int position)
        => Encoding.UTF8.GetString(Take(bytes, ref position, ReadLength(bytes, ref position)));

    private static int ReadLength(ReadOnlySpan<byte> bytes, ref int position)
    {
        ulong length = ReadVarint(bytes, ref position);
        if (length > (ulong)(bytes.Length - position))
        {
            throw new FormatException($"Length {length} at byte {position} runs past the end of the value.");
        }

        return (int)length;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int position)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte b = Take(bytes, ref position, 1)[0];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new FormatException("Varint longer than 10 bytes.");
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int position, int count)
    {
        if (count > bytes.Length - position)
        {
            throw new FormatException($"The value ends early at byte {position} ({count} byte(s) needed).");
        }

        var slice = bytes.Slice(position, count);
        position += count;
        return slice;
    }
}
