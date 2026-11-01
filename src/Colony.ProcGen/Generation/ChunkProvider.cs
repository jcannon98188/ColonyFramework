using System.Diagnostics;
using System.Runtime.InteropServices;
using Colony.Fields;
using Colony.Hashing;
using Colony.Numerics;

namespace Colony.ProcGen;

/// <summary>Options of a <see cref="ChunkProvider"/>; the defaults are what streaming wants (no bookkeeping).</summary>
public sealed record ChunkProviderOptions
{
    /// <summary>Hash each stage's output after it runs (<see cref="GeneratedChunk.StageHashes"/>); validation tooling turns it on.</summary>
    public bool RecordStageHashes { get; init; }

    /// <summary>Time each stage (<see cref="GeneratedChunk.StageTicks"/>, <see cref="Stopwatch"/> ticks); validation tooling turns it on.</summary>
    public bool RecordTiming { get; init; }
}

/// <summary>
/// The ChunkProvider contract (spec section 9.3): generates any chunk from (seed, chunk coordinate) alone, in
/// any order, on any thread, with the same bytes, which streaming relies on. A provider is immutable after
/// construction (the pre-passes are computed once, here) and safe to share between threads; all per-chunk state
/// lives in the <see cref="GeneratedChunk"/> being filled, so give each thread its own chunk.
/// </summary>
public sealed class ChunkProvider
{
    /// <summary>Seed of chunk hashes ("GCHK").</summary>
    private const ulong ChunkHashSeed = 0x4743_484BUL;

    /// <summary>Seed of stage hashes ("GSTG").</summary>
    private const ulong StageHashSeed = 0x4753_5447UL;

    private readonly NoiseKey[] _keys;
    private readonly byte[][] _prePassValues;

    internal ChunkProvider(GenerationPipeline pipeline, ulong seed, ChunkProviderOptions options)
    {
        Pipeline = pipeline;
        Seed = seed;
        Options = options;
        _keys = new NoiseKey[pipeline.StageKeys.Length];
        for (int i = 0; i < _keys.Length; i++)
        {
            _keys[i] = new NoiseKey(seed, pipeline.StageKeys[i]);
        }

        var prePasses = pipeline.PrePasses;
        _prePassValues = new byte[prePasses.Length][];
        for (int i = 0; i < prePasses.Length; i++)
        {
            var prePass = prePasses[i];
            var key = NoiseKey.Create(seed, prePass.Name);
            var context = new PrePassContext(this, i, key, pipeline.Size);
            var value = new byte[prePass.ValueSize];
            prePass.ComputeInto(in context, value);
            _prePassValues[i] = value;
        }
    }

    public GenerationPipeline Pipeline { get; }

    public ulong Seed { get; }

    public ChunkProviderOptions Options { get; }

    /// <summary>A reusable chunk buffer for <see cref="Generate(ChunkCoord, GeneratedChunk)"/>; one per thread.</summary>
    public GeneratedChunk CreateChunk() => new(Pipeline);

    /// <summary>Generates a chunk into a new buffer.</summary>
    public GeneratedChunk Generate(ChunkCoord coord)
    {
        var chunk = CreateChunk();
        Generate(coord, chunk);
        return chunk;
    }

    /// <summary>Generates a chunk into <paramref name="chunk"/>, overwriting everything it held. Allocates nothing once the buffer has grown.</summary>
    public void Generate(ChunkCoord coord, GeneratedChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        GenerateAt(coord.Origin, chunk, null);
    }

    /// <summary>The cached value of a pre-pass by its value type.</summary>
    public T PrePass<T>()
        where T : unmanaged
        => MemoryMarshal.Read<T>(PrePassBytes(typeof(T), Pipeline.PrePasses.Length));

    internal ReadOnlySpan<byte> PrePassBytes(int index) => _prePassValues[index];

    internal ReadOnlySpan<byte> PrePassBytes(Type type, int available)
    {
        int index = Pipeline.PrePassIndex(type);
        if (index < 0 || index >= available)
        {
            throw new InvalidOperationException(index < 0
                ? $"No generation pre-pass produces {type.Name}."
                : $"Pre-pass {type.Name} is registered after the one reading it; pre-passes read only earlier ones.");
        }

        return _prePassValues[index];
    }

