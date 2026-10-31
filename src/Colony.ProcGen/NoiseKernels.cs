namespace Colony.ProcGen;

/// <summary>
/// The noise kernels, written once against <see cref="IIntLanes{TSelf}"/> and instantiated for scalar,
/// Vector128 and Vector256 lanes. Coordinates arrive as raw Q16.16 int32; the integer part is the lattice cell,
/// the low 16 bits the position within it. Results are raw Q16.16 clamped to [-1, 1].
/// Integer products wrap and shifts truncate, so a coordinate outside the Fixed32 range after frequency scaling
/// gives a deterministic but discontinuous result; callers keep coordinates in range.
/// </summary>
internal static class NoiseKernels
{
    internal const int OneQ16 = 1 << 16;

    // Perlin gradient noise normalisation: 2D range is +/- sqrt(2)/2, 3D range is +/- sqrt(3)/2.
    private const int GradientScale2 = 92682; // sqrt(2) in Q16
    private const int GradientScale3 = 75674; // 2/sqrt(3) in Q16

    // Simplex skew constants in Q16: F2 = (sqrt(3)-1)/2, G2 = (3-sqrt(3))/6, F3 = 1/3, G3 = 1/6.
    private const int SkewF2 = 23988;
    private const int UnskewG2 = 13849;
    private const int SkewF3 = 21845;
    private const int UnskewG3 = 10923;
    private const int SimplexRadius2 = OneQ16 / 2; // 0.5
    private const int SimplexRadius3 = 39322; // 0.6
    private const int SimplexScale2 = 70;
    private const int SimplexScale3 = 32;

    /// <summary>The lattice hash of (x, y): bit-identical to <see cref="LatticeHash.Hash2"/>.</summary>
    internal static T Hash2<T>(T keyLow, T keyHigh, T x, T y)
        where T : struct, IIntLanes<T>
    {
        var h = T.Xor(T.Xor(T.Multiply(x, T.Broadcast(unchecked((int)LatticeHash.XMultiplier))), T.Multiply(y, T.Broadcast(unchecked((int)LatticeHash.YMultiplier)))), keyLow);
        h = Fmix(h);
        return Fmix(T.Xor(h, keyHigh));
    }

    /// <summary>The lattice hash of (x, y, z): bit-identical to <see cref="LatticeHash.Hash3"/>.</summary>
    internal static T Hash3<T>(T keyLow, T keyHigh, T x, T y, T z)
        where T : struct, IIntLanes<T>
    {
        var h = T.Xor(T.Multiply(x, T.Broadcast(unchecked((int)LatticeHash.XMultiplier))), T.Multiply(y, T.Broadcast(unchecked((int)LatticeHash.YMultiplier))));
        h = T.Xor(h, T.Multiply(z, T.Broadcast(unchecked((int)LatticeHash.ZMultiplier))));
        h = T.Xor(T.Xor(h, keyLow), T.Broadcast(unchecked((int)LatticeHash.Dimension3Salt)));
        h = Fmix(h);
        return Fmix(T.Xor(h, keyHigh));
    }

    internal static T Fmix<T>(T h)
        where T : struct, IIntLanes<T>
    {
        h = T.Xor(h, T.ShiftRightLogical(h, 16));
        h = T.Multiply(h, T.Broadcast(unchecked((int)LatticeHash.Fmix1)));
        h = T.Xor(h, T.ShiftRightLogical(h, 13));
        h = T.Multiply(h, T.Broadcast(unchecked((int)LatticeHash.Fmix2)));
        return T.Xor(h, T.ShiftRightLogical(h, 16));
    }

    /// <summary>Maps a hash onto a Q16 value in [-1, 1): the top 17 bits minus one.</summary>
    internal static T HashToUnit<T>(T hash)
        where T : struct, IIntLanes<T>
        => T.Subtract(T.ShiftRightLogical(hash, 15), T.Broadcast(OneQ16));

    /// <summary>Perlin's quintic fade 6t^5 - 15t^4 + 10t^3 for t in [0, 1) Q16.</summary>
    internal static T Fade<T>(T t)
        where T : struct, IIntLanes<T>
    {
        var inner = T.Add(T.MultiplyQ16(t, T.Subtract(T.Multiply(t, T.Broadcast(6)), T.Broadcast(15 * OneQ16))), T.Broadcast(10 * OneQ16));
        var t3 = T.MultiplyQ16(T.MultiplyQ16(t, t), t);
        return T.MultiplyQ16(t3, inner);
    }

