using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Colony.Numerics;

/// <summary>
/// A fixed group of float lanes with the primitive operations the float kernels are written against.
/// Every operation is an IEEE 754 basic operation (correctly rounded) or a bit manipulation, so any two
/// implementations produce identical bits for identical inputs. Integer helpers operate on the lane bits
/// reinterpreted as Int32 and return the result as lane bits again.
/// </summary>
internal interface IFloatLanes<TSelf>
    where TSelf : struct, IFloatLanes<TSelf>
{
    public static abstract int Count { get; }

    public static abstract TSelf Zero { get; }

    public static abstract TSelf Broadcast(float value);

    /// <summary>Loads the first <see cref="Count"/> elements.</summary>
    public static abstract TSelf Load(ReadOnlySpan<float> source);

    public static abstract void Store(TSelf value, Span<float> destination);

    /// <summary>Stores the lane bits as integers.</summary>
    public static abstract void StoreBits(TSelf value, Span<int> destination);

    public static abstract float Lane(TSelf value, int index);

    public static abstract TSelf Add(TSelf a, TSelf b);

    public static abstract TSelf Subtract(TSelf a, TSelf b);

    public static abstract TSelf Multiply(TSelf a, TSelf b);

    public static abstract TSelf Divide(TSelf a, TSelf b);

    public static abstract TSelf Sqrt(TSelf a);

    public static abstract TSelf And(TSelf a, TSelf b);

    public static abstract TSelf AndNot(TSelf a, TSelf mask);

    public static abstract TSelf Or(TSelf a, TSelf b);

    public static abstract TSelf Xor(TSelf a, TSelf b);

    public static abstract TSelf LessThan(TSelf a, TSelf b);

    public static abstract TSelf GreaterThan(TSelf a, TSelf b);

    public static abstract TSelf Equal(TSelf a, TSelf b);

    public static abstract TSelf Select(TSelf mask, TSelf whenTrue, TSelf whenFalse);

    /// <summary>Truncates toward zero; inputs must already be within the Int32 range.</summary>
    public static abstract TSelf TruncateToInt32(TSelf a);

    public static abstract TSelf Int32ToSingle(TSelf bits);

    public static abstract TSelf ShiftLeftInt32(TSelf bits, int shift);

    public static abstract TSelf ShiftRightArithmeticInt32(TSelf bits, int shift);

    public static abstract TSelf AddInt32(TSelf a, TSelf b);

    public static abstract TSelf EqualInt32(TSelf a, TSelf b);
}

/// <summary>One lane: the scalar instantiation used for tails and the scalar public API.</summary>
internal readonly struct Lanes1 : IFloatLanes<Lanes1>
{
    private readonly float _value;

    private Lanes1(float value)
    {
        _value = value;
    }

    public static int Count => 1;

    public static Lanes1 Zero => default;

    public static Lanes1 Broadcast(float value) => new(value);

    public static Lanes1 Load(ReadOnlySpan<float> source) => new(source[0]);

    public static void Store(Lanes1 value, Span<float> destination) => destination[0] = value._value;

    public static void StoreBits(Lanes1 value, Span<int> destination) => destination[0] = Bits(value);

    public static float Lane(Lanes1 value, int index) => index == 0 ? value._value : throw new ArgumentOutOfRangeException(nameof(index));

    public static Lanes1 Add(Lanes1 a, Lanes1 b) => new(a._value + b._value);

    public static Lanes1 Subtract(Lanes1 a, Lanes1 b) => new(a._value - b._value);

    public static Lanes1 Multiply(Lanes1 a, Lanes1 b) => new(a._value * b._value);

    public static Lanes1 Divide(Lanes1 a, Lanes1 b) => new(a._value / b._value);

    // Hardware square root is correctly rounded on every supported target; route through the intrinsic
    // rather than MathF so the analyzer ban on System.MathF holds everywhere in the Framework.
    public static Lanes1 Sqrt(Lanes1 a) => new(Vector128.Sqrt(Vector128.CreateScalar(a._value)).ToScalar());

    public static Lanes1 And(Lanes1 a, Lanes1 b) => FromBits(Bits(a) & Bits(b));

    public static Lanes1 AndNot(Lanes1 a, Lanes1 mask) => FromBits(Bits(a) & ~Bits(mask));

    public static Lanes1 Or(Lanes1 a, Lanes1 b) => FromBits(Bits(a) | Bits(b));

    public static Lanes1 Xor(Lanes1 a, Lanes1 b) => FromBits(Bits(a) ^ Bits(b));

    public static Lanes1 LessThan(Lanes1 a, Lanes1 b) => FromBits(a._value < b._value ? -1 : 0);

    public static Lanes1 GreaterThan(Lanes1 a, Lanes1 b) => FromBits(a._value > b._value ? -1 : 0);

    public static Lanes1 Equal(Lanes1 a, Lanes1 b) => FromBits(a._value == b._value ? -1 : 0);

    public static Lanes1 Select(Lanes1 mask, Lanes1 whenTrue, Lanes1 whenFalse)
        => FromBits((Bits(whenTrue) & Bits(mask)) | (Bits(whenFalse) & ~Bits(mask)));

    public static Lanes1 TruncateToInt32(Lanes1 a) => FromBits((int)a._value);

    public static Lanes1 Int32ToSingle(Lanes1 bits) => new(Bits(bits));

    public static Lanes1 ShiftLeftInt32(Lanes1 bits, int shift) => FromBits(Bits(bits) << shift);

    public static Lanes1 ShiftRightArithmeticInt32(Lanes1 bits, int shift) => FromBits(Bits(bits) >> shift);

    public static Lanes1 AddInt32(Lanes1 a, Lanes1 b) => FromBits(unchecked(Bits(a) + Bits(b)));

    public static Lanes1 EqualInt32(Lanes1 a, Lanes1 b) => FromBits(Bits(a) == Bits(b) ? -1 : 0);

    private static int Bits(Lanes1 value) => BitConverter.SingleToInt32Bits(value._value);

    private static Lanes1 FromBits(int bits) => new(BitConverter.Int32BitsToSingle(bits));
}

