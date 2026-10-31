using System.Globalization;

namespace Colony.Numerics;

/// <summary>
/// Signed Q16.16 fixed-point number: 16 integer bits (range [-32768, 32768)) and 16 fractional bits.
/// Every operation is integer arithmetic, so results are bit-identical on every CPU and OS (spec section 6).
/// Arithmetic saturates instead of wrapping; division by zero saturates by the sign of the dividend.
/// Multiplication and division round to nearest, ties away from zero.
/// </summary>
public readonly partial struct Fixed32 : IEquatable<Fixed32>, IComparable<Fixed32>
{
    public const int FractionalBits = 16;
    private const int OneRaw = 1 << FractionalBits;
    private const int FractionMask = OneRaw - 1;
    private const long HalfRaw = OneRaw >> 1;

    public static Fixed32 Zero => default;
    public static Fixed32 One => new(OneRaw);
    public static Fixed32 Half => new(OneRaw >> 1);
    public static Fixed32 MaxValue => new(int.MaxValue);
    public static Fixed32 MinValue => new(int.MinValue);
    /// <summary>The smallest positive value, 2^-16.</summary>
    public static Fixed32 Epsilon => new(1);

    private Fixed32(int raw)
    {
        Raw = raw;
    }

    /// <summary>The underlying scaled integer (value * 65536).</summary>
    public int Raw { get; }

    public static Fixed32 FromRaw(int raw) => new(raw);

    /// <summary>Converts an integer, saturating outside [-32768, 32767].</summary>
    public static Fixed32 FromInt32(int value)
    {
        if (value > short.MaxValue)
        {
            return MaxValue;
        }

        if (value < short.MinValue)
        {
            return MinValue;
        }

        return new(value << FractionalBits);
    }

    /// <summary>
    /// Converts a double, rounding to nearest (ties away from zero) and saturating. NaN becomes zero.
    /// Intended for constants and setup code; simulation code should stay in fixed-point.
    /// </summary>
    public static Fixed32 FromDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return Zero;
        }

        double scaled = value * OneRaw;
        if (scaled >= int.MaxValue)
        {
            return MaxValue;
        }

        if (scaled <= int.MinValue)
        {
            return MinValue;
        }

        // Round half away from zero using only basic IEEE operations (no Math.Round).
        double rounded = scaled >= 0 ? scaled + 0.5 : scaled - 0.5;
        return new((int)rounded);
    }

    public static Fixed32 FromSingle(float value) => FromDouble(value);

    /// <summary>
    /// The ratio <paramref name="numerator"/>/<paramref name="denominator"/> rounded to nearest (ties away
    /// from zero) and saturated. A zero denominator saturates by the sign of the numerator, like division.
    /// </summary>
    public static Fixed32 FromRatio(int numerator, int denominator) => FromInt32(numerator) / FromInt32(denominator);

    /// <summary>Truncates toward zero.</summary>
    public int ToInt32() => Raw < 0 ? -(int)((-(long)Raw) >> FractionalBits) : Raw >> FractionalBits;

    /// <summary>Rounds toward negative infinity.</summary>
    public int FloorToInt32() => Raw >> FractionalBits;

    /// <summary>Rounds to nearest, ties away from zero.</summary>
    public int RoundToInt32() => (int)(RoundRaw(Raw) >> FractionalBits);

    public double ToDouble() => Raw / (double)OneRaw;

    public float ToSingle() => (float)ToDouble();

    public static Fixed32 operator +(Fixed32 a, Fixed32 b) => new(Saturate((long)a.Raw + b.Raw));

    public static Fixed32 operator -(Fixed32 a, Fixed32 b) => new(Saturate((long)a.Raw - b.Raw));

    public static Fixed32 operator -(Fixed32 a) => new(Saturate(-(long)a.Raw));

    public static Fixed32 operator *(Fixed32 a, Fixed32 b)
    {
        long product = (long)a.Raw * b.Raw;
        return new(Saturate(RoundShift(product)));
    }

    public static Fixed32 operator /(Fixed32 a, Fixed32 b)
    {
        if (b.Raw == 0)
        {
            return a.Raw == 0 ? Zero : a.Raw > 0 ? MaxValue : MinValue;
        }

        // Scale the dividend so the quotient keeps 16 fractional bits plus one guard bit for rounding.
        long dividend = (long)a.Raw << (FractionalBits + 1);
        long quotient = dividend / b.Raw;
        return new(Saturate(RoundHalf(quotient)));
    }

    public static Fixed32 Add(Fixed32 a, Fixed32 b) => a + b;

    public static Fixed32 Subtract(Fixed32 a, Fixed32 b) => a - b;

    public static Fixed32 Multiply(Fixed32 a, Fixed32 b) => a * b;

    public static Fixed32 Divide(Fixed32 a, Fixed32 b) => a / b;

    public static Fixed32 Negate(Fixed32 a) => -a;

    public static Fixed32 Min(Fixed32 a, Fixed32 b) => a.Raw <= b.Raw ? a : b;

    public static Fixed32 Max(Fixed32 a, Fixed32 b) => a.Raw >= b.Raw ? a : b;

    public static Fixed32 Abs(Fixed32 a) => a.Raw < 0 ? -a : a;

    public static int Sign(Fixed32 a) => a.Raw > 0 ? 1 : a.Raw < 0 ? -1 : 0;

    public static Fixed32 Clamp(Fixed32 value, Fixed32 min, Fixed32 max) => Min(Max(value, min), max);

    public static Fixed32 Floor(Fixed32 a) => new(a.Raw & ~FractionMask);

    public static Fixed32 Ceiling(Fixed32 a)
    {
        long raw = a.Raw;
        long fraction = raw & FractionMask;
        return new(Saturate(fraction == 0 ? raw : (raw | FractionMask) + 1));
    }

    public static Fixed32 Round(Fixed32 a) => new(Saturate(RoundRaw(a.Raw)));

    /// <summary>Rounds a raw value to the nearest whole multiple of One, ties away from zero.</summary>
    private static long RoundRaw(long raw)
    {
        return raw >= 0
            ? (raw + HalfRaw) & ~(long)FractionMask
            : -((-raw + HalfRaw) & ~(long)FractionMask);
    }

    /// <summary>The fractional part in [0, 1), so that <c>Floor(a) + Fraction(a) == a</c>.</summary>
    public static Fixed32 Fraction(Fixed32 a) => new(a.Raw & FractionMask);

    public int CompareTo(Fixed32 other) => Raw.CompareTo(other.Raw);

    public bool Equals(Fixed32 other) => Raw == other.Raw;

    public override bool Equals(object? obj) => obj is Fixed32 other && Equals(other);

    public override int GetHashCode() => Raw;

    public override string ToString() => ToDouble().ToString("0.#####", CultureInfo.InvariantCulture);

    public static bool operator ==(Fixed32 a, Fixed32 b) => a.Raw == b.Raw;

    public static bool operator !=(Fixed32 a, Fixed32 b) => a.Raw != b.Raw;

    public static bool operator <(Fixed32 a, Fixed32 b) => a.Raw < b.Raw;

    public static bool operator >(Fixed32 a, Fixed32 b) => a.Raw > b.Raw;

    public static bool operator <=(Fixed32 a, Fixed32 b) => a.Raw <= b.Raw;

    public static bool operator >=(Fixed32 a, Fixed32 b) => a.Raw >= b.Raw;

    internal static int Saturate(long raw)
    {
        if (raw > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (raw < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)raw;
    }

    /// <summary>Shifts a Q32.32 product down to Q.16, rounding to nearest with ties away from zero.</summary>
    internal static long RoundShift(long product)
    {
        return product >= 0
            ? (product + HalfRaw) >> FractionalBits
            : -((-product + HalfRaw) >> FractionalBits);
    }

    /// <summary>Drops one guard bit, rounding to nearest with ties away from zero.</summary>
    internal static long RoundHalf(long value)
    {
        return value >= 0 ? (value + 1) >> 1 : -((-value + 1) >> 1);
    }
}
