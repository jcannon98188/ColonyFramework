using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>
/// TASK-40 AC #2: a flow Field never crosses an Unloaded chunk. The search stops at its border, steps never
/// enter it, and a tile whose answer depends on it reports <see cref="FlowStepStatus.NeedsLoading"/> with the
/// chunk in <see cref="FlowField.MissingChunk"/>, as the HPA* path reports NeedsLoading + MissingChunk.
/// </summary>
public sealed class FlowUnloadedTests
{
    // 4 x 1 x 1 chunks, open floor; the only way from chunk 0 to the goal in chunk 3 is through chunk 1.
    private static readonly WorldSize Size = new(64, 16, 1);
    private static readonly ChunkCoord Middle = new(1, 0, 0);
    private static readonly TilePos Goal = new(60, 5, 0);
    private static readonly TilePos West = new(2, 5, 0);
    private static readonly TilePos East = new(40, 5, 0);

    private static World Create(out MovementClass walker)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        builder.AddComponent<TilePos>("tile.pos");
        var fields = builder.AddFields(Size);
        var terrain = fields.AddField<byte>("terrain");
        builder.AddResidency(fields, builder.Component<TilePos>());
        var pathfinding = builder.AddPathfinding(fields);
        walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2 });
        world.Pathfinding().EnsureBuilt(walker);
        return world;
    }

    private static void Unload(World world, ChunkCoord chunk) => world.Residency().ForceResidency(world.Fields().OrdinalOf(chunk), ChunkResidency.Unloaded);

    /// <summary>The oracle with the Unloaded chunks treated as walls.</summary>
    private static void AssertMatchesOracle(World world, FlowField field, params ChunkCoord[] unloaded)
    {
        var expected = FlowOracle.Expected(world, field.Goals, field.Bounds.Min, field.Bounds.Max, t => Array.IndexOf(unloaded, t.Chunk) < 0);
        for (int y = 0; y < Size.Y; y++)
        {
            for (int x = 0; x < Size.X; x++)
            {
                Assert.Equal(expected[x + (Size.X * y)], field.DistanceTo(new TilePos(x, y, 0)));
            }
        }
    }

    [Fact]
    public void A_field_built_with_an_Unloaded_chunk_in_its_bound_stops_at_it_and_names_it()
    {
        using var world = Create(out var walker);
        Unload(world, Middle);
        var service = world.Pathfinding();

        var field = service.FlowField([Goal], walker, radiusChunks: 3);

        Assert.True(field.NeedsLoading);
        Assert.Equal(Middle, field.MissingChunk);
        AssertMatchesOracle(world, field, Middle);
        Assert.Equal(FlowStepStatus.NeedsLoading, field.TryStep(West, out _));
        Assert.Equal(FlowStepStatus.NeedsLoading, service.TryNextStep(field, West, out _));
        Assert.Equal(FlowStepStatus.NeedsLoading, field.TryStep(new TilePos(20, 5, 0), out _)); // inside the Unloaded chunk
        Assert.Equal(FlowStepStatus.Step, field.TryStep(East, out var next));
        Assert.Equal(new TilePos(41, 5, 0), next);

        // The westmost loaded tile east of the gap steps away from it, never into it.
        Assert.Equal(FlowStepStatus.Step, field.TryStep(new TilePos(32, 5, 0), out next));
        Assert.Equal(new TilePos(33, 5, 0), next);
    }

    [Fact]
    public void A_goal_inside_an_Unloaded_chunk_makes_the_field_need_that_chunk()
    {
        using var world = Create(out var walker);
        Unload(world, new ChunkCoord(3, 0, 0));

        var field = world.Pathfinding().FlowField([Goal], walker, radiusChunks: 3);

        Assert.True(field.NeedsLoading);
        Assert.Equal(new ChunkCoord(3, 0, 0), field.MissingChunk);
        Assert.Equal(FlowStepStatus.NeedsLoading, field.TryStep(East, out _));
        Assert.Equal(-1, field.DistanceTo(East));
    }

    [Fact]
    public void A_field_with_no_Unloaded_chunk_in_reach_needs_nothing()
    {
        using var world = Create(out var walker);
        var field = world.Pathfinding().FlowField([Goal], walker, radiusChunks: 3);

        Assert.False(field.NeedsLoading);
        Assert.Equal(FlowStepStatus.Step, field.TryStep(West, out _));
        AssertMatchesOracle(world, field);
    }

    [Fact]
    public void Unloading_a_chunk_after_the_build_repairs_the_field_around_it_and_reloading_restores_it()
    {
        using var world = Create(out var walker);
        var service = world.Pathfinding();
        var field = service.FlowField([Goal], walker, radiusChunks: 3);
        Assert.Equal(FlowStepStatus.Step, field.TryStep(West, out _));

        // Unloaded between Ticks: asking for the Field again repairs it before returning it.
        Unload(world, Middle);
        Assert.Same(field, service.FlowField([Goal], walker, radiusChunks: 3));
        Assert.True(field.NeedsLoading);
        Assert.Equal(Middle, field.MissingChunk);
        Assert.Equal(FlowStepStatus.NeedsLoading, field.TryStep(West, out _));
        AssertMatchesOracle(world, field, Middle);

        // The streaming Phase reloads it (no focus points: the never-unload policy), the Begin repair pass picks it up.
        world.Tick();
        Assert.Equal(ChunkResidency.Active, world.Fields().ResidencyOf(world.Fields().OrdinalOf(Middle)));
        Assert.False(field.NeedsLoading);
        Assert.Equal(FlowStepStatus.Step, field.TryStep(West, out var next));
        Assert.Equal(new TilePos(3, 5, 0), next);
        AssertMatchesOracle(world, field);
    }

    [Fact]
    public void A_streamed_World_walking_a_focus_point_keeps_every_field_equal_to_the_oracle_over_loaded_chunks()
    {
        // Everything starts Unloaded and streams in around a focus point that walks east, one chunk every Tick.
        var builder = new WorldBuilder().DebugValidation(false);
        builder.AddComponent<TilePos>("tile.pos");
        var fields = builder.AddFields(Size);
        var terrain = fields.AddField<byte>("terrain");
        builder.AddResidency(fields, builder.Component<TilePos>(), new ResidencyOptions { InitialResidency = ChunkResidency.Unloaded });
        var pathfinding = builder.AddPathfinding(fields);
        var walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2 });
        world.Fields().ChunkSource = new FloorSource();
        var service = world.Pathfinding();
        service.EnsureBuilt(walker);

        var unloaded = new List<ChunkCoord>();
        int stepsWithUnloaded = 0;
        for (int step = 0; step < 4; step++)
        {
            world.Inputs.Submit(new InputSource(1), new FocusPoint(1, new TilePos(8 + (16 * step), 8, 0), 0));
            world.Tick();
            world.Tick();
            var field = service.FlowField([Goal], walker, radiusChunks: 3);

            unloaded.Clear();
            for (int x = 0; x < Size.ChunksX; x++)
            {
                var chunk = new ChunkCoord(x, 0, 0);
                if (world.Fields().ResidencyOf(world.Fields().OrdinalOf(chunk)) == ChunkResidency.Unloaded)
                {
                    unloaded.Add(chunk);
                }
            }

            AssertMatchesOracle(world, field, [.. unloaded]);
            Assert.Equal(unloaded.Count > 0, field.NeedsLoading);
            if (field.NeedsLoading)
            {
                stepsWithUnloaded++;
                Assert.Contains(field.MissingChunk, unloaded);
            }
        }

        Assert.True(stepsWithUnloaded > 0); // the walk really streamed: some step had part of the bound Unloaded
    }

    private sealed class FloorSource : IChunkSource
    {
        public void GenerateCells(ChunkCoord coord, ChunkCellBuffer cells)
        {
        }

        public void PlaceEntities(ChunkCoord coord, CommandBuffer commands)
        {
        }
    }
}
