using System.Runtime.Intrinsics;

namespace Colony.Numerics;

/// <summary>
/// The Framework's deterministic float layer (spec section 6, ADR 0002). Simulation code uses this instead of
/// <c>System.Math</c>/<c>System.MathF</c> and <c>Vector&lt;T&gt;</c>.
/// <list type="bullet">
/// <item>Fixed logical SIMD width of <see cref="LaneCount"/> lanes: results never depend on the hardware vector width.</item>
/// <item>Reductions (<see cref="Sum(ReadOnlySpan{float})"/>, <see cref="Dot(ReadOnlySpan{float}, ReadOnlySpan{float})"/>) use a fixed tree.</item>
/// <item>No fused multiply-add: <see cref="MultiplyAdd(float, float, float)"/> always rounds twice.</item>
/// <item>Float-to-int conversion is defined: truncate toward zero, saturate, NaN becomes zero.</item>
/// <item>Own transcendentals (<see cref="Sin(float)"/>, <see cref="Cos(float)"/>, <see cref="Exp(float)"/>, <see cref="Log(float)"/>), accurate to a few ulp.</item>
/// </list>
/// Every span operation runs on 256-bit vectors when the hardware has them and on two 128-bit halves otherwise;
/// both produce identical bits (proved by tests), as does the scalar instantiation used for tails and scalars.
/// Nothing here allocates.
/// </summary>
public static class FloatMath
{
    /// <summary>The logical SIMD width in floats. Column padding and reduction trees are defined in terms of it.</summary>
    public const int LaneCount = 8;

    internal static SimdPath ActivePath { get; } = Vector256.IsHardwareAccelerated ? SimdPath.Vector256 : SimdPath.Vector128;

    public static float Sin(float x) => Scalar<SinOp>(x);

    public static float Cos(float x) => Scalar<CosOp>(x);

    public static float Exp(float x) => Scalar<ExpOp>(x);

    public static float Log(float x) => Scalar<LogOp>(x);

    public static float Sqrt(float x) => Scalar<SqrtOp>(x);

    public static float Abs(float x) => Scalar<AbsOp>(x);

    public static float Min(float a, float b) => Lanes1.Lane(FloatKernels.Min(Lanes1.Broadcast(a), Lanes1.Broadcast(b)), 0);

    public static float Max(float a, float b) => Lanes1.Lane(FloatKernels.Max(Lanes1.Broadcast(a), Lanes1.Broadcast(b)), 0);

    /// <summary>(a * b) + c with two roundings, never fused.</summary>
    public static float MultiplyAdd(float a, float b, float c)
        => Lanes1.Lane(FloatKernels.MultiplyAdd(Lanes1.Broadcast(a), Lanes1.Broadcast(b), Lanes1.Broadcast(c)), 0);

    /// <summary>Truncates toward zero and saturates to the Int32 range; NaN becomes zero.</summary>
    public static int ToInt32Saturating(float x)
    {
        Span<int> result = stackalloc int[1];
        Lanes1.StoreBits(FloatKernels.ToInt32Saturating(Lanes1.Broadcast(x)), result);
        return result[0];
    }

    public static void Add(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result) => Add(a, b, result, ActivePath);

    public static void Subtract(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result) => Subtract(a, b, result, ActivePath);

