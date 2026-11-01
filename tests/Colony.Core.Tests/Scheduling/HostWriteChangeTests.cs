namespace Colony.Core.Tests.Scheduling;

/// <summary>
/// Host writes (World API calls between Ticks) must be change-stamped like a System's declared Writes, so a
/// change-filtered System (the spatial index, the work index) sees them on its next run.
/// </summary>
public sealed class HostWriteChangeTests
{
    private static (WorldSchema Schema, ComponentType Health, List<long> Visited) WatchSchema()
    {
        var visited = new List<long>();
        var health = new ComponentType(0);
        var schema = new WorldBuilder().AddComponent<Health>("health")
            .AddSystem(new SystemDefinition
            {
                Name = "Watch",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(health),
                ChangeFilter = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext c, in ChunkView _) => visited.Add(c.Tick),
            })
            .Build();
        return (schema, health, visited);
    }

    private static EntityId CreateOne(World world, ComponentType health)
    {
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create(ComponentMask.Of(health));
        world.Playback(buffer);
        var id = buffer.Resolve(deferred);
        world.ResetScratch();
        return id;
    }

    [Fact]
    public void A_host_write_through_Get_is_seen_by_a_change_filtered_system_on_the_next_tick()
    {
        var (schema, health, visited) = WatchSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var id = CreateOne(world, health);

        world.Tick(); // 0: created before the first Tick
        world.Tick(); // 1: nothing changed
        world.Get<Health>(id, health).Value = 7;
        world.Tick(); // 2: host wrote between Ticks
        world.Tick(); // 3: nothing

        Assert.Equal([0L, 2L], visited);
    }

    [Fact]
    public void A_host_write_through_a_query_column_is_seen_by_a_change_filtered_system()
    {
        var (schema, health, visited) = WatchSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        CreateOne(world, health);

        world.Tick(); // 0
        world.Tick(); // 1
        foreach (var chunk in world.Query(health))
        {
            chunk.Column<Health>(health)[0].Value = 3;
        }

        world.Tick(); // 2
        world.Tick(); // 3

        Assert.Equal([0L, 2L], visited);
    }

    [Fact]
    public void A_host_read_through_GetReadOnly_does_not_stamp_the_chunk()
    {
        var (schema, health, visited) = WatchSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var id = CreateOne(world, health);
        world.Get<Health>(id, health).Value = 5;

        world.Tick(); // 0
        int value = world.GetReadOnly<Health>(id, health).Value;
        world.Tick(); // 1: a read is not a change

        Assert.Equal(5, value);
        Assert.Equal([0L], visited);
    }

    [Fact]
    public void A_get_from_inside_a_system_hook_is_not_stamped_by_the_host_rule()
    {
        // Inside a Tick only the System's declared Writes stamp (TASK-7): a serial hook that reads another
        // Component through World.Get must not make an unrelated change-filtered System revisit the chunk.
        var visited = new List<long>();
        var health = new ComponentType(0);
        EntityId target = default;
        var schema = new WorldBuilder().AddComponent<Health>("health")
            .AddSystem(new SystemDefinition
            {
                Name = "Peek",
                Phase = Phase.Simulate,
                Begin = (in SystemContext c) => _ = c.World.Get<Health>(target, health).Value,
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Watch",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(health),
                ChangeFilter = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext c, in ChunkView _) => visited.Add(c.Tick),
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        target = CreateOne(world, health);

        world.Tick();
        world.Tick();
        world.Tick();

        Assert.Equal([0L], visited);
    }
}