/// <summary>
/// Eight lanes as eight <see cref="Lanes1"/> scalars: the reference instantiation of the logical width.
/// Every operation is a plain loop over the scalar lane, so there is nothing here that could diverge from it.
/// </summary>
internal struct Lanes8Scalar : IFloatLanes<Lanes8Scalar>
{
    private Buffer _lanes;

    [InlineArray(8)]
    private struct Buffer
    {
        private Lanes1 _element0;
    }

    public static int Count => 8;

    public static Lanes8Scalar Zero => default;

    public static Lanes8Scalar Broadcast(float value)
    {
        Lanes8Scalar result = default;
        for (int i = 0; i < 8; i++)
        {
            result._lanes[i] = Lanes1.Broadcast(value);
        }

        return result;
    }

    public static Lanes8Scalar Load(ReadOnlySpan<float> source)
    {
        Lanes8Scalar result = default;
        for (int i = 0; i < 8; i++)
        {
            result._lanes[i] = Lanes1.Load(source[i..]);
        }

        return result;
    }

    public static void Store(Lanes8Scalar value, Span<float> destination)
    {
        for (int i = 0; i < 8; i++)
        {
            Lanes1.Store(value._lanes[i], destination[i..]);
        }
    }

    public static void StoreBits(Lanes8Scalar value, Span<int> destination)
    {
        for (int i = 0; i < 8; i++)
        {
            Lanes1.StoreBits(value._lanes[i], destination[i..]);
        }
    }

    public static float Lane(Lanes8Scalar value, int index) => Lanes1.Lane(value._lanes[index], 0);

