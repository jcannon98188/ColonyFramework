namespace Colony.Numerics;

/// <summary>
/// Table-driven trigonometry and integer square roots shared by <see cref="Fixed32"/> and <see cref="Fixed64"/>.
/// Angles are radians. Internally an angle is converted to a binary fraction of a turn (2^64 = one turn),
/// so range reduction is a wrapping multiply with no division and no platform math. Sine comes from a
/// 1024-step quarter-wave table with linear interpolation; arctangent from a 1024-step table over [0, 1]
/// with octant reduction. All results are bit-identical on every platform (spec section 6).
/// </summary>
internal static class FixedTrig
{
    /// <summary>round(2^64 / (2 pi)): multiplies Q.32 radians into a Q.64 turn fraction after a 32-bit shift.</summary>
    private const ulong TurnsPerRadianQ64 = 2935890503282001226UL;

    internal const long PiQ32 = 13493037705L;
    internal const long HalfPiQ32 = 6746518852L;

    private const int StepBits = 10; // 1024 steps per quarter turn
    private const int QuarterBits = 62;
    private const ulong QuarterMask = (1UL << QuarterBits) - 1;
    private const int LerpBits = 20;

    /// <summary>Sine of a Q32.32 radian angle as a Q32 value in [-2^32, 2^32].</summary>
    internal static long SinQ32(long radiansQ32)
    {
        return SinOfTurn(RadiansToTurn(radiansQ32));
    }

    /// <summary>Cosine of a Q32.32 radian angle as a Q32 value in [-2^32, 2^32].</summary>
    internal static long CosQ32(long radiansQ32)
    {
        return SinOfTurn(RadiansToTurn(radiansQ32) + (1UL << QuarterBits));
    }

    /// <summary>Converts Q32.32 radians to a wrapping fraction of a turn (2^64 = one turn).</summary>
    private static ulong RadiansToTurn(long radiansQ32)
    {
        // Full-width product (64 x 64 -> 128) of the magnitude, then drop the 32 fractional bits of the
        // radian input. Whole turns fall off the top of the 64-bit result, which is exactly the range
        // reduction we want. Working on the magnitude keeps sin odd and cos even bit for bit.
        ulong magnitude = radiansQ32 < 0 ? (ulong)(-(Int128)radiansQ32) : (ulong)radiansQ32;
        ulong turn = (ulong)(((UInt128)magnitude * TurnsPerRadianQ64) >> 32);
        return radiansQ32 < 0 ? 0UL - turn : turn;
    }

    private static long SinOfTurn(ulong turn)
    {
        int quadrant = (int)(turn >> QuarterBits);
        ulong position = turn & QuarterMask;
        if ((quadrant & 1) != 0)
        {
            position = (1UL << QuarterBits) - position;
        }

        long value = LookupQuarter(FixedTables.SinQuarterQ32, position);
        return quadrant >= 2 ? -value : value;
    }

    /// <summary>Linear interpolation into a 1025-entry table indexed by a Q62 fraction of its span.</summary>
    private static long LookupQuarter(ReadOnlySpan<long> table, ulong positionQ62)
    {
        int index = (int)(positionQ62 >> (QuarterBits - StepBits));
        long fraction = (long)((positionQ62 >> (QuarterBits - StepBits - LerpBits)) & ((1UL << LerpBits) - 1));
        if (index >= FixedTables.Steps)
        {
            return table[FixedTables.Steps];
        }

        long a = table[index];
        long b = table[index + 1];
        return a + (((b - a) * fraction + (1L << (LerpBits - 1))) >> LerpBits);
    }

    /// <summary>atan2 for Q32.32 inputs, returning Q32.32 radians in (-pi, pi].</summary>
    internal static long Atan2Q32(long y, long x)
    {
        if (y == 0 && x == 0)
        {
            return 0;
        }

        Int128 ay = y < 0 ? -(Int128)y : y;
        Int128 ax = x < 0 ? -(Int128)x : x;
        bool swapped = ay > ax;
        Int128 numerator = swapped ? ax : ay;
        Int128 denominator = swapped ? ay : ax;

        // ratio in [0, 1] as a Q32 fraction, then a 1024-step table with 22-bit interpolation.
        long ratioQ32 = (long)((numerator << 32) / denominator);
        long angle = LookupUnit(FixedTables.AtanUnitQ32, ratioQ32);
        if (swapped)
        {
            angle = HalfPiQ32 - angle;
        }

        if (x < 0)
        {
            angle = PiQ32 - angle;
        }

        return y < 0 ? -angle : angle;
    }