    internal static T Lerp<T>(T a, T b, T t)
        where T : struct, IIntLanes<T>
        => T.Add(a, T.MultiplyQ16(T.Subtract(b, a), t));

    internal static T Floor<T>(T raw)
        where T : struct, IIntLanes<T>
        => T.ShiftRightArithmetic(raw, 16);

    internal static T Fraction<T>(T raw)
        where T : struct, IIntLanes<T>
        => T.And(raw, T.Broadcast(OneQ16 - 1));

    internal static T Negate<T>(T a)
        where T : struct, IIntLanes<T>
        => T.Subtract(T.Zero, a);

    /// <summary>All ones where bit <paramref name="bit"/> of <paramref name="value"/> is set.</summary>
    internal static T BitMask<T>(T value, int bit)
        where T : struct, IIntLanes<T>
        => T.ShiftRightArithmetic(T.ShiftLeft(value, 31 - bit), 31);

    internal static T ClampUnit<T>(T value)
        where T : struct, IIntLanes<T>
        => T.Min(T.Max(value, T.Broadcast(-OneQ16)), T.Broadcast(OneQ16));

    /// <summary>2D value noise: quintic-faded bilinear interpolation of hashed corner values.</summary>
    internal static T Value2<T>(T keyLow, T keyHigh, T x, T y)
        where T : struct, IIntLanes<T>
    {
        var one = T.Broadcast(1);
        var x0 = Floor(x);
        var y0 = Floor(y);
        var x1 = T.Add(x0, one);
        var y1 = T.Add(y0, one);
        var u = Fade(Fraction(x));
        var v = Fade(Fraction(y));

        var c00 = HashToUnit(Hash2(keyLow, keyHigh, x0, y0));
        var c10 = HashToUnit(Hash2(keyLow, keyHigh, x1, y0));
        var c01 = HashToUnit(Hash2(keyLow, keyHigh, x0, y1));
        var c11 = HashToUnit(Hash2(keyLow, keyHigh, x1, y1));

        return Lerp(Lerp(c00, c10, u), Lerp(c01, c11, u), v);
    }

    /// <summary>3D value noise: quintic-faded trilinear interpolation of hashed corner values.</summary>
    internal static T Value3<T>(T keyLow, T keyHigh, T x, T y, T z)
        where T : struct, IIntLanes<T>
    {
        var one = T.Broadcast(1);
        var x0 = Floor(x);
        var y0 = Floor(y);
        var z0 = Floor(z);
        var x1 = T.Add(x0, one);
        var y1 = T.Add(y0, one);
        var z1 = T.Add(z0, one);
        var u = Fade(Fraction(x));
        var v = Fade(Fraction(y));
        var w = Fade(Fraction(z));

        var c000 = HashToUnit(Hash3(keyLow, keyHigh, x0, y0, z0));
        var c100 = HashToUnit(Hash3(keyLow, keyHigh, x1, y0, z0));
        var c010 = HashToUnit(Hash3(keyLow, keyHigh, x0, y1, z0));
        var c110 = HashToUnit(Hash3(keyLow, keyHigh, x1, y1, z0));
        var c001 = HashToUnit(Hash3(keyLow, keyHigh, x0, y0, z1));
        var c101 = HashToUnit(Hash3(keyLow, keyHigh, x1, y0, z1));
        var c011 = HashToUnit(Hash3(keyLow, keyHigh, x0, y1, z1));
        var c111 = HashToUnit(Hash3(keyLow, keyHigh, x1, y1, z1));

        var front = Lerp(Lerp(c000, c100, u), Lerp(c010, c110, u), v);
        var back = Lerp(Lerp(c001, c101, u), Lerp(c011, c111, u), v);
        return Lerp(front, back, w);
    }

