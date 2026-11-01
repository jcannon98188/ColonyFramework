using System.Buffers.Binary;
using Colony.Hashing;

namespace Colony.Persistence;

/// <summary>
/// The transport form of a join package: the encoded image split into chunks that each verify on their own.
/// Chunk layout, little-endian: magic <c>CLNYJOIN</c> (8), version (4), chunk index (4), chunk count (4),
/// payload length (4), package length (8), package hash (8, XXH64 of the whole image, seed "JPKG"), chunk hash
/// (8, XXH64 of the first 40 header bytes then the payload, seed "JCHK"), then the payload. Chunks may travel
/// and arrive in any order; <see cref="JoinPackageAssembler"/> puts them back together.
/// </summary>
public static class JoinPackageChunks
{
    public const int HeaderSize = 48;
    internal const ulong PackageHashSeed = 0x4A50_4B47UL; // "JPKG"
    internal const ulong ChunkHashSeed = 0x4A43_484BUL;   // "JCHK"

    internal static ReadOnlySpan<byte> Magic => "CLNYJOIN"u8;

    /// <summary>Splits <paramref name="image"/> into chunks of at most <paramref name="chunkSize"/> payload bytes (an empty image gives one empty chunk).</summary>
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> image, int chunkSize = JoinPackage.DefaultChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        int count = Math.Max(1, (int)((image.Length + (long)chunkSize - 1) / chunkSize));
        ulong packageHash = Hash64.Compute(image, PackageHashSeed);
        var chunks = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int offset = i * chunkSize;
            var payload = image.Slice(offset, Math.Min(chunkSize, image.Length - offset));
            var chunk = new byte[HeaderSize + payload.Length];
            Magic.CopyTo(chunk);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8), JoinPackage.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12), (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(16), (uint)count);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(20), (uint)payload.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(24), (ulong)image.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(32), packageHash);
            payload.CopyTo(chunk.AsSpan(HeaderSize));
            BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(40), ChunkHash(chunk));
            chunks[i] = chunk;
        }

        return chunks;
    }

    /// <summary>The hash a chunk's header must carry: header bytes 0..40 then the payload.</summary>
    internal static ulong ChunkHash(ReadOnlySpan<byte> chunk)
    {
        var state = new Hash64State(ChunkHashSeed);
        state.Append(chunk[..40]);
        state.Append(chunk[HeaderSize..]);
        return state.Finish();
    }
}

/// <summary>
/// Receives the chunks of one join package in any order (duplicates ignored), verifying each chunk's hash on
/// arrival, then <see cref="Complete"/> checks that none is missing, verifies the whole-package hash and opens
/// the package. Every failure is a <see cref="JoinPackageException"/> with a specific <see cref="JoinFailureReason"/>.
/// </summary>
public sealed class JoinPackageAssembler
{
    private byte[]?[]? _chunks;
    private ulong _packageLength;
    private ulong _packageHash;

    /// <summary>Chunks of the package (0 until the first arrives).</summary>
    public int ChunkCount => _chunks?.Length ?? 0;

    public int ReceivedCount { get; private set; }

    public bool IsComplete => _chunks is not null && ReceivedCount == _chunks.Length;

    /// <summary>Indices not yet received, ascending.</summary>
    public IReadOnlyList<int> MissingChunks
    {
        get
        {
            var missing = new List<int>();
            for (int i = 0; i < ChunkCount; i++)
            {
                if (_chunks![i] is null)
                {
                    missing.Add(i);
                }
            }

            return missing;
        }
    }

    /// <summary>Verifies and stores one chunk. True once every chunk has arrived.</summary>
    public bool Add(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < JoinPackageChunks.Magic.Length || !chunk[..JoinPackageChunks.Magic.Length].SequenceEqual(JoinPackageChunks.Magic))
        {
            throw new JoinPackageException(JoinFailureReason.NotAJoinPackage, "The data is not a join package chunk (bad magic or too short).");
        }

