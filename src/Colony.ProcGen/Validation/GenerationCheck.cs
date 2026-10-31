using Colony.Fields;

namespace Colony.ProcGen.Validation;

/// <summary>
/// A Game-supplied property of the generated World the validator checks per seed (spec section 9.3, "extensible
/// validators"), for example connectivity or resource placement. Failures are reported with
/// <see cref="GenerationCheckContext.Report"/> and become <see cref="GenerationIssueKind.Check"/> issues.
/// </summary>
public abstract class GenerationCheck
{
    /// <summary>The name issues are attributed to.</summary>
    public abstract string Name { get; }

    public abstract void Check(GenerationCheckContext context);
}

/// <summary>The generated chunks of one seed, as a <see cref="GenerationCheck"/> sees them.</summary>
public sealed class GenerationCheckContext
{
    private readonly GeneratedChunk?[] _grid;
    private readonly List<GenerationIssue> _issues;
    private readonly string _check;

    internal GenerationCheckContext(GenerationPipeline pipeline, ulong seed, GeneratedChunk?[] grid, List<GenerationIssue> issues, string check)
    {
        Pipeline = pipeline;
        Seed = seed;
        _grid = grid;
        _issues = issues;
        _check = check;
    }

    public GenerationPipeline Pipeline { get; }

    public ulong Seed { get; }

    public WorldSize Size => Pipeline.Size;

    /// <summary>The generated chunk at a coordinate, or null when it was not among the validated chunks.</summary>
    public GeneratedChunk? ChunkAt(ChunkCoord coord)
    {
        var size = Size;
        if ((uint)coord.X >= (uint)size.ChunksX || (uint)coord.Y >= (uint)size.ChunksY || (uint)coord.Z >= (uint)size.ChunksZ)
        {
            return null;
        }

        return _grid[coord.X + (size.ChunksX * (coord.Y + (size.ChunksY * coord.Z)))];
    }

    /// <summary>Reports a failure of this check.</summary>
    public void Report(string message, ChunkCoord? chunk = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        _issues.Add(new GenerationIssue(GenerationIssueKind.Check, _check, Seed, chunk, message));
    }
}

/// <summary>
/// Checks that the passable tiles of a Field (by <paramref name="passable"/>) form one 6-connected region over the
/// World's tiles; tiles of chunks that were not validated count as impassable.
/// </summary>
public sealed class ConnectivityCheck<T>(string name, Field<T> field, Func<T, bool> passable) : GenerationCheck
    where T : unmanaged
{
    private readonly Func<T, bool> _passable = passable ?? throw new ArgumentNullException(nameof(passable));

    public override string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public override void Check(GenerationCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = context.Size;
        int sx = size.X;
        int sy = size.Y;
        int tiles = sx * sy * size.Z;

        // 0 = impassable, 1 = passable and unvisited, 2 = visited.
        var state = new byte[tiles];
        int passableCount = 0;
        for (int z = 0; z < size.Z; z++)
        {
            for (int y = 0; y < sy; y++)
            {
                for (int x = 0; x < sx; x++)
                {
                    var chunk = context.ChunkAt(ChunkCoord.OfTile(new TilePos(x, y, z)));
                    if (chunk is not null && _passable(chunk.Cells(field)[FieldChunk.CellIndex(x & FieldChunk.Mask, y & FieldChunk.Mask, z & FieldChunk.Mask)]))
                    {
                        state[x + (sx * (y + (sy * z)))] = 1;
                        passableCount++;
                    }
                }
            }
        }

        var queue = new int[tiles];
        int regions = 0;
        int largest = 0;
        for (int start = 0; start < tiles; start++)
        {
            if (state[start] != 1)
            {
                continue;
            }

            regions++;
            int head = 0;
            int tail = 0;
            queue[tail++] = start;
            state[start] = 2;
            while (head < tail)
            {
                int tile = queue[head++];
                int x = tile % sx;
                int y = (tile / sx) % sy;
                int z = tile / (sx * sy);
                Visit(x > 0, tile - 1);
                Visit(x < sx - 1, tile + 1);
                Visit(y > 0, tile - sx);
                Visit(y < sy - 1, tile + sx);
                Visit(z > 0, tile - (sx * sy));
                Visit(z < size.Z - 1, tile + (sx * sy));
            }

            largest = Math.Max(largest, tail);

            void Visit(bool inside, int next)
            {
                if (inside && state[next] == 1)
                {
                    state[next] = 2;
                    queue[tail++] = next;
                }
            }
        }

        if (regions > 1)
        {
            context.Report($"{passableCount} passable tiles of Field '{context.Pipeline.Fields.First(f => f.Id == field.Id).Name}' fall into {regions} regions; the largest has {largest}.");
        }
    }
}
