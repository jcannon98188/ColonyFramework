using Colony.Numerics;

namespace Colony.Core.Tests.Numerics;

public sealed class FixedVectorTests
{
    private static Fixed32 F(double v) => Fixed32.FromDouble(v);

    private static Fixed64 G(double v) => Fixed64.FromDouble(v);

    [Fact]
    public void Fixed32x2_component_arithmetic_and_products()
    {
        var a = new Fixed32x2(F(1), F(2));
        var b = new Fixed32x2(F(3), F(-4));
        Assert.Equal(new Fixed32x2(F(4), F(-2)), a + b);
        Assert.Equal(new Fixed32x2(F(-2), F(6)), a - b);
        Assert.Equal(new Fixed32x2(F(-1), F(-2)), -a);
        Assert.Equal(new Fixed32x2(F(2), F(4)), a * F(2));
        Assert.Equal(new Fixed32x2(F(2), F(4)), F(2) * a);
        Assert.Equal(new Fixed32x2(F(0.5), F(1)), a / F(2));
        Assert.Equal(new Fixed32x2(F(3), F(-8)), a * b);
        Assert.Equal(F(-5), Fixed32x2.Dot(a, b));
        Assert.Equal(F(25), b.LengthSquared);
        Assert.Equal(F(5), b.Length);
        Assert.Equal(F(-10), Fixed32x2.Cross(a, b)); // 1*(-4) - 2*3
        Assert.Equal(Fixed32x2.Zero, default);
        Assert.Equal(new Fixed32x2(Fixed32.One, Fixed32.One), Fixed32x2.One);
        Assert.Equal(new Fixed32x2(F(7), F(7)), new Fixed32x2(F(7)));
        Assert.True(a != b);
        Assert.True(a == new Fixed32x2(F(1), F(2)));
        Assert.Equal(a.GetHashCode(), new Fixed32x2(F(1), F(2)).GetHashCode());
        Assert.Equal("(1, 2)", a.ToString());
    }

    [Fact]
    public void Fixed32x2_saturates_like_its_components()
    {
        var big = new Fixed32x2(Fixed32.MaxValue, Fixed32.MinValue);
        Assert.Equal(big, big + big);
        Assert.Equal(Fixed32.MaxValue, big.LengthSquared);
        Assert.Equal(new Fixed32x2(Fixed32.MaxValue, Fixed32.MaxValue), Fixed32x2.Abs(big));
        Assert.Equal(new Fixed32x2(F(1), F(-2)), Fixed32x2.Min(new Fixed32x2(F(1), F(5)), new Fixed32x2(F(3), F(-2))));
        Assert.Equal(new Fixed32x2(F(3), F(5)), Fixed32x2.Max(new Fixed32x2(F(1), F(5)), new Fixed32x2(F(3), F(-2))));
    }

    [Fact]
    public void Fixed32x2_Normalize_returns_a_unit_vector_or_zero()
    {
        var n = Fixed32x2.Normalize(new Fixed32x2(F(3), F(4)));
        Assert.Equal(F(0.6), n.X);
        Assert.Equal(F(0.8), n.Y);
        Assert.Equal(Fixed32x2.Zero, Fixed32x2.Normalize(Fixed32x2.Zero));
    }

    [Fact]
    public void Fixed32x3_component_arithmetic_and_products()
    {
        var a = new Fixed32x3(F(1), F(2), F(3));
        var b = new Fixed32x3(F(4), F(-5), F(6));
        Assert.Equal(new Fixed32x3(F(5), F(-3), F(9)), a + b);
        Assert.Equal(new Fixed32x3(F(-3), F(7), F(-3)), a - b);
        Assert.Equal(new Fixed32x3(F(-1), F(-2), F(-3)), -a);
        Assert.Equal(new Fixed32x3(F(2), F(4), F(6)), a * F(2));
        Assert.Equal(new Fixed32x3(F(0.5), F(1), F(1.5)), a / F(2));
        Assert.Equal(new Fixed32x3(F(4), F(-10), F(18)), a * b);
        Assert.Equal(F(12), Fixed32x3.Dot(a, b));
        Assert.Equal(F(14), a.LengthSquared);
        Assert.Equal(F(27), Fixed32x3.Cross(a, b).X); // 2*6 - 3*(-5)
        Assert.Equal(F(6), Fixed32x3.Cross(a, b).Y);  // 3*4 - 1*6
        Assert.Equal(F(-13), Fixed32x3.Cross(a, b).Z); // 1*(-5) - 2*4
        Assert.Equal(F(3), new Fixed32x3(F(0), F(0), F(-3)).Length);
        Assert.Equal(Fixed32x3.Zero, default);
        Assert.Equal(new Fixed32x3(F(2), F(2), F(2)), new Fixed32x3(F(2)));
        Assert.True(a != b);
        Assert.Equal(a.GetHashCode(), new Fixed32x3(F(1), F(2), F(3)).GetHashCode());
        Assert.Equal("(1, 2, 3)", a.ToString());
        Assert.Equal(new Fixed32x3(F(1), F(-5), F(3)), Fixed32x3.Min(a, b));
        Assert.Equal(new Fixed32x3(F(4), F(2), F(6)), Fixed32x3.Max(a, b));
        Assert.Equal(new Fixed32x3(F(4), F(5), F(6)), Fixed32x3.Abs(b));
        Assert.Equal(new Fixed32x3(F(0), F(0), F(1)), Fixed32x3.Normalize(new Fixed32x3(F(0), F(0), F(9))));
        Assert.Equal(Fixed32x3.Zero, Fixed32x3.Normalize(Fixed32x3.Zero));
    }

