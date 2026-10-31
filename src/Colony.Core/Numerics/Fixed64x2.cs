using System.Globalization;

namespace Colony.Numerics;

/// <summary>Two-component Q32.32 vector (16 bytes, unmanaged). Component arithmetic saturates like <see cref="Fixed64"/>.</summary>
public readonly struct Fixed64x2 : IEquatable<Fixed64x2>
{
    public Fixed64x2(Fixed64 x, Fixed64 y)
    {
        X = x;
        Y = y;
    }

    public Fixed64x2(Fixed64 value)
        : this(value, value)
    {
    }

    public Fixed64 X { get; }

    public Fixed64 Y { get; }

    public static Fixed64x2 Zero => default;

    public static Fixed64x2 One => new(Fixed64.One);

    public static Fixed64x2 UnitX => new(Fixed64.One, Fixed64.Zero);

    public static Fixed64x2 UnitY => new(Fixed64.Zero, Fixed64.One);

    /// <summary>Squared length; saturates to <see cref="Fixed64.MaxValue"/> on overflow.</summary>
    public Fixed64 LengthSquared => X * X + Y * Y;

    public Fixed64 Length => Fixed64.Sqrt(LengthSquared);

    public static Fixed64x2 operator +(Fixed64x2 a, Fixed64x2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Fixed64x2 operator -(Fixed64x2 a, Fixed64x2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Fixed64x2 operator -(Fixed64x2 a) => new(-a.X, -a.Y);

    /// <summary>Component-wise product.</summary>
    public static Fixed64x2 operator *(Fixed64x2 a, Fixed64x2 b) => new(a.X * b.X, a.Y * b.Y);

    public static Fixed64x2 operator *(Fixed64x2 a, Fixed64 s) => new(a.X * s, a.Y * s);

    public static Fixed64x2 operator *(Fixed64 s, Fixed64x2 a) => a * s;

    public static Fixed64x2 operator /(Fixed64x2 a, Fixed64 s) => new(a.X / s, a.Y / s);

    public static Fixed64x2 Add(Fixed64x2 a, Fixed64x2 b) => a + b;

    public static Fixed64x2 Subtract(Fixed64x2 a, Fixed64x2 b) => a - b;

    public static Fixed64x2 Negate(Fixed64x2 a) => -a;

    public static Fixed64x2 Multiply(Fixed64x2 a, Fixed64x2 b) => a * b;

    public static Fixed64x2 Multiply(Fixed64x2 a, Fixed64 s) => a * s;

    public static Fixed64x2 Divide(Fixed64x2 a, Fixed64 s) => a / s;

    public static Fixed64 Dot(Fixed64x2 a, Fixed64x2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>The z component of the 3D cross product: <c>a.X * b.Y - a.Y * b.X</c>.</summary>
    public static Fixed64 Cross(Fixed64x2 a, Fixed64x2 b) => a.X * b.Y - a.Y * b.X;

    public static Fixed64x2 Min(Fixed64x2 a, Fixed64x2 b) => new(Fixed64.Min(a.X, b.X), Fixed64.Min(a.Y, b.Y));

    public static Fixed64x2 Max(Fixed64x2 a, Fixed64x2 b) => new(Fixed64.Max(a.X, b.X), Fixed64.Max(a.Y, b.Y));

    public static Fixed64x2 Abs(Fixed64x2 a) => new(Fixed64.Abs(a.X), Fixed64.Abs(a.Y));

    /// <summary>Unit vector in the same direction, or zero for the zero vector.</summary>
    public static Fixed64x2 Normalize(Fixed64x2 a)
    {
        var length = a.Length;
        return length == Fixed64.Zero ? Zero : a / length;
    }

    public bool Equals(Fixed64x2 other) => X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is Fixed64x2 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1})", X, Y);

    public static bool operator ==(Fixed64x2 a, Fixed64x2 b) => a.Equals(b);

    public static bool operator !=(Fixed64x2 a, Fixed64x2 b) => !a.Equals(b);
}
