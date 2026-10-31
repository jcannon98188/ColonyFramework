namespace Colony.ProcGen;

/// <summary>
/// The noise kernels, written once against <see cref="IIntLanes{TSelf}"/> and instantiated for scalar,
/// Vector128 and Vector256 lanes. Coordinates arrive as raw Q16.16 int32; the integer part is the lattice cell,
/// the low 16 bits the position within it. Results are raw Q16.16 in roughly [-1, 1].
/// </summary>
internal static class NoiseKernels
{
    internal const int OneQ16 = 1 << 16;

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
}