    public static void Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result) => Multiply(a, b, result, ActivePath);

    public static void Divide(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result) => Divide(a, b, result, ActivePath);

    public static void Min(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result) => Min(a, b, result, ActivePath);

    public static void Max(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result) => Max(a, b, result, ActivePath);

    public static void Scale(ReadOnlySpan<float> a, float scale, Span<float> result) => Scale(a, scale, result, ActivePath);

    /// <summary>result[i] = (a[i] * b[i]) + c[i] with two roundings, never fused.</summary>
    public static void MultiplyAdd(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> c, Span<float> result) => MultiplyAdd(a, b, c, result, ActivePath);

    public static void Sqrt(ReadOnlySpan<float> x, Span<float> result) => Sqrt(x, result, ActivePath);

    public static void Abs(ReadOnlySpan<float> x, Span<float> result) => Abs(x, result, ActivePath);

    public static void Sin(ReadOnlySpan<float> x, Span<float> result) => Sin(x, result, ActivePath);

    public static void Cos(ReadOnlySpan<float> x, Span<float> result) => Cos(x, result, ActivePath);

    public static void Exp(ReadOnlySpan<float> x, Span<float> result) => Exp(x, result, ActivePath);

    public static void Log(ReadOnlySpan<float> x, Span<float> result) => Log(x, result, ActivePath);

    public static void ToInt32Saturating(ReadOnlySpan<float> x, Span<int> result) => ToInt32Saturating(x, result, ActivePath);

    /// <summary>Fixed-tree sum: eight lane accumulators over blocks of eight, then ((l0+l1)+(l2+l3))+((l4+l5)+(l6+l7)).</summary>
    public static float Sum(ReadOnlySpan<float> x) => Sum(x, ActivePath);

    /// <summary>Fixed-tree dot product with separately rounded multiply and add per lane.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => Dot(a, b, ActivePath);

    internal static void Add(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result, SimdPath path) => Binary<AddOp>(a, b, result, path);

    internal static void Subtract(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result, SimdPath path) => Binary<SubtractOp>(a, b, result, path);

    internal static void Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result, SimdPath path) => Binary<MultiplyOp>(a, b, result, path);

    internal static void Divide(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result, SimdPath path) => Binary<DivideOp>(a, b, result, path);

    internal static void Min(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result, SimdPath path) => Binary<MinOp>(a, b, result, path);

    internal static void Max(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result, SimdPath path) => Binary<MaxOp>(a, b, result, path);

    internal static void Sqrt(ReadOnlySpan<float> x, Span<float> result, SimdPath path) => Unary<SqrtOp>(x, result, path);

    internal static void Abs(ReadOnlySpan<float> x, Span<float> result, SimdPath path) => Unary<AbsOp>(x, result, path);

    internal static void Sin(ReadOnlySpan<float> x, Span<float> result, SimdPath path) => Unary<SinOp>(x, result, path);

    internal static void Cos(ReadOnlySpan<float> x, Span<float> result, SimdPath path) => Unary<CosOp>(x, result, path);

    internal static void Exp(ReadOnlySpan<float> x, Span<float> result, SimdPath path) => Unary<ExpOp>(x, result, path);

    internal static void Log(ReadOnlySpan<float> x, Span<float> result, SimdPath path) => Unary<LogOp>(x, result, path);

    internal static void Scale(ReadOnlySpan<float> a, float scale, Span<float> result, SimdPath path)
    {
        RequireSameLength(a.Length, result.Length);
        int done = path switch
        {
            SimdPath.Vector256 => FloatKernels.Scale<Lanes256>(a, scale, result),
            SimdPath.Vector128 => FloatKernels.Scale<Lanes128x2>(a, scale, result),
            _ => FloatKernels.Scale<Lanes8Scalar>(a, scale, result),
        };
        FloatKernels.Scale<Lanes1>(a[done..], scale, result[done..]);
    }

    internal static void MultiplyAdd(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> c, Span<float> result, SimdPath path)
    {
        RequireSameLength(a.Length, b.Length);
        RequireSameLength(a.Length, c.Length);
        RequireSameLength(a.Length, result.Length);
        int done = path switch
        {
            SimdPath.Vector256 => FloatKernels.MultiplyAdd<Lanes256>(a, b, c, result),
            SimdPath.Vector128 => FloatKernels.MultiplyAdd<Lanes128x2>(a, b, c, result),
            _ => FloatKernels.MultiplyAdd<Lanes8Scalar>(a, b, c, result),
        };
        FloatKernels.MultiplyAdd<Lanes1>(a[done..], b[done..], c[done..], result[done..]);
    }

    internal static void ToInt32Saturating(ReadOnlySpan<float> x, Span<int> result, SimdPath path)
    {
        RequireSameLength(x.Length, result.Length);
        int done = path switch
        {
            SimdPath.Vector256 => FloatKernels.ToInt32Saturating<Lanes256>(x, result),
            SimdPath.Vector128 => FloatKernels.ToInt32Saturating<Lanes128x2>(x, result),
            _ => FloatKernels.ToInt32Saturating<Lanes8Scalar>(x, result),
        };
        FloatKernels.ToInt32Saturating<Lanes1>(x[done..], result[done..]);
    }

    internal static float Sum(ReadOnlySpan<float> x, SimdPath path)
    {
        return path switch
        {
            SimdPath.Vector256 => FloatKernels.Sum<Lanes256>(x),
            SimdPath.Vector128 => FloatKernels.Sum<Lanes128x2>(x),
            _ => FloatKernels.Sum<Lanes8Scalar>(x),
        };
    }

    internal static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b, SimdPath path)
    {
        RequireSameLength(a.Length, b.Length);
        return path switch
        {
            SimdPath.Vector256 => FloatKernels.Dot<Lanes256>(a, b),
            SimdPath.Vector128 => FloatKernels.Dot<Lanes128x2>(a, b),
            _ => FloatKernels.Dot<Lanes8Scalar>(a, b),
        };
    }

    private static float Scalar<TOp>(float x)
        where TOp : struct, IUnaryOp
        => Lanes1.Lane(TOp.Apply(Lanes1.Broadcast(x)), 0);

    private static void Unary<TOp>(ReadOnlySpan<float> x, Span<float> result, SimdPath path)
        where TOp : struct, IUnaryOp
    {
        RequireSameLength(x.Length, result.Length);
        int done = path switch
        {
            SimdPath.Vector256 => FloatKernels.Unary<Lanes256, TOp>(x, result),
            SimdPath.Vector128 => FloatKernels.Unary<Lanes128x2, TOp>(x, result),
            _ => FloatKernels.Unary<Lanes8Scalar, TOp>(x, result),
        };
        FloatKernels.Unary<Lanes1, TOp>(x[done..], result[done..]);
    }

    private static void Binary<TOp>(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result, SimdPath path)
        where TOp : struct, IBinaryOp
    {
        RequireSameLength(a.Length, b.Length);
        RequireSameLength(a.Length, result.Length);
        int done = path switch
        {
            SimdPath.Vector256 => FloatKernels.Binary<Lanes256, TOp>(a, b, result),
            SimdPath.Vector128 => FloatKernels.Binary<Lanes128x2, TOp>(a, b, result),
            _ => FloatKernels.Binary<Lanes8Scalar, TOp>(a, b, result),
        };
        FloatKernels.Binary<Lanes1, TOp>(a[done..], b[done..], result[done..]);
    }

    private static void RequireSameLength(int expected, int actual)
    {
        if (expected != actual)
        {
            throw new ArgumentException("All spans passed to a FloatMath operation must have the same length.");
        }
    }
}
