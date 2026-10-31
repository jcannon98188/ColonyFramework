namespace Colony;

/// <summary>
/// Allocation-free enumeration of the rows of one chunk on which every listed Component is enabled
/// (spec section 4.1), in ascending row order. Obtained from <see cref="ChunkView.EnabledRows(ComponentMask)"/>;
/// it is its own enumerator. Components in the mask that the chunk does not have are ignored.
/// </summary>
public ref struct EnabledRowEnumerator
{
    private readonly Archetype _archetype;
    private readonly nint _chunk;
    private readonly ComponentMask _components;
    private readonly int _count;
    private int _word;
    private ulong _bits;

    internal EnabledRowEnumerator(Archetype archetype, nint chunk, ComponentMask components, int count)
    {
        _archetype = archetype;
        _chunk = chunk;
        _components = components;
        _count = count;
        _word = -1;
        _bits = 0;
        Current = -1;
    }

    public readonly EnabledRowEnumerator GetEnumerator() => this;

    /// <summary>The current row.</summary>
    public int Current { get; private set; }

    public bool MoveNext()
    {
        while (_bits == 0)
        {
            _word++;
            if (_word >= Archetype.EnabledWords(_count))
            {
                return false;
            }

            _bits = Load(_word);
        }

        int bit = Archetype.TrailingZeroCount(_bits);
        _bits &= _bits - 1;
        Current = (_word << 6) + bit;
        return true;
    }

    private unsafe ulong Load(int word)
    {
        ulong acc = ulong.MaxValue;
        var ids = _archetype.ComponentIds;
        var offsets = _archetype.EnabledOffsets;
        for (int i = 0; i < ids.Length; i++)
        {
            if (_components.Contains(new ComponentType(ids[i])))
            {
                acc &= ((ulong*)((byte*)_chunk + offsets[i]))[word];
            }
        }

        // Bits past the live rows are zero in every bitset, but a mask with no column present would be all ones.
        int remaining = _count - (word << 6);
        return remaining >= 64 ? acc : acc & ((1UL << remaining) - 1);
    }
}
