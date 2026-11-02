namespace Colony;

/// <summary>
/// Allocation-free enumeration of the chunks of every archetype matching an all/none filter, in
/// archetype id then chunk order (the canonical order for hashing and job partitioning). Use with
/// <c>foreach</c>; the query is its own enumerator. Take it after a sync point and finish with it
/// before the next structural change. Chunks in which a queried Component is disabled on every row are
/// skipped unless the query ignores enable bits (spec section 4.1).
/// </summary>
public ref struct ChunkQuery
{
    private readonly Archetype[] _archetypes;
    private readonly int _archetypeCount;
    private readonly ComponentMask _all;
    private readonly ComponentMask _none;
    private readonly bool _validate;
    private readonly bool _ignoreEnableBits;
    private readonly World? _host;
    private int _archetype;
    private int _chunk;

    internal ChunkQuery(Archetype[] archetypes, int archetypeCount, ComponentMask all, ComponentMask none, bool validate, bool ignoreEnableBits, World? host = null)
    {
        _archetypes = archetypes;
        _archetypeCount = archetypeCount;
        _all = all;
        _none = none;
        _validate = validate;
        _ignoreEnableBits = ignoreEnableBits;
        _host = host;
        _archetype = -1;
        _chunk = -1;
    }

    public readonly ChunkQuery GetEnumerator() => this;

    public readonly ChunkView Current => new(_archetypes[_archetype], _chunk, _validate, _host);

    public bool MoveNext()
    {
        while (true)
        {
            if (_archetype >= 0 && ++_chunk < _archetypes[_archetype].ChunkCount)
            {
                if (_ignoreEnableBits || !_archetypes[_archetype].HasFullyDisabledColumn(_chunk, _all))
                {
                    return true;
                }

                continue;
            }

            if (++_archetype >= _archetypeCount)
            {
                return false;
            }

            var archetype = _archetypes[_archetype];
            _chunk = -1;
            if (!(archetype.ChunkCount > 0 && archetype.Signature.ContainsAll(_all) && archetype.Signature.ContainsNone(_none)))
            {
                _chunk = archetype.ChunkCount; // skip this archetype entirely
            }
        }
    }

    /// <summary>Total live rows across the matching archetypes (enable bits not considered), without enumerating columns.</summary>
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