    /// <summary>
    /// Dot product of (dx, dy) with one of eight gradients chosen by the low three hash bits:
    /// bit 2 selects axial over diagonal, bit 1 the axis (or y sign), bit 0 the x sign.
    /// </summary>
    internal static T Gradient2Dot<T>(T hash, T dx, T dy)
        where T : struct, IIntLanes<T>
    {
        var signX = BitMask(hash, 0);
        var signY = BitMask(hash, 1);
        var axial = BitMask(hash, 2);
        var sx = T.Select(signX, Negate(dx), dx);
        var sy = T.Select(signY, Negate(dy), dy);
        var axialValue = T.Select(signY, T.Select(signX, Negate(dy), dy), sx);
        return T.Select(axial, axialValue, T.Add(sx, sy));
    }

    /// <summary>Improved Perlin gradient selection: the twelve cube-edge gradients from the low four hash bits.</summary>
    internal static T Gradient3Dot<T>(T hash, T dx, T dy, T dz)
        where T : struct, IIntLanes<T>
    {
        var low = T.And(hash, T.Broadcast(15));
        var u = T.Select(BitMask(hash, 3), dy, dx);
        var is12Or14 = T.Equal(T.And(low, T.Broadcast(13)), T.Broadcast(12));
        var v = T.Select(BitMask(hash, 2), T.Select(is12Or14, dx, dz), dy);
        return T.Add(T.Select(BitMask(hash, 0), Negate(u), u), T.Select(BitMask(hash, 1), Negate(v), v));
    }

    /// <summary>2D gradient (Perlin) noise, normalised to [-1, 1] and clamped.</summary>
    internal static T Gradient2<T>(T keyLow, T keyHigh, T x, T y)
        where T : struct, IIntLanes<T>
    {
        var one = T.Broadcast(1);
        var oneQ16 = T.Broadcast(OneQ16);
        var x0 = Floor(x);
        var y0 = Floor(y);
        var x1 = T.Add(x0, one);
        var y1 = T.Add(y0, one);
        var fx = Fraction(x);
        var fy = Fraction(y);
        var gx = T.Subtract(fx, oneQ16);
        var gy = T.Subtract(fy, oneQ16);
        var u = Fade(fx);
        var v = Fade(fy);

        var n00 = Gradient2Dot(Hash2(keyLow, keyHigh, x0, y0), fx, fy);
        var n10 = Gradient2Dot(Hash2(keyLow, keyHigh, x1, y0), gx, fy);
        var n01 = Gradient2Dot(Hash2(keyLow, keyHigh, x0, y1), fx, gy);
        var n11 = Gradient2Dot(Hash2(keyLow, keyHigh, x1, y1), gx, gy);

        var n = Lerp(Lerp(n00, n10, u), Lerp(n01, n11, u), v);
        return ClampUnit(T.MultiplyQ16(n, T.Broadcast(GradientScale2)));
    }

    /// <summary>3D gradient (Perlin) noise, normalised to [-1, 1] and clamped.</summary>
    internal static T Gradient3<T>(T keyLow, T keyHigh, T x, T y, T z)
        where T : struct, IIntLanes<T>
    {
        var one = T.Broadcast(1);
        var oneQ16 = T.Broadcast(OneQ16);
        var x0 = Floor(x);
        var y0 = Floor(y);
        var z0 = Floor(z);
        var x1 = T.Add(x0, one);
        var y1 = T.Add(y0, one);
        var z1 = T.Add(z0, one);
        var fx = Fraction(x);
        var fy = Fraction(y);
        var fz = Fraction(z);
        var gx = T.Subtract(fx, oneQ16);
        var gy = T.Subtract(fy, oneQ16);
        var gz = T.Subtract(fz, oneQ16);
        var u = Fade(fx);
        var v = Fade(fy);
        var w = Fade(fz);

        var n000 = Gradient3Dot(Hash3(keyLow, keyHigh, x0, y0, z0), fx, fy, fz);
        var n100 = Gradient3Dot(Hash3(keyLow, keyHigh, x1, y0, z0), gx, fy, fz);
        var n010 = Gradient3Dot(Hash3(keyLow, keyHigh, x0, y1, z0), fx, gy, fz);
        var n110 = Gradient3Dot(Hash3(keyLow, keyHigh, x1, y1, z0), gx, gy, fz);
        var n001 = Gradient3Dot(Hash3(keyLow, keyHigh, x0, y0, z1), fx, fy, gz);
        var n101 = Gradient3Dot(Hash3(keyLow, keyHigh, x1, y0, z1), gx, fy, gz);
        var n011 = Gradient3Dot(Hash3(keyLow, keyHigh, x0, y1, z1), fx, gy, gz);
        var n111 = Gradient3Dot(Hash3(keyLow, keyHigh, x1, y1, z1), gx, gy, gz);

        var front = Lerp(Lerp(n000, n100, u), Lerp(n010, n110, u), v);
        var back = Lerp(Lerp(n001, n101, u), Lerp(n011, n111, u), v);
        var n = Lerp(front, back, w);
        return ClampUnit(T.MultiplyQ16(n, T.Broadcast(GradientScale3)));
    }

