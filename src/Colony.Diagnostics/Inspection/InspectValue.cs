using System.Globalization;

namespace Colony.Diagnostics.Inspection;

/// <summary>Kind of an <see cref="InspectValue"/>; the byte is the binary tag of the inspection protocol.</summary>
public enum InspectValueKind : byte
{
    Null = 0,
    Boolean = 1,
    Whole = 2,
    Number = 3,
    Text = 4,
    Bytes = 5,
    List = 6,
    Map = 7,
}

/// <summary>
/// One value of the inspection protocol (spec section 10.2): the tree every request and response is made of.
/// It has a compact binary form (<see cref="InspectCodec"/>) used on the wire and a JSON mapping
/// (<see cref="InspectJson"/>) for people and scripts. Map entries keep their insertion order, so both forms
/// are deterministic. Immutable once built.
/// </summary>
public sealed class InspectValue : IEquatable<InspectValue>
{
    private static readonly InspectValue[] NoItems = [];
    private static readonly KeyValuePair<string, InspectValue>[] NoEntries = [];

    private readonly long _whole;
    private double _number;
    private string? _text;
    private byte[]? _bytes;
    private InspectValue[] _items = NoItems;
    private KeyValuePair<string, InspectValue>[] _entries = NoEntries;

    private InspectValue(InspectValueKind kind) => Kind = kind;

    private InspectValue(long whole, InspectValueKind kind)
        : this(kind) => _whole = whole;

    public static InspectValue Null { get; } = new(InspectValueKind.Null);

    public static InspectValue True { get; } = new(1, InspectValueKind.Boolean);

    public static InspectValue False { get; } = new(0, InspectValueKind.Boolean);

    public InspectValueKind Kind { get; }

    public bool IsNull => Kind == InspectValueKind.Null;

    public static InspectValue From(bool value) => value ? True : False;

    public static InspectValue From(long value) => new(value, InspectValueKind.Whole);

    public static InspectValue From(ulong value) => new(unchecked((long)value), InspectValueKind.Whole);

    public static InspectValue From(double value) => new(InspectValueKind.Number) { _number = value };

    public static InspectValue From(string? value) => value is null ? Null : new(InspectValueKind.Text) { _text = value };

    public static InspectValue From(ReadOnlySpan<byte> value) => new(InspectValueKind.Bytes) { _bytes = value.ToArray() };

    public static InspectValue List(params ReadOnlySpan<InspectValue> items) => new(InspectValueKind.List) { _items = items.ToArray() };

    public static InspectValue List(IEnumerable<InspectValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(InspectValueKind.List) { _items = [.. items] };
    }

    public static InspectValue Map(params ReadOnlySpan<KeyValuePair<string, InspectValue>> entries)
    {
        var copy = entries.ToArray();
        for (int i = 0; i < copy.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(copy[i].Key, nameof(entries));
            ArgumentNullException.ThrowIfNull(copy[i].Value, nameof(entries));
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(copy[i].Key, copy[j].Key, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Duplicate map key '{copy[i].Key}'.", nameof(entries));
                }
            }
        }

        return new(InspectValueKind.Map) { _entries = copy };
    }

    public static InspectValue Map(IEnumerable<KeyValuePair<string, InspectValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Map([.. entries]);
    }

    public static implicit operator InspectValue(long value) => From(value);

    public static implicit operator InspectValue(string? value) => From(value);

    public static implicit operator InspectValue(bool value) => From(value);

    public static implicit operator InspectValue(double value) => From(value);

    public bool AsBoolean => Kind == InspectValueKind.Boolean ? _whole != 0 : throw Mismatch(InspectValueKind.Boolean);

    /// <summary>The integer; a <see cref="InspectValueKind.Number"/> with an integral value is accepted too (JSON clients).</summary>
    public long AsWhole => Kind switch
    {
        InspectValueKind.Whole => _whole,
        InspectValueKind.Number when _number == Math.Floor(_number) && Math.Abs(_number) < 9.0e15 => (long)_number,
        _ => throw Mismatch(InspectValueKind.Whole),
    };

    public double AsNumber => Kind switch
    {
        InspectValueKind.Number => _number,
        InspectValueKind.Whole => _whole,
        _ => throw Mismatch(InspectValueKind.Number),
    };

    public string AsText => Kind == InspectValueKind.Text ? _text! : throw Mismatch(InspectValueKind.Text);

    public ReadOnlySpan<byte> AsBytes => Kind == InspectValueKind.Bytes ? _bytes : throw Mismatch(InspectValueKind.Bytes);

    public IReadOnlyList<InspectValue> Items => Kind == InspectValueKind.List ? _items : throw Mismatch(InspectValueKind.List);

    public IReadOnlyList<KeyValuePair<string, InspectValue>> Entries => Kind == InspectValueKind.Map ? _entries : throw Mismatch(InspectValueKind.Map);

    /// <summary>The value under <paramref name="key"/> of a map, or <see cref="Null"/> when absent.</summary>
    public InspectValue this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            foreach (var entry in Entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    return entry.Value;
                }
            }

            return Null;
        }
    }

    public InspectValue this[int index] => Items[index];

    public bool Has(string key) => !this[key].IsNull;

    /// <summary>A copy of this map with <paramref name="key"/> set (replaced in place, or appended).</summary>
    public InspectValue With(string key, InspectValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        var entries = new List<KeyValuePair<string, InspectValue>>(Entries);
        int at = entries.FindIndex(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (at >= 0)
        {
            entries[at] = new(key, value);
        }
        else
        {
            entries.Add(new(key, value));
        }

        return new(InspectValueKind.Map) { _entries = [.. entries] };
    }

    public bool Equals(InspectValue? other)
    {
        if (other is null || other.Kind != Kind)
        {
            return false;
        }

        switch (Kind)
        {
            case InspectValueKind.Null:
                return true;
            case InspectValueKind.Boolean:
            case InspectValueKind.Whole:
                return _whole == other._whole;
            case InspectValueKind.Number:
                return BitConverter.DoubleToInt64Bits(_number) == BitConverter.DoubleToInt64Bits(other._number);
            case InspectValueKind.Text:
                return string.Equals(_text, other._text, StringComparison.Ordinal);
            case InspectValueKind.Bytes:
                return _bytes.AsSpan().SequenceEqual(other._bytes);
            case InspectValueKind.List:
                return _items.Length == other._items.Length && _items.Zip(other._items).All(p => p.First.Equals(p.Second));
            default:
                return _entries.Length == other._entries.Length
                    && _entries.Zip(other._entries).All(p => string.Equals(p.First.Key, p.Second.Key, StringComparison.Ordinal) && p.First.Value.Equals(p.Second.Value));
        }
    }

    public override bool Equals(object? obj) => obj is InspectValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, _whole);

    public override string ToString() => InspectJson.Write(this);

    private InvalidOperationException Mismatch(InspectValueKind wanted)
        => new(string.Create(CultureInfo.InvariantCulture, $"Expected a {wanted} value, found {Kind}."));
}
