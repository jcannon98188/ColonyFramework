namespace Colony.Core.Tests;

/// <summary>
/// AC #4: after warm-up, a 100k-Entity create, iterate and mutate loop allocates nothing on the managed
/// heap. Measured with GC.GetAllocatedBytesForCurrentThread around the steady-state iterations, which
/// is what the Framework's allocation harness (TASK-10) will do across all Tick threads.
/// </summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class AllocationTests
{
    private const int EntityCount = 100_000;

    [Fact]
    public void A_100k_entity_create_iterate_and_mutate_loop_allocates_nothing_in_steady_state()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld(new WorldOptions { InitialEntityCapacity = EntityCount * 2 });
        var position = world.Schema.Component<Position>();
        var hunger = world.Schema.Component<Hunger>();
        var health = world.Schema.Component<Health>();
        var colonist = ComponentMask.Of(position, hunger, health);
        var buffer = world.CreateCommandBuffer();

        // Warm-up: the first pass rents slabs, grows the Entity index and the arena, JITs everything.
        RunTick(world, buffer, colonist, hunger, health, position);
        RunTick(world, buffer, colonist, hunger, health, position);
        Assert.Equal(EntityCount, world.EntityCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            RunTick(world, buffer, colonist, hunger, health, position);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(EntityCount, world.EntityCount);
    }

    /// <summary>
    /// One steady-state Tick: churn 1% of the colony through the command buffer (create new, destroy
    /// old), iterate every chunk with SIMD-friendly padded columns, mutate per Entity, reset scratch.
    /// </summary>
    private static void RunTick(World world, CommandBuffer buffer, ComponentMask colonist, ComponentType hunger, ComponentType health, ComponentType position)
    {
        int alive = world.EntityCount;
        int toCreate = EntityCount - alive + Math.Min(alive, EntityCount / 100);
        for (int i = 0; i < toCreate; i++)
        {
            var e = buffer.Create(colonist);
            buffer.Add(e, position, new Position { X = i, Y = -i });
        }

        int destroyed = 0;
        int destroyBudget = Math.Min(alive, EntityCount / 100);
        foreach (var chunk in world.Query(colonist))
        {
            var entities = chunk.Entities;
            for (int row = 0; row < entities.Length && destroyed < destroyBudget; row += 7)
            {
                buffer.Destroy(entities[row]);
                destroyed++;
            }
        }

        world.Playback(buffer);

        foreach (var chunk in world.Query(colonist))
        {
            var hungers = chunk.PaddedColumn<Hunger>(hunger);
            for (int i = 0; i < hungers.Length; i++)
            {
                hungers[i].Value += 0.25f;
            }

            var healths = chunk.Column<Health>(health);
            var positions = chunk.Column<Position>(position);
            for (int i = 0; i < healths.Length; i++)
            {
                healths[i].Value += positions[i].X & 1;
            }
        }

        world.ResetScratch();
    }
}
