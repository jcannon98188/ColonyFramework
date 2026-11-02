using System.Runtime.CompilerServices;

namespace Colony.Core.Tests;

/// <summary>TASK-44: <see cref="ValueLayout.DataMaskOf{T}"/> finds padding, including nested padding, and never marks data as padding.</summary>
public sealed class ValueLayoutTests
{
    [Fact]
    public void Padding_bytes_are_found_at_the_top_level_and_inside_nested_structs()
    {
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x00 }, ValueLayout.DataMaskOf<Padded>());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0xFF }, ValueLayout.DataMaskOf<Nested>());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00 }, ValueLayout.DataMaskOf<FloatAndBool>());
    }

    [Fact]
    public void Types_without_padding_or_with_an_unmeasurable_layout_get_no_mask()
    {
        Assert.Null(ValueLayout.DataMaskOf<int>());
        Assert.Null(ValueLayout.DataMaskOf<Tight>());
        Assert.Null(ValueLayout.DataMaskOf<WithFixedBuffer>());
        Assert.Null(ValueLayout.DataMaskOf<WithInlineArray>());
        Assert.Null(ValueLayout.DataMaskOf<OverridesEquals>());
    }

    [Fact]
    public void Zeroing_and_comparing_work_over_values_back_to_back()
    {
        byte[] mask = [0xFF, 0x00];
        byte[] values = [1, 9, 2, 8];
        ValueLayout.ZeroPadding(mask, values);
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, values);
        Assert.True(ValueLayout.DataEqual(mask, [1, 5, 2, 6], [1, 7, 2, 0]));
        Assert.False(ValueLayout.DataEqual(mask, [1, 5, 2, 6], [1, 5, 3, 6]));
    }

    public struct Padded
    {
        public ushort A;
        public byte B;
    }

    public struct Nested
    {
        public Padded Inner;
        public int X;
    }

    public struct FloatAndBool
    {
        public float F;
        public bool B;
    }

    public struct Tight
    {
        public int A;
        public short B;
        public short C;
    }

    public unsafe struct WithFixedBuffer
    {
        public fixed byte Bytes[6];
        public int X;
    }

    [InlineArray(3)]
    public struct ThreeBytes
    {
        private byte _element;
    }

    public struct WithInlineArray
    {
        public ThreeBytes Bytes;
        public int X;
    }

    public struct OverridesEquals : IEquatable<OverridesEquals>
    {
        public ushort A;
        public byte B;

        public readonly bool Equals(OverridesEquals other) => A == other.A;

        public override readonly bool Equals(object? obj) => obj is OverridesEquals other && Equals(other);

        public override readonly int GetHashCode() => A;

        public static bool operator ==(OverridesEquals left, OverridesEquals right) => left.Equals(right);

        public static bool operator !=(OverridesEquals left, OverridesEquals right) => !left.Equals(right);
    }
}
