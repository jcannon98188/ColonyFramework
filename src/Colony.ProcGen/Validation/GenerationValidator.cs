using System.Diagnostics;
using System.Globalization;
using Colony.Fields;
using Colony.Hashing;
using Colony.Numerics;

namespace Colony.ProcGen.Validation;

/// <summary>What <see cref="GenerationValidator.Validate"/> generates and checks.</summary>
public sealed record ValidationOptions
{
    /// <summary>Seeds to validate; golden hashes are per (seed, stage).</summary>
    public IReadOnlyList<ulong> Seeds { get; init; } = [1];

    /// <summary>Chunks to generate per seed, in this order; null means every chunk of the World (x fastest).</summary>
    public IReadOnlyList<ChunkCoord>? Chunks { get; init; }

    /// <summary>Golden hashes to compare against; null skips the drift check.</summary>
    public GoldenHashes? Golden { get; init; }

    /// <summary>Generate the chunks again in reverse, shuffled and multi-threaded order and compare every stage's output.</summary>
    public bool CheckOrder { get; init; } = true;

    /// <summary>Threads of the multi-threaded order check.</summary>
    public int Threads { get; init; } = 4;

    /// <summary>Compare every chunk face with a window generated straddling it, per stage.</summary>
    public bool CheckSeams { get; init; } = true;

    /// <summary>Game-supplied checks run on each seed's generated chunks.</summary>
    public IReadOnlyList<GenerationCheck> Checks { get; init; } = [];
}

/// <summary>
/// Validation tooling for a generation pipeline (spec section 9.3): golden hashes per stage, order and thread
/// independence, seam checks across chunk faces, Game-supplied checks and per-stage timing, all attributed to the
/// stage responsible. Tooling, not simulation: it allocates freely and may use threads.
/// </summary>
public static class GenerationValidator
{
    /// <summary>Distance of a seam window from the chunk it starts in: it straddles the face at its middle.</summary>
    private const int SeamOffset = FieldChunk.Size / 2;

    public static GenerationReport Validate(GenerationPipeline pipeline, ValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        options ??= new ValidationOptions();
        if (options.Threads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Threads, "Threads must be at least 1.");
        }

        var chunks = options.Chunks?.ToArray() ?? AllChunks(pipeline.Size);
        var grid = IndexGrid(pipeline.Size, chunks);
        int stageCount = pipeline.Stages.Count;
        var ticks = new long[stageCount];
        var issues = new List<GenerationIssue>();
        var golden = new List<GoldenEntry>();
        int seams = 0;
        var recording = new ChunkProviderOptions { RecordStageHashes = true, RecordTiming = true };
        foreach (ulong seed in options.Seeds)
        {
            var provider = pipeline.CreateProvider(seed, recording);
            var generated = new GeneratedChunk[chunks.Length];
            var baseline = new ulong[chunks.Length][];
            for (int i = 0; i < chunks.Length; i++)
            {
                generated[i] = provider.Generate(chunks[i]);
                baseline[i] = generated[i].StageHashes.ToArray();
                var stageTicks = generated[i].StageTicks;
                for (int k = 0; k < stageCount; k++)
                {
                    ticks[k] += stageTicks[k];
                }
            }

            for (int k = 0; k < stageCount; k++)
            {
                ulong hash = Hash64.Combine(seed, GenerationPipeline.KeyOf(pipeline.Stages[k].Name));
                for (int i = 0; i < chunks.Length; i++)
                {
                    hash = Hash64.Combine(hash, baseline[i][k]);
                }

                golden.Add(new GoldenEntry(seed, pipeline.Stages[k].Name, hash));
            }

            if (options.CheckOrder)
            {
                CheckOrder(provider, chunks, baseline, options.Threads, issues);
            }

            if (options.CheckSeams)
            {
                seams += CheckSeams(provider, chunks, grid, issues);
            }

            if (options.Checks.Count > 0)
            {
                var byCoord = new GeneratedChunk?[grid.Length];
                for (int i = 0; i < grid.Length; i++)
                {
                    byCoord[i] = grid[i] < 0 ? null : generated[grid[i]];
                }

                foreach (var check in options.Checks)
                {
                    check.Check(new GenerationCheckContext(pipeline, seed, byCoord, issues, check.Name));
                }
            }
        }

