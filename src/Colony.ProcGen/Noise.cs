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
/// Outputs are in [-1, 1].
/// </summary>
public static class Noise
{
    internal static KernelPath ActivePath { get; } = Vector256.IsHardwareAccelerated ? KernelPath.Vector256 : KernelPath.Vector128;

    /// <summary>2D value noise: smoothly interpolated hashed lattice values.</summary>
    public static Fixed32 Value(NoiseKey key, Fixed32 x, Fixed32 y) => Point2<ValueOp>(key, x, y);

    /// <summary>3D value noise.</summary>
    public static Fixed32 Value(NoiseKey key, Fixed32 x, Fixed32 y, Fixed32 z) => Point3<ValueOp>(key, x, y, z);

    /// <summary>2D value noise for a batch of positions. <paramref name="result"/> may alias an input.</summary>
    public static void Value(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result) => Value(key, x, y, result, ActivePath);

    /// <summary>3D value noise for a batch of positions.</summary>
    public static void Value(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result) => Value(key, x, y, z, result, ActivePath);

    /// <summary>2D gradient (Perlin) noise: zero at lattice points.</summary>
    public static Fixed32 Gradient(NoiseKey key, Fixed32 x, Fixed32 y) => Point2<GradientOp>(key, x, y);

    /// <summary>3D gradient (Perlin) noise.</summary>
    public static Fixed32 Gradient(NoiseKey key, Fixed32 x, Fixed32 y, Fixed32 z) => Point3<GradientOp>(key, x, y, z);

    /// <summary>2D gradient noise for a batch of positions.</summary>
    public static void Gradient(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result) => Gradient(key, x, y, result, ActivePath);

    /// <summary>3D gradient noise for a batch of positions.</summary>
    public static void Gradient(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result) => Gradient(key, x, y, z, result, ActivePath);

    /// <summary>2D simplex noise: isotropic, cheaper than gradient noise per sample in 3D.</summary>
    public static Fixed32 Simplex(NoiseKey key, Fixed32 x, Fixed32 y) => Point2<SimplexOp>(key, x, y);

    /// <summary>3D simplex noise.</summary>
    public static Fixed32 Simplex(NoiseKey key, Fixed32 x, Fixed32 y, Fixed32 z) => Point3<SimplexOp>(key, x, y, z);

    /// <summary>2D simplex noise for a batch of positions.</summary>
    public static void Simplex(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result) => Simplex(key, x, y, result, ActivePath);

    /// <summary>3D simplex noise for a batch of positions.</summary>
    public static void Simplex(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result) => Simplex(key, x, y, z, result, ActivePath);

    internal static void Value(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result, KernelPath path) => Batch2<ValueOp>(key, x, y, result, path);

    internal static void Value(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result, KernelPath path) => Batch3<ValueOp>(key, x, y, z, result, path);

    internal static void Gradient(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result, KernelPath path) => Batch2<GradientOp>(key, x, y, result, path);

    internal static void Gradient(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result, KernelPath path) => Batch3<GradientOp>(key, x, y, z, result, path);

    internal static void Simplex(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result, KernelPath path) => Batch2<SimplexOp>(key, x, y, result, path);

    internal static void Simplex(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result, KernelPath path) => Batch3<SimplexOp>(key, x, y, z, result, path);

    internal static ReadOnlySpan<int> Raw(ReadOnlySpan<Fixed32> values) => MemoryMarshal.Cast<Fixed32, int>(values);

    internal static Span<int> Raw(Span<Fixed32> values) => MemoryMarshal.Cast<Fixed32, int>(values);

    internal static void ThrowLengthMismatch() => throw new ArgumentException("All coordinate and result spans must have the same length.");