    /// <summary>
    /// One simplex corner contribution in Q28: <c>(radius - |d|^2)^4 * scale * (gradient . d)</c>, or zero outside
    /// the kernel radius. The falloff is squared in Q22 and raised to the fourth power in Q28 so the fourth power
    /// keeps 12 more bits than a plain Q16 chain would.
    /// </summary>
    private static T SimplexContribution2<T>(T hash, T dx, T dy, T radius)
        where T : struct, IIntLanes<T>
    {
        var t = T.Subtract(radius, T.Add(T.MultiplyQ16(dx, dx), T.MultiplyQ16(dy, dy)));
        var inside = T.LessThan(T.Zero, t);
        var t19 = T.ShiftLeft(T.Max(t, T.Zero), 3);
        var t2 = T.MultiplyQ16(t19, t19); // Q22
        var t4 = T.MultiplyQ16(t2, t2); // Q28
        var dot = T.Multiply(Gradient2Dot(hash, dx, dy), T.Broadcast(SimplexScale2));
        return T.And(inside, T.MultiplyQ16(t4, dot));
    }

    private static T SimplexContribution3<T>(T hash, T dx, T dy, T dz, T radius)
        where T : struct, IIntLanes<T>
    {
        var t = T.Subtract(radius, T.Add(T.Add(T.MultiplyQ16(dx, dx), T.MultiplyQ16(dy, dy)), T.MultiplyQ16(dz, dz)));
        var inside = T.LessThan(T.Zero, t);
        var t19 = T.ShiftLeft(T.Max(t, T.Zero), 3);
        var t2 = T.MultiplyQ16(t19, t19); // Q22
        var t4 = T.MultiplyQ16(t2, t2); // Q28
        var dot = T.Multiply(Gradient3Dot(hash, dx, dy, dz), T.Broadcast(SimplexScale3));
        return T.And(inside, T.MultiplyQ16(t4, dot));
    }

    /// <summary>2D simplex noise (Gustavson's formulation), normalised to [-1, 1] and clamped.</summary>
    internal static T Simplex2<T>(T keyLow, T keyHigh, T x, T y)
        where T : struct, IIntLanes<T>
    {
        var one = T.Broadcast(1);
        var oneQ16 = T.Broadcast(OneQ16);
        var g2 = T.Broadcast(UnskewG2);

        var s = T.MultiplyQ16(T.Add(x, y), T.Broadcast(SkewF2));
        var i = Floor(T.Add(x, s));
        var j = Floor(T.Add(y, s));
        var t = T.Multiply(T.Add(i, j), g2);
        var x0 = T.Subtract(x, T.Subtract(T.ShiftLeft(i, 16), t));
        var y0 = T.Subtract(y, T.Subtract(T.ShiftLeft(j, 16), t));

        // The middle corner is (1, 0) when x0 > y0, else (0, 1).
        var xFirst = T.LessThan(y0, x0);
        var i1 = T.And(xFirst, one);
        var j1 = T.AndNot(one, xFirst);
        var x1 = T.Add(T.Subtract(x0, T.ShiftLeft(i1, 16)), g2);
        var y1 = T.Add(T.Subtract(y0, T.ShiftLeft(j1, 16)), g2);
        var x2 = T.Add(T.Subtract(x0, oneQ16), T.Broadcast(2 * UnskewG2));
        var y2 = T.Add(T.Subtract(y0, oneQ16), T.Broadcast(2 * UnskewG2));

        var radius = T.Broadcast(SimplexRadius2);
        var n0 = SimplexContribution2(Hash2(keyLow, keyHigh, i, j), x0, y0, radius);
        var n1 = SimplexContribution2(Hash2(keyLow, keyHigh, T.Add(i, i1), T.Add(j, j1)), x1, y1, radius);
        var n2 = SimplexContribution2(Hash2(keyLow, keyHigh, T.Add(i, one), T.Add(j, one)), x2, y2, radius);

        return ClampUnit(T.ShiftRightArithmetic(T.Add(T.Add(n0, n1), n2), 12));
    }