        var hashes = new GoldenHashes(golden);
        if (options.Golden is { } expected)
        {
            CompareGolden(pipeline, options.Seeds, hashes, expected, issues);
        }

        var stages = new StageReport[stageCount];
        for (int k = 0; k < stageCount; k++)
        {
            stages[k] = new StageReport(pipeline.Stages[k].Name, options.Seeds.Count * chunks.Length, TimeSpan.FromTicks(ticks[k] * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        }

        return new GenerationReport(pipeline, [.. options.Seeds], chunks.Length, stages, issues, hashes, seams);
    }

    private static ChunkCoord[] AllChunks(WorldSize size)
    {
        var coords = new ChunkCoord[size.ChunkCount];
        int n = 0;
        for (int z = 0; z < size.ChunksZ; z++)
        {
            for (int y = 0; y < size.ChunksY; y++)
            {
                for (int x = 0; x < size.ChunksX; x++)
                {
                    coords[n++] = new ChunkCoord(x, y, z);
                }
            }
        }

        return coords;
    }

    /// <summary>Index into <paramref name="chunks"/> per World chunk (x fastest), -1 where not validated.</summary>
    private static int[] IndexGrid(WorldSize size, ChunkCoord[] chunks)
    {
        var grid = new int[size.ChunkCount];
        Array.Fill(grid, -1);
        for (int i = 0; i < chunks.Length; i++)
        {
            var c = chunks[i];
            if ((uint)c.X >= (uint)size.ChunksX || (uint)c.Y >= (uint)size.ChunksY || (uint)c.Z >= (uint)size.ChunksZ)
            {
                throw new ArgumentException($"Chunk ({c.X}, {c.Y}, {c.Z}) is outside the {size} World.", nameof(chunks));
            }

            int cell = c.X + (size.ChunksX * (c.Y + (size.ChunksY * c.Z)));
            if (grid[cell] >= 0)
            {
                throw new ArgumentException($"Chunk ({c.X}, {c.Y}, {c.Z}) is listed twice.", nameof(chunks));
            }

            grid[cell] = i;
        }

        return grid;
    }

    private static void CheckOrder(ChunkProvider provider, ChunkCoord[] chunks, ulong[][] baseline, int threads, List<GenerationIssue> issues)
    {
        int n = chunks.Length;
        var reverse = new int[n];
        var shuffled = new int[n];
        for (int i = 0; i < n; i++)
        {
            reverse[i] = n - 1 - i;
            shuffled[i] = i;
        }

        var random = new RandomSource(provider.Seed, 0x5348_5546UL);
        for (int i = n - 1; i > 0; i--)
        {
            int j = random.Int32At((ulong)i, 0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        var runs = new (string Name, ulong[][] Hashes)[]
        {
            ("reverse order", Sequential(provider, chunks, reverse)),
            ("shuffled order", Sequential(provider, chunks, shuffled)),
            ($"{threads.ToString(CultureInfo.InvariantCulture)} threads", Parallel(provider, chunks, threads)),
        };

        var pipeline = provider.Pipeline;
        for (int k = 0; k < pipeline.Stages.Count; k++)
        {
            int count = 0;
            ChunkCoord? first = null;
            var how = new List<string>();
            foreach (var (name, hashes) in runs)
            {
                bool differs = false;
                for (int i = 0; i < n; i++)
                {
                    if (FirstDifference(baseline[i], hashes[i]) == k)
                    {
                        differs = true;
                        count++;
                        first ??= chunks[i];
                    }
                }

                if (differs)
                {
                    how.Add(name);
                }
            }

            if (count > 0)
            {
                issues.Add(new GenerationIssue(GenerationIssueKind.OrderDependence, pipeline.Stages[k].Name, provider.Seed, first,
                    $"output changed in {count} chunk generation(s) when generated in {string.Join(", ", how)}; the stage depends on state other than (seed, chunk)."));
            }
        }
    }

    private static int FirstDifference(ulong[] expected, ulong[] actual)
    {
        for (int k = 0; k < expected.Length; k++)
        {
            if (expected[k] != actual[k])
            {
                return k;
            }
        }

        return -1;
    }

    private static ulong[][] Sequential(ChunkProvider provider, ChunkCoord[] chunks, int[] order)
    {
        var hashes = new ulong[chunks.Length][];
        var chunk = provider.CreateChunk();
        foreach (int i in order)
        {
            provider.Generate(chunks[i], chunk);
            hashes[i] = chunk.StageHashes.ToArray();
        }

        return hashes;
    }

    private static ulong[][] Parallel(ChunkProvider provider, ChunkCoord[] chunks, int threads)
    {
        var hashes = new ulong[chunks.Length][];
        System.Threading.Tasks.Parallel.For(
            0,
            chunks.Length,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            provider.CreateChunk,
            (i, _, chunk) =>
            {
                provider.Generate(chunks[i], chunk);
                hashes[i] = chunk.StageHashes.ToArray();
                return chunk;
            },
            _ => { });
        return hashes;
    }

    /// <summary>
    /// For each validated chunk and each axis with a validated neighbour, generates the window starting
    /// <see cref="SeamOffset"/> tiles into the chunk and compares, after every seam-checkable stage, the cells the
    /// stage writes with the two real chunks. The first mismatching stage of a face is the one reported.
    /// </summary>
    private static int CheckSeams(ChunkProvider provider, ChunkCoord[] chunks, int[] grid, List<GenerationIssue> issues)
    {
        var pipeline = provider.Pipeline;
        var size = pipeline.Size;
        var checkable = SeamCheckable(pipeline);
        var own = new Snapshot(pipeline, checkable);
        var next = new Snapshot(pipeline, checkable);
        var window = new Snapshot(pipeline, checkable);
        var chunk = provider.CreateChunk();
        var counts = new int[pipeline.Stages.Count];
        var firsts = new ChunkCoord?[pipeline.Stages.Count];
        var firstCells = new int[pipeline.Stages.Count];
        int seams = 0;
        Span<int> dims = [size.ChunksX, size.ChunksY, size.ChunksZ];
        for (int i = 0; i < chunks.Length; i++)
        {
            var c = chunks[i];
            bool ownReady = false;
            for (int axis = 0; axis < 3; axis++)
            {
                Span<int> n = [c.X, c.Y, c.Z];
                n[axis]++;
                if (n[axis] >= dims[axis] || grid[n[0] + (size.ChunksX * (n[1] + (size.ChunksY * n[2])))] < 0)
                {
                    continue;
                }

                if (!ownReady)
                {
                    provider.GenerateAt(c.Origin, chunk, own);
                    ownReady = true;
                }

                provider.GenerateAt(new ChunkCoord(n[0], n[1], n[2]).Origin, chunk, next);
                var origin = c.Origin;
                var shifted = new TilePos(origin.X + (axis == 0 ? SeamOffset : 0), origin.Y + (axis == 1 ? SeamOffset : 0), origin.Z + (axis == 2 ? SeamOffset : 0));
                provider.GenerateAt(shifted, chunk, window);
                seams++;
                int stage = FirstSeamMismatch(pipeline, checkable, axis, own, next, window, out int cells);
                if (stage >= 0)
                {
                    if (counts[stage]++ == 0)
                    {
                        firsts[stage] = c;
                        firstCells[stage] = cells;
                    }
                }
            }
        }

        for (int k = 0; k < counts.Length; k++)
        {
            if (counts[k] > 0)
            {
                var f = firsts[k]!.Value;
                issues.Add(new GenerationIssue(GenerationIssueKind.SeamMismatch, pipeline.Stages[k].Name, provider.Seed, f,
                    $"output differs across {counts[k]} chunk face(s) from a window generated straddling the face (first: a face of chunk ({f.X}, {f.Y}, {f.Z}), {firstCells[k]} cell(s)); key features by world position or region, not by chunk, or declare the stage ChunkLocal."));
            }
        }

        return seams;
    }

    /// <summary>A stage is seam-checkable unless it is ChunkLocal or reads (or rewrites) a Field a non-checkable stage wrote.</summary>
    private static bool[] SeamCheckable(GenerationPipeline pipeline)
    {
        var tainted = new bool[pipeline.SlotCount];
        var checkable = new bool[pipeline.Stages.Count];
        for (int k = 0; k < checkable.Length; k++)
        {
            bool ok = !pipeline.Stages[k].ChunkLocal;
            for (int s = 0; s < tainted.Length && ok; s++)
            {
                if (tainted[s] && (pipeline.ReadSlots[k][s] || pipeline.HaloSlots[k][s] || pipeline.WriteSlots[k][s]))
                {
                    ok = false;
                }
            }

            checkable[k] = ok;
            if (!ok)
            {
                for (int s = 0; s < tainted.Length; s++)
                {
                    tainted[s] |= pipeline.WriteSlots[k][s];
                }
            }
        }

        return checkable;
    }

    private static int FirstSeamMismatch(GenerationPipeline pipeline, bool[] checkable, int axis, Snapshot own, Snapshot next, Snapshot window, out int cells)
    {
        cells = 0;
        for (int k = 0; k < checkable.Length; k++)
        {
            if (!checkable[k])
            {
                continue;
            }

            for (int s = 0; s < pipeline.SlotCount; s++)
            {
                if (!pipeline.WriteSlots[k][s])
                {
                    continue;
                }

                int cellSize = pipeline.SlotDescriptor(s).CellSize;
                var w = window.Cells(k, s);
                var a = own.Cells(k, s);
                var b = next.Cells(k, s);
                for (int index = 0; index < FieldChunk.CellCount; index++)
                {
                    var (x, y, z) = FieldChunk.CellCoordinates(index);
                    Span<int> local = [x, y, z];
                    var source = local[axis] < SeamOffset ? a : b;
                    local[axis] += local[axis] < SeamOffset ? SeamOffset : -SeamOffset;
                    int other = FieldChunk.CellIndex(local[0], local[1], local[2]);
                    if (!w.Slice(index * cellSize, cellSize).SequenceEqual(source.Slice(other * cellSize, cellSize)))
                    {
                        cells++;
                    }
                }
            }

            if (cells > 0)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>Copies, after each seam-checkable stage, the cells it wrote.</summary>
    private sealed class Snapshot : IStageObserver
    {
        private readonly GenerationPipeline _pipeline;
        private readonly bool[] _checkable;
        private readonly byte[]?[][] _cells;

        public Snapshot(GenerationPipeline pipeline, bool[] checkable)
        {
            _pipeline = pipeline;
            _checkable = checkable;
            _cells = new byte[]?[pipeline.Stages.Count][];
            for (int k = 0; k < _cells.Length; k++)
            {
                _cells[k] = new byte[]?[pipeline.SlotCount];
                for (int s = 0; s < pipeline.SlotCount; s++)
                {
                    if (checkable[k] && pipeline.WriteSlots[k][s])
                    {
                        _cells[k][s] = new byte[FieldChunk.CellCount * pipeline.SlotDescriptor(s).CellSize];
                    }
                }
            }
        }

        public ReadOnlySpan<byte> Cells(int stage, int slot) => _cells[stage][slot];

        public void AfterStage(int stage, GeneratedChunk chunk)
        {
            if (!_checkable[stage])
            {
                return;
            }

            for (int s = 0; s < _pipeline.SlotCount; s++)
            {
                if (_cells[stage][s] is { } copy)
                {
                    chunk.Slot(s).CopyTo(copy);
                }
            }
        }
    }

    private static void CompareGolden(GenerationPipeline pipeline, IReadOnlyList<ulong> seeds, GoldenHashes actual, GoldenHashes expected, List<GenerationIssue> issues)
    {
        foreach (var entry in actual.Entries)
        {
            if (!expected.TryGet(entry.Seed, entry.Stage, out ulong hash))
            {
                issues.Add(new GenerationIssue(GenerationIssueKind.GoldenDrift, entry.Stage, entry.Seed, null, $"no golden hash for this stage (actual {entry.Hash:x16}); accept the new golden file if the stage is new."));
            }
            else if (hash != entry.Hash)
            {
                issues.Add(new GenerationIssue(GenerationIssueKind.GoldenDrift, entry.Stage, entry.Seed, null, $"hash {entry.Hash:x16} differs from golden {hash:x16}: the stage's output changed; bump the generation version (now {pipeline.Version}) if intended."));
            }
        }

        foreach (var entry in expected.Entries)
        {
            if (seeds.Contains(entry.Seed) && !actual.TryGet(entry.Seed, entry.Stage, out _))
            {
                issues.Add(new GenerationIssue(GenerationIssueKind.GoldenDrift, entry.Stage, entry.Seed, null, "the golden file has this stage but the pipeline no longer does."));
            }
        }
    }
}