    public static Lanes8Scalar Add(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Add(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Subtract(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Subtract(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Multiply(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Multiply(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Divide(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Divide(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Sqrt(Lanes8Scalar a)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Sqrt(a._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar And(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.And(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar AndNot(Lanes8Scalar a, Lanes8Scalar mask)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.AndNot(a._lanes[i], mask._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Or(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Or(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Xor(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Xor(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar LessThan(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.LessThan(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar GreaterThan(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.GreaterThan(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Equal(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Equal(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Select(Lanes8Scalar mask, Lanes8Scalar whenTrue, Lanes8Scalar whenFalse)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Select(mask._lanes[i], whenTrue._lanes[i], whenFalse._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar TruncateToInt32(Lanes8Scalar a)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.TruncateToInt32(a._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar Int32ToSingle(Lanes8Scalar bits)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.Int32ToSingle(bits._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar ShiftLeftInt32(Lanes8Scalar bits, int shift)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.ShiftLeftInt32(bits._lanes[i], shift);
        }

        return r;
    }

    public static Lanes8Scalar ShiftRightArithmeticInt32(Lanes8Scalar bits, int shift)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.ShiftRightArithmeticInt32(bits._lanes[i], shift);
        }

        return r;
    }

    public static Lanes8Scalar AddInt32(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.AddInt32(a._lanes[i], b._lanes[i]);
        }

        return r;
    }

    public static Lanes8Scalar EqualInt32(Lanes8Scalar a, Lanes8Scalar b)
    {
        Lanes8Scalar r = default;
        for (int i = 0; i < 8; i++)
        {
            r._lanes[i] = Lanes1.EqualInt32(a._lanes[i], b._lanes[i]);
        }

        return r;
    }
}

/// <summary>Eight lanes as two <see cref="Vector128{T}"/> halves: the fallback for hardware without 256-bit vectors.</summary>
internal readonly struct Lanes128x2 : IFloatLanes<Lanes128x2>
{
    private readonly Vector128<float> _lower;
    private readonly Vector128<float> _upper;

    private Lanes128x2(Vector128<float> lower, Vector128<float> upper)
    {
        _lower = lower;
        _upper = upper;
    }

    public static int Count => 8;

    public static Lanes128x2 Zero => default;

    public static Lanes128x2 Broadcast(float value) => new(Vector128.Create(value), Vector128.Create(value));

    public static Lanes128x2 Load(ReadOnlySpan<float> source) => new(Vector128.Create(source), Vector128.Create(source[4..]));

    public static void Store(Lanes128x2 value, Span<float> destination)
    {
        value._lower.CopyTo(destination);
        value._upper.CopyTo(destination[4..]);
    }

    public static void StoreBits(Lanes128x2 value, Span<int> destination)
    {
        value._lower.AsInt32().CopyTo(destination);
        value._upper.AsInt32().CopyTo(destination[4..]);
    }

    public static float Lane(Lanes128x2 value, int index) => index < 4 ? value._lower.GetElement(index) : value._upper.GetElement(index - 4);

    public static Lanes128x2 Add(Lanes128x2 a, Lanes128x2 b) => new(a._lower + b._lower, a._upper + b._upper);

    public static Lanes128x2 Subtract(Lanes128x2 a, Lanes128x2 b) => new(a._lower - b._lower, a._upper - b._upper);

    public static Lanes128x2 Multiply(Lanes128x2 a, Lanes128x2 b) => new(a._lower * b._lower, a._upper * b._upper);

    public static Lanes128x2 Divide(Lanes128x2 a, Lanes128x2 b) => new(a._lower / b._lower, a._upper / b._upper);

    public static Lanes128x2 Sqrt(Lanes128x2 a) => new(Vector128.Sqrt(a._lower), Vector128.Sqrt(a._upper));

    public static Lanes128x2 And(Lanes128x2 a, Lanes128x2 b) => new(a._lower & b._lower, a._upper & b._upper);

    public static Lanes128x2 AndNot(Lanes128x2 a, Lanes128x2 mask) => new(Vector128.AndNot(a._lower, mask._lower), Vector128.AndNot(a._upper, mask._upper));

    public static Lanes128x2 Or(Lanes128x2 a, Lanes128x2 b) => new(a._lower | b._lower, a._upper | b._upper);

    public static Lanes128x2 Xor(Lanes128x2 a, Lanes128x2 b) => new(a._lower ^ b._lower, a._upper ^ b._upper);

    public static Lanes128x2 LessThan(Lanes128x2 a, Lanes128x2 b) => new(Vector128.LessThan(a._lower, b._lower), Vector128.LessThan(a._upper, b._upper));

    public static Lanes128x2 GreaterThan(Lanes128x2 a, Lanes128x2 b) => new(Vector128.GreaterThan(a._lower, b._lower), Vector128.GreaterThan(a._upper, b._upper));

    public static Lanes128x2 Equal(Lanes128x2 a, Lanes128x2 b) => new(Vector128.Equals(a._lower, b._lower), Vector128.Equals(a._upper, b._upper));

    public static Lanes128x2 Select(Lanes128x2 mask, Lanes128x2 whenTrue, Lanes128x2 whenFalse)
        => new(Vector128.ConditionalSelect(mask._lower, whenTrue._lower, whenFalse._lower), Vector128.ConditionalSelect(mask._upper, whenTrue._upper, whenFalse._upper));

    public static Lanes128x2 TruncateToInt32(Lanes128x2 a) => new(Vector128.ConvertToInt32(a._lower).AsSingle(), Vector128.ConvertToInt32(a._upper).AsSingle());

    public static Lanes128x2 Int32ToSingle(Lanes128x2 bits) => new(Vector128.ConvertToSingle(bits._lower.AsInt32()), Vector128.ConvertToSingle(bits._upper.AsInt32()));

    public static Lanes128x2 ShiftLeftInt32(Lanes128x2 bits, int shift) => new(Vector128.ShiftLeft(bits._lower.AsInt32(), shift).AsSingle(), Vector128.ShiftLeft(bits._upper.AsInt32(), shift).AsSingle());

    public static Lanes128x2 ShiftRightArithmeticInt32(Lanes128x2 bits, int shift) => new(Vector128.ShiftRightArithmetic(bits._lower.AsInt32(), shift).AsSingle(), Vector128.ShiftRightArithmetic(bits._upper.AsInt32(), shift).AsSingle());

    public static Lanes128x2 AddInt32(Lanes128x2 a, Lanes128x2 b) => new((a._lower.AsInt32() + b._lower.AsInt32()).AsSingle(), (a._upper.AsInt32() + b._upper.AsInt32()).AsSingle());

    public static Lanes128x2 EqualInt32(Lanes128x2 a, Lanes128x2 b) => new(Vector128.Equals(a._lower.AsInt32(), b._lower.AsInt32()).AsSingle(), Vector128.Equals(a._upper.AsInt32(), b._upper.AsInt32()).AsSingle());
}

/// <summary>Eight lanes as one <see cref="Vector256{T}"/>: the wide path on AVX/AVX2 hardware.</summary>
internal readonly struct Lanes256 : IFloatLanes<Lanes256>
{
    private readonly Vector256<float> _value;

    private Lanes256(Vector256<float> value)
    {
        _value = value;
    }

    public static int Count => 8;

    public static Lanes256 Zero => default;

    public static Lanes256 Broadcast(float value) => new(Vector256.Create(value));

    public static Lanes256 Load(ReadOnlySpan<float> source) => new(Vector256.Create(source));

    public static void Store(Lanes256 value, Span<float> destination) => value._value.CopyTo(destination);

    public static void StoreBits(Lanes256 value, Span<int> destination) => value._value.AsInt32().CopyTo(destination);

    public static float Lane(Lanes256 value, int index) => value._value.GetElement(index);

    public static Lanes256 Add(Lanes256 a, Lanes256 b) => new(a._value + b._value);

    public static Lanes256 Subtract(Lanes256 a, Lanes256 b) => new(a._value - b._value);

    public static Lanes256 Multiply(Lanes256 a, Lanes256 b) => new(a._value * b._value);

    public static Lanes256 Divide(Lanes256 a, Lanes256 b) => new(a._value / b._value);

    public static Lanes256 Sqrt(Lanes256 a) => new(Vector256.Sqrt(a._value));

    public static Lanes256 And(Lanes256 a, Lanes256 b) => new(a._value & b._value);

    public static Lanes256 AndNot(Lanes256 a, Lanes256 mask) => new(Vector256.AndNot(a._value, mask._value));

    public static Lanes256 Or(Lanes256 a, Lanes256 b) => new(a._value | b._value);

    public static Lanes256 Xor(Lanes256 a, Lanes256 b) => new(a._value ^ b._value);

    public static Lanes256 LessThan(Lanes256 a, Lanes256 b) => new(Vector256.LessThan(a._value, b._value));

    public static Lanes256 GreaterThan(Lanes256 a, Lanes256 b) => new(Vector256.GreaterThan(a._value, b._value));

    public static Lanes256 Equal(Lanes256 a, Lanes256 b) => new(Vector256.Equals(a._value, b._value));

    public static Lanes256 Select(Lanes256 mask, Lanes256 whenTrue, Lanes256 whenFalse) => new(Vector256.ConditionalSelect(mask._value, whenTrue._value, whenFalse._value));

    public static Lanes256 TruncateToInt32(Lanes256 a) => new(Vector256.ConvertToInt32(a._value).AsSingle());

    public static Lanes256 Int32ToSingle(Lanes256 bits) => new(Vector256.ConvertToSingle(bits._value.AsInt32()));

    public static Lanes256 ShiftLeftInt32(Lanes256 bits, int shift) => new(Vector256.ShiftLeft(bits._value.AsInt32(), shift).AsSingle());

    public static Lanes256 ShiftRightArithmeticInt32(Lanes256 bits, int shift) => new(Vector256.ShiftRightArithmetic(bits._value.AsInt32(), shift).AsSingle());

    public static Lanes256 AddInt32(Lanes256 a, Lanes256 b) => new((a._value.AsInt32() + b._value.AsInt32()).AsSingle());

    public static Lanes256 EqualInt32(Lanes256 a, Lanes256 b) => new(Vector256.Equals(a._value.AsInt32(), b._value.AsInt32()).AsSingle());
}
