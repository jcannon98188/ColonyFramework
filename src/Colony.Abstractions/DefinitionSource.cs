namespace Colony.Plugins;

/// <summary>
/// Where Definitions come from (spec section 8): a file format, a database, code. A source yields plain
/// <see cref="DefinitionDocument"/>s; the Definition registry in Colony.Plugins binds them to the registered
/// unmanaged Definition types. Sources run at World build only, never during Ticks.
/// </summary>
public interface IDefinitionSource
{
    /// <summary>Human-readable origin for error messages (a file name, "code").</summary>
    public string Name { get; }

    /// <summary>Every Definition the source holds, in the source's own order (documents of a type are applied in this order).</summary>
    public IEnumerable<DefinitionDocument> Read();
}

/// <summary>One Definition as data: the Definition type's registered name, the Definition's own name and its fields. A document for a name that already exists patches that Definition field by field.</summary>
public sealed record DefinitionDocument(string Type, string Name, IReadOnlyList<DefinitionField> Fields);

public readonly record struct DefinitionField(string Name, DefinitionValue Value);

public enum DefinitionValueKind
{
    Null = 0,
    /// <summary>An integral value held exactly in <see cref="DefinitionValue.Whole"/>.</summary>
    Whole = 1,
    Number = 2,
    Boolean = 3,
    Text = 4,
}

/// <summary>A scalar from a Definition source. Integers and numbers are kept apart so exact integer fields never pass through a double.</summary>
public readonly record struct DefinitionValue
{
    private DefinitionValue(DefinitionValueKind kind, long whole, double number, string? text)
    {
        Kind = kind;
        Whole = whole;
        Number = number;
        Text = text;
    }

    public DefinitionValueKind Kind { get; }

    /// <summary>The value for <see cref="DefinitionValueKind.Whole"/>; also set for <see cref="DefinitionValueKind.Boolean"/> (0/1).</summary>
    public long Whole { get; }

    /// <summary>The value for <see cref="DefinitionValueKind.Number"/>; for a Whole, its double conversion.</summary>
    public double Number { get; }

    public string? Text { get; }

    public static DefinitionValue Null => default;

    public static DefinitionValue FromWhole(long value) => new(DefinitionValueKind.Whole, value, value, null);

    public static DefinitionValue FromNumber(double value) => new(DefinitionValueKind.Number, 0, value, null);

    public static DefinitionValue FromBoolean(bool value) => new(DefinitionValueKind.Boolean, value ? 1 : 0, value ? 1 : 0, null);

    public static DefinitionValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(DefinitionValueKind.Text, 0, 0, value);
    }

    public override string ToString() => Kind switch
    {
        DefinitionValueKind.Whole => Whole.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DefinitionValueKind.Number => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DefinitionValueKind.Boolean => Whole != 0 ? "true" : "false",
        DefinitionValueKind.Text => Text ?? string.Empty,
        _ => "null",
    };
}
