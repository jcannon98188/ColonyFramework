namespace Colony;

/// <summary>
/// Allocation-free enumeration of the chunks of every archetype matching an all/none filter, in
/// archetype id then chunk order (the canonical order for hashing and job partitioning). Use with
/// <c>foreach</c>; the query is its own enumerator. Take it after a sync point and finish with it
/// before the next structural change.
/// </summary>
public ref struct ChunkQuery
{
    private readonly Archetype[] _archetypes;
    private readonly int _archetypeCount;
    private readonly ComponentMask _all;
    private readonly ComponentMask _none;
    private readonly bool _validate;
    private int _archetype;
    private int _chunk;

    internal ChunkQuery(Archetype[] archetypes, int archetypeCount, ComponentMask all, ComponentMask none, bool validate)
    {
        _archetypes = archetypes;
        _archetypeCount = archetypeCount;
        _all = all;
        _none = none;
        _validate = validate;
        _archetype = -1;
        _chunk = -1;
    }

    public readonly ChunkQuery GetEnumerator() => this;

    public readonly ChunkView Current => new(_archetypes[_archetype], _chunk, _validate);

    public bool MoveNext()
    {
        if (_archetype >= 0 && ++_chunk < _archetypes[_archetype].ChunkCount)
        {
            return true;
        }

        while (++_archetype < _archetypeCount)
        {
            var archetype = _archetypes[_archetype];
            if (archetype.ChunkCount > 0 && archetype.Signature.ContainsAll(_all) && archetype.Signature.ContainsNone(_none))
            {
                _chunk = 0;
                return true;
            }
        }

        return false;
    }

    /// <summary>Total live rows across the matching chunks, without enumerating columns.</summary>
    public readonly int EntityCount()
    {
        int total = 0;
        for (int i = 0; i < _archetypeCount; i++)
        {
            var archetype = _archetypes[i];
            if (archetype.Signature.ContainsAll(_all) && archetype.Signature.ContainsNone(_none))
            {
                total += archetype.EntityCount;
            }
        }

        return total;
    }
}
