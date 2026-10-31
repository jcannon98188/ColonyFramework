using System.Globalization;

namespace Colony.Numerics;

/// <summary>
/// Signed Q32.32 fixed-point number: 32 integer bits and 32 fractional bits.
/// Same contract as <see cref="Fixed32"/>: integer-only, saturating, round-to-nearest ties away from zero,
/// division by zero saturates by the sign of the dividend.
/// </summary>
public readonly partial struct Fixed64 : IEquatable<Fixed64>, IComparable<Fixed64>
{
    public const int FractionalBits = 32;
    private const long OneRaw = 1L << FractionalBits;
    private const long FractionMask = OneRaw - 1;
    private const long HalfRaw = OneRaw >> 1;

    public static Fixed64 Zero => default;
    public static Fixed64 One => new(OneRaw);
    public static Fixed64 Half => new(OneRaw >> 1);
    public static Fixed64 MaxValue => new(long.MaxValue);
    public static Fixed64 MinValue => new(long.MinValue);
    /// <summary>The smallest positive value, 2^-32.</summary>
    public static Fixed64 Epsilon => new(1);

    private Fixed64(long raw)
    {
        Raw = raw;
    }

    /// <summary>The underlying scaled integer (value * 2^32).</summary>
    public long Raw { get; }

    public static Fixed64 FromRaw(long raw) => new(raw);

    public static Fixed64 FromInt32(int value) => new((long)value << FractionalBits);

    /// <summary>Converts an integer, saturating outside the 32-bit integer range.</summary>
    public static Fixed64 FromInt64(long value)
    {
        if (value > int.MaxValue)
        {
            return MaxValue;
        }

        if (value < int.MinValue)
        {
            return MinValue;
        }

        return new(value << FractionalBits);
    }

    /// <summary>Exact widening conversion from Q16.16.</summary>
    public static Fixed64 FromFixed32(Fixed32 value) => new((long)value.Raw << (FractionalBits - Fixed32.FractionalBits));

    /// <summary>
    /// Converts a double, rounding to nearest (ties away from zero) and saturating. NaN becomes zero.
    /// </summary>
    public static Fixed64 FromDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return Zero;
        }

        double scaled = value * OneRaw;
        if (scaled >= 9223372036854775807.0)
        {
            return MaxValue;
        }

        if (scaled <= -9223372036854775808.0)
        {
            return MinValue;
        }

        double rounded = scaled >= 0 ? scaled + 0.5 : scaled - 0.5;
        return new((long)rounded);
    }

    public static Fixed64 FromSingle(float value) => FromDouble(value);

    /// <summary>Narrows to Q16.16, rounding to nearest (ties away from zero) and saturating.</summary>
    public Fixed32 ToFixed32()
    {
        const int shift = FractionalBits - Fixed32.FractionalBits;
        const long half = 1L << (shift - 1);
        Int128 raw = Raw;
        Int128 rounded = raw >= 0 ? (raw + half) >> shift : -((-raw + half) >> shift);
        return Fixed32.FromRaw(Fixed32.Saturate((long)Saturate(rounded)));
    }

    /// <summary>Truncates toward zero.</summary>
    public long ToInt64() => Raw < 0 ? -(long)((-(Int128)Raw) >> FractionalBits) : Raw >> FractionalBits;

    /// <summary>Rounds toward negative infinity.</summary>
    public long FloorToInt64() => Raw >> FractionalBits;

    /// <summary>Rounds to nearest, ties away from zero.</summary>
    public long RoundToInt64() => (long)(RoundRaw(Raw) >> FractionalBits);

    public double ToDouble() => Raw / (double)OneRaw;

    public float ToSingle() => (float)ToDouble();

    public static Fixed64 operator +(Fixed64 a, Fixed64 b) => new(Saturate((Int128)a.Raw + b.Raw));

    public static Fixed64 operator -(Fixed64 a, Fixed64 b) => new(Saturate((Int128)a.Raw - b.Raw));

    public static Fixed64 operator -(Fixed64 a) => new(Saturate(-(Int128)a.Raw));

    public static Fixed64 operator *(Fixed64 a, Fixed64 b)
    {
        Int128 product = (Int128)a.Raw * b.Raw;
        return new(Saturate(RoundShift(product)));
    }

    public static Fixed64 operator /(Fixed64 a, Fixed64 b)
    {
        if (b.Raw == 0)
        {
            return a.Raw == 0 ? Zero : a.Raw > 0 ? MaxValue : MinValue;
        }

        Int128 dividend = (Int128)a.Raw << (FractionalBits + 1);
        Int128 quotient = dividend / b.Raw;
        return new(Saturate(RoundHalf(quotient)));
    }

    public static Fixed64 Add(Fixed64 a, Fixed64 b) => a + b;

    public static Fixed64 Subtract(Fixed64 a, Fixed64 b) => a - b;

    public static Fixed64 Multiply(Fixed64 a, Fixed64 b) => a * b;

    public static Fixed64 Divide(Fixed64 a, Fixed64 b) => a / b;

    public static Fixed64 Negate(Fixed64 a) => -a;

    public static Fixed64 Min(Fixed64 a, Fixed64 b) => a.Raw <= b.Raw ? a : b;

    public static Fixed64 Max(Fixed64 a, Fixed64 b) => a.Raw >= b.Raw ? a : b;

    public static Fixed64 Abs(Fixed64 a) => a.Raw < 0 ? -a : a;

    public static int Sign(Fixed64 a) => a.Raw > 0 ? 1 : a.Raw < 0 ? -1 : 0;

    public static Fixed64 Clamp(Fixed64 value, Fixed64 min, Fixed64 max) => Min(Max(value, min), max);

    public static Fixed64 Floor(Fixed64 a) => new(a.Raw & ~FractionMask);

    public static Fixed64 Ceiling(Fixed64 a)
    {
        Int128 raw = a.Raw;
        long fraction = a.Raw & FractionMask;
        return new(Saturate(fraction == 0 ? raw : (raw | FractionMask) + 1));
    }

    public static Fixed64 Round(Fixed64 a) => new(Saturate(RoundRaw(a.Raw)));

    /// <summary>The fractional part in [0, 1), so that <c>Floor(a) + Fraction(a) == a</c>.</summary>
    public static Fixed64 Fraction(Fixed64 a) => new(a.Raw & FractionMask);

    public int CompareTo(Fixed64 other) => Raw.CompareTo(other.Raw);

    public bool Equals(Fixed64 other) => Raw == other.Raw;

    public override bool Equals(object? obj) => obj is Fixed64 other && Equals(other);

    public override int GetHashCode() => Raw.GetHashCode();

    public override string ToString() => ToDouble().ToString("0.##########", CultureInfo.InvariantCulture);

    public static bool operator ==(Fixed64 a, Fixed64 b) => a.Raw == b.Raw;

    public static bool operator !=(Fixed64 a, Fixed64 b) => a.Raw != b.Raw;

    public static bool operator <(Fixed64 a, Fixed64 b) => a.Raw < b.Raw;

    public static bool operator >(Fixed64 a, Fixed64 b) => a.Raw > b.Raw;

    public static bool operator <=(Fixed64 a, Fixed64 b) => a.Raw <= b.Raw;

    public static bool operator >=(Fixed64 a, Fixed64 b) => a.Raw >= b.Raw;

    private static Int128 RoundRaw(long raw)
    {
        return raw >= 0
            ? ((Int128)raw + HalfRaw) & ~(Int128)FractionMask
            : -((-(Int128)raw + HalfRaw) & ~(Int128)FractionMask);
    }

    internal static long Saturate(Int128 raw)
    {
        if (raw > long.MaxValue)
        {
            return long.MaxValue;
        }

        if (raw < long.MinValue)
        {
            return long.MinValue;
        }

        return (long)raw;
    }

    /// <summary>Shifts a Q64.64 product down to Q.32, rounding to nearest with ties away from zero.</summary>
    internal static Int128 RoundShift(Int128 product)
    {
        return product >= 0
            ? (product + HalfRaw) >> FractionalBits
            : -((-product + HalfRaw) >> FractionalBits);
    }

    internal static Int128 RoundHalf(Int128 value)
    {
        return value >= 0 ? (value + 1) >> 1 : -((-value + 1) >> 1);
    }
}
