namespace Colony;

/// <summary>
/// World-level Entity index: maps an Entity slot to its archetype, chunk and row and holds the slot's
/// generation (spec section 4.1). Freed slots are reused LIFO with the generation bumped, so a stale
/// <see cref="EntityId"/> fails the generation check. A slot whose generation reaches the maximum is
/// retired rather than wrapped.
/// </summary>
internal sealed class EntityIndex
{
    private int[] _archetype;
    private int[] _chunk;
    private int[] _row;
    private uint[] _generation;
    private int[] _free;
    private int _freeCount;

    public EntityIndex(int initialCapacity)
    {
        int capacity = Math.Max(16, initialCapacity);
        _archetype = new int[capacity];
        _chunk = new int[capacity];
        _row = new int[capacity];
        _generation = new uint[capacity];
        _free = new int[Math.Max(16, capacity / 4)];
        Array.Fill(_archetype, -1);
    }

    /// <summary>Slots ever handed out; the non-fragmenting store sizes itself to this.</summary>
    public int HighWater { get; private set; }

    public int Capacity => _archetype.Length;

    public int AliveCount { get; private set; }

    public bool IsAlive(EntityId id)
    {
        int index = id.Index;
        return !id.IsDeferred && (uint)index < (uint)HighWater && _generation[index] == id.Generation && _archetype[index] >= 0;
    }

    /// <summary>True when the slot currently holds a live Entity (no generation check).</summary>
    public bool IsAliveSlot(int index) => (uint)index < (uint)HighWater && _archetype[index] >= 0;

    public EntityId Allocate(int archetype, int chunk, int row)
    {
        int index;
        if (_freeCount > 0)
        {
            index = _free[--_freeCount];
        }
        else
        {
            if (HighWater == _archetype.Length)
            {
                Grow();
            }

            index = HighWater++;
            _generation[index] = 1;
        }

        _archetype[index] = archetype;
        _chunk[index] = chunk;
        _row[index] = row;
        AliveCount++;
        return new EntityId(index, _generation[index]);
    }

    public void Free(int index)
    {
        _archetype[index] = -1;
        AliveCount--;
        if (_generation[index] < EntityId.MaxGeneration)
        {
            _generation[index]++;
            if (_freeCount == _free.Length)
            {
                Array.Resize(ref _free, _free.Length * 2);
            }

            _free[_freeCount++] = index;
        }
    }

    public int ArchetypeOf(int index) => _archetype[index];

    public int ChunkOf(int index) => _chunk[index];

    public int RowOf(int index) => _row[index];

    public void Move(int index, int archetype, int chunk, int row)
    {
        _archetype[index] = archetype;
        _chunk[index] = chunk;
        _row[index] = row;
    }

    public void Relocate(int index, int chunk, int row)
    {
        _chunk[index] = chunk;
        _row[index] = row;
    }

    private void Grow()
    {
        int capacity = _archetype.Length * 2;
        Array.Resize(ref _archetype, capacity);
        Array.Resize(ref _chunk, capacity);
        Array.Resize(ref _row, capacity);
        Array.Resize(ref _generation, capacity);
        Array.Fill(_archetype, -1, HighWater, capacity - HighWater);
    }
}
