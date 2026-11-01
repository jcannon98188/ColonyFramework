using Colony.Fields;

namespace Colony.ProcGen.Tests.Pipeline;

/// <summary>TASK-26 AC #5: never-generated chunks are generated on demand through the pipeline and are not stored until changed; the evict/reload round trip keeps the hash.</summary>
public sealed class StreamingGenerationTests
{
    private const ulong Seed = 0x5EED_0026UL;

    private static TerrainPipeline.Composition ComposeStreaming(ChunkResidency initial)
        => TerrainPipeline.Compose(c => c.Builder.AddResidency(c.Fields, c.Builder.Component<TilePos>(), new ResidencyOptions { InitialResidency = initial }));

    [Fact]
    public void Chunks_are_generated_on_demand_when_a_focus_point_reaches_them_and_dropped_again_while_unchanged()
    {
        var composition = ComposeStreaming(ChunkResidency.Unloaded);
        using var world = composition.Builder.Build().CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = 2 });
        var fields = world.Fields();
        var residency = world.Residency();
        var generation = world.Generation();
        Assert.Same(generation, fields.ChunkSource);
        Assert.Equal(fields.ChunkCount, residency.Counts.Unloaded);
        Assert.All(Enumerable.Range(0, fields.ChunkCount), o => Assert.False(residency.HasBeenGenerated(o) || residency.HasStoredCells(o)));

        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, new TilePos(20, 20, 0), 0)); // chunk (1, 1, 0)
        world.Tick();
        world.Tick();

        // The Active chunk and its Hibernating ring were generated; the column x = 3 was never touched.
        var centre = new ChunkCoord(1, 1, 0);
        Assert.Equal(ChunkResidency.Active, residency.ResidencyOf(centre));
        Assert.Equal(ChunkResidency.Hibernating, residency.ResidencyOf(new ChunkCoord(2, 2, 0)));
        Assert.Equal(ChunkResidency.Unloaded, residency.ResidencyOf(new ChunkCoord(3, 1, 0)));
        var expected = generation.Provider.Generate(centre);
        var height = fields.Accessor(composition.Height);
        var cells = expected.Cells(composition.Height);
        for (int i = 0; i < FieldChunk.Size * FieldChunk.Size; i++)
        {
            var (x, y, _) = FieldChunk.CellCoordinates(i);
            Assert.Equal(cells[i], height[centre.Origin.X + x, centre.Origin.Y + y, 0]);
        }

        var placed = EntitiesIn(world, composition, centre);
        Assert.Equal(expected.PlacementCount, placed.Count);
        Assert.Equal(expected.Placements.ToArray().Select(p => p.Id).Order(), placed.Select(p => p.Entity.Id).Order());
        Assert.All(placed, p => Assert.False(residency.IsFrozen(p.Id)));
        var ring = new ChunkCoord(2, 2, 0);
        var ringEntities = EntitiesIn(world, composition, ring);
        Assert.Equal(generation.Provider.Generate(ring).PlacementCount, ringEntities.Count);
        Assert.All(ringEntities, p => Assert.True(residency.IsFrozen(p.Id)));
        Assert.True(residency.HasBeenGenerated(fields.OrdinalOf(ring)));
        Assert.Empty(EntitiesIn(world, composition, new ChunkCoord(3, 1, 0)));
        Assert.False(residency.HasBeenGenerated(fields.OrdinalOf(new ChunkCoord(3, 1, 0))));

        // Change one cell of the Active chunk, then move the focus to the far corner: the changed chunk is kept as its
        // save section, the untouched ring chunks are regenerable and keep nothing.
        height[centre.Origin.X + 3, centre.Origin.Y + 3, 0] = -1;
        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, new TilePos(60, 40, 0), 0)); // chunk (3, 2, 0)
        world.Tick();
        world.Tick();
        Assert.Equal(ChunkResidency.Unloaded, residency.ResidencyOf(centre));
        Assert.Equal(ChunkResidency.Unloaded, residency.ResidencyOf(new ChunkCoord(0, 0, 0)));
        Assert.True(residency.HasStoredCells(fields.OrdinalOf(centre)));
        Assert.False(residency.HasStoredCells(fields.OrdinalOf(new ChunkCoord(0, 0, 0))));
        Assert.True(residency.HasBeenGenerated(fields.OrdinalOf(new ChunkCoord(0, 0, 0))));
        Assert.All(placed, p => Assert.True(residency.IsFrozen(p.Id))); // chunk-owned Entities stay, frozen in place
        Assert.True(residency.RingInvariantHolds());

        // Coming back: the stored chunk shows the change, the regenerated one its generated cells, and no Entity is placed twice.
        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, new TilePos(20, 20, 0), 0));
        world.Tick();
        world.Tick();
        Assert.Equal(-1, height[centre.Origin.X + 3, centre.Origin.Y + 3, 0]);
        Assert.Equal(expected.PlacementCount, EntitiesIn(world, composition, centre).Count);
        Assert.Equal(generation.Provider.Generate(ring).PlacementCount, EntitiesIn(world, composition, ring).Count);
        Assert.All(EntitiesIn(world, composition, centre), p => Assert.False(residency.IsFrozen(p.Id)));
    }

    [Fact]
    public void Streaming_everything_in_on_demand_and_a_forced_round_trip_match_a_World_generated_up_front()
    {
        var upFront = TerrainPipeline.Compose(c => c.Builder.AddResidency(c.Fields, c.Builder.Component<TilePos>()));
        using var reference = upFront.Builder.Build().CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = 1 });
        reference.Generation().GenerateAll();

        var streaming = ComposeStreaming(ChunkResidency.Unloaded);
        using var world = streaming.Builder.Build().CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = 1 });

        var everything = new FocusPoint(1, new TilePos(32, 24, 0), 4);
        foreach (var w in new[] { reference, world })
        {
            w.Inputs.Submit(new InputSource(1), everything);
            w.Tick();
            w.Tick();
        }

        // The schemas differ (initial residency is schema data), so compare the Fields and the placed Entities rather than the World hash.
        var fields = world.Fields();
        Assert.Equal(fields.ChunkCount, world.Residency().Counts.Active);
        Assert.Equal(FieldsHash(reference), FieldsHash(world));
        Assert.Equal(AllPlacements(reference, upFront), AllPlacements(world, streaming));

        // AC #2 on the streaming World itself: out and back in, nothing kept in between, same hash afterwards.
        ulong before = world.Hash();
        int ordinal = fields.OrdinalOf(new ChunkCoord(2, 1, 0));
        var storage = fields.StorageOf(streaming.Height);
        long allocated = storage.CellBytesAllocated;
        long chunkBytes = storage.IsUniform(ordinal) ? 0 : storage.CellSize * FieldChunk.CellCount;
        Assert.True(chunkBytes > 0, "the test chunk should be dense");
        world.Residency().ForceResidency(ordinal, ChunkResidency.Unloaded);
        Assert.False(world.Residency().HasStoredCells(ordinal)); // unchanged since generation: regenerable, nothing kept
        Assert.Equal(allocated - chunkBytes, storage.CellBytesAllocated);
        Assert.Equal(FieldsHash(reference), FieldsHash(world)); // hashed from the regeneration
        world.Residency().ForceResidency(ordinal, ChunkResidency.Active);
        Assert.Equal(allocated, storage.CellBytesAllocated);
        Assert.Equal(before, world.Hash());
        Assert.Equal(AllPlacements(reference, upFront), AllPlacements(world, streaming)); // nothing placed twice

        for (int t = 0; t < 2; t++)
        {
            reference.Tick();
            world.Tick();
            Assert.Equal(FieldsHash(reference), FieldsHash(world));
        }
    }

    private static List<(ulong Id, TilePos Position)> AllPlacements(World world, TerrainPipeline.Composition composition)
    {
        var tilePos = world.Schema.Component<TilePos>();
        var generated = composition.Generation.GeneratedEntity;
        var result = new List<(ulong, TilePos)>();
        foreach (var chunk in world.Query(ComponentMask.Of(tilePos, generated), default, ignoreEnableBits: true))
        {
            var positions = chunk.Column<TilePos>(tilePos);
            var entities = chunk.Column<GeneratedEntity>(generated);
            for (int row = 0; row < positions.Length; row++)
            {
                result.Add((entities[row].Id, positions[row]));
            }
        }

        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    private static ulong FieldsHash(World world)
    {
        var state = new Hashing.Hash64State(0);
        ((IWorldExtension)world.Fields()).Hash(ref state);
        return state.Finish();
    }

    private static List<(EntityId Id, GeneratedEntity Entity)> EntitiesIn(World world, TerrainPipeline.Composition composition, ChunkCoord coord)
    {
        var tilePos = world.Schema.Component<TilePos>();
        var generated = composition.Generation.GeneratedEntity;
        var result = new List<(EntityId, GeneratedEntity)>();
        foreach (var chunk in world.Query(ComponentMask.Of(tilePos, generated), default, ignoreEnableBits: true))
        {
            var positions = chunk.Column<TilePos>(tilePos);
            var entities = chunk.Column<GeneratedEntity>(generated);
            var ids = chunk.Entities;
            for (int row = 0; row < positions.Length; row++)
            {
                if (positions[row].Chunk == coord)
                {
                    result.Add((ids[row], entities[row]));
                }
            }
        }

        return result;
    }
}
