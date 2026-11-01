using System.Buffers.Binary;
using Colony.Numerics;

namespace Colony.Plugins;

/// <summary>The scalar encodings a raw (layout-only) Definition field can hold; the values are <c>COLONY_FIELD_*</c> of <c>colony.h</c>.</summary>
internal enum RawFieldKind
{
    Int32 = 1,
    Int64 = 2,
    UInt32 = 3,
    UInt8 = 4,
    Bool = 5,
    Float32 = 6,
    Float64 = 7,
    StringId = 8,
    Fixed32 = 9,
    Fixed64 = 10,
}

/// <summary>
/// One field of a Definition type registered by layout (TASK-28): its source name, encoding and byte offset.
/// Values convert exactly as <see cref="DefinitionLayout{T}"/> converts them for a C# struct field of the same
/// type, so a raw type and a C# type with the same layout bake identical bytes from the same source.
/// </summary>
internal sealed record RawDefinitionField(string Name, RawFieldKind Kind, int Offset)
{
    public static int SizeOf(RawFieldKind kind) => kind switch
    {
        RawFieldKind.Int32 or RawFieldKind.UInt32 or RawFieldKind.Float32 or RawFieldKind.StringId or RawFieldKind.Fixed32 => 4,
        RawFieldKind.Int64 or RawFieldKind.Float64 or RawFieldKind.Fixed64 => 8,
        RawFieldKind.UInt8 or RawFieldKind.Bool => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Definition field kind."),
    };

    public void Write(Span<byte> target, DefinitionValue value, DefinitionRegistry registry, string context, string pluginId)
    {
        switch (Kind)
        {
            case RawFieldKind.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(target, checked((int)Integer(value, context, pluginId)));
                break;
            case RawFieldKind.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(target, Integer(value, context, pluginId));
                break;
            case RawFieldKind.UInt32:
                BinaryPrimitives.WriteUInt32LittleEndian(target, checked((uint)Integer(value, context, pluginId)));
                break;
            case RawFieldKind.UInt8:
                target[0] = checked((byte)Integer(value, context, pluginId));
                break;
            case RawFieldKind.Bool:
                target[0] = value.Kind == DefinitionValueKind.Boolean
                    ? (byte)(value.Whole != 0 ? 1 : 0)
                    : throw Mismatch("a boolean", value, context, pluginId);
                break;
            case RawFieldKind.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(target, (float)Number(value, context, pluginId));
                break;
            case RawFieldKind.Float64:
                BinaryPrimitives.WriteDoubleLittleEndian(target, Number(value, context, pluginId));
                break;
            case RawFieldKind.Fixed32:
                BinaryPrimitives.WriteInt32LittleEndian(target, Fixed32.FromDouble(Number(value, context, pluginId)).Raw);
                break;
            case RawFieldKind.Fixed64:
                BinaryPrimitives.WriteInt64LittleEndian(target, Fixed64.FromDouble(Number(value, context, pluginId)).Raw);
                break;
            case RawFieldKind.StringId:
                uint id = value.Kind switch
                {
                    DefinitionValueKind.Null => StringId.Null.Value,
                    DefinitionValueKind.Text => registry.Strings.Intern(value.Text!).Value,
                    _ => throw Mismatch("text", value, context, pluginId),
                };
                BinaryPrimitives.WriteUInt32LittleEndian(target, id);
                break;
            default:
                throw new PluginException(pluginId, $"{context}: field '{Name}' has unknown kind {Kind}.");
        }
    }

    private long Integer(DefinitionValue value, string context, string pluginId)
    {
        if (value.Kind == DefinitionValueKind.Whole)
        {
            return value.Whole;
        }

        if (value.Kind == DefinitionValueKind.Number && Math.Floor(value.Number) == value.Number && Math.Abs(value.Number) < 9007199254740992.0)
        {
            return (long)value.Number;
        }

        throw Mismatch("an integer", value, context, pluginId);
    }

    private double Number(DefinitionValue value, string context, string pluginId)
        => value.Kind is DefinitionValueKind.Whole or DefinitionValueKind.Number
            ? value.Number
            : throw Mismatch("a number", value, context, pluginId);

    private PluginException Mismatch(string expected, DefinitionValue value, string context, string pluginId)
        => new(pluginId, $"{context} sets field '{Name}' to {value.Kind.ToString().ToUpperInvariant()} '{value}'; it takes {expected}.");
}
