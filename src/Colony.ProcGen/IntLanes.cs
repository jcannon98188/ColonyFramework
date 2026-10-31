using System.Runtime.Intrinsics;

namespace Colony.ProcGen;

/// <summary>
/// A fixed group of int32 lanes with the primitive operations the noise kernels are written against.
/// Every operation is two's-complement integer arithmetic or a bit manipulation, so any two implementations
/// produce identical bits for identical inputs. Q16.16 fixed-point values travel as their raw int32.
/// </summary>
internal interface IIntLanes<TSelf>
    where TSelf : struct, IIntLanes<TSelf>
{
    public static abstract int Count { get; }

    public static abstract TSelf Zero { get; }

    public static abstract TSelf Broadcast(int value);

    /// <summary>Loads the first <see cref="Count"/> elements.</summary>
    public static abstract TSelf Load(ReadOnlySpan<int> source);

    public static abstract void Store(TSelf value, Span<int> destination);

    public static abstract int Lane(TSelf value, int index);

    public static abstract TSelf Add(TSelf a, TSelf b);

    public static abstract TSelf Subtract(TSelf a, TSelf b);

    /// <summary>Low 32 bits of the product (wrapping).</summary>
    public static abstract TSelf Multiply(TSelf a, TSelf b);

    /// <summary>Q16.16 product: <c>((long)a * b + 0x8000) &gt;&gt; 16</c>, rounded half up, truncated to 32 bits.</summary>
    public static abstract TSelf MultiplyQ16(TSelf a, TSelf b);

    public static abstract TSelf ShiftLeft(TSelf a, int shift);

    public static abstract TSelf ShiftRightArithmetic(TSelf a, int shift);

    public static abstract TSelf ShiftRightLogical(TSelf a, int shift);

    public static abstract TSelf And(TSelf a, TSelf b);

    public static abstract TSelf AndNot(TSelf a, TSelf mask);

    public static abstract TSelf Or(TSelf a, TSelf b);

    public static abstract TSelf Xor(TSelf a, TSelf b);

    /// <summary>All ones where <c>a &lt; b</c> (signed), else zero.</summary>
    public static abstract TSelf LessThan(TSelf a, TSelf b);

    public static abstract TSelf Equal(TSelf a, TSelf b);

    public static abstract TSelf Select(TSelf mask, TSelf whenTrue, TSelf whenFalse);

    public static abstract TSelf Min(TSelf a, TSelf b);

    public static abstract TSelf Max(TSelf a, TSelf b);
}

/// <summary>One lane: the scalar instantiation used for tails and the point API. The reference semantics.</summary>
internal readonly struct Lanes1 : IIntLanes<Lanes1>
{
    private readonly int _value;

    private Lanes1(int value)
    {
        _value = value;
    }

    public static int Count => 1;

    public static Lanes1 Zero => default;

    public static Lanes1 Broadcast(int value) => new(value);

    public static Lanes1 Load(ReadOnlySpan<int> source) => new(source[0]);

    public static void Store(Lanes1 value, Span<int> destination) => destination[0] = value._value;

    public static int Lane(Lanes1 value, int index) => index == 0 ? value._value : throw new ArgumentOutOfRangeException(nameof(index));

    public static Lanes1 Add(Lanes1 a, Lanes1 b) => new(unchecked(a._value + b._value));

    public static Lanes1 Subtract(Lanes1 a, Lanes1 b) => new(unchecked(a._value - b._value));

    public static Lanes1 Multiply(Lanes1 a, Lanes1 b) => new(unchecked(a._value * b._value));

    public static Lanes1 MultiplyQ16(Lanes1 a, Lanes1 b) => new(unchecked((int)((((long)a._value * b._value) + 0x8000) >> 16)));

    public static Lanes1 ShiftLeft(Lanes1 a, int shift) => new(a._value << shift);

    public static Lanes1 ShiftRightArithmetic(Lanes1 a, int shift) => new(a._value >> shift);

    public static Lanes1 ShiftRightLogical(Lanes1 a, int shift) => new(a._value >>> shift);

    public static Lanes1 And(Lanes1 a, Lanes1 b) => new(a._value & b._value);

    public static Lanes1 AndNot(Lanes1 a, Lanes1 mask) => new(a._value & ~mask._value);

    public static Lanes1 Or(Lanes1 a, Lanes1 b) => new(a._value | b._value);

    public static Lanes1 Xor(Lanes1 a, Lanes1 b) => new(a._value ^ b._value);

    public static Lanes1 LessThan(Lanes1 a, Lanes1 b) => new(a._value < b._value ? -1 : 0);

    public static Lanes1 Equal(Lanes1 a, Lanes1 b) => new(a._value == b._value ? -1 : 0);

    public static Lanes1 Select(Lanes1 mask, Lanes1 whenTrue, Lanes1 whenFalse) => new((whenTrue._value & mask._value) | (whenFalse._value & ~mask._value));

    public static Lanes1 Min(Lanes1 a, Lanes1 b) => new(a._value < b._value ? a._value : b._value);

    public static Lanes1 Max(Lanes1 a, Lanes1 b) => new(a._value > b._value ? a._value : b._value);
}