    /// <summary>3D simplex noise (Gustavson's formulation), normalised to [-1, 1] and clamped.</summary>
    internal static T Simplex3<T>(T keyLow, T keyHigh, T x, T y, T z)
        where T : struct, IIntLanes<T>
    {
        var one = T.Broadcast(1);
        var g3 = T.Broadcast(UnskewG3);

        var s = T.MultiplyQ16(T.Add(T.Add(x, y), z), T.Broadcast(SkewF3));
        var i = Floor(T.Add(x, s));
        var j = Floor(T.Add(y, s));
        var k = Floor(T.Add(z, s));
        var t = T.Multiply(T.Add(T.Add(i, j), k), g3);
        var x0 = T.Subtract(x, T.Subtract(T.ShiftLeft(i, 16), t));
        var y0 = T.Subtract(y, T.Subtract(T.ShiftLeft(j, 16), t));
        var z0 = T.Subtract(z, T.Subtract(T.ShiftLeft(k, 16), t));

        // Rank the three offsets (ties broken toward x, then y): the largest axis moves first, the smallest last.
        var xBeatsY = T.AndNot(T.Broadcast(-1), T.LessThan(x0, y0)); // x0 >= y0
        var xBeatsZ = T.AndNot(T.Broadcast(-1), T.LessThan(x0, z0));
        var yBeatsZ = T.AndNot(T.Broadcast(-1), T.LessThan(y0, z0));
        var yBeatsX = T.LessThan(x0, y0);
        var zBeatsX = T.LessThan(x0, z0);
        var zBeatsY = T.LessThan(y0, z0);
        var i1 = T.And(T.And(xBeatsY, xBeatsZ), one);
        var j1 = T.And(T.And(yBeatsX, yBeatsZ), one);
        var k1 = T.And(T.And(zBeatsX, zBeatsY), one);
        var i2 = T.And(T.Or(xBeatsY, xBeatsZ), one);
        var j2 = T.And(T.Or(yBeatsX, yBeatsZ), one);
        var k2 = T.And(T.Or(zBeatsX, zBeatsY), one);

        var x1 = T.Add(T.Subtract(x0, T.ShiftLeft(i1, 16)), g3);
        var y1 = T.Add(T.Subtract(y0, T.ShiftLeft(j1, 16)), g3);
        var z1 = T.Add(T.Subtract(z0, T.ShiftLeft(k1, 16)), g3);
        var twoG3 = T.Broadcast(2 * UnskewG3);
        var x2 = T.Add(T.Subtract(x0, T.ShiftLeft(i2, 16)), twoG3);
        var y2 = T.Add(T.Subtract(y0, T.ShiftLeft(j2, 16)), twoG3);
        var z2 = T.Add(T.Subtract(z0, T.ShiftLeft(k2, 16)), twoG3);
        var half = T.Broadcast(OneQ16 / 2); // 1 - 3 * G3
        var x3 = T.Subtract(x0, half);
        var y3 = T.Subtract(y0, half);
        var z3 = T.Subtract(z0, half);

        var radius = T.Broadcast(SimplexRadius3);
        var n0 = SimplexContribution3(Hash3(keyLow, keyHigh, i, j, k), x0, y0, z0, radius);
        var n1 = SimplexContribution3(Hash3(keyLow, keyHigh, T.Add(i, i1), T.Add(j, j1), T.Add(k, k1)), x1, y1, z1, radius);
        var n2 = SimplexContribution3(Hash3(keyLow, keyHigh, T.Add(i, i2), T.Add(j, j2), T.Add(k, k2)), x2, y2, z2, radius);
        var n3 = SimplexContribution3(Hash3(keyLow, keyHigh, T.Add(i, one), T.Add(j, one), T.Add(k, one)), x3, y3, z3, radius);

        return ClampUnit(T.ShiftRightArithmetic(T.Add(T.Add(n0, n1), T.Add(n2, n3)), 12));
    }
}
