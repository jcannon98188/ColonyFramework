using System.Collections;
using System.Collections.Immutable;

namespace Colony.Generators;

/// <summary>
/// An immutable array with sequence equality, so generator models that hold lists stay equatable and the
/// incremental pipeline can skip unchanged steps.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T> where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(ImmutableArray<T> items)
    {
        _items = items.IsDefault ? ImmutableArray<T>.Empty : items;
    }

    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    public int Count => _items.IsDefault ? 0 : _items.Length;

    public T this[int index] => _items[index];

    public ImmutableArray<T> AsImmutableArray() => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public bool Equals(EquatableArray<T> other)
    {
        var left = AsImmutableArray();
        var right = other.AsImmutableArray();
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (var item in AsImmutableArray())
        {
            hash = unchecked((hash * 31) + (item?.GetHashCode() ?? 0));
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}

internal static class EquatableArrayExtensions
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items) where T : IEquatable<T> => new(items.ToImmutableArray());
}
