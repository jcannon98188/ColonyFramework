using System.Runtime.InteropServices;

namespace GpuField.Fields;

/// <summary>
/// A Field stored as 16x16x16 row-major chunks (x fastest, then y, then z) in a slot pool.
/// The chunk table maps chunk id -> pool slot, or -1 for a uniform (single-value) chunk.
/// Double-buffered: <see cref="Cur"/> is read by a stencil step, <see cref="Next"/> written, then swapped.
/// Inactive allocated chunks are not supported (allocated == active) to keep the double buffer trivially consistent.
/// </summary>
public sealed unsafe class ChunkedField<T> : IDisposable where T : unmanaged
{
    public const int C = 16, CellsPerChunk = C * C * C, RowCells = C;
    public readonly int SizeX, SizeY, SizeZ, Cx, Cy, Cz, ChunkCount;
    public readonly int[] SlotOf;        // chunk id -> slot, -1 = uniform
    public readonly T[] UniformValue;    // chunk id -> value when uniform
    public readonly int[] ChunkOfSlot;   // slot -> chunk id
    public readonly int SlotCount;
    public T* Cur, Next;
    readonly byte* _baseA, _baseB;
    public readonly T* UniformRows;      // chunk id -> 16 copies of the uniform value (lets SIMD treat uniform rows as real rows)

    public ChunkedField(int sx, int sy, int sz, Func<int, bool> allocate, T uniform)
    {
        SizeX = sx; SizeY = sy; SizeZ = sz;
        Cx = sx / C; Cy = sy / C; Cz = sz / C; ChunkCount = Cx * Cy * Cz;
        SlotOf = new int[ChunkCount];
        UniformValue = new T[ChunkCount];
        var slots = new List<int>();
        for (int c = 0; c < ChunkCount; c++)
        {
            if (allocate(c)) { SlotOf[c] = slots.Count; slots.Add(c); }
            else { SlotOf[c] = -1; UniformValue[c] = uniform; }
        }
        ChunkOfSlot = slots.ToArray();
        SlotCount = slots.Count;
        nuint bytes = (nuint)SlotCount * CellsPerChunk * (nuint)sizeof(T);
        // 64 bytes of padding on both sides so SIMD kernels may do the +-1 element unaligned row loads unconditionally.
        _baseA = (byte*)NativeMemory.AlignedAlloc(bytes + 128, 64); _baseB = (byte*)NativeMemory.AlignedAlloc(bytes + 128, 64);
        NativeMemory.Clear(_baseA, bytes + 128); NativeMemory.Clear(_baseB, bytes + 128);
        Cur = (T*)(_baseA + 64); Next = (T*)(_baseB + 64);
        NativeMemory.Clear(Cur, bytes);
        NativeMemory.Clear(Next, bytes);
        UniformRows = (T*)NativeMemory.AlignedAlloc((nuint)ChunkCount * RowCells * (nuint)sizeof(T), 64);
        for (int c = 0; c < ChunkCount; c++)
            new Span<T>(UniformRows + (nint)c * RowCells, RowCells).Fill(UniformValue[c]);
    }

    public long PoolBytes => (long)SlotCount * CellsPerChunk * sizeof(T);
    public long HostBytes => 2 * PoolBytes + (long)ChunkCount * (RowCells * sizeof(T) + 4 + sizeof(T)) + SlotCount * 4L;

    public int ChunkId(int cx, int cy, int cz) => (cz * Cy + cy) * Cx + cx;
    public (int cx, int cy, int cz) ChunkCoord(int id) => (id % Cx, id / Cx % Cy, id / (Cx * Cy));

    public T* ChunkPtr(T* pool, int slot) => pool + (nint)slot * CellsPerChunk;

    public T Get(T* pool, int x, int y, int z)
    {
        int cid = ChunkId(x >> 4, y >> 4, z >> 4);
        int s = SlotOf[cid];
        if (s < 0) return UniformValue[cid];
        return pool[(nint)s * CellsPerChunk + ((z & 15) * C + (y & 15)) * C + (x & 15)];
    }

    public void Set(T* pool, int x, int y, int z, T v)
    {
        int cid = ChunkId(x >> 4, y >> 4, z >> 4);
        int s = SlotOf[cid];
        if (s < 0) throw new InvalidOperationException("write into uniform chunk");
        pool[(nint)s * CellsPerChunk + ((z & 15) * C + (y & 15)) * C + (x & 15)] = v;
    }

    public void Swap() { T* t = Cur; Cur = Next; Next = t; }

    /// <summary>Order-dependent 64-bit hash of the current pool (all allocated chunks in slot order).</summary>
    public ulong Hash() => Hashing.Hash64((byte*)Cur, PoolBytes);

    public void Dispose()
    {
        NativeMemory.AlignedFree(_baseA); NativeMemory.AlignedFree(_baseB); NativeMemory.AlignedFree(UniformRows);
    }
}

public static unsafe class Hashing
{
    /// <summary>Simple, fast, deterministic 64-bit hash (multiply-xorshift over 8-byte words, 4 lanes). Not cryptographic.</summary>
    public static ulong Hash64(byte* p, long bytes)
    {
        const ulong K = 0x9E3779B97F4A7C15UL;
        ulong h0 = 1, h1 = 2, h2 = 3, h3 = 4;
        long words = bytes / 8, i = 0;
        ulong* w = (ulong*)p;
        for (; i + 4 <= words; i += 4)
        {
            h0 = (h0 ^ w[i]) * K; h0 ^= h0 >> 29;
            h1 = (h1 ^ w[i + 1]) * K; h1 ^= h1 >> 29;
            h2 = (h2 ^ w[i + 2]) * K; h2 ^= h2 >> 29;
            h3 = (h3 ^ w[i + 3]) * K; h3 ^= h3 >> 29;
        }
        for (; i < words; i++) { h0 = (h0 ^ w[i]) * K; h0 ^= h0 >> 29; }
        ulong h = h0 * 31 + h1; h = h * 31 + h2; h = h * 31 + h3;
        return (h ^ (h >> 31)) * K ^ (ulong)bytes;
    }
}