    /// <summary>
    /// Generates the window whose minimum tile is <paramref name="origin"/> (a chunk origin, or any tile for the
    /// seam check) through every stage, calling <paramref name="observer"/> after each.
    /// </summary>
    internal void GenerateAt(TilePos origin, GeneratedChunk chunk, IStageObserver? observer)
    {
        if (!ReferenceEquals(chunk.Pipeline, Pipeline))
        {
            throw new ArgumentException("The chunk buffer belongs to another pipeline.", nameof(chunk));
        }

        chunk.Reset(origin, Seed, Pipeline.Stages.Count);
        bool hashes = Options.RecordStageHashes;
        bool timing = Options.RecordTiming;
        var stages = Pipeline.Stages;
        for (int k = 0; k < stages.Count; k++)
        {
            long start = timing ? Stopwatch.GetTimestamp() : 0;
            int firstPlacement = chunk.PlacementCount;
            RunStage(k, origin, chunk);
            if (timing)
            {
                chunk.StageTicksBuffer[k] = Stopwatch.GetTimestamp() - start;
            }

            if (hashes)
            {
                chunk.StageHashesBuffer[k] = HashStage(k, chunk, firstPlacement);
            }

            observer?.AfterStage(k, chunk);
        }
    }

    /// <summary>The neighbour window <paramref name="index"/> (0..26, x fastest, 13 = centre) of a chunk with its base stages run.</summary>
    internal GeneratedChunk Neighbour(GeneratedChunk chunk, int index)
    {
        if (index == 13)
        {
            return chunk;
        }

        var neighbour = chunk.NeighbourBuffer(index);
        if (!chunk.NeighbourReady[index])
        {
            int dx = (index % 3) - 1;
            int dy = ((index / 3) % 3) - 1;
            int dz = (index / 9) - 1;
            var origin = new TilePos(chunk.Origin.X + (dx * FieldChunk.Size), chunk.Origin.Y + (dy * FieldChunk.Size), chunk.Origin.Z + (dz * FieldChunk.Size));
            neighbour.Reset(origin, Seed, Pipeline.Stages.Count);
            foreach (int k in Pipeline.BaseStages)
            {
                RunStage(k, origin, neighbour);
            }

            chunk.NeighbourReady[index] = true;
        }

        return neighbour;
    }

    internal NoiseKey KeyOf(int stage) => _keys[stage];

    internal RandomSource RandomOf(int stage, TilePos origin) => new(Seed, Hash64.Combine(Pipeline.StageKeys[stage], OriginKey(origin)));

    internal ulong PlacementId(int stage, TilePos origin, int index)
        => Hash64.Combine(Hash64.Combine(Hash64.Combine(Seed, Pipeline.StageKeys[stage]), OriginKey(origin)), (ulong)(uint)index);

    internal static ulong OriginKey(TilePos origin)
        => Hash64.Combine(Hash64.Combine((ulong)(uint)origin.X, (ulong)(uint)origin.Y), (ulong)(uint)origin.Z);

    internal static ulong HashChunk(GeneratedChunk chunk)
    {
        var state = new Hash64State(ChunkHashSeed);
        state.Append(chunk.Origin.X);
        state.Append(chunk.Origin.Y);
        state.Append(chunk.Origin.Z);
        var pipeline = chunk.Pipeline;
        for (int slot = 0; slot < pipeline.SlotCount; slot++)
        {
            state.Append(pipeline.SlotDescriptor(slot).LayoutHash);
            state.Append(chunk.Slot(slot));
        }

        AppendPlacements(ref state, chunk.Placements);
        return state.Finish();
    }

    private void RunStage(int k, TilePos origin, GeneratedChunk chunk)
    {
        var context = new ChunkGenerationContext(this, chunk, k, origin);
        Pipeline.Stages[k].Stage.Generate(ref context);
    }

    private ulong HashStage(int k, GeneratedChunk chunk, int firstPlacement)
    {
        var state = new Hash64State(StageHashSeed);
        state.Append(Pipeline.StageKeys[k]);
        var writes = Pipeline.WriteSlots[k];
        for (int slot = 0; slot < writes.Length; slot++)
        {
            if (writes[slot])
            {
                state.Append(chunk.Slot(slot));
            }
        }

        AppendPlacements(ref state, chunk.Placements[firstPlacement..]);
        return state.Finish();
    }

    private static void AppendPlacements(ref Hash64State state, ReadOnlySpan<Placement> placements)
    {
        state.Append(placements.Length);
        foreach (ref readonly var p in placements)
        {
            state.Append(p.Id);
            state.Append(p.Position.X);
            state.Append(p.Position.Y);
            state.Append(p.Position.Z);
            state.Append(p.Kind);
            state.Append(p.Data);
        }
    }
}

/// <summary>Called by <see cref="ChunkProvider.GenerateAt"/> after each stage (validation tooling).</summary>
internal interface IStageObserver
{
    public void AfterStage(int stage, GeneratedChunk chunk);
}