    [Fact]
    public void Fixed64x2_and_Fixed64x3_mirror_the_Fixed32_vectors()
    {
        var a = new Fixed64x2(G(3), G(4));
        Assert.Equal(G(5), a.Length);
        Assert.Equal(G(25), a.LengthSquared);
        Assert.Equal(new Fixed64x2(G(6), G(8)), a * G(2));
        Assert.Equal(new Fixed64x2(G(1.5), G(2)), a / G(2));
        Assert.Equal(G(0), Fixed64x2.Dot(a, new Fixed64x2(G(4), G(-3))));
        Assert.Equal(G(-25), Fixed64x2.Cross(a, new Fixed64x2(G(4), G(-3))));
        Assert.Equal(new Fixed64x2(G(0.6), G(0.8)), Fixed64x2.Normalize(a));
        Assert.Equal(new Fixed64x2(G(-3), G(-4)), -a);
        Assert.Equal(new Fixed64x2(G(7), G(8)), a + new Fixed64x2(G(4)));
        Assert.Equal(new Fixed64x2(G(-1), G(0)), a - new Fixed64x2(G(4)));
        Assert.Equal(new Fixed64x2(G(12), G(16)), a * new Fixed64x2(G(4)));
        Assert.Equal(Fixed64x2.One, Fixed64x2.Max(Fixed64x2.Zero, Fixed64x2.One));
        Assert.Equal(Fixed64x2.Zero, Fixed64x2.Min(Fixed64x2.Zero, Fixed64x2.One));
        Assert.Equal(a, Fixed64x2.Abs(-a));
        Assert.True(a == new Fixed64x2(G(3), G(4)));
        Assert.True(a != Fixed64x2.Zero);
        Assert.Equal(a.GetHashCode(), new Fixed64x2(G(3), G(4)).GetHashCode());
        Assert.Equal("(3, 4)", a.ToString());

        var b = new Fixed64x3(G(1), G(2), G(3));
        var c = new Fixed64x3(G(4), G(-5), G(6));
        Assert.Equal(G(12), Fixed64x3.Dot(b, c));
        Assert.Equal(new Fixed64x3(G(27), G(6), G(-13)), Fixed64x3.Cross(b, c));
        Assert.Equal(new Fixed64x3(G(5), G(-3), G(9)), b + c);
        Assert.Equal(new Fixed64x3(G(-3), G(7), G(-3)), b - c);
        Assert.Equal(new Fixed64x3(G(4), G(-10), G(18)), b * c);
        Assert.Equal(new Fixed64x3(G(2), G(4), G(6)), b * G(2));
        Assert.Equal(new Fixed64x3(G(0.5), G(1), G(1.5)), b / G(2));
        Assert.Equal(new Fixed64x3(G(-1), G(-2), G(-3)), -b);
        Assert.Equal(G(14), b.LengthSquared);
        Assert.Equal(G(3), new Fixed64x3(G(0), G(3), G(0)).Length);
        Assert.Equal(new Fixed64x3(G(0), G(1), G(0)), Fixed64x3.Normalize(new Fixed64x3(G(0), G(5), G(0))));
        Assert.Equal(Fixed64x3.Zero, Fixed64x3.Normalize(Fixed64x3.Zero));
        Assert.Equal(new Fixed64x3(G(1), G(-5), G(3)), Fixed64x3.Min(b, c));
        Assert.Equal(new Fixed64x3(G(4), G(2), G(6)), Fixed64x3.Max(b, c));
        Assert.Equal(new Fixed64x3(G(4), G(5), G(6)), Fixed64x3.Abs(c));
        Assert.Equal(new Fixed64x3(G(2), G(2), G(2)), new Fixed64x3(G(2)));
        Assert.Equal(Fixed64x3.One, new Fixed64x3(Fixed64.One));
        Assert.True(b != c);
        Assert.True(b == new Fixed64x3(G(1), G(2), G(3)));
        Assert.Equal(b.GetHashCode(), new Fixed64x3(G(1), G(2), G(3)).GetHashCode());
        Assert.Equal("(1, 2, 3)", b.ToString());
    }

    [Fact]
    public void Vectors_are_unmanaged_and_tightly_packed()
    {
        Assert.Equal(8, System.Runtime.CompilerServices.Unsafe.SizeOf<Fixed32x2>());
        Assert.Equal(12, System.Runtime.CompilerServices.Unsafe.SizeOf<Fixed32x3>());
        Assert.Equal(16, System.Runtime.CompilerServices.Unsafe.SizeOf<Fixed64x2>());
        Assert.Equal(24, System.Runtime.CompilerServices.Unsafe.SizeOf<Fixed64x3>());
    }
}
