using Colony.Scenario;

namespace Colony.Core.Tests;

/// <summary>
/// TASK-32: the hierarchical World hash (World, then Components and extension state, then chunk) for desync
/// localisation. The root equals <see cref="World.Hash"/>; two trees compare level by level and name the
/// diverging chunk; building the tree is on demand and costs Ticks nothing.
/// </summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class HashTreeTests
{
    [Fact]
    public void The_root_of_the_tree_is_the_world_hash_and_every_component_has_one_leaf_per_chunk()
    {
        var schema = ColonistScenario.BuildSchema();
        using var world = ColonistScenario.CreateWorld(schema, workers: 1, colonists: 5_000, food: 500);
        world.Tick();

        var tree = world.HashTree();

        Assert.Equal(world.Hash(), tree.WorldHash);
        Assert.Equal(world.CurrentTick, tree.Tick);
        Assert.Equal(schema.SchemaHash, tree.SchemaHash);
        var components = tree.Group("components");
        Assert.NotNull(components);
        var hunger = components.Child(schema.Describe(schema.Component<ColonistHunger>()).Name);
        Assert.NotNull(hunger);
        Assert.Equal(HashTreeLeafKind.ArchetypeChunk, hunger.LeafKind);
        int chunksWithHunger = 0;
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            var (signature, _, chunks) = world.DescribeArchetype(a);
            if (signature.Contains(schema.Component<ColonistHunger>()))
            {
                chunksWithHunger += chunks;
            }
        }

        Assert.Equal(chunksWithHunger, hunger.Leaves.Count);
        Assert.NotEqual(0, chunksWithHunger);
        Assert.NotNull(tree.Group("resources"));
        Assert.NotNull(tree.Group("random"));
    }

    [Fact]
    public void Two_trees_of_identical_worlds_compare_identical_and_a_perturbed_chunk_is_localised_exactly()
    {
        var schema = ColonistScenario.BuildSchema();
        using var a = ColonistScenario.CreateWorld(schema, workers: 1, colonists: 5_000, food: 500);
        using var b = ColonistScenario.CreateWorld(schema, workers: 2, colonists: 5_000, food: 500);
        a.Tick();
        b.Tick();
        var same = WorldHashTree.Compare(a.HashTree(), b.HashTree());
        Assert.True(same.Identical);
        Assert.Empty(same.Differences);

        // Negative control: poke one hunger value in a known chunk of b.
        var hunger = schema.Component<ColonistHunger>();
        int archetype = -1;
        for (int i = 0; i < b.ArchetypeCount; i++)
        {
            var (signature, entities, _) = b.DescribeArchetype(i);
            if (entities > 0 && signature.Contains(hunger))
            {
                archetype = i;
                break;
            }
        }

        Assert.NotEqual(-1, archetype);
        int chunk = b.DescribeArchetype(archetype).ChunkCount - 1;
        var victim = b.ArchetypeEntities(archetype, chunk)[0];
        b.Get<ColonistHunger>(victim, hunger).Value += 1000f;

        var comparison = WorldHashTree.Compare(a.HashTree(), b.HashTree());

        Assert.False(comparison.Identical);
        var difference = Assert.Single(comparison.Differences);
        Assert.Equal("components", difference.Group);
        Assert.Equal(schema.Describe(hunger).Name, difference.Item);
        Assert.Equal(HashTreeLeafKind.ArchetypeChunk, difference.LeafKind);
        Assert.Equal(archetype, difference.Left!.Value.X);
        Assert.Equal(chunk, difference.Left.Value.Y);
        Assert.NotEqual(difference.Left.Value.Hash, difference.Right!.Value.Hash);
        Assert.Contains($"archetype {archetype} chunk {chunk}", difference.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tick_difference_is_reported_as_the_tick_group()
    {
        var schema = ColonistScenario.BuildSchema();
        using var a = ColonistScenario.CreateWorld(schema, workers: 1, colonists: 500, food: 50);
        using var b = ColonistScenario.CreateWorld(schema, workers: 1, colonists: 500, food: 50);
        a.Tick();

        var comparison = WorldHashTree.Compare(a.HashTree(), b.HashTree());

        Assert.False(comparison.Identical);
        Assert.Contains(comparison.Differences, d => d.Group == "tick");
    }

    [Fact]
    public void Trees_of_different_schemas_are_refused()
    {
        using var a = new WorldBuilder().DebugValidation(false).Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var b = new WorldBuilder().AddComponent<ColonistHunger>("hunger").DebugValidation(false).Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        Assert.NotEqual(a.Schema.SchemaHash, b.Schema.SchemaHash);
        Assert.Throws<ArgumentException>(() => WorldHashTree.Compare(a.HashTree(), b.HashTree()));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(0)]
    public void The_tree_is_identical_on_one_and_many_workers(int workers)
    {
        var schema = ColonistScenario.BuildSchema();
        using var one = ColonistScenario.CreateWorld(schema, workers: 1, colonists: 20_000, food: 2_000);
        using var many = ColonistScenario.CreateWorld(schema, workers, colonists: 20_000, food: 2_000);
        for (int t = 0; t < 10; t++)
        {
            one.Tick();
            many.Tick();
        }

        var left = one.HashTree();
        var right = many.HashTree();
        Assert.Equal(left.WorldHash, right.WorldHash);
        Assert.True(WorldHashTree.Compare(left, right).Identical);
        Assert.Equal(left.Groups.Count, right.Groups.Count);
        for (int g = 0; g < left.Groups.Count; g++)
        {
            Assert.Equal(left.Groups[g].Hash, right.Groups[g].Hash);
        }
    }

    [Fact]
    public void Ticks_that_do_not_ask_for_the_tree_allocate_nothing()
    {
        var schema = ColonistScenario.BuildSchema();
        using var world = ColonistScenario.CreateWorld(schema, workers: 2, colonists: 5_000, food: 500);
        for (int t = 0; t < 4; t++)
        {
            world.Tick();
        }

        _ = world.HashTree(); // a tree was asked for once; the Ticks after it must stay allocation free
        long workersBefore = world.WorkerAllocatedBytes;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 4; t++)
        {
            world.Tick();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long workersAllocated = world.WorkerAllocatedBytes - workersBefore;
        Assert.Equal(0, allocated);
        Assert.Equal(0, workersAllocated);
    }
}
