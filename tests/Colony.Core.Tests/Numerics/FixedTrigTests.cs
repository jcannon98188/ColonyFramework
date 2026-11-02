using Colony.Numerics;

namespace Colony.Core.Tests.Numerics;

/// <summary>
/// Golden values come from an independent high-precision computation (tools/scripts/gen_fixed_tables.py),
/// quantised to the input type. Fixed32 results must land within one ulp (2^-16); Fixed64 results,
/// which come from a 1024-step quarter-wave table with linear interpolation, within 2^-21.
/// </summary>
public sealed class FixedTrigTests
{
    private const long Fixed64TrigTolerance = 2048; // Q32 ulps = 4.8e-7 absolute

    [Theory]
    [InlineData(0, 0, 65536)]
    [InlineData(0x8000, 31420, 57513)]        // 0.5
    [InlineData(0x10000, 55147, 35409)]       // 1.0
    [InlineData(0x20000, 59592, -27273)]      // 2.0
    [InlineData(0x30000, 9248, -64880)]       // 3.0
    [InlineData(-0x10000, -55147, 35409)]     // -1.0
    [InlineData(0xA0000, -35653, -54989)]     // 10.0
    [InlineData(0x640000, -33185, 56513)]     // 100.0
    [InlineData(205887, 0, -65536)]           // nearest Fixed32 to pi
    [InlineData(102944, 65536, 0)]            // nearest Fixed32 to pi/2
    [InlineData(-102944, -65536, 0)]
    public void Fixed32_Sin_and_Cos_match_golden_values_within_one_ulp(int angleRaw, int expectedSin, int expectedCos)
    {
        var angle = Fixed32.FromRaw(angleRaw);
        Assert.InRange(Fixed32.Sin(angle).Raw, expectedSin - 1, expectedSin + 1);
        Assert.InRange(Fixed32.Cos(angle).Raw, expectedCos - 1, expectedCos + 1);
        var (sin, cos) = Fixed32.SinCos(angle);
        Assert.Equal(Fixed32.Sin(angle), sin);
        Assert.Equal(Fixed32.Cos(angle), cos);
    }

    [Theory]
    [InlineData(0x80000000L, 2059117009L)]           // 0.5
    [InlineData(0x100000000L, 3614090360L)]          // 1.0
    [InlineData(0x200000000L, 3905402711L)]          // 2.0
    [InlineData(0x300000000L, 606105819L)]           // 3.0
    [InlineData(-0x100000000L, -3614090360L)]        // -1.0
    [InlineData(0xA00000000L, -2336552880L)]         // 10.0
    [InlineData(0x6400000000L, -2174823868L)]        // 100.0
    [InlineData(13493037705L, 0L)]                   // nearest Fixed64 to pi
    [InlineData(6746518852L, 4294967296L)]           // nearest Fixed64 to pi/2
    public void Fixed64_Sin_matches_golden_values_within_tolerance(long angleRaw, long expectedSin)
    {
        var sin = Fixed64.Sin(Fixed64.FromRaw(angleRaw));
        Assert.InRange(sin.Raw, expectedSin - Fixed64TrigTolerance, expectedSin + Fixed64TrigTolerance);
    }

    [Fact]
    public void Fixed64_Cos_is_Sin_shifted_by_a_quarter_turn()
    {
        Assert.InRange(Fixed64.Cos(Fixed64.FromRaw(13493037705L)).Raw, -4294967296L - Fixed64TrigTolerance, -4294967296L + Fixed64TrigTolerance);
        Assert.Equal(Fixed64.One, Fixed64.Cos(Fixed64.Zero));
        Assert.Equal(Fixed64.Zero, Fixed64.Sin(Fixed64.Zero));
        Assert.Equal(Fixed64.Sin(Fixed64.FromInt64(2)), Fixed64.SinCos(Fixed64.FromInt64(2)).Sin);
    }

    [Fact]
    public void Sin_is_odd_and_Cos_is_even_bit_for_bit()
    {
        for (int i = -5000; i <= 5000; i += 37)
        {
            var a = Fixed32.FromRaw(i * 1000);
            Assert.Equal(-Fixed32.Sin(a), Fixed32.Sin(-a));
            Assert.Equal(Fixed32.Cos(a), Fixed32.Cos(-a));
            var b = Fixed64.FromRaw(i * 1_000_000_000L);
            Assert.Equal(-Fixed64.Sin(b), Fixed64.Sin(-b));
            Assert.Equal(Fixed64.Cos(b), Fixed64.Cos(-b));
        }
    }

