using System.Globalization;

namespace Colony.Numerics;

/// <summary>Three-component Q32.32 vector (24 bytes, unmanaged). Component arithmetic saturates like <see cref="Fixed64"/>.</summary>
public readonly struct Fixed64x3 : IEquatable<Fixed64x3>
{
    public Fixed64x3(Fixed64 x, Fixed64 y, Fixed64 z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Fixed64x3(Fixed64 value)
        : this(value, value, value)
    {
    }

    public Fixed64 X { get; }

    public Fixed64 Y { get; }

    public Fixed64 Z { get; }

    public static Fixed64x3 Zero => default;

    public static Fixed64x3 One => new(Fixed64.One);

    public static Fixed64x3 UnitX => new(Fixed64.One, Fixed64.Zero, Fixed64.Zero);

    public static Fixed64x3 UnitY => new(Fixed64.Zero, Fixed64.One, Fixed64.Zero);

    public static Fixed64x3 UnitZ => new(Fixed64.Zero, Fixed64.Zero, Fixed64.One);

    /// <summary>Squared length; saturates to <see cref="Fixed64.MaxValue"/> on overflow.</summary>
    public Fixed64 LengthSquared => X * X + Y * Y + Z * Z;

    public Fixed64 Length => Fixed64.Sqrt(LengthSquared);

    public static Fixed64x3 operator +(Fixed64x3 a, Fixed64x3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Fixed64x3 operator -(Fixed64x3 a, Fixed64x3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Fixed64x3 operator -(Fixed64x3 a) => new(-a.X, -a.Y, -a.Z);

    /// <summary>Component-wise product.</summary>
    public static Fixed64x3 operator *(Fixed64x3 a, Fixed64x3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    public static Fixed64x3 operator *(Fixed64x3 a, Fixed64 s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Fixed64x3 operator *(Fixed64 s, Fixed64x3 a) => a * s;

    public static Fixed64x3 operator /(Fixed64x3 a, Fixed64 s) => new(a.X / s, a.Y / s, a.Z / s);

    public static Fixed64x3 Add(Fixed64x3 a, Fixed64x3 b) => a + b;

    public static Fixed64x3 Subtract(Fixed64x3 a, Fixed64x3 b) => a - b;

    public static Fixed64x3 Negate(Fixed64x3 a) => -a;

    public static Fixed64x3 Multiply(Fixed64x3 a, Fixed64x3 b) => a * b;

    public static Fixed64x3 Multiply(Fixed64x3 a, Fixed64 s) => a * s;

    public static Fixed64x3 Divide(Fixed64x3 a, Fixed64 s) => a / s;

    public static Fixed64 Dot(Fixed64x3 a, Fixed64x3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Fixed64x3 Cross(Fixed64x3 a, Fixed64x3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static Fixed64x3 Min(Fixed64x3 a, Fixed64x3 b) => new(Fixed64.Min(a.X, b.X), Fixed64.Min(a.Y, b.Y), Fixed64.Min(a.Z, b.Z));

    public static Fixed64x3 Max(Fixed64x3 a, Fixed64x3 b) => new(Fixed64.Max(a.X, b.X), Fixed64.Max(a.Y, b.Y), Fixed64.Max(a.Z, b.Z));

    public static Fixed64x3 Abs(Fixed64x3 a) => new(Fixed64.Abs(a.X), Fixed64.Abs(a.Y), Fixed64.Abs(a.Z));

    /// <summary>Unit vector in the same direction, or zero for the zero vector.</summary>
    public static Fixed64x3 Normalize(Fixed64x3 a)
    {
        var length = a.Length;
        return length == Fixed64.Zero ? Zero : a / length;
    }

    public bool Equals(Fixed64x3 other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is Fixed64x3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw, Z.Raw);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", X, Y, Z);

    public static bool operator ==(Fixed64x3 a, Fixed64x3 b) => a.Equals(b);

    public static bool operator !=(Fixed64x3 a, Fixed64x3 b) => !a.Equals(b);
}
