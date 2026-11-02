namespace Colony.Numerics;

/// <summary>An elementwise operation the span loops can be instantiated with (no delegates, no allocation).</summary>
internal interface IUnaryOp
{
    public static abstract T Apply<T>(T x)
        where T : struct, IFloatLanes<T>;
}

internal interface IBinaryOp
{
    public static abstract T Apply<T>(T a, T b)
        where T : struct, IFloatLanes<T>;
}

/// <summary>
/// The float kernels, written once against <see cref="IFloatLanes{TSelf}"/> and instantiated per path.
/// Only IEEE basic operations and bit manipulation are used, products and sums are never fused, and
/// every constant is a float literal, so each instantiation produces identical bits.
/// Transcendentals follow the classic Cephes single-precision formulations (range reduction plus a
/// short polynomial), which give roughly 1-2 ulp accuracy over the documented domain.
/// </summary>
internal static class FloatKernels
{
    // 1.5 * 2^23: adding and subtracting it rounds a float to the nearest integer (ties to even) for |x| < 2^22.
    private const float RoundingMagic = 12582912f;
    private const float SignBit = -0.0f;

    // pi/2 in three parts; the first has 10 significant bits so k * PiOver2A is exact for k < 2^14.
    private const float PiOver2A = 1.5703125f;
    private const float PiOver2B = 4.837512969970703125e-4f;
    private const float PiOver2C = 7.54978995489188216e-8f;
    private const float TwoOverPi = 0.636619772367581343f;

    // Beyond this magnitude a float has no fractional bits left, so trigonometric reduction is meaningless.
    private const float TrigDomain = 16777216f;

    private const float Log2E = 1.44269504088896341f;
    private const float Ln2A = 0.693359375f;
    private const float Ln2B = -2.12194440e-4f;

    internal static T Add<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => T.Add(a, b);

    internal static T Subtract<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => T.Subtract(a, b);

    internal static T Multiply<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => T.Multiply(a, b);

    internal static T Divide<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => T.Divide(a, b);

    /// <summary>Returns <paramref name="b"/> when it is strictly less than <paramref name="a"/>, else <paramref name="a"/> (so NaN in <paramref name="b"/> is never chosen).</summary>
    internal static T Min<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => T.Select(T.LessThan(b, a), b, a);

    internal static T Max<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => T.Select(T.GreaterThan(b, a), b, a);

    internal static T Abs<T>(T x)
        where T : struct, IFloatLanes<T>
        => T.AndNot(x, T.Broadcast(SignBit));

    internal static T Sqrt<T>(T x)
        where T : struct, IFloatLanes<T>
        => T.Sqrt(x);

    /// <summary>(a * b) + c with two roundings; never a fused multiply-add.</summary>
    internal static T MultiplyAdd<T>(T a, T b, T c)
        where T : struct, IFloatLanes<T>
        => T.Add(T.Multiply(a, b), c);

    /// <summary>Rounds to the nearest integer-valued float (ties to even) for |x| &lt; 2^22.</summary>
    private static T RoundNearest<T>(T x)
        where T : struct, IFloatLanes<T>
    {
        T magic = T.Broadcast(RoundingMagic);
        return T.Subtract(T.Add(x, magic), magic);
    }

    private static T IntBits<T>(int value)
        where T : struct, IFloatLanes<T>
        => T.Broadcast(BitConverter.Int32BitsToSingle(value));

    internal static T Sin<T>(T x)
        where T : struct, IFloatLanes<T>
    {
        SinCosCore(x, out T sin, out _);
        return sin;
    }

    internal static T Cos<T>(T x)
        where T : struct, IFloatLanes<T>
    {
        SinCosCore(x, out _, out T cos);
        return cos;
    }