        if (chunk.Length < JoinPackageChunks.HeaderSize)
        {
            throw new JoinPackageException(JoinFailureReason.ChunkTruncated, $"The chunk is {chunk.Length} bytes, shorter than its {JoinPackageChunks.HeaderSize}-byte header.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]);
        if (version != JoinPackage.Version)
        {
            throw new JoinPackageException(JoinFailureReason.UnsupportedVersion, $"Join chunk version {version} is not supported by this build (version {JoinPackage.Version}).");
        }

        uint index = BinaryPrimitives.ReadUInt32LittleEndian(chunk[12..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(chunk[16..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[20..]);
        ulong packageLength = BinaryPrimitives.ReadUInt64LittleEndian(chunk[24..]);
        ulong packageHash = BinaryPrimitives.ReadUInt64LittleEndian(chunk[32..]);
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(chunk[40..]);
        long actualLength = chunk.Length - JoinPackageChunks.HeaderSize;
        if (actualLength < length)
        {
            throw new JoinPackageException(JoinFailureReason.ChunkTruncated, $"Chunk {index} declares {length} payload bytes but carries {actualLength}.");
        }

        if (actualLength > length || JoinPackageChunks.ChunkHash(chunk) != hash)
        {
            throw new JoinPackageException(JoinFailureReason.ChunkCorrupt, $"Chunk {index} is corrupt: its bytes do not match its hash.");
        }

        if (count == 0 || index >= count || packageLength > int.MaxValue)
        {
            throw new JoinPackageException(JoinFailureReason.ChunkCorrupt, $"Chunk {index} of {count} describes an impossible package.");
        }

        if (_chunks is null)
        {
            _chunks = new byte[count][];
            _packageLength = packageLength;
            _packageHash = packageHash;
        }
        else if (count != _chunks.Length || packageLength != _packageLength || packageHash != _packageHash)
        {
            throw new JoinPackageException(JoinFailureReason.ChunkFromAnotherPackage, $"Chunk {index} belongs to another package (package hash 0x{packageHash:X16}, expected 0x{_packageHash:X16}).");
        }

        if (_chunks[index] is { } existing)
        {
            if (!existing.AsSpan().SequenceEqual(chunk))
            {
                throw new JoinPackageException(JoinFailureReason.ChunkFromAnotherPackage, $"Chunk {index} arrived twice with different bytes.");
            }

            return IsComplete;
        }

        _chunks[index] = chunk.ToArray();
        ReceivedCount++;
        return IsComplete;
    }

    /// <summary>The reassembled image, verified against the whole-package hash.</summary>
    public byte[] Image()
    {
        if (!IsComplete)
        {
            throw new JoinPackageException(JoinFailureReason.ChunksMissing, ChunkCount == 0
                ? "No chunk of the package has arrived."
                : $"{ChunkCount - ReceivedCount} of {ChunkCount} chunks are missing (first missing: {MissingChunks[0]}).");
        }

        long total = 0;
        foreach (var c in _chunks!)
        {
            total += c!.Length - JoinPackageChunks.HeaderSize;
        }

        if ((ulong)total != _packageLength)
        {
            throw new JoinPackageException(JoinFailureReason.PackageCorrupt, $"The chunks hold {total} bytes but the package is {_packageLength} bytes.");
        }

        var image = new byte[total];
        int offset = 0;
        foreach (var c in _chunks)
        {
            var payload = c.AsSpan(JoinPackageChunks.HeaderSize);
            payload.CopyTo(image.AsSpan(offset));
            offset += payload.Length;
        }

        ulong hash = Hash64.Compute(image, JoinPackageChunks.PackageHashSeed);
        if (hash != _packageHash)
        {
            throw new JoinPackageException(JoinFailureReason.PackageCorrupt, $"The reassembled package hashes to 0x{hash:X16} but its chunks recorded 0x{_packageHash:X16}.");
        }

        return image;
    }

    /// <summary>Reassembles, verifies and opens the package.</summary>
    public JoinPackage Complete() => JoinPackage.Open(Image());
}
