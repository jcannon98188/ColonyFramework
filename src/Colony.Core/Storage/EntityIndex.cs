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
    private uint[] _freedEpoch;
    private int[] _free;
    private int _freeCount;

    public EntityIndex(int initialCapacity)
    {
        int capacity = Math.Max(16, initialCapacity);
        _archetype = new int[capacity];
        _chunk = new int[capacity];
        _row = new int[capacity];
        _generation = new uint[capacity];
        _freedEpoch = new uint[capacity];
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

    /// <summary>
    /// True when <paramref name="id"/> named the Entity that was destroyed in the slot during structural
    /// epoch <paramref name="epoch"/> (the current Phase's merge): the op lost a same-Phase conflict rather
    /// than being stale from an earlier Phase.
    /// </summary>
    public bool DiedInEpoch(EntityId id, uint epoch)
    {
        int index = id.Index;
        return (uint)index < (uint)HighWater && _freedEpoch[index] == epoch && _generation[index] == id.Generation + 1;
    }

    public void Free(int index, uint epoch)
    {
        _archetype[index] = -1;
        _freedEpoch[index] = epoch;
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

    /// <summary>The id of the Entity in a live slot.</summary>
    public EntityId IdAt(int index) => new(index, _generation[index]);

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

    // ---- Save/load seams (TASK-14). The free list order and the generations of dead slots are not hashed but
    // decide which ids future creates hand out, so a load restores them exactly. ----

    public uint GenerationAt(int index) => _generation[index];

    public uint FreedEpochAt(int index) => _freedEpoch[index];

    /// <summary>The free slots, bottom to top: the last entry is the next slot <see cref="Allocate"/> reuses.</summary>
    public ReadOnlySpan<int> FreeSlots => _free.AsSpan(0, _freeCount);

    /// <summary>Replaces the whole index with saved slot state: every slot dead, generations and the free stack as saved. <see cref="RestoreSlot"/> then revives the live ones.</summary>
    public void Restore(int highWater, ReadOnlySpan<uint> generations, ReadOnlySpan<uint> freedEpochs, ReadOnlySpan<int> freeSlots)
    {
        if (generations.Length != highWater || freedEpochs.Length != highWater)
        {
            throw new ArgumentException("Generations and freed epochs must have one entry per slot up to the high-water mark.", nameof(generations));
        }

        int capacity = Math.Max(_archetype.Length, highWater);
        _archetype = new int[capacity];
        _chunk = new int[capacity];
        _row = new int[capacity];
        _generation = new uint[capacity];
        _freedEpoch = new uint[capacity];
        Array.Fill(_archetype, -1);
        generations.CopyTo(_generation);
        freedEpochs.CopyTo(_freedEpoch);
        _free = new int[Math.Max(16, Math.Max(freeSlots.Length, capacity / 4))];
        freeSlots.CopyTo(_free);
        _freeCount = freeSlots.Length;
        HighWater = highWater;
        AliveCount = 0;
    }

    /// <summary>Revives a saved slot at its saved location (after <see cref="Restore"/>).</summary>
    public void RestoreSlot(int index, int archetype, int chunk, int row)
    {
        if ((uint)index >= (uint)HighWater || _archetype[index] >= 0)
        {
            throw new ArgumentException($"Entity slot {index} is outside the saved index or appears twice.", nameof(index));
        }

        _archetype[index] = archetype;
        _chunk[index] = chunk;
        _row[index] = row;
        AliveCount++;
    }

    private void Grow()
    {
        int capacity = _archetype.Length * 2;
        Array.Resize(ref _archetype, capacity);
        Array.Resize(ref _chunk, capacity);
        Array.Resize(ref _row, capacity);
        Array.Resize(ref _generation, capacity);
        Array.Resize(ref _freedEpoch, capacity);
        Array.Fill(_archetype, -1, HighWater, capacity - HighWater);
    }
}