    private static void SinCosCore<T>(T x, out T sin, out T cos)
        where T : struct, IFloatLanes<T>
    {
        // Out-of-domain lanes (NaN, infinity, |x| >= 2^24) are computed on zero and patched at the end.
        T inDomain = T.LessThan(Abs(x), T.Broadcast(TrigDomain));
        T xs = T.Select(inDomain, x, T.Zero);

        // k = round(x * 2/pi); r = x - k * pi/2 in three exact-ish steps.
        T k = RoundNearest(T.Multiply(xs, T.Broadcast(TwoOverPi)));
        T r = T.Subtract(xs, T.Multiply(k, T.Broadcast(PiOver2A)));
        r = T.Subtract(r, T.Multiply(k, T.Broadcast(PiOver2B)));
        r = T.Subtract(r, T.Multiply(k, T.Broadcast(PiOver2C)));
        T r2 = T.Multiply(r, r);

        // sin(r) = r + r^3 (S1 + r^2 (S2 + r^2 S3))
        T sinPoly = T.Broadcast(-1.9515295891e-4f);
        sinPoly = T.Add(T.Multiply(sinPoly, r2), T.Broadcast(8.3321608736e-3f));
        sinPoly = T.Add(T.Multiply(sinPoly, r2), T.Broadcast(-1.6666654611e-1f));
        T sinR = T.Add(r, T.Multiply(T.Multiply(sinPoly, r2), r));

        // cos(r) = 1 - r^2/2 + r^4 (C1 + r^2 (C2 + r^2 C3))
        T cosPoly = T.Broadcast(2.443315711809948e-5f);
        cosPoly = T.Add(T.Multiply(cosPoly, r2), T.Broadcast(-1.388731625493765e-3f));
        cosPoly = T.Add(T.Multiply(cosPoly, r2), T.Broadcast(4.166664568298827e-2f));
        T cosR = T.Multiply(T.Multiply(cosPoly, r2), r2);
        cosR = T.Subtract(cosR, T.Multiply(r2, T.Broadcast(0.5f)));
        cosR = T.Add(cosR, T.Broadcast(1f));

        // Quadrant selection from the low two bits of k.
        T kBits = T.TruncateToInt32(k);
        T one = IntBits<T>(1);
        T two = IntBits<T>(2);
        T odd = T.EqualInt32(T.And(kBits, one), one);
        T upper = T.EqualInt32(T.And(kBits, two), two);
        T signBit = T.Broadcast(SignBit);

        T sinValue = T.Xor(T.Select(odd, cosR, sinR), T.And(upper, signBit));
        T cosValue = T.Xor(T.Select(odd, sinR, cosR), T.And(T.Xor(odd, upper), signBit));

        // Out of domain: x - x is NaN for NaN and infinities, zero for huge finite inputs.
        T outOfDomain = T.Subtract(x, x);
        sin = T.Select(inDomain, sinValue, outOfDomain);
        cos = T.Select(inDomain, cosValue, outOfDomain);
    }

    internal static T Exp<T>(T x)
        where T : struct, IFloatLanes<T>
    {
        // Clamp so the scale factor stays constructible; results beyond the clamp are 0 and +infinity anyway.
        T xc = Max(Min(x, T.Broadcast(88.8f)), T.Broadcast(-104f));

        T k = RoundNearest(T.Multiply(xc, T.Broadcast(Log2E)));
        T r = T.Subtract(xc, T.Multiply(k, T.Broadcast(Ln2A)));
        r = T.Subtract(r, T.Multiply(k, T.Broadcast(Ln2B)));

        T p = T.Broadcast(1.9875691500e-4f);
        p = T.Add(T.Multiply(p, r), T.Broadcast(1.3981999507e-3f));
        p = T.Add(T.Multiply(p, r), T.Broadcast(8.3334519073e-3f));
        p = T.Add(T.Multiply(p, r), T.Broadcast(4.1665795894e-2f));
        p = T.Add(T.Multiply(p, r), T.Broadcast(1.6666665459e-1f));
        p = T.Add(T.Multiply(p, r), T.Broadcast(5.0000001201e-1f));
        T y = T.Add(T.Add(T.Multiply(T.Multiply(p, r), r), r), T.Broadcast(1f));

        // 2^k in two halves so that k in [-150, 128] never needs an out-of-range exponent field.
        T kBits = T.TruncateToInt32(k);
        T half = T.ShiftRightArithmeticInt32(kBits, 1);
        T rest = T.AddInt32(kBits, T.Xor(half, IntBits<T>(-1))); // k - half, via k + ~half + 1
        rest = T.AddInt32(rest, IntBits<T>(1));
        y = T.Multiply(y, PowerOfTwo(half));
        y = T.Multiply(y, PowerOfTwo(rest));

        // NaN propagates (the clamp would otherwise swallow it).
        return T.Select(T.Equal(x, x), y, x);
    }

    /// <summary>2^k for integer-valued lane bits k in [-126, 127].</summary>
    private static T PowerOfTwo<T>(T kBits)
        where T : struct, IFloatLanes<T>
        => T.ShiftLeftInt32(T.AddInt32(kBits, IntBits<T>(127)), 23);

