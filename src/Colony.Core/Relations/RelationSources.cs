namespace Colony;

/// <summary>
/// The sources currently related to one target in one Relation (<see cref="World.SourcesOf"/>), in
/// ascending Entity slot order. Valid until the next structural change. Allocates nothing:
/// <c>foreach (EntityId source in world.SourcesOf(target, relation))</c>.
/// </summary>
public readonly ref struct RelationSources
{
    private readonly RelationIndex? _index;
    private readonly int _relation;
    private readonly int _head;

    internal RelationSources(RelationIndex index, int relation, int target)
    {
        _index = index;
        _relation = relation;
        _head = index.Head(relation, target);
        Count = index.Count(relation, target);
    }

    /// <summary>Number of sources.</summary>
    public int Count { get; }

    public bool IsEmpty => Count == 0;

    public Enumerator GetEnumerator() => new(_index, _relation, _head);

    /// <summary>Copies the sources in order into <paramref name="destination"/>; returns how many were written (at most its length).</summary>
    public int CopyTo(Span<EntityId> destination)
    {
        int written = 0;
        foreach (var source in this)
        {
            if (written == destination.Length)
            {
                break;
            }

            destination[written++] = source;
        }

        return written;
    }

    public ref struct Enumerator
    {
        private readonly RelationIndex? _index;
        private readonly int _relation;
        private int _next;
        private EntityId _current;

        internal Enumerator(RelationIndex? index, int relation, int head)
        {
            _index = index;
            _relation = relation;
            _next = head;
        }

        public readonly EntityId Current => _current;

        public bool MoveNext()
        {
            if (_next < 0 || _index is null)
            {
                return false;
            }

            _current = _index.IdOf(_relation, _next);
            _next = _index.Next(_relation, _next);
            return true;
        }
    }
}
