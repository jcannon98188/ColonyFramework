using System.Runtime.InteropServices;
using Colony.Numerics;

namespace Colony.ProcGen;

public static partial class Noise
{
    /// <summary>2D cellular (Worley) noise: F1, F2 and the nearest cell's hash.</summary>
    public static CellularSample Cellular(NoiseKey key, Fixed32 x, Fixed32 y)
    {
        CellularKernels.Cellular2(Lanes1.Broadcast((int)key.MixLow), Lanes1.Broadcast((int)key.MixHigh), Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw), out var f1, out var f2, out var cell);
        return new CellularSample(Fixed32.FromRaw(Lanes1.Lane(f1, 0)), Fixed32.FromRaw(Lanes1.Lane(f2, 0)), (uint)Lanes1.Lane(cell, 0));
    }

    /// <summary>3D cellular (Worley) noise.</summary>
    public static CellularSample Cellular(NoiseKey key, Fixed32 x, Fixed32 y, Fixed32 z)
    {
        CellularKernels.Cellular3(Lanes1.Broadcast((int)key.MixLow), Lanes1.Broadcast((int)key.MixHigh), Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw), Lanes1.Broadcast(z.Raw), out var f1, out var f2, out var cell);
        return new CellularSample(Fixed32.FromRaw(Lanes1.Lane(f1, 0)), Fixed32.FromRaw(Lanes1.Lane(f2, 0)), (uint)Lanes1.Lane(cell, 0));
    }

    /// <summary>2D cellular noise for a batch of positions, structure-of-arrays out.</summary>
    public static void Cellular(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> f1, Span<Fixed32> f2, Span<uint> cells) => Cellular(key, x, y, f1, f2, cells, ActivePath);

    /// <summary>3D cellular noise for a batch of positions.</summary>
    public static void Cellular(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> f1, Span<Fixed32> f2, Span<uint> cells) => Cellular(key, x, y, z, f1, f2, cells, ActivePath);

    internal static void Cellular(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> f1, Span<Fixed32> f2, Span<uint> cells, KernelPath path)
    {
        if (x.Length != y.Length || x.Length != f1.Length || x.Length != f2.Length || x.Length != cells.Length)
        {
            ThrowLengthMismatch();
        }

        var xs = Raw(x);
        var ys = Raw(y);
        var r1 = Raw(f1);
        var r2 = Raw(f2);
        var rc = MemoryMarshal.Cast<uint, int>(cells);
        int i = path switch
        {
            KernelPath.Vector256 => RunCellular2<Lanes256>(key, xs, ys, r1, r2, rc),
            KernelPath.Vector128 => RunCellular2<Lanes128>(key, xs, ys, r1, r2, rc),
            _ => 0,
        };

        RunCellular2<Lanes1>(key, xs[i..], ys[i..], r1[i..], r2[i..], rc[i..]);
    }

    internal static void Cellular(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> f1, Span<Fixed32> f2, Span<uint> cells, KernelPath path)
    {
        if (x.Length != y.Length || x.Length != z.Length || x.Length != f1.Length || x.Length != f2.Length || x.Length != cells.Length)
        {
            ThrowLengthMismatch();
        }

        var xs = Raw(x);
        var ys = Raw(y);
        var zs = Raw(z);
        var r1 = Raw(f1);
        var r2 = Raw(f2);
        var rc = MemoryMarshal.Cast<uint, int>(cells);
        int i = path switch
        {
            KernelPath.Vector256 => RunCellular3<Lanes256>(key, xs, ys, zs, r1, r2, rc),
            KernelPath.Vector128 => RunCellular3<Lanes128>(key, xs, ys, zs, r1, r2, rc),
            _ => 0,
        };

        RunCellular3<Lanes1>(key, xs[i..], ys[i..], zs[i..], r1[i..], r2[i..], rc[i..]);
    }

    private static int RunCellular2<T>(NoiseKey key, ReadOnlySpan<int> x, ReadOnlySpan<int> y, Span<int> f1, Span<int> f2, Span<int> cells)
        where T : struct, IIntLanes<T>
    {
        var keyLow = T.Broadcast((int)key.MixLow);
        var keyHigh = T.Broadcast((int)key.MixHigh);
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            CellularKernels.Cellular2(keyLow, keyHigh, T.Load(x[i..]), T.Load(y[i..]), out var d1, out var d2, out var cell);
            T.Store(d1, f1[i..]);
            T.Store(d2, f2[i..]);
            T.Store(cell, cells[i..]);
        }

        return i;
    }

    private static int RunCellular3<T>(NoiseKey key, ReadOnlySpan<int> x, ReadOnlySpan<int> y, ReadOnlySpan<int> z, Span<int> f1, Span<int> f2, Span<int> cells)
        where T : struct, IIntLanes<T>
    {
        var keyLow = T.Broadcast((int)key.MixLow);
        var keyHigh = T.Broadcast((int)key.MixHigh);
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            CellularKernels.Cellular3(keyLow, keyHigh, T.Load(x[i..]), T.Load(y[i..]), T.Load(z[i..]), out var d1, out var d2, out var cell);
            T.Store(d1, f1[i..]);
            T.Store(d2, f2[i..]);
            T.Store(cell, cells[i..]);
        }

        return i;
    }
}
