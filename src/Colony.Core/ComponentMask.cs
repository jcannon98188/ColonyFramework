using System.Numerics;
using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// Set of <see cref="ComponentType"/>s as a 256-bit mask; an archetype signature or a query filter.
/// </summary>
public readonly struct ComponentMask : IEquatable<ComponentMask>
{
    /// <summary>Largest number of fragmenting Components one schema may register.</summary>
    public const int Capacity = 256;

    private readonly ulong _w0;
    private readonly ulong _w1;
    private readonly ulong _w2;
    private readonly ulong _w3;

    private ComponentMask(ulong w0, ulong w1, ulong w2, ulong w3)
    {
        _w0 = w0;
        _w1 = w1;
        _w2 = w2;
        _w3 = w3;
    }

    public static ComponentMask Empty => default;

    public bool IsEmpty => (_w0 | _w1 | _w2 | _w3) == 0;

    public int Count => BitOperations.PopCount(_w0) + BitOperations.PopCount(_w1) + BitOperations.PopCount(_w2) + BitOperations.PopCount(_w3);

    public static ComponentMask Of(params ReadOnlySpan<ComponentType> types)
    {
        var mask = Empty;
        foreach (var type in types)
        {
            mask = mask.With(type);
        }

        return mask;
    }

    public bool Contains(ComponentType type)
    {
        int id = type.Id;
        ValidateId(id);
        return (Word(id >> 6) & (1UL << (id & 63))) != 0;
    }

    public ComponentMask With(ComponentType type)
    {
        int id = type.Id;
        ValidateId(id);
        ulong bit = 1UL << (id & 63);
        return (id >> 6) switch
        {
            0 => new ComponentMask(_w0 | bit, _w1, _w2, _w3),
            1 => new ComponentMask(_w0, _w1 | bit, _w2, _w3),
            2 => new ComponentMask(_w0, _w1, _w2 | bit, _w3),
            _ => new ComponentMask(_w0, _w1, _w2, _w3 | bit),
        };
    }

    public ComponentMask Without(ComponentType type)
    {
        int id = type.Id;
        ValidateId(id);
        ulong bit = ~(1UL << (id & 63));
        return (id >> 6) switch
        {
            0 => new ComponentMask(_w0 & bit, _w1, _w2, _w3),
            1 => new ComponentMask(_w0, _w1 & bit, _w2, _w3),
            2 => new ComponentMask(_w0, _w1, _w2 & bit, _w3),
            _ => new ComponentMask(_w0, _w1, _w2, _w3 & bit),
        };
    }

    /// <summary>True when every Component in <paramref name="other"/> is also in this mask.</summary>
    public bool ContainsAll(ComponentMask other)
        => (_w0 & other._w0) == other._w0 && (_w1 & other._w1) == other._w1
        && (_w2 & other._w2) == other._w2 && (_w3 & other._w3) == other._w3;

    /// <summary>True when no Component in <paramref name="other"/> is in this mask.</summary>
    public bool ContainsNone(ComponentMask other)
        => ((_w0 & other._w0) | (_w1 & other._w1) | (_w2 & other._w2) | (_w3 & other._w3)) == 0;

    /// <summary>Enumerates set ids in ascending order; <paramref name="ids"/> must hold <see cref="Count"/> entries.</summary>
    internal int CopyIds(Span<int> ids)
    {
        int n = 0;
        n = CopyWord(_w0, 0, ids, n);
        n = CopyWord(_w1, 64, ids, n);
        n = CopyWord(_w2, 128, ids, n);
        return CopyWord(_w3, 192, ids, n);
    }

    private static int CopyWord(ulong word, int offset, Span<int> ids, int n)
    {
        while (word != 0)
        {
            int bit = BitOperations.TrailingZeroCount(word);
            ids[n++] = offset + bit;
            word &= word - 1;
        }

        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong Word(int w) => w switch { 0 => _w0, 1 => _w1, 2 => _w2, _ => _w3 };

    private static void ValidateId(int id)
    {
        if ((uint)id >= Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Component id is outside the mask capacity.");
        }
    }

    public bool Equals(ComponentMask other) => _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

    public override bool Equals(object? obj) => obj is ComponentMask other && Equals(other);

    public override int GetHashCode()
    {
        ulong h = Fnv1a.Hash(_w0);
        h = Fnv1a.Hash(_w1, h);
        h = Fnv1a.Hash(_w2, h);
        h = Fnv1a.Hash(_w3, h);
        return (int)h ^ (int)(h >> 32);
    }

    public static bool operator ==(ComponentMask left, ComponentMask right) => left.Equals(right);

    public static bool operator !=(ComponentMask left, ComponentMask right) => !left.Equals(right);
}
