using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Colony.Numerics;

namespace Colony.ProcGen;

/// <summary>
/// Deterministic fixed-point noise (spec section 9.3). Every function is a pure function of a <see cref="NoiseKey"/>
/// and a Q16.16 coordinate: integer lattice hashing, integer interpolation, no floats anywhere, so results are
/// bit-identical across threads, evaluation order, CPUs and SIMD widths. Coordinates are in lattice units (one
/// lattice cell per whole number); scale them before calling to set the frequency. Point overloads evaluate one
/// sample; span overloads evaluate a structure-of-arrays batch through the SIMD kernels and allocate nothing.
/// </summary>
public static class Noise
{
    internal static KernelPath ActivePath { get; } = Vector256.IsHardwareAccelerated ? KernelPath.Vector256 : KernelPath.Vector128;

    /// <summary>2D value noise in [-1, 1).</summary>
    public static Fixed32 Value(NoiseKey key, Fixed32 x, Fixed32 y) => Point2<ValueOp2>(key, x, y);

    /// <summary>2D value noise for a batch of positions. <paramref name="result"/> may alias an input.</summary>
    public static void Value(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result) => Value(key, x, y, result, ActivePath);

    internal static void Value(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result, KernelPath path) => Batch2<ValueOp2>(key, x, y, result, path);

    private static Fixed32 Point2<TOp>(NoiseKey key, Fixed32 x, Fixed32 y)
        where TOp : struct, INoiseOp2
    {
        var r = TOp.Evaluate(Lanes1.Broadcast((int)key.MixLow), Lanes1.Broadcast((int)key.MixHigh), Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw));
        return Fixed32.FromRaw(Lanes1.Lane(r, 0));
    }

    private static void Batch2<TOp>(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result, KernelPath path)
        where TOp : struct, INoiseOp2
    {
        if (x.Length != y.Length || x.Length != result.Length)
        {
            ThrowLengthMismatch();
        }

        var xs = MemoryMarshal.Cast<Fixed32, int>(x);
        var ys = MemoryMarshal.Cast<Fixed32, int>(y);
        var rs = MemoryMarshal.Cast<Fixed32, int>(result);
        int i = 0;
        switch (path)
        {
            case KernelPath.Vector256:
                i = Run2<TOp, Lanes256>(key, xs, ys, rs);
                break;
            case KernelPath.Vector128:
                i = Run2<TOp, Lanes128>(key, xs, ys, rs);
                break;
            default:
                break;
        }

        Run2<TOp, Lanes1>(key, xs[i..], ys[i..], rs[i..]);
    }

    /// <summary>Runs whole lane groups and returns the number of elements processed.</summary>
    private static int Run2<TOp, T>(NoiseKey key, ReadOnlySpan<int> x, ReadOnlySpan<int> y, Span<int> result)
        where TOp : struct, INoiseOp2
        where T : struct, IIntLanes<T>
    {
        var keyLow = T.Broadcast((int)key.MixLow);
        var keyHigh = T.Broadcast((int)key.MixHigh);
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            T.Store(TOp.Evaluate(keyLow, keyHigh, T.Load(x[i..]), T.Load(y[i..])), result[i..]);
        }

        return i;
    }

    private static void ThrowLengthMismatch() => throw new ArgumentException("x, y and result spans must have the same length.");

    /// <summary>A 2D kernel: (key words, x, y) to a Q16 value, generic over the lane type.</summary>
    private interface INoiseOp2
    {
        public static abstract T Evaluate<T>(T keyLow, T keyHigh, T x, T y)
            where T : struct, IIntLanes<T>;
    }

    private readonly struct ValueOp2 : INoiseOp2
    {
        public static T Evaluate<T>(T keyLow, T keyHigh, T x, T y)
            where T : struct, IIntLanes<T>
            => NoiseKernels.Value2(keyLow, keyHigh, x, y);
    }
}
