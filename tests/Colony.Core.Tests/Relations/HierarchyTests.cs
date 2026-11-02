namespace Colony.Core.Tests.Relations;

/// <summary>AC #2: the built-in parent/child hierarchy with cascade delete removes descendants deterministically without managed allocation.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class HierarchyTests
{
    private static WorldSchema Schema(bool cascade = true)
    {
        var builder = new WorldBuilder().AddComponent<Position>("position").DebugValidation(false);
        builder.AddHierarchy(cascade);
        return builder.Build();
    }

    private static EntityId[] Spawn(World world, CommandBuffer buffer, int count)
    {
        var deferred = new EntityId[count];
        for (int i = 0; i < count; i++)
        {
            deferred[i] = buffer.Create(ComponentMask.Of(world.Schema.Component<Position>()));
        }

        world.Playback(buffer);
        var ids = new EntityId[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = buffer.Resolve(deferred[i]);
        }

        return ids;
    }

    [Fact]
    public void The_hierarchy_is_a_relation_named_colony_parent_with_parent_and_children_lookups()
    {
        var schema = Schema();
        Assert.True(schema.HasHierarchy);
        Assert.Equal(WorldBuilder.HierarchyRelationName, schema.DescribeRelation(schema.Hierarchy).Name);
        Assert.True(schema.DescribeRelation(schema.Hierarchy).CascadeDelete);
        Assert.False(new WorldBuilder().Build().HasHierarchy);
        Assert.Throws<InvalidOperationException>(() => new WorldBuilder().Build().Hierarchy);

        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var ids = Spawn(world, buffer, 4);
        buffer.SetParent(ids[1], ids[0]);
        buffer.SetParent(ids[2], ids[0]);
        buffer.SetParent(ids[3], ids[1]);
        world.Playback(buffer);

        Assert.Equal(ids[0], world.ParentOf(ids[1]));
        Assert.Equal(EntityId.Null, world.ParentOf(ids[0]));
        Assert.Equal(2, world.ChildrenOf(ids[0]).Count);
        Span<EntityId> children = stackalloc EntityId[4];
        Assert.Equal(2, world.ChildrenOf(ids[0]).CopyTo(children));
        Assert.Equal(ids[1], children[0]);
        Assert.Equal(ids[2], children[1]);

        buffer.ClearParent(ids[3]);
        world.Playback(buffer);
        Assert.Equal(EntityId.Null, world.ParentOf(ids[3]));
        Assert.Equal(0, world.ChildrenOf(ids[1]).Count);
    }

    [Fact]
    public void Destroying_a_parent_destroys_every_descendant_and_nothing_else()
    {
        var schema = Schema();
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var ids = Spawn(world, buffer, 8);
        // ids[0] -> {1, 2}; 1 -> {3, 4}; 4 -> {5}; 6 -> {7} is a separate tree.
        buffer.SetParent(ids[1], ids[0]);
        buffer.SetParent(ids[2], ids[0]);
        buffer.SetParent(ids[3], ids[1]);
        buffer.SetParent(ids[4], ids[1]);
        buffer.SetParent(ids[5], ids[4]);
        buffer.SetParent(ids[7], ids[6]);
        world.Playback(buffer);

        buffer.Destroy(ids[0]);
        world.Playback(buffer);

        for (int i = 0; i <= 5; i++)
        {
            Assert.False(world.IsAlive(ids[i]));
        }

        Assert.True(world.IsAlive(ids[6]));
        Assert.True(world.IsAlive(ids[7]));
        Assert.Equal(ids[6], world.ParentOf(ids[7]));
        Assert.Equal(2, world.EntityCount);
        Assert.Equal(6, world.Counters.Destroys);
        Assert.Equal(0, world.Counters.StaleSkipped);
    }

    [Fact]
    public void Destroying_a_subtree_root_detaches_it_from_its_parent_first()
    {
        var schema = Schema();
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var ids = Spawn(world, buffer, 3);
        buffer.SetParent(ids[1], ids[0]);
        buffer.SetParent(ids[2], ids[1]);
        world.Playback(buffer);

        buffer.Destroy(ids[1]);
        world.Playback(buffer);

        Assert.True(world.IsAlive(ids[0]));
        Assert.False(world.IsAlive(ids[1]));
        Assert.False(world.IsAlive(ids[2]));
        Assert.Equal(0, world.ChildrenOf(ids[0]).Count);
    }

    [Fact]
    public void A_cycle_is_destroyed_once_and_an_explicit_destroy_of_a_cascaded_child_is_a_conflict()
    {
        var schema = Schema();
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var ids = Spawn(world, buffer, 3);
        buffer.SetParent(ids[1], ids[0]);
        buffer.SetParent(ids[0], ids[1]);
        buffer.SetParent(ids[2], ids[0]);
        world.Playback(buffer);

        buffer.Destroy(ids[0]);
        buffer.Destroy(ids[2]);
        world.Playback(buffer);

        Assert.Equal(0, world.EntityCount);
        Assert.Equal(3, world.Counters.Destroys);
        Assert.Equal(1, world.Counters.StaleSkipped);
        var failure = Assert.Single(world.Events<StructuralFailure>(schema.StructuralFailures).ToArray());
        Assert.Equal(ids[2], failure.Entity);
        Assert.Equal(StructuralFailureReason.Conflict, failure.Reason);
    }

    [Fact]
    public void Without_cascade_children_are_orphaned_instead()
    {
        var schema = Schema(cascade: false);
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var ids = Spawn(world, buffer, 3);
        buffer.SetParent(ids[1], ids[0]);
        buffer.SetParent(ids[2], ids[1]);
        world.Playback(buffer);

        buffer.Destroy(ids[0]);
        world.Playback(buffer);

        Assert.True(world.IsAlive(ids[1]));
        Assert.Equal(EntityId.Null, world.ParentOf(ids[1]));
        Assert.Equal(ids[1], world.ParentOf(ids[2]));
    }

    [Fact]
    public void Cascade_delete_is_deterministic_across_insertion_orders()
    {
        var schema = Schema();
        using var a = schema.CreateWorld();
        using var b = schema.CreateWorld();
        BuildAndCollapse(a, reverseLinks: false);
        BuildAndCollapse(b, reverseLinks: true);
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.EntityCount, b.EntityCount);

        static void BuildAndCollapse(World world, bool reverseLinks)
        {
            var buffer = world.CreateCommandBuffer();
            var ids = Spawn(world, buffer, 40);
            for (int step = 0; step < 39; step++)
            {
                int i = reverseLinks ? 39 - step : step + 1;
                buffer.SetParent(ids[i], ids[(i - 1) / 3]);
            }

            world.Playback(buffer);
            buffer.Destroy(ids[1]);
            world.Playback(buffer);
            world.ResetScratch();
        }
    }

    [Fact]
    public void Cascade_delete_of_a_deep_tree_allocates_nothing_in_steady_state()
    {
        const int Nodes = 2000;
        var schema = Schema();
        using var world = schema.CreateWorld(new WorldOptions { InitialEntityCapacity = Nodes * 4 });
        var buffer = world.CreateCommandBuffer();

        // Warm-up: grows the Entity index, the relation index, the cascade work list and the arena.
        BuildTreeAndDestroyRoot(world, buffer, Nodes);
        BuildTreeAndDestroyRoot(world, buffer, Nodes);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            BuildTreeAndDestroyRoot(world, buffer, Nodes);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        int alive = world.EntityCount;

        Assert.Equal(0, allocated);
        Assert.Equal(0, alive);
    }

    /// <summary>Builds a tree of fan-out 3 (and one long chain) with deferred handles, plays it back, then destroys the root.</summary>
    private static void BuildTreeAndDestroyRoot(World world, CommandBuffer buffer, int nodes)
    {
        var position = world.Schema.Component<Position>();
        var root = buffer.Create(ComponentMask.Of(position));
        var previous = root;
        for (int i = 1; i < nodes; i++)
        {
            var node = buffer.Create(ComponentMask.Of(position));
            // Half the nodes form a chain (deep cascade), half a bushy tree under the root.
            buffer.SetParent(node, (i & 1) == 0 ? previous : root);
            if ((i & 1) == 0)
            {
                previous = node;
            }
        }

        world.Playback(buffer);
        var rootId = buffer.Resolve(root);
        buffer.Destroy(rootId);
        world.Playback(buffer);
        world.ResetScratch();
    }
}
