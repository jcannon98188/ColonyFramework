using Colony.Fields;
using Colony.Persistence;

namespace Colony.Jobs.Tests;

/// <summary>AC #1 (Reachable) and AC #2: the index follows the change feed in the Late Phase and is rebuilt on load, never saved or hashed.</summary>
public sealed class WorkIndexUpdateTests
{
    [Fact]
    public void Items_behind_a_wall_are_excluded_by_the_reachability_filter_for_the_requesting_movement_class()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 16, 1));
        fixture.WallColumn(20);
        var standing = new TilePos(10, 8, 0);
        var ids = fixture.SpawnItems(
        [
            (new TilePos(22, 8, 0), WorkTestWorld.Mine), // nearest, but behind the wall
            (new TilePos(5, 8, 0), WorkTestWorld.Mine),
            (new TilePos(20, 8, 0), WorkTestWorld.Mine), // on the wall tile itself: impassable
        ]);
        fixture.World.Tick();
        var hits = new WorkHit[4];

        int unfiltered = fixture.WorkIndex.QueryNearest(standing, WorkTestWorld.Mine, 50, hits);
        Assert.Equal(3, unfiltered);
        int reachable = fixture.WorkIndex.QueryNearest(standing, WorkTestWorld.Mine, 50, hits, new ReachableFrom(fixture.Pathfinding, standing, fixture.Walker));

        Assert.Equal(1, reachable);
        Assert.Equal(ids[1], hits[0].Entity);
    }

    [Fact]
    public void Opening_a_door_makes_items_behind_the_wall_reachable_from_the_next_Tick()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 16, 1));
        fixture.WallColumn(20);
        var standing = new TilePos(10, 8, 0);
        var ids = fixture.SpawnItems([(new TilePos(22, 8, 0), WorkTestWorld.Mine)]);
        fixture.World.Tick();
        var hits = new WorkHit[4];
        Assert.Equal(0, fixture.WorkIndex.QueryNearest(standing, WorkTestWorld.Mine, 50, hits, new ReachableFrom(fixture.Pathfinding, standing, fixture.Walker)));

        fixture.WallColumn(20, doorY: 3);
        fixture.World.Tick();

        Assert.Equal(1, fixture.WorkIndex.QueryNearest(standing, WorkTestWorld.Mine, 50, hits, new ReachableFrom(fixture.Pathfinding, standing, fixture.Walker)));
        Assert.Equal(ids[0], hits[0].Entity);
    }

    [Fact]
    public void Items_appear_only_after_the_Late_Phase_update_and_only_while_available()
    {
        using var fixture = new WorkTestWorld(new WorldSize(32, 32, 1));
        var origin = new TilePos(0, 0, 0);
        var ids = fixture.SpawnItems([(new TilePos(1, 0, 0), WorkTestWorld.Haul), (new TilePos(2, 0, 0), WorkTestWorld.Haul)]);
        Assert.Empty(fixture.Nearest(origin, 10, 4, WorkTestWorld.Haul));

        fixture.World.Tick();
        Assert.Equal([ids[0], ids[1]], fixture.Nearest(origin, 10, 4, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());
        Assert.Equal(2, fixture.WorkIndex.Count);

        // Taking an item through a System write (stamped) drops it from the lists at the next update.
        TakeThroughSystem(fixture, ids[0]);
        Assert.Equal([ids[1]], fixture.Nearest(origin, 10, 4, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());
        Assert.Equal(1, fixture.WorkIndex.CountOfType(WorkTestWorld.Haul));

        // Releasing it brings it back.
        TakeThroughSystem(fixture, ids[0], available: true);
        Assert.Equal([ids[0], ids[1]], fixture.Nearest(origin, 10, 4, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());
    }

    [Fact]
    public void Host_writes_through_World_Get_between_Ticks_update_the_index_at_the_next_Late_update()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 64, 1));
        var world = fixture.World;
        var origin = new TilePos(0, 0, 0);
        var ids = fixture.SpawnItems([(new TilePos(1, 0, 0), WorkTestWorld.Haul), (new TilePos(2, 0, 0), WorkTestWorld.Haul)]);
        world.Tick();

        // Taken by the host: no command buffer, no System.
        ref var item = ref world.Get<WorkItem>(ids[0], fixture.Index.WorkItem);
        item = item.WithAvailable(false);
        world.Tick();
        Assert.Equal([ids[1]], fixture.Nearest(origin, 10, 4, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());

        // Moved by the host.
        world.Get<TilePos>(ids[1], fixture.TilePos) = new TilePos(40, 40, 0);
        world.Tick();
        Assert.Empty(fixture.Nearest(origin, 10, 4, WorkTestWorld.Haul));
        Assert.Equal([ids[1]], fixture.Nearest(new TilePos(40, 40, 0), 0, 4, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());
    }

    [Fact]
    public void A_moved_item_is_found_at_its_new_tile_and_a_retyped_item_under_its_new_type()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 64, 1));
        var id = fixture.SpawnItem(new TilePos(1, 1, 0), WorkTestWorld.Haul);
        fixture.World.Tick();

        var buffer = fixture.World.CreateCommandBuffer();
        buffer.Add(id, fixture.TilePos, new TilePos(40, 40, 0));
        buffer.Add(id, fixture.Index.WorkItem, WorkItem.Available(WorkTestWorld.Build));
        fixture.World.Playback(buffer);
        fixture.World.ResetScratch();
        fixture.World.Tick();

        Assert.Empty(fixture.Nearest(new TilePos(0, 0, 0), 5, 4, WorkTestWorld.Haul, WorkTestWorld.Build));
        var hits = fixture.Nearest(new TilePos(40, 40, 0), 0, 4, WorkTestWorld.Build);
        Assert.Equal([id], hits.Select(h => h.Entity).ToArray());
        Assert.Equal(1, fixture.WorkIndex.Count);
        Assert.Equal(0, fixture.WorkIndex.CountOfType(WorkTestWorld.Haul));
    }

    [Fact]
    public void Destroyed_items_and_items_that_lose_a_Component_leave_the_index_at_the_next_update()
    {
        using var fixture = new WorkTestWorld(new WorldSize(32, 32, 1));
        var ids = fixture.SpawnItems([(new TilePos(1, 0, 0), WorkTestWorld.Haul), (new TilePos(2, 0, 0), WorkTestWorld.Haul), (new TilePos(3, 0, 0), WorkTestWorld.Haul)]);
        fixture.World.Tick();
        Assert.Equal(3, fixture.WorkIndex.Count);

        var buffer = fixture.World.CreateCommandBuffer();
        buffer.Destroy(ids[0]);
        buffer.Remove(ids[1], fixture.TilePos);
        fixture.World.Playback(buffer);
        fixture.World.ResetScratch();
        fixture.World.Tick();

        Assert.Equal([ids[2]], fixture.Nearest(new TilePos(0, 0, 0), 10, 4, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());
        Assert.Equal(1, fixture.WorkIndex.Count);
        Assert.Equal(2, fixture.WorkIndex.LastUpdateRemoved);
    }

    [Fact]
    public void Items_outside_the_World_are_not_listed_and_a_type_beyond_the_registered_count_faults_the_update()
    {
        using var fixture = new WorkTestWorld(new WorldSize(32, 32, 1), options: new WorkIndexOptions { WorkTypeCount = 3 });
        fixture.SpawnItem(new TilePos(-1, 0, 0), WorkTestWorld.Haul);
        fixture.SpawnItem(new TilePos(0, 40, 0), WorkTestWorld.Haul);
        fixture.World.Tick();
        Assert.Equal(0, fixture.WorkIndex.Count);

        fixture.SpawnItem(new TilePos(0, 0, 0), 3);
        Assert.Throws<SystemFaultException>(() => fixture.World.Tick());
    }

    [Fact]
    public void The_incremental_update_matches_a_rebuild_and_a_naive_model_under_churn_on_1_and_4_workers()
    {
        var one = Churn(workers: 1);
        var four = Churn(workers: 4);

        Assert.Equal(one, four);
    }

    [Fact]
    public void The_index_is_not_hashed_and_a_loaded_World_rebuilds_it()
    {
        using var fixture = new WorkTestWorld(new WorldSize(32, 32, 1), pathfinding: false);
        var ids = fixture.SpawnItems([(new TilePos(1, 0, 0), WorkTestWorld.Haul), (new TilePos(2, 0, 0), WorkTestWorld.Mine), (new TilePos(3, 0, 0), WorkTestWorld.Haul)], available: true);
        fixture.SpawnItem(new TilePos(4, 0, 0), WorkTestWorld.Haul, available: false);
        ulong before = fixture.World.Hash();
        fixture.World.Tick();
        ulong afterIndexing = fixture.World.Hash();
        fixture.WorkIndex.Clear();
        Assert.Equal(afterIndexing, fixture.World.Hash());
        fixture.WorkIndex.Rebuild();
        Assert.Equal(3, fixture.WorkIndex.Count);
        Assert.NotEqual(before, afterIndexing); // the Tick itself moves the hash; the index never does

        byte[] image = fixture.World.SaveToBytes();
        using var loaded = WorldPersistence.Load(image, fixture.Schema);

        var index = loaded.WorkIndex();
        Assert.Equal(3, index.Count);
        var hits = new WorkHit[4];
        int n = index.QueryNearest(new TilePos(0, 0, 0), WorkTestWorld.Haul, 10, hits);
        Assert.Equal([ids[0], ids[2]], hits[..n].Select(h => h.Entity).ToArray());
        Assert.Equal(fixture.World.Hash(), loaded.Hash());
    }

    [Fact]
    public void Queries_allocate_nothing()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 64, 1), pathfinding: false);
        var items = new (TilePos, int)[200];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = (new TilePos((i * 7) % 64, (i * 13) % 64, 0), i % 3);
        }

        fixture.SpawnItems(items);
        fixture.World.Tick();
        var index = fixture.WorkIndex;
        var hits = new WorkHit[8];
        ReadOnlySpan<int> types = [WorkTestWorld.Haul, WorkTestWorld.Mine];
        int warm = 0;
        for (int i = 0; i < 50; i++)
        {
            warm += index.QueryNearest(new TilePos(i, i, 0), types, 30, hits);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int found = 0;
        for (int i = 0; i < 50; i++)
        {
            found += index.QueryNearest(new TilePos(i, i, 0), types, 30, hits);
            found += index.QueryNearest(new TilePos(63 - i, i, 0), WorkTestWorld.Build, int.MaxValue, hits);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(found > 0 && warm > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void The_Late_update_allocates_nothing_in_steady_state(int workers)
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 64, 1), workers, new WorkIndexOptions { InitialCapacity = 4096 }, pathfinding: false, configure: AddToggler);
        var world = fixture.World;
        fixture.SpawnItems(Grid(1500));
        for (int t = 0; t < 12; t++)
        {
            world.Tick();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long workersBefore = world.WorkerAllocatedBytes;
        for (int t = 0; t < 6; t++)
        {
            world.Tick();
        }

        long main = GC.GetAllocatedBytesForCurrentThread() - before;
        long onWorkers = world.WorkerAllocatedBytes - workersBefore;

        Assert.Equal(0, main);
        Assert.Equal(0, onWorkers);
        Assert.True(fixture.WorkIndex.LastUpdateEntries > 0);
    }

    private static (TilePos, int)[] Grid(int count)
    {
        var items = new (TilePos, int)[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = (new TilePos(i % 64, (i / 64) % 64, 0), i % 3);
        }

        return items;
    }

    /// <summary>A Simulate System that flips the availability of one stripe of items per Tick (stamped writes, so the Late update sees every chunk).</summary>
    private static void AddToggler(WorldBuilder builder, WorkIndexBuilder index)
    {
        var workItem = index.WorkItem;
        builder.AddSystem(new SystemDefinition
        {
            Name = "test.toggle",
            Phase = Phase.Simulate,
            Writes = ComponentMask.Of(workItem),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var items = chunk.Column<WorkItem>(workItem);
                int stripe = (int)(context.Tick % 5);
                for (int i = stripe; i < items.Length; i += 5)
                {
                    items[i] = items[i].WithAvailable(!items[i].IsAvailable);
                }
            },
        });
    }

    private static void TakeThroughSystem(WorkTestWorld fixture, EntityId id, bool available = false)
    {
        var buffer = fixture.World.CreateCommandBuffer();
        var item = fixture.World.Get<WorkItem>(id, fixture.Index.WorkItem);
        buffer.Add(id, fixture.Index.WorkItem, item.WithAvailable(available));
        fixture.World.Playback(buffer);
        fixture.World.ResetScratch();
        fixture.World.Tick();
    }

    /// <summary>Random churn (spawn, move, flip, destroy) from a seeded sequence; after each Tick the queries must match a naive model and a Rebuild. Returns the query transcript.</summary>
    private static string Churn(int workers)
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 64, 16), workers, pathfinding: false);
        var world = fixture.World;
        var random = new Numerics.RandomSequence(new Numerics.RandomSource(99, 1));
        var alive = new List<EntityId>();
        var transcript = new System.Text.StringBuilder();
        var hits = new WorkHit[6];
        ReadOnlySpan<int> types = [WorkTestWorld.Haul, WorkTestWorld.Mine];
        for (int tick = 0; tick < 25; tick++)
        {
            var buffer = world.CreateCommandBuffer();
            var spawned = new List<EntityId>();
            int spawn = random.NextInt32(0, 40);
            for (int i = 0; i < spawn; i++)
            {
                var deferred = buffer.Create(fixture.Index.ItemComponents);
                buffer.Add(deferred, fixture.TilePos, RandomTile(ref random));
                buffer.Add(deferred, fixture.Index.WorkItem, new WorkItem(random.NextInt32(0, 3), (byte)random.NextInt32(0, 2)));
                spawned.Add(deferred);
            }

            for (int i = alive.Count - 1; i >= 0; i--)
            {
                int roll = random.NextInt32(0, 10);
                if (roll == 0)
                {
                    buffer.Destroy(alive[i]);
                    alive.RemoveAt(i);
                }
                else if (roll == 1)
                {
                    buffer.Add(alive[i], fixture.TilePos, RandomTile(ref random));
                }
                else if (roll == 2)
                {
                    buffer.Add(alive[i], fixture.Index.WorkItem, new WorkItem(random.NextInt32(0, 3), (byte)random.NextInt32(0, 2)));
                }
                else if (roll == 3)
                {
                    buffer.Remove(alive[i], fixture.Index.WorkItem);
                    alive.RemoveAt(i);
                }
            }

            world.Playback(buffer);
            foreach (var deferred in spawned)
            {
                alive.Add(buffer.Resolve(deferred));
            }

            world.ResetScratch();
            world.Tick();

            var expected = Model(world, fixture);
            for (int q = 0; q < 6; q++)
            {
                var origin = RandomTile(ref random);
                int radius = random.NextInt32(0, 40);
                int n = fixture.WorkIndex.QueryNearest(origin, types, radius, hits);
                var naive = expected
                    .Where(e => e.Type is WorkTestWorld.Haul or WorkTestWorld.Mine && SpatialIndex.DistanceSquared(origin, e.Position) <= (long)radius * radius)
                    .OrderBy(e => SpatialIndex.DistanceSquared(origin, e.Position)).ThenBy(e => e.Entity.Value)
                    .Take(hits.Length)
                    .Select(e => e.Entity)
                    .ToArray();
                Assert.Equal(naive, hits[..n].Select(h => h.Entity).ToArray());
                transcript.Append(tick).Append(':').AppendJoin(',', hits[..n].Select(h => h.Entity.Value)).Append(';');
            }

            Assert.Equal(expected.Count, fixture.WorkIndex.Count);
            int incrementalCount = fixture.WorkIndex.Count;
            fixture.WorkIndex.Rebuild();
            Assert.Equal(incrementalCount, fixture.WorkIndex.Count);
        }

        return transcript.ToString();
    }

    private static TilePos RandomTile(ref Numerics.RandomSequence random)
    {
        // Mostly inside, sometimes outside the 64x64x16 World.
        return new TilePos(random.NextInt32(-2, 66), random.NextInt32(-2, 66), random.NextInt32(-1, 17));
    }

    private static List<(EntityId Entity, TilePos Position, int Type)> Model(World world, WorkTestWorld fixture)
    {
        var result = new List<(EntityId, TilePos, int)>();
        foreach (var chunk in world.Query(fixture.Index.ItemComponents))
        {
            var entities = chunk.Entities;
            var positions = chunk.Column<TilePos>(fixture.TilePos);
            var items = chunk.Column<WorkItem>(fixture.Index.WorkItem);
            for (int i = 0; i < entities.Length; i++)
            {
                if (items[i].IsAvailable && world.Fields().Size.Contains(positions[i]))
                {
                    result.Add((entities[i], positions[i], items[i].Type));
                }
            }
        }

        return result;
    }
}