    internal static T Log<T>(T x)
        where T : struct, IFloatLanes<T>
    {
        // Denormals are scaled into the normal range first (2^23) and the exponent corrected afterwards.
        T isDenormal = T.LessThan(x, T.Broadcast(1.17549435e-38f));
        T xs = T.Select(isDenormal, T.Multiply(x, T.Broadcast(8388608f)), x);

        T exponent = T.AddInt32(T.ShiftRightArithmeticInt32(xs, 23), IntBits<T>(-127));
        T mantissa = T.Or(T.And(xs, IntBits<T>(0x007FFFFF)), IntBits<T>(0x3F800000)); // [1, 2)

        // Centre the mantissa on 1: if m > sqrt(2) use m/2 and bump the exponent.
        T big = T.GreaterThan(mantissa, T.Broadcast(1.41421356f));
        mantissa = T.Select(big, T.Multiply(mantissa, T.Broadcast(0.5f)), mantissa);
        exponent = T.AddInt32(exponent, T.And(big, IntBits<T>(1)));
        exponent = T.AddInt32(exponent, T.And(isDenormal, IntBits<T>(-23)));
        T e = T.Int32ToSingle(exponent);

        T f = T.Subtract(mantissa, T.Broadcast(1f));
        T z = T.Multiply(f, f);

        T y = T.Broadcast(7.0376836292e-2f);
        y = T.Add(T.Multiply(y, f), T.Broadcast(-1.1514610310e-1f));
        y = T.Add(T.Multiply(y, f), T.Broadcast(1.1676998740e-1f));
        y = T.Add(T.Multiply(y, f), T.Broadcast(-1.2420140846e-1f));
        y = T.Add(T.Multiply(y, f), T.Broadcast(1.4249322787e-1f));
        y = T.Add(T.Multiply(y, f), T.Broadcast(-1.6668057665e-1f));
        y = T.Add(T.Multiply(y, f), T.Broadcast(2.0000714765e-1f));
        y = T.Add(T.Multiply(y, f), T.Broadcast(-2.4999993993e-1f));
        y = T.Add(T.Multiply(y, f), T.Broadcast(3.3333331174e-1f));
        y = T.Multiply(T.Multiply(y, f), z);

        y = T.Add(y, T.Multiply(e, T.Broadcast(Ln2B)));
        y = T.Subtract(y, T.Multiply(z, T.Broadcast(0.5f)));
        T result = T.Add(f, y);
        result = T.Add(result, T.Multiply(e, T.Broadcast(Ln2A)));

        // Edges: negative or NaN -> NaN, zero -> -infinity, +infinity -> +infinity.
        T positive = T.GreaterThan(x, T.Zero);
        T zeroOrNaN = T.Select(T.Equal(x, T.Zero), T.Broadcast(float.NegativeInfinity), T.Broadcast(float.NaN));
        result = T.Select(positive, result, zeroOrNaN);
        T infinity = T.Broadcast(float.PositiveInfinity);
        return T.Select(T.Equal(x, infinity), infinity, result);
    }

    /// <summary>Truncates toward zero with saturation; NaN becomes zero. Result lanes hold Int32 bits.</summary>
    internal static T ToInt32Saturating<T>(T x)
        where T : struct, IFloatLanes<T>
    {
        T low = T.Broadcast(-2147483648f);
        T high = T.Broadcast(2147483520f); // largest float below 2^31
        T clamped = T.Select(T.LessThan(x, low), low, x);
        T aboveHigh = T.GreaterThan(x, high);
        clamped = T.Select(aboveHigh, high, clamped);
        T bits = T.TruncateToInt32(clamped);
        bits = T.Select(aboveHigh, IntBits<T>(int.MaxValue), bits);
        return T.And(bits, T.Equal(x, x));
    }

    /// <summary>Applies <typeparamref name="TOp"/> to whole lane groups; returns how many elements were processed.</summary>
    internal static int Unary<TLanes, TOp>(ReadOnlySpan<float> x, Span<float> result)
        where TLanes : struct, IFloatLanes<TLanes>
        where TOp : struct, IUnaryOp
    {
        int width = TLanes.Count;
        int i = 0;
        for (; i + width <= x.Length; i += width)
        {
            TLanes.Store(TOp.Apply(TLanes.Load(x.Slice(i, width))), result.Slice(i, width));
        }

        return i;
    }

    internal static int Binary<TLanes, TOp>(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result)
        where TLanes : struct, IFloatLanes<TLanes>
        where TOp : struct, IBinaryOp
    {
        int width = TLanes.Count;
        int i = 0;
        for (; i + width <= a.Length; i += width)
        {
            TLanes.Store(TOp.Apply(TLanes.Load(a.Slice(i, width)), TLanes.Load(b.Slice(i, width))), result.Slice(i, width));
        }

        return i;
    }

    internal static int Scale<TLanes>(ReadOnlySpan<float> a, float scale, Span<float> result)
        where TLanes : struct, IFloatLanes<TLanes>
    {
        int width = TLanes.Count;
        TLanes s = TLanes.Broadcast(scale);
        int i = 0;
        for (; i + width <= a.Length; i += width)
        {
            TLanes.Store(TLanes.Multiply(TLanes.Load(a.Slice(i, width)), s), result.Slice(i, width));
        }

        return i;
    }

