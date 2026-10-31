using System.Globalization;

namespace Colony.Numerics;

/// <summary>Two-component Q16.16 vector (8 bytes, unmanaged). Component arithmetic saturates like <see cref="Fixed32"/>.</summary>
public readonly struct Fixed32x2 : IEquatable<Fixed32x2>
{
    public Fixed32x2(Fixed32 x, Fixed32 y)
    {
        X = x;
        Y = y;
    }

    public Fixed32x2(Fixed32 value)
        : this(value, value)
    {
    }

    public Fixed32 X { get; }

    public Fixed32 Y { get; }

    public static Fixed32x2 Zero => default;

    public static Fixed32x2 One => new(Fixed32.One);

    public static Fixed32x2 UnitX => new(Fixed32.One, Fixed32.Zero);

    public static Fixed32x2 UnitY => new(Fixed32.Zero, Fixed32.One);

    /// <summary>Squared length; saturates to <see cref="Fixed32.MaxValue"/> on overflow.</summary>
    public Fixed32 LengthSquared => X * X + Y * Y;

    public Fixed32 Length => Fixed32.Sqrt(LengthSquared);

    public static Fixed32x2 operator +(Fixed32x2 a, Fixed32x2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Fixed32x2 operator -(Fixed32x2 a, Fixed32x2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Fixed32x2 operator -(Fixed32x2 a) => new(-a.X, -a.Y);

    /// <summary>Component-wise product.</summary>
    public static Fixed32x2 operator *(Fixed32x2 a, Fixed32x2 b) => new(a.X * b.X, a.Y * b.Y);

    public static Fixed32x2 operator *(Fixed32x2 a, Fixed32 s) => new(a.X * s, a.Y * s);

    public static Fixed32x2 operator *(Fixed32 s, Fixed32x2 a) => a * s;

    public static Fixed32x2 operator /(Fixed32x2 a, Fixed32 s) => new(a.X / s, a.Y / s);

    public static Fixed32x2 Add(Fixed32x2 a, Fixed32x2 b) => a + b;

    public static Fixed32x2 Subtract(Fixed32x2 a, Fixed32x2 b) => a - b;

    public static Fixed32x2 Negate(Fixed32x2 a) => -a;

    public static Fixed32x2 Multiply(Fixed32x2 a, Fixed32x2 b) => a * b;

    public static Fixed32x2 Multiply(Fixed32x2 a, Fixed32 s) => a * s;

    public static Fixed32x2 Divide(Fixed32x2 a, Fixed32 s) => a / s;

    public static Fixed32 Dot(Fixed32x2 a, Fixed32x2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>The z component of the 3D cross product: <c>a.X * b.Y - a.Y * b.X</c>.</summary>
    public static Fixed32 Cross(Fixed32x2 a, Fixed32x2 b) => a.X * b.Y - a.Y * b.X;

    public static Fixed32x2 Min(Fixed32x2 a, Fixed32x2 b) => new(Fixed32.Min(a.X, b.X), Fixed32.Min(a.Y, b.Y));

    public static Fixed32x2 Max(Fixed32x2 a, Fixed32x2 b) => new(Fixed32.Max(a.X, b.X), Fixed32.Max(a.Y, b.Y));

    public static Fixed32x2 Abs(Fixed32x2 a) => new(Fixed32.Abs(a.X), Fixed32.Abs(a.Y));

    /// <summary>Unit vector in the same direction, or zero for the zero vector.</summary>
    public static Fixed32x2 Normalize(Fixed32x2 a)
    {
        var length = a.Length;
        return length == Fixed32.Zero ? Zero : a / length;
    }

    public bool Equals(Fixed32x2 other) => X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is Fixed32x2 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1})", X, Y);

    public static bool operator ==(Fixed32x2 a, Fixed32x2 b) => a.Equals(b);

    public static bool operator !=(Fixed32x2 a, Fixed32x2 b) => !a.Equals(b);
}
