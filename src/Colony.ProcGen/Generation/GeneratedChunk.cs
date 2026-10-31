using System.Runtime.InteropServices;
using Colony.Fields;

namespace Colony.ProcGen;

/// <summary>
/// One Entity a placement stage asked for (spec section 9.3). <see cref="Id"/> is derived from (seed, stage,
/// chunk coordinate, index) by <see cref="DeriveId"/>, so it is the same whichever order or thread generated the
/// chunk; it is not an <see cref="EntityId"/> (the World allocates those) and is stored on the Entity as
/// <see cref="GeneratedEntity.Id"/>.
/// </summary>
/// <param name="Id">Stable generation id.</param>
/// <param name="Stage">Execution-order index of the placing stage.</param>
/// <param name="Index">Index of the placement among the stage's placements in the chunk.</param>
/// <param name="Position">Tile, inside the chunk.</param>
/// <param name="Kind">Game-defined kind.</param>
/// <param name="Data">Game-defined payload.</param>
public readonly record struct Placement(ulong Id, int Stage, int Index, TilePos Position, int Kind, uint Data)
{
    /// <summary>The id of placement <paramref name="index"/> of stage <paramref name="stageName"/> in chunk <paramref name="coord"/> for <paramref name="seed"/>.</summary>
    public static ulong DeriveId(ulong seed, string stageName, ChunkCoord coord, int index)
    {
        ArgumentNullException.ThrowIfNull(stageName);
        return Hashing.Hash64.Combine(
            Hashing.Hash64.Combine(Hashing.Hash64.Combine(seed, GenerationPipeline.KeyOf(stageName)), ChunkProvider.OriginKey(coord.Origin)),
            (ulong)(uint)index);
    }
}

/// <summary>The Component a placed Entity carries: its generation id and the stage's kind and payload.</summary>
public readonly record struct GeneratedEntity(ulong Id, int Kind, uint Data);

/// <summary>
/// The output of generating one chunk: the cells of every Field the pipeline touches, the placements, and (when
/// the provider records them) per-stage hashes and timings. Reusable: <see cref="ChunkProvider.Generate(ChunkCoord, GeneratedChunk)"/>
/// overwrites everything. Not thread-safe; one per thread.
/// </summary>
public sealed class GeneratedChunk
{
    private readonly byte[][] _slots;
    private Placement[] _placements = new Placement[16];
    private GeneratedChunk?[]? _neighbours;
    private bool[]? _neighbourReady;
    private ulong[] _stageHashes = [];
    private long[] _stageTicks = [];

    internal GeneratedChunk(GenerationPipeline pipeline)
    {
        Pipeline = pipeline;
        _slots = new byte[pipeline.SlotCount][];
        for (int s = 0; s < _slots.Length; s++)
        {
            _slots[s] = new byte[FieldChunk.CellCount * pipeline.SlotDescriptor(s).CellSize];
        }
    }

    public GenerationPipeline Pipeline { get; }

    /// <summary>The chunk coordinate (of <see cref="Origin"/>).</summary>
    public ChunkCoord Coord => ChunkCoord.OfTile(Origin);

    /// <summary>Minimum tile of the generated window.</summary>
    public TilePos Origin { get; private set; }

    public ulong Seed { get; private set; }

    public int PlacementCount { get; private set; }

    public ReadOnlySpan<Placement> Placements => _placements.AsSpan(0, PlacementCount);

    /// <summary>Per-stage output hashes in execution order (zero unless <see cref="ChunkProviderOptions.RecordStageHashes"/>).</summary>
    public ReadOnlySpan<ulong> StageHashes => _stageHashes;

    /// <summary>Per-stage durations in <see cref="System.Diagnostics.Stopwatch"/> ticks (zero unless <see cref="ChunkProviderOptions.RecordTiming"/>).</summary>
    public ReadOnlySpan<long> StageTicks => _stageTicks;

    internal Span<ulong> StageHashesBuffer => _stageHashes;

    internal Span<long> StageTicksBuffer => _stageTicks;

    internal bool[] NeighbourReady => _neighbourReady!;

    /// <summary>The generated cells of a Field in storage order.</summary>
    public ReadOnlySpan<T> Cells<T>(Field<T> field)
        where T : unmanaged
        => MemoryMarshal.Cast<byte, T>(Slot(CheckedSlot(field.Id, System.Runtime.CompilerServices.Unsafe.SizeOf<T>())));

    /// <summary>The generated cells of a Field by id, as raw bytes.</summary>
    public ReadOnlySpan<byte> CellBytes(int fieldId) => Slot(CheckedSlot(fieldId, -1));

    /// <summary>XXH64 of the chunk: origin, every Field's cells (Field id order) and the placements. Equal bytes, equal hash.</summary>
    public ulong Hash() => ChunkProvider.HashChunk(this);

    internal ReadOnlySpan<byte> Slot(int slot) => _slots[slot];

    internal Span<byte> WritableSlot(int slot) => _slots[slot];

    internal void Reset(TilePos origin, ulong seed, int stageCount)
    {
        Origin = origin;
        Seed = seed;
        PlacementCount = 0;
        foreach (var slot in _slots)
        {
            Array.Clear(slot);
        }

        if (_stageHashes.Length != stageCount)
        {
            _stageHashes = new ulong[stageCount];
            _stageTicks = new long[stageCount];
        }
        else
        {
            Array.Clear(_stageHashes);
            Array.Clear(_stageTicks);
        }

        if (_neighbourReady is not null)
        {
            Array.Clear(_neighbourReady);
        }
        else if (Pipeline.HasHalo)
        {
            _neighbourReady = new bool[27];
            _neighbours = new GeneratedChunk?[27];
        }
    }

    internal GeneratedChunk NeighbourBuffer(int index) => _neighbours![index] ??= new GeneratedChunk(Pipeline);

    internal void AddPlacement(in Placement placement)
    {
        if (PlacementCount == _placements.Length)
        {
            Array.Resize(ref _placements, _placements.Length * 2);
        }

        _placements[PlacementCount++] = placement;
    }

    internal int CountPlacements(int stage)
    {
        int count = 0;
        for (int i = PlacementCount - 1; i >= 0 && _placements[i].Stage == stage; i--)
        {
            count++;
        }

        return count;
    }

    private int CheckedSlot(int fieldId, int cellSize)
    {
        int slot = Pipeline.SlotOf(fieldId);
        if (slot < 0)
        {
            throw new ArgumentException($"Field #{fieldId} is not touched by any generation stage.", nameof(fieldId));
        }

        if (cellSize >= 0 && Pipeline.SlotDescriptor(slot).CellSize != cellSize)
        {
            throw new ArgumentException($"Field '{Pipeline.SlotDescriptor(slot).Name}' has {Pipeline.SlotDescriptor(slot).CellSize}-byte cells, not {cellSize}.", nameof(fieldId));
        }

        return slot;
    }
}
