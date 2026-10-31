using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GpuPath.Bench;

namespace GpuPath.Core;

/// <summary>Growable unmanaged buffer. Growth reallocates (rare, amortised, unmanaged); steady state is allocation-free.</summary>
public unsafe struct UBuf<T> where T : unmanaged
{
    public T* P;
    public long Len, Cap;

    public UBuf(long cap) { Cap = Math.Max(16, cap); P = Mem.Alloc<T>(Cap, zero: false); Len = 0; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(T v) { if (Len == Cap) Grow(); P[Len++] = v; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Grow()
    {
        long nc = Cap * 2;
        T* np = Mem.Alloc<T>(nc, zero: false);
        NativeMemory.Copy(P, np, (nuint)(Len * sizeof(T)));
        Mem.Free(P, Cap);
        P = np; Cap = nc;
    }

    public void Clear() => Len = 0;
    public void Free() { Mem.Free(P, Cap); P = null; Cap = Len = 0; }
    public long Bytes => Cap * sizeof(T);
}