    internal static int MultiplyAdd<TLanes>(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> c, Span<float> result)
        where TLanes : struct, IFloatLanes<TLanes>
    {
        int width = TLanes.Count;
        int i = 0;
        for (; i + width <= a.Length; i += width)
        {
            TLanes.Store(MultiplyAdd(TLanes.Load(a.Slice(i, width)), TLanes.Load(b.Slice(i, width)), TLanes.Load(c.Slice(i, width))), result.Slice(i, width));
        }

        return i;
    }

    internal static int ToInt32Saturating<TLanes>(ReadOnlySpan<float> x, Span<int> result)
        where TLanes : struct, IFloatLanes<TLanes>
    {
        int width = TLanes.Count;
        int i = 0;
        for (; i + width <= x.Length; i += width)
        {
            TLanes.StoreBits(ToInt32Saturating(TLanes.Load(x.Slice(i, width))), result.Slice(i, width));
        }

        return i;
    }

    /// <summary>
    /// Fixed-tree sum over eight logical lanes: lane accumulators over whole blocks, a zero-padded final
    /// block, then ((l0+l1)+(l2+l3))+((l4+l5)+(l6+l7)). Independent of the hardware width by construction.
    /// </summary>
    internal static float Sum<TLanes>(ReadOnlySpan<float> x)
        where TLanes : struct, IFloatLanes<TLanes>
    {
        TLanes acc = TLanes.Zero;
        int i = 0;
        for (; i + 8 <= x.Length; i += 8)
        {
            acc = TLanes.Add(acc, TLanes.Load(x.Slice(i, 8)));
        }

        if (i < x.Length)
        {
            Span<float> tail = stackalloc float[8];
            x[i..].CopyTo(tail);
            acc = TLanes.Add(acc, TLanes.Load(tail));
        }

        return HorizontalSum(acc);
    }

    /// <summary>Fixed-tree dot product: per-lane multiply then add (two roundings), then the same tree as <see cref="Sum{TLanes}"/>.</summary>
    internal static float Dot<TLanes>(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        where TLanes : struct, IFloatLanes<TLanes>
    {
        TLanes acc = TLanes.Zero;
        int i = 0;
        for (; i + 8 <= a.Length; i += 8)
        {
            acc = TLanes.Add(acc, TLanes.Multiply(TLanes.Load(a.Slice(i, 8)), TLanes.Load(b.Slice(i, 8))));
        }

        if (i < a.Length)
        {
            Span<float> tailA = stackalloc float[8];
            Span<float> tailB = stackalloc float[8];
            a[i..].CopyTo(tailA);
            b[i..].CopyTo(tailB);
            acc = TLanes.Add(acc, TLanes.Multiply(TLanes.Load(tailA), TLanes.Load(tailB)));
        }

        return HorizontalSum(acc);
    }

    private static float HorizontalSum<TLanes>(TLanes acc)
        where TLanes : struct, IFloatLanes<TLanes>
    {
        float s01 = TLanes.Lane(acc, 0) + TLanes.Lane(acc, 1);
        float s23 = TLanes.Lane(acc, 2) + TLanes.Lane(acc, 3);
        float s45 = TLanes.Lane(acc, 4) + TLanes.Lane(acc, 5);
        float s67 = TLanes.Lane(acc, 6) + TLanes.Lane(acc, 7);
        return (s01 + s23) + (s45 + s67);
    }
}

internal readonly struct AddOp : IBinaryOp
{
    public static T Apply<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Add(a, b);
}

internal readonly struct SubtractOp : IBinaryOp
{
    public static T Apply<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Subtract(a, b);
}

internal readonly struct MultiplyOp : IBinaryOp
{
    public static T Apply<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Multiply(a, b);
}

internal readonly struct DivideOp : IBinaryOp
{
    public static T Apply<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Divide(a, b);
}

internal readonly struct MinOp : IBinaryOp
{
    public static T Apply<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Min(a, b);
}

internal readonly struct MaxOp : IBinaryOp
{
    public static T Apply<T>(T a, T b)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Max(a, b);
}

internal readonly struct SqrtOp : IUnaryOp
{
    public static T Apply<T>(T x)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Sqrt(x);
}

internal readonly struct AbsOp : IUnaryOp
{
    public static T Apply<T>(T x)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Abs(x);
}

internal readonly struct SinOp : IUnaryOp
{
    public static T Apply<T>(T x)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Sin(x);
}

internal readonly struct CosOp : IUnaryOp
{
    public static T Apply<T>(T x)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Cos(x);
}

internal readonly struct ExpOp : IUnaryOp
{
    public static T Apply<T>(T x)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Exp(x);
}

internal readonly struct LogOp : IUnaryOp
{
    public static T Apply<T>(T x)
        where T : struct, IFloatLanes<T>
        => FloatKernels.Log(x);
}
