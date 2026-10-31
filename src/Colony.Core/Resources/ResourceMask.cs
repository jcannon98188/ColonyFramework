using System.Numerics;
using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// Set of <see cref="ResourceType"/>s as a 256-bit mask: a System's declared Resource access
/// (<see cref="SystemDefinition.ReadsResources"/> and <see cref="SystemDefinition.WritesResources"/>).
/// </summary>
public readonly struct ResourceMask : IEquatable<ResourceMask>
{
    /// <summary>Largest number of Resources (values and event queues) one schema may register.</summary>
    public const int Capacity = 256;

    private readonly ulong _w0;
    private readonly ulong _w1;
    private readonly ulong _w2;
    private readonly ulong _w3;

    private ResourceMask(ulong w0, ulong w1, ulong w2, ulong w3)
    {
        _w0 = w0;
        _w1 = w1;
        _w2 = w2;
        _w3 = w3;
    }

    public static ResourceMask Empty => default;

    public bool IsEmpty => (_w0 | _w1 | _w2 | _w3) == 0;

    public int Count => BitOperations.PopCount(_w0) + BitOperations.PopCount(_w1) + BitOperations.PopCount(_w2) + BitOperations.PopCount(_w3);

    public static ResourceMask Of(params ReadOnlySpan<ResourceType> types)
    {
        var mask = Empty;
        foreach (var type in types)
        {
            mask = mask.With(type);
        }

        return mask;
    }

    public bool Contains(ResourceType type)
    {
        int id = type.Id;
        ValidateId(id);
        return (Word(id >> 6) & (1UL << (id & 63))) != 0;
    }

    public ResourceMask With(ResourceType type)
    {
        int id = type.Id;
        ValidateId(id);
        ulong bit = 1UL << (id & 63);
        return (id >> 6) switch
        {
            0 => new ResourceMask(_w0 | bit, _w1, _w2, _w3),
            1 => new ResourceMask(_w0, _w1 | bit, _w2, _w3),
            2 => new ResourceMask(_w0, _w1, _w2 | bit, _w3),
            _ => new ResourceMask(_w0, _w1, _w2, _w3 | bit),
        };
    }

    /// <summary>True when at least one Resource is in both masks.</summary>
    public bool Intersects(ResourceMask other)
        => ((_w0 & other._w0) | (_w1 & other._w1) | (_w2 & other._w2) | (_w3 & other._w3)) != 0;

    public ResourceMask Union(ResourceMask other) => new(_w0 | other._w0, _w1 | other._w1, _w2 | other._w2, _w3 | other._w3);

    public ResourceMask Intersect(ResourceMask other) => new(_w0 & other._w0, _w1 & other._w1, _w2 & other._w2, _w3 & other._w3);

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
            throw new ArgumentOutOfRangeException(nameof(id), id, "Resource id is outside the mask capacity.");
        }
    }

    public bool Equals(ResourceMask other) => _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

    public override bool Equals(object? obj) => obj is ResourceMask other && Equals(other);

    public override int GetHashCode()
    {
        ulong h = Hashing.Hash64.Combine(_w0, _w1);
        h = Hashing.Hash64.Combine(h, _w2);
        h = Hashing.Hash64.Combine(h, _w3);
        return (int)h ^ (int)(h >> 32);
    }

    public static bool operator ==(ResourceMask left, ResourceMask right) => left.Equals(right);

    public static bool operator !=(ResourceMask left, ResourceMask right) => !left.Equals(right);
}
