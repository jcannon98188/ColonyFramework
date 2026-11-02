using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// Header shared by every buffer Component: the live element count and the overflow handle (0 = elements
/// live inline). Sits at offset 0 of <see cref="BufferComponent{TElement, TInline}"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BufferHeader
{
    public int Length;
    public uint Handle;
}

/// <summary>
/// A buffer Component (spec section 4.1, variable data): up to <c>sizeof(TInline) / sizeof(TElement)</c>
/// elements inline in the chunk, then overflow to the World-owned size-class allocator. Register it with
/// <see cref="WorldBuilder.AddBufferComponent{TElement, TInline}"/> and work with it through
/// <see cref="World.GetBuffer{TElement}"/> / <see cref="ChunkView.Buffer{TElement}"/>, which return a
/// <see cref="BufferAccessor{T}"/>. The value is plain data while inline; once it owns overflow storage it
/// must not be copied (a <see cref="CommandBuffer.Add{T}(EntityId, ComponentType, in T)"/> of such a value throws).
/// Hashed logically (length and elements), so the handle never leaks into the World hash.
/// </summary>
/// <typeparam name="TElement">Element type.</typeparam>
/// <typeparam name="TInline">Inline storage: an unmanaged struct whose size is a positive multiple of the element size, such as <see cref="Inline8{T}"/>.</typeparam>
[StructLayout(LayoutKind.Sequential)]
public struct BufferComponent<TElement, TInline> : IEquatable<BufferComponent<TElement, TInline>>
    where TElement : unmanaged
    where TInline : unmanaged
{
    internal BufferHeader Header;
    internal TInline Inline;

    /// <summary>Elements the Component holds inline before overflowing.</summary>
    internal static int InlineCapacity => Unsafe.SizeOf<TInline>() / Unsafe.SizeOf<TElement>();

    /// <summary>Live elements.</summary>
    public readonly int Length => Header.Length;

    /// <summary>True while every element lives inline (the value is plain data).</summary>
    public readonly bool IsInline => Header.Handle == 0;

    /// <summary>Byte offset of the inline storage inside the Component.</summary>
    internal static int InlineOffset
    {
        get
        {
            BufferComponent<TElement, TInline> probe = default;
            return (int)Unsafe.ByteOffset(ref Unsafe.As<BufferComponent<TElement, TInline>, byte>(ref probe), ref Unsafe.As<TInline, byte>(ref probe.Inline));
        }
    }

    /// <summary>Bitwise equality of the header and inline bytes (two inline buffers with equal elements are equal).</summary>
    public readonly bool Equals(BufferComponent<TElement, TInline> other)
        => MemoryMarshal.AsBytes(new ReadOnlySpan<BufferComponent<TElement, TInline>>(in this))
            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<BufferComponent<TElement, TInline>>(in other)));

    public override readonly bool Equals(object? obj) => obj is BufferComponent<TElement, TInline> other && Equals(other);

    public override readonly int GetHashCode() => (int)Hashing.Hash64.Compute(MemoryMarshal.AsBytes(new ReadOnlySpan<BufferComponent<TElement, TInline>>(in this)), 0);

    public static bool operator ==(BufferComponent<TElement, TInline> left, BufferComponent<TElement, TInline> right) => left.Equals(right);

    public static bool operator !=(BufferComponent<TElement, TInline> left, BufferComponent<TElement, TInline> right) => !left.Equals(right);
}

/// <summary>Inline storage for 4 elements (see <see cref="BufferComponent{TElement, TInline}"/>).</summary>
[InlineArray(4)]
public struct Inline4<T> : IEquatable<Inline4<T>> where T : unmanaged
{
    private T _element0;

    public readonly bool Equals(Inline4<T> other) => MemoryMarshal.AsBytes(new ReadOnlySpan<Inline4<T>>(in this)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Inline4<T>>(in other)));

    public override readonly bool Equals(object? obj) => obj is Inline4<T> other && Equals(other);

    public override readonly int GetHashCode() => (int)Hashing.Hash64.Compute(MemoryMarshal.AsBytes(new ReadOnlySpan<Inline4<T>>(in this)), 0);

    public static bool operator ==(Inline4<T> left, Inline4<T> right) => left.Equals(right);

    public static bool operator !=(Inline4<T> left, Inline4<T> right) => !left.Equals(right);
}

/// <summary>Inline storage for 8 elements (see <see cref="BufferComponent{TElement, TInline}"/>).</summary>
[InlineArray(8)]
public struct Inline8<T> : IEquatable<Inline8<T>> where T : unmanaged
{
    private T _element0;

    public readonly bool Equals(Inline8<T> other) => MemoryMarshal.AsBytes(new ReadOnlySpan<Inline8<T>>(in this)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Inline8<T>>(in other)));

    public override readonly bool Equals(object? obj) => obj is Inline8<T> other && Equals(other);

    public override readonly int GetHashCode() => (int)Hashing.Hash64.Compute(MemoryMarshal.AsBytes(new ReadOnlySpan<Inline8<T>>(in this)), 0);

    public static bool operator ==(Inline8<T> left, Inline8<T> right) => left.Equals(right);

    public static bool operator !=(Inline8<T> left, Inline8<T> right) => !left.Equals(right);
}

/// <summary>Inline storage for 16 elements (see <see cref="BufferComponent{TElement, TInline}"/>).</summary>
[InlineArray(16)]
public struct Inline16<T> : IEquatable<Inline16<T>> where T : unmanaged
{
    private T _element0;

    public readonly bool Equals(Inline16<T> other) => MemoryMarshal.AsBytes(new ReadOnlySpan<Inline16<T>>(in this)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Inline16<T>>(in other)));

    public override readonly bool Equals(object? obj) => obj is Inline16<T> other && Equals(other);

    public override readonly int GetHashCode() => (int)Hashing.Hash64.Compute(MemoryMarshal.AsBytes(new ReadOnlySpan<Inline16<T>>(in this)), 0);

    public static bool operator ==(Inline16<T> left, Inline16<T> right) => left.Equals(right);

    public static bool operator !=(Inline16<T> left, Inline16<T> right) => !left.Equals(right);
}

/// <summary>Inline storage for 32 elements (see <see cref="BufferComponent{TElement, TInline}"/>).</summary>
[InlineArray(32)]
public struct Inline32<T> : IEquatable<Inline32<T>> where T : unmanaged
{
    private T _element0;

    public readonly bool Equals(Inline32<T> other) => MemoryMarshal.AsBytes(new ReadOnlySpan<Inline32<T>>(in this)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Inline32<T>>(in other)));

    public override readonly bool Equals(object? obj) => obj is Inline32<T> other && Equals(other);

    public override readonly int GetHashCode() => (int)Hashing.Hash64.Compute(MemoryMarshal.AsBytes(new ReadOnlySpan<Inline32<T>>(in this)), 0);

    public static bool operator ==(Inline32<T> left, Inline32<T> right) => left.Equals(right);

    public static bool operator !=(Inline32<T> left, Inline32<T> right) => !left.Equals(right);
}
