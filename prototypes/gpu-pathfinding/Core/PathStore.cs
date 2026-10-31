using System.Runtime.InteropServices;
using GpuPath.Bench;

namespace GpuPath.Core;

/// <summary>A per-unit FIFO of fixed-size items stored in a chain of pool blocks.</summary>
public struct PStream { public int Head, Pos, Count; public static PStream Empty => new() { Head = -1 }; }

/// <summary>
/// Pool of 256-byte blocks (first int = next block). One pool per unit partition, so writers never contend.
/// Grows by reallocation (rare; blocks are addressed by index).
/// </summary>
public unsafe struct BlockPool
{
    public const int BS = 256;
    byte* _data; int _cap, _free; public int Used;

    public BlockPool(int initialBlocks)
    {
        _cap = 0; _data = null; _free = -1; Used = 0;
        Grow(initialBlocks);
    }

    void Grow(int add)
    {
        int nc = _cap + add;
        byte* nd = Mem.Alloc<byte>((long)nc * BS, false);
        if (_data != null) { NativeMemory.Copy(_data, nd, (nuint)((long)_cap * BS)); Mem.Free(_data, (long)_cap * BS); }
        _data = nd;
        for (int b = nc - 1; b >= _cap; b--) { *(int*)(_data + (long)b * BS) = _free; _free = b; }
        _cap = nc;
    }

    int Alloc()
    {
        if (_free < 0) Grow(Math.Max(64, _cap));
        int b = _free;
        _free = *(int*)(_data + (long)b * BS);
        *(int*)(_data + (long)b * BS) = -1;
        Used++;
        return b;
    }

    void FreeBlock(int b) { *(int*)(_data + (long)b * BS) = _free; _free = b; Used--; }

    public byte* Ptr(int b) => _data + (long)b * BS;
    public long Bytes => (long)_cap * BS;

    public void Clear(ref PStream s)
    {
        int b = s.Head;
        while (b >= 0) { int n = *(int*)Ptr(b); FreeBlock(b); b = n; }
        s.Head = -1; s.Pos = 0; s.Count = 0;
    }

    /// <summary>Replaces the stream content with `n` items of `size` bytes.</summary>
    public void Write(ref PStream s, byte* src, int n, int size)
    {
        Clear(ref s);
        if (n == 0) return;
        int per = (BS - 4) / size;
        int prev = -1;
        for (int i = 0; i < n; i += per)
        {
            int b = Alloc();
            if (prev < 0) s.Head = b; else *(int*)Ptr(prev) = b;
            int k = Math.Min(per, n - i);
            NativeMemory.Copy(src + (long)i * size, Ptr(b) + 4, (nuint)(k * size));
            prev = b;
        }
        s.Pos = 0; s.Count = n;
    }

    /// <summary>Pointer to item i ahead of the read position (i &lt; Count).</summary>
    public byte* Peek(ref PStream s, int i, int size)
    {
        int per = (BS - 4) / size;
        int idx = s.Pos + i;
        int b = s.Head;
        while (idx >= per) { b = *(int*)Ptr(b); idx -= per; }
        return Ptr(b) + 4 + idx * size;
    }

    public void Pop(ref PStream s, int size)
    {
        int per = (BS - 4) / size;
        s.Pos++; s.Count--;
        if (s.Count == 0) { Clear(ref s); return; }
        if (s.Pos == per) { int n = *(int*)Ptr(s.Head); FreeBlock(s.Head); s.Head = n; s.Pos = 0; }
    }

    public void Free() { Mem.Free(_data, (long)_cap * BS); _data = null; _cap = 0; }
}
