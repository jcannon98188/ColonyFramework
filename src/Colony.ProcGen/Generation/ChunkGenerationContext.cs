using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Colony.Fields;
using Colony.Numerics;

namespace Colony.ProcGen;

/// <summary>
/// What a <see cref="GenerationStage"/> sees while generating one chunk (spec section 9.3): the coordinate, the
/// stage's randomness keyed by (seed, stage name hash, chunk coordinate, use index), declared Field access and
/// the pre-pass values. Access outside the stage's declarations throws <see cref="InvalidOperationException"/>.
/// </summary>
public ref struct ChunkGenerationContext
{
    private readonly ChunkProvider _provider;
    private readonly GeneratedChunk _chunk;
    private readonly int _stage;
    private int _placed;

    internal ChunkGenerationContext(ChunkProvider provider, GeneratedChunk chunk, int stage, TilePos origin)
    {
        _provider = provider;
        _chunk = chunk;
        _stage = stage;
        Origin = origin;
        Key = provider.KeyOf(stage);
        Random = provider.RandomOf(stage, origin);
        _placed = 0;
    }

    /// <summary>The chunk being generated.</summary>
    public readonly ChunkCoord Coord => ChunkCoord.OfTile(Origin);

    /// <summary>The chunk's minimum tile; cell (x, y, z) of the chunk is tile <c>Origin + (x, y, z)</c>.</summary>
    public TilePos Origin { get; }

    public readonly ulong Seed => _provider.Seed;

    public readonly WorldSize Size => _provider.Pipeline.Size;

    public readonly string StageName => _provider.Pipeline.Stages[_stage].Name;

    /// <summary>The (seed, stage) noise key: sample noise at world coordinates and region-keyed spots with it, so features continue across chunk edges.</summary>
    public NoiseKey Key { get; }

    /// <summary>The (seed, stage, chunk) stream, addressed by use index: chunk-keyed draws for <see cref="GenerationStage.ChunkLocal"/> stages.</summary>
    public RandomSource Random { get; }

    /// <summary>The chunk's cells of a Field the stage declared in Reads or Writes, as left by the earlier stages.</summary>
    public readonly ReadOnlySpan<T> Read<T>(Field<T> field)
        where T : unmanaged
    {
        int slot = Slot(field.Id, Unsafe.SizeOf<T>());
        if (!_provider.Pipeline.ReadSlots[_stage][slot] && !_provider.Pipeline.WriteSlots[_stage][slot])
        {
            throw Undeclared("read", slot, "Reads");
        }

        return MemoryMarshal.Cast<byte, T>(_chunk.Slot(slot));
    }

    /// <summary>The chunk's cells of a Field the stage declared in Writes, holding what earlier stages wrote (zero if none).</summary>
    public readonly Span<T> Write<T>(Field<T> field)
        where T : unmanaged
    {
        int slot = Slot(field.Id, Unsafe.SizeOf<T>());
        if (!_provider.Pipeline.WriteSlots[_stage][slot])
        {
            throw Undeclared("write", slot, "Writes");
        }

        return MemoryMarshal.Cast<byte, T>(_chunk.WritableSlot(slot));
    }

    /// <summary>
    /// A view of a Field declared in HaloReads over this chunk and its 26 neighbours (local coordinates in
    /// [-16, 32) per axis). Neighbours hold their base data, generated from their own coordinates on first access.
    /// </summary>
    public readonly HaloView<T> Halo<T>(Field<T> field)
        where T : unmanaged
    {
        int slot = Slot(field.Id, Unsafe.SizeOf<T>());
        if (!_provider.Pipeline.HaloSlots[_stage][slot])
        {
            throw Undeclared("halo-read", slot, "HaloReads");
        }

        return new HaloView<T>(_provider, _chunk, slot);
    }

    /// <summary>The value of a world-level pre-pass, computed once per (pipeline, seed).</summary>
    public readonly T PrePass<T>()
        where T : unmanaged
        => _provider.PrePass<T>();

    /// <summary>
    /// Places an Entity at a tile inside this chunk. Its id derives from (seed, stage, chunk coordinate, the
    /// placement's index within this stage), so placements are stable regardless of other stages.
    /// </summary>
    public void Place(TilePos position, int kind, uint data = 0)
    {
        var origin = Origin;
        if ((uint)(position.X - origin.X) >= FieldChunk.Size || (uint)(position.Y - origin.Y) >= FieldChunk.Size || (uint)(position.Z - origin.Z) >= FieldChunk.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Stage '{StageName}' placed outside chunk {Coord}; a chunk places only inside itself.");
        }

        int index = _placed++;
        _chunk.AddPlacement(new Placement(_provider.PlacementId(_stage, origin, index), _stage, index, position, kind, data));
    }

    private readonly int Slot(int fieldId, int cellSize)
    {
        int slot = _provider.Pipeline.SlotOf(fieldId);
        if (slot < 0)
        {
            throw new InvalidOperationException($"Stage '{StageName}' used Field #{fieldId}, which no stage declares.");
        }

        if (_provider.Pipeline.SlotDescriptor(slot).CellSize != cellSize)
        {
            throw new ArgumentException($"Field '{_provider.Pipeline.SlotDescriptor(slot).Name}' has {_provider.Pipeline.SlotDescriptor(slot).CellSize}-byte cells, not {cellSize}.", nameof(fieldId));
        }

        return slot;
    }

    private readonly InvalidOperationException Undeclared(string verb, int slot, string list)
        => new($"Stage '{StageName}' tried to {verb} Field '{_provider.Pipeline.SlotDescriptor(slot).Name}' without declaring it in {list}.");
}

