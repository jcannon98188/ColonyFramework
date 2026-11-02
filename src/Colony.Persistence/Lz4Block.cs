using System.Buffers.Binary;

namespace Colony.Persistence;

/// <summary>
/// A deterministic LZ4 block codec (the standard LZ4 block format, decodable by any LZ4 implementation) for
/// per-section compression (spec section 7.1). The compressor is a plain greedy hash-chain matcher: a pure
/// function of its input, so the same section compresses to the same bytes on every machine and run. No
/// package dependency: Core-level packages carry only the BCL.
/// </summary>
internal static class Lz4Block
{
    private const int MinMatch = 4;
    private const int LastLiterals = 5;
    private const int MatchFindLimit = 12;
    private const int HashBits = 14;
    private const int MaxDistance = 65535;

    /// <summary>An upper bound on the compressed size of <paramref name="length"/> bytes.</summary>
    public static int MaxCompressedLength(int length) => length + (length / 255) + 16;

    /// <summary>Compresses <paramref name="source"/> into <paramref name="destination"/> (at least <see cref="MaxCompressedLength"/> bytes) and returns the compressed length.</summary>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int length = source.Length;
        int op = 0;
        if (length <= MatchFindLimit)
        {
            EmitLastLiterals(source, destination, ref op);
            return op;
        }

        var table = new int[1 << HashBits];
        Array.Fill(table, -1);
        int anchor = 0;
        int ip = 0;
        int findLimit = length - MatchFindLimit;
        int matchLimit = length - LastLiterals;
        while (TryFindMatch(source, table, findLimit, ref ip, out int match))
        {
            while (ip > anchor && match > 0 && source[ip - 1] == source[match - 1])
            {
                ip--;
                match--;
            }

            int matchLength = MinMatch;
            while (ip + matchLength < matchLimit && source[ip + matchLength] == source[match + matchLength])
            {
                matchLength++;
            }

            EmitSequence(destination, ref op, source.Slice(anchor, ip - anchor), ip - match, matchLength - MinMatch);
            ip += matchLength;
            anchor = ip;
        }

        EmitLastLiterals(source[anchor..], destination, ref op);
        return op;
    }

    /// <summary>Decompresses a block into exactly <paramref name="destination"/>.Length bytes; malformed input throws <see cref="InvalidDataException"/>.</summary>
    public static void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int ip = 0;
        int op = 0;
        int length = source.Length;
        if (length == 0)
        {
            if (destination.Length != 0)
            {
                throw new InvalidDataException("The compressed block is empty but the section is not.");
            }

            return;
        }

        while (true)
        {
            if (ip >= length)
            {
                throw new InvalidDataException("The compressed block ends inside a sequence.");
            }

            byte token = source[ip++];
            int literals = token >> 4;
            if (literals == 15)
            {
                literals += ReadExtension(source, ref ip);
            }

            if (ip + literals > length || op + literals > destination.Length)
            {
                throw new InvalidDataException("A literal run overruns the block or the section.");
            }

            source.Slice(ip, literals).CopyTo(destination[op..]);
            ip += literals;
            op += literals;
            if (ip == length)
            {
                if (op != destination.Length)
                {
                    throw new InvalidDataException($"The block decodes to {op} bytes, not the {destination.Length} the section declares.");
                }

                return;
            }

            if (ip + 2 > length)
            {
                throw new InvalidDataException("The compressed block ends inside a match offset.");
            }

            int offset = BinaryPrimitives.ReadUInt16LittleEndian(source[ip..]);
            ip += 2;
            if (offset == 0 || offset > op)
            {
                throw new InvalidDataException("A match offset points before the start of the section.");
            }

            int matchLength = token & 15;
            if (matchLength == 15)
            {
                matchLength += ReadExtension(source, ref ip);
            }

            matchLength += MinMatch;
            if (op + matchLength > destination.Length)
            {
                throw new InvalidDataException("A match overruns the section.");
            }

            int from = op - offset;
            for (int i = 0; i < matchLength; i++)
            {
                destination[op + i] = destination[from + i];
            }

            op += matchLength;
        }
    }

    private static int ReadExtension(ReadOnlySpan<byte> source, ref int ip)
    {
        int total = 0;
        byte next;
        do
        {
            if (ip >= source.Length)
            {
                throw new InvalidDataException("The compressed block ends inside a length extension.");
            }

            next = source[ip++];
            total += next;
        }
        while (next == 255);

        return total;
    }

    private static bool TryFindMatch(ReadOnlySpan<byte> source, int[] table, int findLimit, ref int ip, out int match)
    {
        int step = 1;
        int acceleration = 1 << 6;
        while (ip < findLimit)
        {
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(source[ip..]);
            int hash = (int)((sequence * 2654435761u) >> (32 - HashBits));
            int candidate = table[hash];
            table[hash] = ip;
            if (candidate >= 0 && ip - candidate <= MaxDistance && BinaryPrimitives.ReadUInt32LittleEndian(source[candidate..]) == sequence)
            {
                match = candidate;
                return true;
            }

            ip += step;
            step = acceleration++ >> 6;
        }

        match = -1;
        return false;
    }

    private static void EmitSequence(Span<byte> destination, ref int op, ReadOnlySpan<byte> literals, int offset, int matchExtra)
    {
        int token = op++;
        destination[token] = 0;
        WriteLength(destination, ref op, token, literals.Length, high: true);
        literals.CopyTo(destination[op..]);
        op += literals.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[op..], (ushort)offset);
        op += 2;
        WriteLength(destination, ref op, token, matchExtra, high: false);
    }

    private static void EmitLastLiterals(ReadOnlySpan<byte> literals, Span<byte> destination, ref int op)
    {
        int token = op++;
        destination[token] = 0;
        WriteLength(destination, ref op, token, literals.Length, high: true);
        literals.CopyTo(destination[op..]);
        op += literals.Length;
    }

    private static void WriteLength(Span<byte> destination, ref int op, int token, int value, bool high)
    {
        if (value < 15)
        {
            destination[token] |= (byte)(high ? value << 4 : value);
            return;
        }

        destination[token] |= (byte)(high ? 15 << 4 : 15);
        int remaining = value - 15;
        while (remaining >= 255)
        {
            destination[op++] = 255;
            remaining -= 255;
        }

        destination[op++] = (byte)remaining;
    }
}
