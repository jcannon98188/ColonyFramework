using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// Which bytes of an unmanaged value hold data and which are padding (spec section 6: the World hash excludes
/// padding). Padding is not reliably zero: the JIT may build a struct field by field and leave whatever its
/// register or stack slot held in the gaps, which differs between machines and runtimes (TASK-44, first seen on
/// Linux). Hashing, comparing and saving through the mask makes such values deterministic.
/// </summary>
public static class ValueLayout
{
    /// <summary>
    /// A mask of <typeparamref name="T"/>'s bytes, 0xFF for data and 0x00 for padding, or null when every byte is
    /// data or the layout cannot be measured safely. Measured once per call (schema build time, not a Tick path) by
    /// flipping each byte of a zero value and asking the runtime's field-wise <see cref="ValueType.Equals(object)"/>
    /// whether the value changed, so nested structs are covered. Conservative: a type with a fixed buffer, an inline
    /// array, an <c>Equals</c> override anywhere in it, or fields whose metadata was trimmed gets null (all data),
    /// never a mask that could hide data bytes.
    /// </summary>
    public static byte[]? DataMaskOf<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicMethods)] T>()
        where T : unmanaged
    {
        if (IsPrimitiveLike(typeof(T)) || !CanProbe(typeof(T), depth: 0))
        {
            return null;
        }

        int size = Unsafe.SizeOf<T>();
        var mask = new byte[size];
        object zero = default(T);
        try
        {
            for (int i = 0; i < size; i++)
            {
                T flipped = default;
                Unsafe.Add(ref Unsafe.As<T, byte>(ref flipped), i) = 1;
                mask[i] = zero.Equals(flipped) ? (byte)0x00 : (byte)0xFF;
            }
        }
        catch (NotSupportedException)
        {
            return null; // the runtime refuses field-wise Equals (inline arrays): treat every byte as data
        }

        return mask.AsSpan().ContainsAnyExcept((byte)0xFF) ? mask : null;
    }

    /// <summary>Zeroes the padding bytes of <paramref name="values"/>, a whole number of values laid out back to back, under a mask from <see cref="DataMaskOf{T}"/>.</summary>
    public static void ZeroPadding(ReadOnlySpan<byte> mask, Span<byte> values)
    {
        if (mask.IsEmpty)
        {
            return;
        }

        if (values.Length % mask.Length != 0)
        {
            throw new ArgumentException($"{values.Length} bytes are not a whole number of {mask.Length}-byte values.", nameof(values));
        }

        for (int offset = 0; offset < values.Length; offset += mask.Length)
        {
            var value = values.Slice(offset, mask.Length);
            for (int i = 0; i < mask.Length; i++)
            {
                value[i] &= mask[i];
            }
        }
    }

    /// <summary>True when <paramref name="left"/> and <paramref name="right"/> (values back to back) are equal outside their padding.</summary>
    public static bool DataEqual(ReadOnlySpan<byte> mask, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (mask.IsEmpty || left.Length != right.Length)
        {
            return left.SequenceEqual(right);
        }

        if (left.Length % mask.Length != 0)
        {
            throw new ArgumentException($"{left.Length} bytes are not a whole number of {mask.Length}-byte values.", nameof(left));
        }

        for (int offset = 0; offset < left.Length; offset += mask.Length)
        {
            for (int i = 0; i < mask.Length; i++)
            {
                if (((left[offset + i] ^ right[offset + i]) & mask[i]) != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsPrimitiveLike(Type type) => type.IsPrimitive || type.IsEnum || type.IsPointer || type.IsFunctionPointer;

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Nested field types are not annotated; trimmed metadata shows as no fields, which falls back to no mask (every byte data).")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "As above: missing metadata only ever makes the result more conservative.")]
    private static bool CanProbe(Type type, int depth)
    {
        if (IsPrimitiveLike(type))
        {
            return true;
        }

        if (depth > 16 || !type.IsValueType || type.IsDefined(typeof(InlineArrayAttribute), inherit: false))
        {
            return false;
        }

        // An Equals override would answer instead of the runtime's field-wise comparison.
        var equals = type.GetMethod(nameof(Equals), BindingFlags.Public | BindingFlags.Instance, [typeof(object)]);
        if (equals is null || equals.DeclaringType != typeof(ValueType))
        {
            return false;
        }

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fields.Length == 0)
        {
            return false;
        }

        foreach (var field in fields)
        {
            // Field-wise Equals compares only the first element of a fixed buffer, so the rest would look like padding.
            if (field.IsDefined(typeof(FixedBufferAttribute), inherit: false) || !CanProbe(field.FieldType, depth + 1))
            {
                return false;
            }
        }

        return true;
    }
}