/// <summary>A Field seen across the chunk edge (<see cref="ChunkGenerationContext.Halo{T}"/>).</summary>
public readonly ref struct HaloView<T>
    where T : unmanaged
{
    private readonly ChunkProvider _provider;
    private readonly GeneratedChunk _chunk;
    private readonly int _slot;

    internal HaloView(ChunkProvider provider, GeneratedChunk chunk, int slot)
    {
        _provider = provider;
        _chunk = chunk;
        _slot = slot;
    }

    /// <summary>The cell at chunk-local coordinates, each in [-16, 32): negative or past 15 reads a neighbour's base data.</summary>
    public T this[int x, int y, int z]
    {
        get
        {
            const int Span3 = 3 * FieldChunk.Size;
            if ((uint)(x + FieldChunk.Size) >= Span3 || (uint)(y + FieldChunk.Size) >= Span3 || (uint)(z + FieldChunk.Size) >= Span3)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"Halo coordinates ({x}, {y}, {z}) are outside [-16, 32).");
            }

            int index = ((x + FieldChunk.Size) >> FieldChunk.Shift) + (3 * ((y + FieldChunk.Size) >> FieldChunk.Shift)) + (9 * ((z + FieldChunk.Size) >> FieldChunk.Shift));
            var source = _provider.Neighbour(_chunk, index);
            return MemoryMarshal.Cast<byte, T>(source.Slot(_slot))[FieldChunk.CellIndex(x & FieldChunk.Mask, y & FieldChunk.Mask, z & FieldChunk.Mask)];
        }
    }
}

/// <summary>What a <see cref="GenerationPrePass{T}"/> sees: the seed, the World size, its randomness and the earlier pre-passes.</summary>
public readonly ref struct PrePassContext
{
    private readonly ChunkProvider _provider;
    private readonly int _index;

    internal PrePassContext(ChunkProvider provider, int index, NoiseKey key, WorldSize size)
    {
        _provider = provider;
        _index = index;
        Key = key;
        Size = size;
        Random = new RandomSource(key.Seed, key.Stage);
    }

    public ulong Seed => Key.Seed;

    public WorldSize Size { get; }

    /// <summary>The (seed, pre-pass name) noise key.</summary>
    public NoiseKey Key { get; }

    /// <summary>The (seed, pre-pass name) stream, addressed by use index.</summary>
    public RandomSource Random { get; }

    /// <summary>The value of a pre-pass registered before this one.</summary>
    public T PrePass<T>()
        where T : unmanaged
        => MemoryMarshal.Read<T>(_provider.PrePassBytes(typeof(T), _index));
}