    private static long LookupUnit(ReadOnlySpan<long> table, long positionQ32)
    {
        const int lerpBits = 32 - StepBits;
        int index = (int)(positionQ32 >> lerpBits);
        if (index >= FixedTables.Steps)
        {
            return table[FixedTables.Steps];
        }

        long fraction = positionQ32 & ((1L << lerpBits) - 1);
        long a = table[index];
        long b = table[index + 1];
        return a + (((b - a) * fraction + (1L << (lerpBits - 1))) >> lerpBits);
    }

    /// <summary>Floor of the square root of a 64-bit unsigned integer (digit-by-digit, deterministic).</summary>
    internal static ulong IntegerSqrt(ulong value)
    {
        ulong result = 0;
        ulong bit = 1UL << 62;
        while (bit > value)
        {
            bit >>= 2;
        }

        while (bit != 0)
        {
            if (value >= result + bit)
            {
                value -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }

            bit >>= 2;
        }

        return result;
    }

    /// <summary>Floor of the square root of a 128-bit unsigned integer.</summary>
    internal static UInt128 IntegerSqrt(UInt128 value)
    {
        UInt128 result = 0;
        UInt128 bit = (UInt128)1 << 126;
        while (bit > value)
        {
            bit >>= 2;
        }

        while (bit != 0)
        {
            if (value >= result + bit)
            {
                value -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }

            bit >>= 2;
        }

        return result;
    }
}

public readonly partial struct Fixed32
{
    public static Fixed32 Sin(Fixed32 radians) => FromQ32(FixedTrig.SinQ32((long)radians.Raw << 16));

    public static Fixed32 Cos(Fixed32 radians) => FromQ32(FixedTrig.CosQ32((long)radians.Raw << 16));

    public static (Fixed32 Sin, Fixed32 Cos) SinCos(Fixed32 radians) => (Sin(radians), Cos(radians));

    /// <summary>Angle of the vector (x, y) in radians, in (-pi, pi]. <c>Atan2(0, 0)</c> is zero.</summary>
    public static Fixed32 Atan2(Fixed32 y, Fixed32 x) => FromQ32(FixedTrig.Atan2Q32((long)y.Raw << 16, (long)x.Raw << 16));

    /// <summary>Correctly rounded square root. Negative inputs are defined as zero.</summary>
    public static Fixed32 Sqrt(Fixed32 value)
    {
        if (value.Raw <= 0)
        {
            return Zero;
        }

        // sqrt(raw * 2^18) = 2 * sqrt(raw) * 2^8 = 2 * result; the extra bit rounds to nearest.
        ulong doubled = FixedTrig.IntegerSqrt((ulong)value.Raw << (FractionalBits + 2));
        return new(Saturate((long)((doubled + 1) >> 1)));
    }

    /// <summary>Narrows a Q32 value to Q16.16, rounding to nearest with ties away from zero.</summary>
    private static Fixed32 FromQ32(long valueQ32)
    {
        return new(Saturate(RoundShift(valueQ32)));
    }
}

public readonly partial struct Fixed64
{
    public static Fixed64 Sin(Fixed64 radians) => new(FixedTrig.SinQ32(radians.Raw));

    public static Fixed64 Cos(Fixed64 radians) => new(FixedTrig.CosQ32(radians.Raw));

    public static (Fixed64 Sin, Fixed64 Cos) SinCos(Fixed64 radians) => (Sin(radians), Cos(radians));

    /// <summary>Angle of the vector (x, y) in radians, in (-pi, pi]. <c>Atan2(0, 0)</c> is zero.</summary>
    public static Fixed64 Atan2(Fixed64 y, Fixed64 x) => new(FixedTrig.Atan2Q32(y.Raw, x.Raw));

    /// <summary>Correctly rounded square root. Negative inputs are defined as zero.</summary>
    public static Fixed64 Sqrt(Fixed64 value)
    {
        if (value.Raw <= 0)
        {
            return Zero;
        }

        UInt128 doubled = FixedTrig.IntegerSqrt((UInt128)(ulong)value.Raw << (FractionalBits + 2));
        return new((long)((doubled + 1) >> 1));
    }
}
