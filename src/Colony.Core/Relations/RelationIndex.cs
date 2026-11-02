namespace Colony;

/// <summary>
/// The built-in reverse index of every Relation (spec section 4.1): per Relation and per target Entity slot,
/// an intrusive doubly linked list of the source slots kept in ascending slot order, so
/// <see cref="World.SourcesOf"/> enumerates sources in a deterministic order whatever the insertion order.
/// Maintained inline by the World's structural operations (which run only at sync points), sized to the
/// Entity index, never hashed; <see cref="World.RebuildRelationIndex"/> recreates it from the Components.
/// </summary>
internal sealed class RelationIndex
{
    private const int None = -1;

    private readonly int _relationCount;
    private readonly int[][] _head;
    private readonly int[][] _count;
    private readonly int[][] _next;
    private readonly int[][] _prev;
    private readonly EntityId[][] _ids;
    private int _capacity;

    public RelationIndex(int relationCount, int initialCapacity)
    {
        _relationCount = relationCount;
        _head = new int[relationCount][];
        _count = new int[relationCount][];
        _next = new int[relationCount][];
        _prev = new int[relationCount][];
        _ids = new EntityId[relationCount][];
        for (int r = 0; r < relationCount; r++)
        {
            _head[r] = [];
            _count[r] = [];
            _next[r] = [];
            _prev[r] = [];
            _ids[r] = [];
        }

        EnsureCapacity(initialCapacity);
    }

    public int Capacity => _capacity;

    /// <summary>Grows every per-slot array to hold <paramref name="entityCapacity"/> slots; new slots are empty.</summary>
    public void EnsureCapacity(int entityCapacity)
    {
        if (entityCapacity <= _capacity)
        {
            return;
        }

        int capacity = Math.Max(64, _capacity);
        while (capacity < entityCapacity)
        {
            capacity *= 2;
        }

        for (int r = 0; r < _relationCount; r++)
        {
            Array.Resize(ref _head[r], capacity);
            Array.Fill(_head[r], None, _capacity, capacity - _capacity);
            Array.Resize(ref _count[r], capacity);
            Array.Resize(ref _next[r], capacity);
            Array.Resize(ref _prev[r], capacity);
            Array.Resize(ref _ids[r], capacity);
        }

        _capacity = capacity;
    }

    /// <summary>First source slot of <paramref name="target"/>, or -1.</summary>
    public int Head(int relation, int target) => _head[relation][target];

    public int Count(int relation, int target) => _count[relation][target];

    public int Next(int relation, int source) => _next[relation][source];

    public EntityId IdOf(int relation, int source) => _ids[relation][source];

    /// <summary>Inserts <paramref name="source"/> into the target's list, keeping ascending slot order. The source must not be linked in this Relation.</summary>
    public void Link(int relation, EntityId source, int target)
    {
        int slot = source.Index;
        int[] next = _next[relation];
        int[] prev = _prev[relation];
        _ids[relation][slot] = source;
        _count[relation][target]++;

        int after = None;
        int cursor = _head[relation][target];
        while (cursor != None && cursor < slot)
        {
            after = cursor;
            cursor = next[cursor];
        }

        next[slot] = cursor;
        prev[slot] = after;
        if (after == None)
        {
            _head[relation][target] = slot;
        }
        else
        {
            next[after] = slot;
        }

        if (cursor != None)
        {
            prev[cursor] = slot;
        }
    }

    /// <summary>Removes <paramref name="source"/> from the target's list.</summary>
    public void Unlink(int relation, int source, int target)
    {
        int[] next = _next[relation];
        int[] prev = _prev[relation];
        int before = prev[source];
        int after = next[source];
        if (before == None)
        {
            _head[relation][target] = after;
        }
        else
        {
            next[before] = after;
        }

        if (after != None)
        {
            prev[after] = before;
        }

        _count[relation][target]--;
        _ids[relation][source] = EntityId.Null;
    }

    /// <summary>Empties every list (for a rebuild).</summary>
    public void Clear()
    {
        for (int r = 0; r < _relationCount; r++)
        {
            Array.Fill(_head[r], None);
            Array.Clear(_count[r]);
            Array.Clear(_ids[r]);
        }
    }
}
