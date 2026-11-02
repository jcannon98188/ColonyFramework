using Colony.Numerics;

namespace Colony.Core.Tests.Numerics;

public sealed class Fixed32Tests
{
    [Fact]
    public void One_has_sixteen_fractional_bits()
    {
        Assert.Equal(1 << 16, Fixed32.One.Raw);
        Assert.Equal(16, Fixed32.FractionalBits);
        Assert.Equal(0, Fixed32.Zero.Raw);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0x10000)]
    [InlineData(-1, -0x10000)]
    [InlineData(32767, 0x7FFF0000)]
    [InlineData(-32768, int.MinValue)]
    public void FromInt32_scales_by_one(int value, int expectedRaw)
    {
        Assert.Equal(expectedRaw, Fixed32.FromInt32(value).Raw);
    }

    [Theory]
    [InlineData(32768, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(-32769, int.MinValue)]
    [InlineData(int.MinValue, int.MinValue)]
    public void FromInt32_saturates_out_of_range_values(int value, int expectedRaw)
    {
        Assert.Equal(expectedRaw, Fixed32.FromInt32(value).Raw);
    }

    [Theory]
    [InlineData(0.5, 0x8000)]
    [InlineData(-0.5, -0x8000)]
    [InlineData(1.5, 0x18000)]
    [InlineData(0.00001, 1)]       // 0.655 ulp rounds up to one ulp
    [InlineData(0.000005, 0)]      // 0.33 ulp rounds down to zero
    [InlineData(0.00000762939453125, 1)] // exactly half an ulp (2^-17) rounds away from zero
    [InlineData(-0.00000762939453125, -1)]
    [InlineData(-0.0, 0)]          // there is no negative zero in fixed-point
    public void FromDouble_rounds_to_nearest_ties_away_from_zero(double value, int expectedRaw)
    {
        Assert.Equal(expectedRaw, Fixed32.FromDouble(value).Raw);
    }

    [Theory]
    [InlineData(40000.0, int.MaxValue)]
    [InlineData(-40000.0, int.MinValue)]
    [InlineData(double.PositiveInfinity, int.MaxValue)]
    [InlineData(double.NegativeInfinity, int.MinValue)]
    [InlineData(double.NaN, 0)]
    public void FromDouble_saturates_and_maps_NaN_to_zero(double value, int expectedRaw)
    {
        Assert.Equal(expectedRaw, Fixed32.FromDouble(value).Raw);
    }

    [Fact]
    public void FromSingle_matches_FromDouble_for_representable_values()
    {
        Assert.Equal(Fixed32.FromDouble(0.25), Fixed32.FromSingle(0.25f));
        Assert.Equal(Fixed32.FromDouble(-3.75), Fixed32.FromSingle(-3.75f));
        Assert.Equal(Fixed32.MaxValue, Fixed32.FromSingle(float.PositiveInfinity));
        Assert.Equal(Fixed32.Zero, Fixed32.FromSingle(float.NaN));
    }

    [Theory]
    [InlineData(0x18000, 1, 1, 2)]      // 1.5 -> trunc 1, floor 1, round 2
    [InlineData(-0x18000, -1, -2, -2)]  // -1.5 -> trunc -1, floor -2, round -2 (ties away from zero)
    [InlineData(0x8000, 0, 0, 1)]       // 0.5 -> round 1
    [InlineData(-0x8000, 0, -1, -1)]    // -0.5 -> round -1
    [InlineData(0x7FFF, 0, 0, 0)]       // 0.49998 -> 0
    [InlineData(int.MaxValue, 32767, 32767, 32768)]
    [InlineData(int.MinValue, -32768, -32768, -32768)]
    public void Integer_conversions_truncate_floor_and_round(int raw, int truncated, int floored, int rounded)
    {
        var value = Fixed32.FromRaw(raw);
        Assert.Equal(truncated, value.ToInt32());
        Assert.Equal(floored, value.FloorToInt32());
        Assert.Equal(rounded, value.RoundToInt32());
    }

    [Fact]
    public void ToDouble_and_ToSingle_are_exact_for_fixed_values()
    {
        Assert.Equal(1.5, Fixed32.FromRaw(0x18000).ToDouble());
        Assert.Equal(-0.25f, Fixed32.FromRaw(-0x4000).ToSingle());
        Assert.Equal(32767.999984741211, Fixed32.MaxValue.ToDouble());
    }

    [Fact]
    public void Addition_and_subtraction_saturate_instead_of_wrapping()
    {
        Assert.Equal(Fixed32.FromRaw(0x28000), Fixed32.FromRaw(0x10000) + Fixed32.FromRaw(0x18000));
        Assert.Equal(Fixed32.FromRaw(-0x8000), Fixed32.FromRaw(0x10000) - Fixed32.FromRaw(0x18000));
        Assert.Equal(Fixed32.MaxValue, Fixed32.MaxValue + Fixed32.One);
        Assert.Equal(Fixed32.MinValue, Fixed32.MinValue - Fixed32.One);
        Assert.Equal(Fixed32.MinValue, Fixed32.MinValue + Fixed32.MinValue);
    }

    [Fact]
    public void Negation_of_zero_is_zero_and_negation_of_MinValue_saturates()
    {
        Assert.Equal(Fixed32.Zero, -Fixed32.Zero);
        Assert.Equal(Fixed32.MaxValue, -Fixed32.MinValue);
        Assert.Equal(Fixed32.FromInt32(-3), -Fixed32.FromInt32(3));
    }

    [Theory]
    [InlineData(0x18000, 0x18000, 0x24000)]       // 1.5 * 1.5 = 2.25
    [InlineData(-0x18000, 0x20000, -0x30000)]     // -1.5 * 2 = -3
    [InlineData(1, 1, 0)]                         // ulp * ulp underflows to zero
    [InlineData(0x8000, 1, 1)]                    // 0.5 * ulp = half an ulp, ties away from zero -> one ulp
    [InlineData(-0x8000, 1, -1)]                  // ties away from zero on the negative side
    [InlineData(0x7FFFFFFF, 0x20000, 0x7FFFFFFF)] // overflow saturates
    [InlineData(int.MinValue, 0x20000, int.MinValue)]
    [InlineData(int.MinValue, int.MinValue, 0x7FFFFFFF)] // (-32768)^2 overflows positive
    public void Multiplication_rounds_to_nearest_and_saturates(int a, int b, int expectedRaw)
    {
        Assert.Equal(Fixed32.FromRaw(expectedRaw), Fixed32.FromRaw(a) * Fixed32.FromRaw(b));
    }

    [Theory]
    [InlineData(0x30000, 0x20000, 0x18000)]  // 3 / 2 = 1.5
    [InlineData(0x10000, 0x30000, 0x5555)]   // 1 / 3 = 0.33333 -> 21845.33 rounds to 21845
    [InlineData(0x20000, 0x30000, 0xAAAB)]   // 2 / 3 -> 43690.67 rounds to 43691
    [InlineData(-0x10000, 0x30000, -0x5555)]
    [InlineData(0x7FFFFFFF, 1, 0x7FFFFFFF)]  // overflow saturates
    [InlineData(int.MinValue, -1, 0x7FFFFFFF)]
    public void Division_rounds_to_nearest_and_saturates(int a, int b, int expectedRaw)
    {
        Assert.Equal(Fixed32.FromRaw(expectedRaw), Fixed32.FromRaw(a) / Fixed32.FromRaw(b));
    }

    [Fact]
    public void Division_by_zero_saturates_by_sign_of_dividend_and_never_throws()
    {
        Assert.Equal(Fixed32.MaxValue, Fixed32.One / Fixed32.Zero);
        Assert.Equal(Fixed32.MinValue, -Fixed32.One / Fixed32.Zero);
        Assert.Equal(Fixed32.Zero, Fixed32.Zero / Fixed32.Zero);
    }

    [Fact]
    public void Comparison_and_equality_follow_the_raw_value()
    {
        var a = Fixed32.FromInt32(2);
        var b = Fixed32.FromRaw(0x20001);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= Fixed32.FromInt32(2));
        Assert.True(a >= Fixed32.FromInt32(2));
        Assert.True(a == Fixed32.FromInt32(2));
        Assert.True(a != b);
        Assert.Equal(a.GetHashCode(), Fixed32.FromInt32(2).GetHashCode());
        Assert.Equal(-1, a.CompareTo(b));
        Assert.Equal(Fixed32.FromInt32(-2), Fixed32.Min(a, Fixed32.FromInt32(-2)));
        Assert.Equal(b, Fixed32.Max(a, b));
        Assert.Equal(Fixed32.FromInt32(2), Fixed32.Abs(Fixed32.FromInt32(-2)));
        Assert.Equal(Fixed32.MaxValue, Fixed32.Abs(Fixed32.MinValue));
        Assert.Equal(1, Fixed32.Sign(b));
        Assert.Equal(0, Fixed32.Sign(Fixed32.Zero));
    }

    [Fact]
    public void Floor_Ceiling_Round_and_Fraction_work_on_fixed_values()
    {
        var x = Fixed32.FromRaw(-0x18000); // -1.5
        Assert.Equal(Fixed32.FromInt32(-2), Fixed32.Floor(x));
        Assert.Equal(Fixed32.FromInt32(-1), Fixed32.Ceiling(x));
        Assert.Equal(Fixed32.FromInt32(-2), Fixed32.Round(x));
        Assert.Equal(Fixed32.FromRaw(0x8000), Fixed32.Fraction(x)); // fraction is always in [0,1)
        Assert.Equal(Fixed32.MaxValue, Fixed32.Ceiling(Fixed32.MaxValue)); // saturates
    }

    [Fact]
    public void ToString_uses_invariant_culture_decimal_form()
    {
        Assert.Equal("1.5", Fixed32.FromRaw(0x18000).ToString());
        Assert.Equal("-0.25", Fixed32.FromRaw(-0x4000).ToString());
    }
}
