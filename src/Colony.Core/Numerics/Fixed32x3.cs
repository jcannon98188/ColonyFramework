using System.Globalization;

namespace Colony.Numerics;

/// <summary>Three-component Q16.16 vector (12 bytes, unmanaged). Component arithmetic saturates like <see cref="Fixed32"/>.</summary>
public readonly struct Fixed32x3 : IEquatable<Fixed32x3>
{
    public Fixed32x3(Fixed32 x, Fixed32 y, Fixed32 z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Fixed32x3(Fixed32 value)
        : this(value, value, value)
    {
    }

    public Fixed32 X { get; }

    public Fixed32 Y { get; }

    public Fixed32 Z { get; }

    public static Fixed32x3 Zero => default;

    public static Fixed32x3 One => new(Fixed32.One);

    public static Fixed32x3 UnitX => new(Fixed32.One, Fixed32.Zero, Fixed32.Zero);

    public static Fixed32x3 UnitY => new(Fixed32.Zero, Fixed32.One, Fixed32.Zero);

    public static Fixed32x3 UnitZ => new(Fixed32.Zero, Fixed32.Zero, Fixed32.One);

    /// <summary>Squared length; saturates to <see cref="Fixed32.MaxValue"/> on overflow.</summary>
    public Fixed32 LengthSquared => X * X + Y * Y + Z * Z;

    public Fixed32 Length => Fixed32.Sqrt(LengthSquared);

    public static Fixed32x3 operator +(Fixed32x3 a, Fixed32x3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Fixed32x3 operator -(Fixed32x3 a, Fixed32x3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Fixed32x3 operator -(Fixed32x3 a) => new(-a.X, -a.Y, -a.Z);

    /// <summary>Component-wise product.</summary>
    public static Fixed32x3 operator *(Fixed32x3 a, Fixed32x3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    public static Fixed32x3 operator *(Fixed32x3 a, Fixed32 s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Fixed32x3 operator *(Fixed32 s, Fixed32x3 a) => a * s;

    public static Fixed32x3 operator /(Fixed32x3 a, Fixed32 s) => new(a.X / s, a.Y / s, a.Z / s);

    public static Fixed32x3 Add(Fixed32x3 a, Fixed32x3 b) => a + b;

    public static Fixed32x3 Subtract(Fixed32x3 a, Fixed32x3 b) => a - b;

    public static Fixed32x3 Negate(Fixed32x3 a) => -a;

    public static Fixed32x3 Multiply(Fixed32x3 a, Fixed32x3 b) => a * b;

    public static Fixed32x3 Multiply(Fixed32x3 a, Fixed32 s) => a * s;

    public static Fixed32x3 Divide(Fixed32x3 a, Fixed32 s) => a / s;

    public static Fixed32 Dot(Fixed32x3 a, Fixed32x3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Fixed32x3 Cross(Fixed32x3 a, Fixed32x3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static Fixed32x3 Min(Fixed32x3 a, Fixed32x3 b) => new(Fixed32.Min(a.X, b.X), Fixed32.Min(a.Y, b.Y), Fixed32.Min(a.Z, b.Z));

    public static Fixed32x3 Max(Fixed32x3 a, Fixed32x3 b) => new(Fixed32.Max(a.X, b.X), Fixed32.Max(a.Y, b.Y), Fixed32.Max(a.Z, b.Z));

    public static Fixed32x3 Abs(Fixed32x3 a) => new(Fixed32.Abs(a.X), Fixed32.Abs(a.Y), Fixed32.Abs(a.Z));

    /// <summary>Unit vector in the same direction, or zero for the zero vector.</summary>
    public static Fixed32x3 Normalize(Fixed32x3 a)
    {
        var length = a.Length;
        return length == Fixed32.Zero ? Zero : a / length;
    }

    public bool Equals(Fixed32x3 other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is Fixed32x3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw, Z.Raw);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", X, Y, Z);

    public static bool operator ==(Fixed32x3 a, Fixed32x3 b) => a.Equals(b);

    public static bool operator !=(Fixed32x3 a, Fixed32x3 b) => !a.Equals(b);
}
