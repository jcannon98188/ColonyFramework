using System.Buffers.Binary;
using Colony.Numerics;

namespace Colony.Diagnostics.Inspection;

/// <summary>
/// Turns raw Component bytes into named values ("data, not pixels", spec section 10.2) using the field layout the
/// source generator records (<see cref="ComponentDescriptor.Fields"/>). Fields of a type it does not know, and
/// Components registered without a layout, stay raw bytes.
/// </summary>
public static class ComponentValues
{
    /// <summary>A map of field name to value, or null when the Component has no field layout.</summary>
    public static InspectValue? Decode(in ComponentDescriptor component, ReadOnlySpan<byte> bytes)
    {
        var fields = component.Fields;
        if (fields.IsDefaultOrEmpty)
        {
            return null;
        }

        var entries = new List<KeyValuePair<string, InspectValue>>(fields.Length);
        foreach (var field in fields)
        {
            if (field.Offset < 0 || field.Offset + field.Size > bytes.Length)
            {
                continue;
            }

            entries.Add(new(field.Name, DecodeScalar(field.TypeName, bytes.Slice(field.Offset, field.Size))));
        }

        return InspectValue.Map(entries);
    }

    /// <summary>One field by its stable type name (as the generator writes it: C# keyword or full name).</summary>
    public static InspectValue DecodeScalar(string typeName, ReadOnlySpan<byte> b)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return (typeName, b.Length) switch
        {
            ("int" or "System.Int32", 4) => InspectValue.From(BinaryPrimitives.ReadInt32LittleEndian(b)),
            ("uint" or "System.UInt32", 4) => InspectValue.From(BinaryPrimitives.ReadUInt32LittleEndian(b)),
            ("long" or "System.Int64", 8) => InspectValue.From(BinaryPrimitives.ReadInt64LittleEndian(b)),
            ("ulong" or "System.UInt64", 8) => InspectValue.From(BinaryPrimitives.ReadUInt64LittleEndian(b)),
            ("short" or "System.Int16", 2) => InspectValue.From(BinaryPrimitives.ReadInt16LittleEndian(b)),
            ("ushort" or "System.UInt16", 2) => InspectValue.From(BinaryPrimitives.ReadUInt16LittleEndian(b)),
            ("byte" or "System.Byte", 1) => InspectValue.From(b[0]),
            ("sbyte" or "System.SByte", 1) => InspectValue.From((sbyte)b[0]),
            ("bool" or "System.Boolean", 1) => InspectValue.From(b[0] != 0),
            ("float" or "System.Single", 4) => InspectValue.From(BinaryPrimitives.ReadSingleLittleEndian(b)),
            ("double" or "System.Double", 8) => InspectValue.From(BinaryPrimitives.ReadDoubleLittleEndian(b)),
            ("Colony.Numerics.Fixed32", 4) => InspectValue.From(Fixed32.FromRaw(BinaryPrimitives.ReadInt32LittleEndian(b)).ToDouble()),
            ("Colony.Numerics.Fixed64", 8) => InspectValue.From(Fixed64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(b)).ToDouble()),
            ("Colony.EntityId", 8) => InspectValue.From(BinaryPrimitives.ReadUInt64LittleEndian(b)),
            _ => InspectValue.From(b),
        };
    }
}