    [Fact]
    public void Sin_and_Cos_never_leave_the_unit_range()
    {
        for (int i = int.MinValue; i < int.MaxValue - (1 << 20); i += 1 << 20)
        {
            var a = Fixed32.FromRaw(i);
            Assert.InRange(Fixed32.Sin(a).Raw, -65536, 65536);
            Assert.InRange(Fixed32.Cos(a).Raw, -65536, 65536);
        }

        Assert.InRange(Fixed64.Sin(Fixed64.MaxValue).Raw, -(1L << 32), 1L << 32);
        Assert.InRange(Fixed64.Cos(Fixed64.MinValue).Raw, -(1L << 32), 1L << 32);
    }

    [Theory]
    [InlineData(3, 4, 42172)]
    [InlineData(-3, 4, -42172)]
    [InlineData(-4, 3, -60771)]
    [InlineData(1, 0, 102944)]
    [InlineData(-1, 0, -102944)]
    [InlineData(0, -1, 205887)]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 51472)]
    [InlineData(-1, -1, -154416)]
    [InlineData(100, 0.0625, 102903)]
    public void Fixed32_Atan2_matches_golden_values_within_one_ulp(double y, double x, int expectedRaw)
    {
        var result = Fixed32.Atan2(Fixed32.FromDouble(y), Fixed32.FromDouble(x));
        Assert.InRange(result.Raw, expectedRaw - 1, expectedRaw + 1);
    }

    [Fact]
    public void Fixed32_Atan2_handles_tiny_ratios_and_extreme_values()
    {
        Assert.InRange(Fixed32.Atan2(Fixed32.FromDouble(0.001), Fixed32.One).Raw, 65, 67);
        Assert.InRange(Fixed32.Atan2(Fixed32.MaxValue, Fixed32.Epsilon).Raw, 102943, 102945);
        Assert.InRange(Fixed32.Atan2(Fixed32.MinValue, Fixed32.MinValue).Raw, -154417, -154415);
    }

    [Theory]
    [InlineData(3, 4, 2763816217L)]
    [InlineData(-3, 4, -2763816217L)]
    [InlineData(1, 0, 6746518852L)]
    [InlineData(0, -1, 13493037705L)]
    [InlineData(-1, -1, -10119778278L)]
    [InlineData(0, 0, 0L)]
    public void Fixed64_Atan2_matches_golden_values_within_tolerance(double y, double x, long expectedRaw)
    {
        var result = Fixed64.Atan2(Fixed64.FromDouble(y), Fixed64.FromDouble(x));
        Assert.InRange(result.Raw, expectedRaw - Fixed64TrigTolerance, expectedRaw + Fixed64TrigTolerance);
        Assert.InRange(Fixed64.Atan2(Fixed64.MaxValue, Fixed64.MinValue).Raw, 10119778278L - Fixed64TrigTolerance, 10119778278L + Fixed64TrigTolerance);
    }

    [Theory]
    [InlineData(0x20000, 92682)]        // sqrt(2) = 1.41421356 -> 92681.9 rounds up
    [InlineData(0x4000, 0x8000)]        // sqrt(0.25) = 0.5 exactly
    [InlineData(0x640000, 0xA0000)]     // sqrt(100) = 10
    [InlineData(0x30000, 113512)]       // sqrt(3)
    [InlineData(0x7FFFFFFF, 11863283)]  // sqrt(MaxValue)
    [InlineData(0, 0)]
    [InlineData(1, 256)]                // sqrt(2^-16) = 2^-8
    [InlineData(-0x10000, 0)]           // negative input is defined as zero
    public void Fixed32_Sqrt_is_correctly_rounded(int raw, int expectedRaw)
    {
        Assert.Equal(Fixed32.FromRaw(expectedRaw), Fixed32.Sqrt(Fixed32.FromRaw(raw)));
    }

    [Theory]
    [InlineData(0x200000000L, 6074001000L)]
    [InlineData(0x40000000L, 0x80000000L)]
    [InlineData(0x6400000000L, 0xA00000000L)]
    [InlineData(0x300000000L, 7439101574L)]
    [InlineData(0x7FFFFFFF00000000L, 199032864720089L)]
    [InlineData(long.MaxValue, 199032864766430L)]
    [InlineData(1L, 0x10000L)]
    [InlineData(-1L, 0L)]
    public void Fixed64_Sqrt_is_correctly_rounded(long raw, long expectedRaw)
    {
        Assert.Equal(Fixed64.FromRaw(expectedRaw), Fixed64.Sqrt(Fixed64.FromRaw(raw)));
    }

    [Fact]
    public void Sqrt_squared_never_exceeds_the_input_by_more_than_rounding()
    {
        for (int i = 1; i < 100_000; i += 997)
        {
            var x = Fixed32.FromRaw(i * 21_473);
            var s = Fixed32.Sqrt(x);
            var back = s * s;
            Assert.InRange(back.Raw, x.Raw - 2 * s.Raw / 65536 - 2, x.Raw + 2 * s.Raw / 65536 + 2);
        }
    }
}