    private static Fixed32 Point2<TOp>(NoiseKey key, Fixed32 x, Fixed32 y)
        where TOp : struct, INoiseOp
    {
        var r = TOp.Evaluate2(Lanes1.Broadcast((int)key.MixLow), Lanes1.Broadcast((int)key.MixHigh), Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw));
        return Fixed32.FromRaw(Lanes1.Lane(r, 0));
    }

    private static Fixed32 Point3<TOp>(NoiseKey key, Fixed32 x, Fixed32 y, Fixed32 z)
        where TOp : struct, INoiseOp
    {
        var r = TOp.Evaluate3(Lanes1.Broadcast((int)key.MixLow), Lanes1.Broadcast((int)key.MixHigh), Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw), Lanes1.Broadcast(z.Raw));
        return Fixed32.FromRaw(Lanes1.Lane(r, 0));
    }

    private static void Batch2<TOp>(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result, KernelPath path)
        where TOp : struct, INoiseOp
    {
        if (x.Length != y.Length || x.Length != result.Length)
        {
            ThrowLengthMismatch();
        }

        var xs = Raw(x);
        var ys = Raw(y);
        var rs = Raw(result);
        int i = path switch
        {
            KernelPath.Vector256 => Run2<TOp, Lanes256>(key, xs, ys, rs),
            KernelPath.Vector128 => Run2<TOp, Lanes128>(key, xs, ys, rs),
            _ => 0,
        };

        Run2<TOp, Lanes1>(key, xs[i..], ys[i..], rs[i..]);
    }

    private static void Batch3<TOp>(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result, KernelPath path)
        where TOp : struct, INoiseOp
    {
        if (x.Length != y.Length || x.Length != z.Length || x.Length != result.Length)
        {
            ThrowLengthMismatch();
        }

        var xs = Raw(x);
        var ys = Raw(y);
        var zs = Raw(z);
        var rs = Raw(result);
        int i = path switch
        {
            KernelPath.Vector256 => Run3<TOp, Lanes256>(key, xs, ys, zs, rs),
            KernelPath.Vector128 => Run3<TOp, Lanes128>(key, xs, ys, zs, rs),
            _ => 0,
        };

        Run3<TOp, Lanes1>(key, xs[i..], ys[i..], zs[i..], rs[i..]);
    }

    /// <summary>Runs whole lane groups and returns the number of elements processed.</summary>
    private static int Run2<TOp, T>(NoiseKey key, ReadOnlySpan<int> x, ReadOnlySpan<int> y, Span<int> result)
        where TOp : struct, INoiseOp
        where T : struct, IIntLanes<T>
    {
        var keyLow = T.Broadcast((int)key.MixLow);
        var keyHigh = T.Broadcast((int)key.MixHigh);
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            T.Store(TOp.Evaluate2(keyLow, keyHigh, T.Load(x[i..]), T.Load(y[i..])), result[i..]);
        }

        return i;
    }

    private static int Run3<TOp, T>(NoiseKey key, ReadOnlySpan<int> x, ReadOnlySpan<int> y, ReadOnlySpan<int> z, Span<int> result)
        where TOp : struct, INoiseOp
        where T : struct, IIntLanes<T>
    {
        var keyLow = T.Broadcast((int)key.MixLow);
        var keyHigh = T.Broadcast((int)key.MixHigh);
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            T.Store(TOp.Evaluate3(keyLow, keyHigh, T.Load(x[i..]), T.Load(y[i..]), T.Load(z[i..])), result[i..]);
        }

        return i;
    }

    /// <summary>A scalar-valued noise basis in 2D and 3D, generic over the lane type.</summary>
    internal interface INoiseOp
    {
        public static abstract T Evaluate2<T>(T keyLow, T keyHigh, T x, T y)
            where T : struct, IIntLanes<T>;

        public static abstract T Evaluate3<T>(T keyLow, T keyHigh, T x, T y, T z)
            where T : struct, IIntLanes<T>;
    }

    internal readonly struct ValueOp : INoiseOp
    {
        public static T Evaluate2<T>(T keyLow, T keyHigh, T x, T y)
            where T : struct, IIntLanes<T>
            => NoiseKernels.Value2(keyLow, keyHigh, x, y);

        public static T Evaluate3<T>(T keyLow, T keyHigh, T x, T y, T z)
            where T : struct, IIntLanes<T>
            => NoiseKernels.Value3(keyLow, keyHigh, x, y, z);
    }

    internal readonly struct GradientOp : INoiseOp
    {
        public static T Evaluate2<T>(T keyLow, T keyHigh, T x, T y)
            where T : struct, IIntLanes<T>
            => NoiseKernels.Gradient2(keyLow, keyHigh, x, y);

        public static T Evaluate3<T>(T keyLow, T keyHigh, T x, T y, T z)
            where T : struct, IIntLanes<T>
            => NoiseKernels.Gradient3(keyLow, keyHigh, x, y, z);
    }

    internal readonly struct SimplexOp : INoiseOp
    {
        public static T Evaluate2<T>(T keyLow, T keyHigh, T x, T y)
            where T : struct, IIntLanes<T>
            => NoiseKernels.Simplex2(keyLow, keyHigh, x, y);

        public static T Evaluate3<T>(T keyLow, T keyHigh, T x, T y, T z)
            where T : struct, IIntLanes<T>
            => NoiseKernels.Simplex3(keyLow, keyHigh, x, y, z);
    }
}