/// <summary>Four lanes in one <see cref="Vector128{T}"/>: the fallback for hardware without 256-bit vectors.</summary>
internal readonly struct Lanes128 : IIntLanes<Lanes128>
{
    private readonly Vector128<int> _value;

    private Lanes128(Vector128<int> value)
    {
        _value = value;
    }

    public static int Count => 4;

    public static Lanes128 Zero => default;

    public static Lanes128 Broadcast(int value) => new(Vector128.Create(value));

    public static Lanes128 Load(ReadOnlySpan<int> source) => new(Vector128.Create(source));

    public static void Store(Lanes128 value, Span<int> destination) => value._value.CopyTo(destination);

    public static int Lane(Lanes128 value, int index) => value._value.GetElement(index);

    public static Lanes128 Add(Lanes128 a, Lanes128 b) => new(a._value + b._value);

    public static Lanes128 Subtract(Lanes128 a, Lanes128 b) => new(a._value - b._value);

    public static Lanes128 Multiply(Lanes128 a, Lanes128 b) => new(a._value * b._value);

    public static Lanes128 MultiplyQ16(Lanes128 a, Lanes128 b)
    {
        var (aLower, aUpper) = Vector128.Widen(a._value);
        var (bLower, bUpper) = Vector128.Widen(b._value);
        var half = Vector128.Create(0x8000L);
        var lower = Vector128.ShiftRightArithmetic((aLower * bLower) + half, 16);
        var upper = Vector128.ShiftRightArithmetic((aUpper * bUpper) + half, 16);
        return new(Vector128.Narrow(lower, upper));
    }

    public static Lanes128 ShiftLeft(Lanes128 a, int shift) => new(Vector128.ShiftLeft(a._value, shift));

    public static Lanes128 ShiftRightArithmetic(Lanes128 a, int shift) => new(Vector128.ShiftRightArithmetic(a._value, shift));

    public static Lanes128 ShiftRightLogical(Lanes128 a, int shift) => new(Vector128.ShiftRightLogical(a._value, shift));

    public static Lanes128 And(Lanes128 a, Lanes128 b) => new(a._value & b._value);

    public static Lanes128 AndNot(Lanes128 a, Lanes128 mask) => new(Vector128.AndNot(a._value, mask._value));

    public static Lanes128 Or(Lanes128 a, Lanes128 b) => new(a._value | b._value);

    public static Lanes128 Xor(Lanes128 a, Lanes128 b) => new(a._value ^ b._value);

    public static Lanes128 LessThan(Lanes128 a, Lanes128 b) => new(Vector128.LessThan(a._value, b._value));

    public static Lanes128 Equal(Lanes128 a, Lanes128 b) => new(Vector128.Equals(a._value, b._value));

    public static Lanes128 Select(Lanes128 mask, Lanes128 whenTrue, Lanes128 whenFalse) => new(Vector128.ConditionalSelect(mask._value, whenTrue._value, whenFalse._value));

    public static Lanes128 Min(Lanes128 a, Lanes128 b) => new(Vector128.Min(a._value, b._value));

    public static Lanes128 Max(Lanes128 a, Lanes128 b) => new(Vector128.Max(a._value, b._value));
}

/// <summary>Eight lanes in one <see cref="Vector256{T}"/>: the wide path on AVX2 hardware.</summary>
internal readonly struct Lanes256 : IIntLanes<Lanes256>
{
    private readonly Vector256<int> _value;

    private Lanes256(Vector256<int> value)
    {
        _value = value;
    }

    public static int Count => 8;

    public static Lanes256 Zero => default;

    public static Lanes256 Broadcast(int value) => new(Vector256.Create(value));

    public static Lanes256 Load(ReadOnlySpan<int> source) => new(Vector256.Create(source));

    public static void Store(Lanes256 value, Span<int> destination) => value._value.CopyTo(destination);

    public static int Lane(Lanes256 value, int index) => value._value.GetElement(index);

    public static Lanes256 Add(Lanes256 a, Lanes256 b) => new(a._value + b._value);

    public static Lanes256 Subtract(Lanes256 a, Lanes256 b) => new(a._value - b._value);

    public static Lanes256 Multiply(Lanes256 a, Lanes256 b) => new(a._value * b._value);

    public static Lanes256 MultiplyQ16(Lanes256 a, Lanes256 b)
    {
        var (aLower, aUpper) = Vector256.Widen(a._value);
        var (bLower, bUpper) = Vector256.Widen(b._value);
        var half = Vector256.Create(0x8000L);
        var lower = Vector256.ShiftRightArithmetic((aLower * bLower) + half, 16);
        var upper = Vector256.ShiftRightArithmetic((aUpper * bUpper) + half, 16);
        return new(Vector256.Narrow(lower, upper));
    }

    public static Lanes256 ShiftLeft(Lanes256 a, int shift) => new(Vector256.ShiftLeft(a._value, shift));

    public static Lanes256 ShiftRightArithmetic(Lanes256 a, int shift) => new(Vector256.ShiftRightArithmetic(a._value, shift));

    public static Lanes256 ShiftRightLogical(Lanes256 a, int shift) => new(Vector256.ShiftRightLogical(a._value, shift));

    public static Lanes256 And(Lanes256 a, Lanes256 b) => new(a._value & b._value);

    public static Lanes256 AndNot(Lanes256 a, Lanes256 mask) => new(Vector256.AndNot(a._value, mask._value));

    public static Lanes256 Or(Lanes256 a, Lanes256 b) => new(a._value | b._value);

    public static Lanes256 Xor(Lanes256 a, Lanes256 b) => new(a._value ^ b._value);

    public static Lanes256 LessThan(Lanes256 a, Lanes256 b) => new(Vector256.LessThan(a._value, b._value));

    public static Lanes256 Equal(Lanes256 a, Lanes256 b) => new(Vector256.Equals(a._value, b._value));

    public static Lanes256 Select(Lanes256 mask, Lanes256 whenTrue, Lanes256 whenFalse) => new(Vector256.ConditionalSelect(mask._value, whenTrue._value, whenFalse._value));

    public static Lanes256 Min(Lanes256 a, Lanes256 b) => new(Vector256.Min(a._value, b._value));

    public static Lanes256 Max(Lanes256 a, Lanes256 b) => new(Vector256.Max(a._value, b._value));
}
