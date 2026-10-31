using Colony.Numerics;

namespace Colony.Core.Tests.Numerics;

public sealed class Fixed64Tests
{
    [Fact]
    public void One_has_thirty_two_fractional_bits()
    {
        Assert.Equal(1L << 32, Fixed64.One.Raw);
        Assert.Equal(32, Fixed64.FractionalBits);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L << 32)]
    [InlineData(-1L, -(1L << 32))]
    [InlineData(int.MaxValue, 0x7FFFFFFF00000000L)]
    [InlineData(int.MinValue, long.MinValue)]
    [InlineData((long)int.MaxValue + 1, long.MaxValue)]
    [InlineData((long)int.MinValue - 1, long.MinValue)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void FromInt64_scales_and_saturates(long value, long expectedRaw)
    {
        Assert.Equal(expectedRaw, Fixed64.FromInt64(value).Raw);
    }

    [Theory]
    [InlineData(0.5, 0x80000000L)]
    [InlineData(-0.5, -0x80000000L)]
    [InlineData(1.16415321826934814453125e-10, 1L)] // exactly half an ulp (2^-33) rounds away from zero
    [InlineData(-0.0, 0L)]
    [InlineData(1e10, long.MaxValue)]
    [InlineData(-1e10, long.MinValue)]
    [InlineData(double.NaN, 0L)]
    [InlineData(double.PositiveInfinity, long.MaxValue)]
    public void FromDouble_rounds_ties_away_from_zero_and_saturates(double value, long expectedRaw)
    {
        Assert.Equal(expectedRaw, Fixed64.FromDouble(value).Raw);
    }

    [Fact]
    public void Conversions_to_and_from_Fixed32_are_exact_or_saturating()
    {
        var f32 = Fixed32.FromRaw(0x18000); // 1.5
        Assert.Equal(0x180000000L, Fixed64.FromFixed32(f32).Raw);
        Assert.Equal(f32, Fixed64.FromFixed32(f32).ToFixed32());
        Assert.Equal(Fixed32.MaxValue, Fixed64.FromInt64(40000).ToFixed32());
        Assert.Equal(Fixed32.MinValue, Fixed64.FromInt64(-40000).ToFixed32());
        // Dropping 16 fractional bits rounds to nearest, ties away from zero.
        Assert.Equal(1, Fixed64.FromRaw(0x8000).ToFixed32().Raw);
        Assert.Equal(0, Fixed64.FromRaw(0x7FFF).ToFixed32().Raw);
        Assert.Equal(-1, Fixed64.FromRaw(-0x8000).ToFixed32().Raw);
    }

    [Theory]
    [InlineData(0x180000000L, 1L, 1L, 2L)]      // 1.5
    [InlineData(-0x180000000L, -1L, -2L, -2L)]  // -1.5
    [InlineData(-0x80000000L, 0L, -1L, -1L)]    // -0.5
    [InlineData(-0x7FFFFFFFL, 0L, -1L, 0L)]     // just above -0.5 rounds to zero
    [InlineData(long.MaxValue, int.MaxValue, int.MaxValue, 1L << 31)]
    [InlineData(long.MinValue, int.MinValue, int.MinValue, int.MinValue)]
    public void Integer_conversions_truncate_floor_and_round(long raw, long truncated, long floored, long rounded)
    {
        var value = Fixed64.FromRaw(raw);
        Assert.Equal(truncated, value.ToInt64());
        Assert.Equal(floored, value.FloorToInt64());
        Assert.Equal(rounded, value.RoundToInt64());
    }

    [Fact]
    public void Addition_subtraction_and_negation_saturate()
    {
        Assert.Equal(Fixed64.FromInt64(3), Fixed64.FromInt64(1) + Fixed64.FromInt64(2));
        Assert.Equal(Fixed64.FromInt64(-1), Fixed64.FromInt64(1) - Fixed64.FromInt64(2));
        Assert.Equal(Fixed64.MaxValue, Fixed64.MaxValue + Fixed64.One);
        Assert.Equal(Fixed64.MinValue, Fixed64.MinValue - Fixed64.One);
        Assert.Equal(Fixed64.MaxValue, -Fixed64.MinValue);
        Assert.Equal(Fixed64.Zero, -Fixed64.Zero);
    }

    [Theory]
    [InlineData(0x180000000L, 0x180000000L, 0x240000000L)] // 1.5 * 1.5 = 2.25
    [InlineData(-0x180000000L, 0x200000000L, -0x300000000L)]
    [InlineData(1L, 1L, 0L)]
    [InlineData(0x80000000L, 1L, 1L)]                        // half an ulp rounds away from zero
    [InlineData(-0x80000000L, 1L, -1L)]
    [InlineData(long.MaxValue, 0x200000000L, long.MaxValue)]  // saturates
    [InlineData(long.MinValue, long.MinValue, long.MaxValue)]
    public void Multiplication_rounds_to_nearest_and_saturates(long a, long b, long expectedRaw)
    {
        Assert.Equal(Fixed64.FromRaw(expectedRaw), Fixed64.FromRaw(a) * Fixed64.FromRaw(b));
    }

    [Theory]
    [InlineData(0x300000000L, 0x200000000L, 0x180000000L)]   // 3 / 2
    [InlineData(0x100000000L, 0x300000000L, 0x55555555L)]    // 1 / 3 = 1431655765.33 -> 1431655765
    [InlineData(0x200000000L, 0x300000000L, 0xAAAAAAABL)]    // 2 / 3 -> rounds up
    [InlineData(long.MaxValue, 1L, long.MaxValue)]
    [InlineData(long.MinValue, -1L, long.MaxValue)]
    [InlineData(0x100000000L, 0L, long.MaxValue)]            // division by zero saturates
    [InlineData(-0x100000000L, 0L, long.MinValue)]
    [InlineData(0L, 0L, 0L)]
    public void Division_rounds_to_nearest_and_saturates(long a, long b, long expectedRaw)
    {
        Assert.Equal(Fixed64.FromRaw(expectedRaw), Fixed64.FromRaw(a) / Fixed64.FromRaw(b));
    }

    [Fact]
    public void Comparison_helpers_and_rounding_functions()
    {
        var x = Fixed64.FromRaw(-0x180000000L); // -1.5
        Assert.Equal(Fixed64.FromInt64(-2), Fixed64.Floor(x));
        Assert.Equal(Fixed64.FromInt64(-1), Fixed64.Ceiling(x));
        Assert.Equal(Fixed64.FromInt64(-2), Fixed64.Round(x));
        Assert.Equal(Fixed64.FromRaw(0x80000000L), Fixed64.Fraction(x));
        Assert.Equal(Fixed64.FromInt64(2), Fixed64.Abs(Fixed64.FromInt64(-2)));
        Assert.Equal(Fixed64.MaxValue, Fixed64.Abs(Fixed64.MinValue));
        Assert.Equal(-1, Fixed64.Sign(x));
        Assert.Equal(x, Fixed64.Min(x, Fixed64.Zero));
        Assert.Equal(Fixed64.Zero, Fixed64.Max(x, Fixed64.Zero));
        Assert.Equal(Fixed64.One, Fixed64.Clamp(Fixed64.FromInt64(7), Fixed64.Zero, Fixed64.One));
        Assert.True(x < Fixed64.Zero);
        Assert.True(Fixed64.Zero > x);
        Assert.True(x <= Fixed64.FromRaw(-0x180000000L));
        Assert.True(x >= Fixed64.FromRaw(-0x180000000L));
        Assert.True(x != Fixed64.Zero);
        Assert.Equal(-1, x.CompareTo(Fixed64.Zero));
        Assert.Equal(x.GetHashCode(), Fixed64.FromRaw(-0x180000000L).GetHashCode());
        Assert.Equal("-1.5", x.ToString());
        Assert.Equal(-1.5, x.ToDouble());
        Assert.Equal(-1.5f, x.ToSingle());
    }
}
