using Colony.Fields;

namespace Colony.ProcGen.Tests.Pipeline;

/// <summary>AC #1: a chunk is a pure function of (seed, coordinate), whatever the order or the thread.</summary>
public sealed class ChunkProviderTests
{
    private const ulong Seed = 0x5EED_0001UL;

    [Fact]
    public void A_chunk_generated_alone_in_any_order_or_on_any_thread_has_identical_bytes()
    {
        var pipeline = TerrainPipeline.Compose().Generation.Pipeline;
        var coords = TerrainPipeline.AllChunks();

        var forward = new ulong[coords.Length];
        var forwardProvider = pipeline.CreateProvider(Seed);
        for (int i = 0; i < coords.Length; i++)
        {
            forward[i] = forwardProvider.Generate(coords[i]).Hash();
        }

        var reverse = new ulong[coords.Length];
        var reverseProvider = pipeline.CreateProvider(Seed);
        var reused = reverseProvider.CreateChunk();
        for (int i = coords.Length - 1; i >= 0; i--)
        {
            reverseProvider.Generate(coords[i], reused);
            reverse[i] = reused.Hash();
        }

        var parallel = new ulong[coords.Length];
        var parallelProvider = pipeline.CreateProvider(Seed);
        Parallel.For(0, coords.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => parallel[i] = parallelProvider.Generate(coords[i]).Hash());

        Assert.Equal(forward, reverse);
        Assert.Equal(forward, parallel);
        Assert.Equal(coords.Length, forward.Distinct().Count());
    }

    [Fact]
    public void Generated_cells_are_the_bytes_the_stages_wrote()
    {
        var composition = TerrainPipeline.Compose();
        var provider = composition.Generation.Pipeline.CreateProvider(Seed);
        var chunk = provider.Generate(new ChunkCoord(1, 1, 0));

        var height = chunk.Cells(composition.Height);
        var smooth = chunk.Cells(composition.Smooth);
        Assert.Equal(FieldChunk.CellCount, height.Length);
        Assert.Contains(height.ToArray(), h => h != 0);
        Assert.NotEqual(height.ToArray(), smooth.ToArray());
        Assert.All(chunk.Placements.ToArray(), p => Assert.Equal(TerrainPipeline.TreeStage.Kind, p.Kind));
        Assert.Equal(new ChunkCoord(1, 1, 0), chunk.Coord);
        Assert.Equal(Seed, chunk.Seed);
    }
}
