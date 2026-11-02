using Colony.Fields;
using Colony.Pathfinding;

namespace Colony.Jobs.Tests;

/// <summary>
/// TASK-41: the shipped reachability filters (<see cref="ReachableWorkFilter"/>, <see cref="ReachableHaulFilter"/>)
/// over <see cref="PathfindingService.Reachable"/> drive the work index and the hauling matcher, follow doors
/// opening and closing from the next Tick, work from parallel chunk jobs and allocate nothing.
/// </summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class ReachabilityFilterTests
{
    private const int Wood = 1;
    private const int DoorY = 3;

    private static readonly TilePos WestSupply = new(5, 8, 0);
    private static readonly TilePos EastSupply = new(22, 8, 0);
    private static readonly TilePos Delivery = new(18, 8, 0);

    private static EntityId Id(int index) => new(index, 1);

    [Fact]
    public void The_matcher_skips_a_nearer_supply_behind_a_closed_door_and_uses_it_while_the_door_is_open()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 16, 1));
        fixture.WallColumn(20);
        fixture.World.Tick();
        var filter = new ReachableHaulFilter(fixture.Pathfinding, fixture.Walker);

        Assert.Equal(Id(2), MatchOne(filter).Supply);

        fixture.WallColumn(20, DoorY);
        fixture.World.Tick();
        Assert.Equal(Id(1), MatchOne(filter).Supply);

        fixture.WallColumn(20);
        fixture.World.Tick();
        Assert.Equal(Id(2), MatchOne(filter).Supply);
    }

    [Fact]
    public void A_haul_filter_with_a_hauler_also_requires_the_hauler_to_reach_the_supply()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 16, 1));
        fixture.WallColumn(20);
        fixture.World.Tick();
        var hauler = new TilePos(30, 8, 0); // east of the wall, beside the east supply
        var filter = new ReachableHaulFilter(fixture.Pathfinding, fixture.Walker, hauler);

        // Closed: the east supply cannot reach the delivery, the hauler cannot reach the west supply.
        Assert.Equal(0, Match(filter, out _));

        fixture.WallColumn(20, DoorY);
        fixture.World.Tick();
        Assert.Equal(1, Match(filter, out var match));
        Assert.Equal(Id(1), match.Supply);
    }

    [Fact]
    public void Impassable_and_out_of_World_endpoints_are_never_accepted()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 16, 1));
        fixture.WallColumn(20);
        fixture.World.Tick();
        var standing = new TilePos(10, 8, 0);
        var work = new ReachableWorkFilter(fixture.Pathfinding, standing, fixture.Walker);
        var haul = new ReachableHaulFilter(fixture.Pathfinding, fixture.Walker);

        Assert.True(work.Accepts(Id(1), new TilePos(11, 8, 0), 0));
        Assert.False(work.Accepts(Id(1), new TilePos(20, 8, 0), 0));
        Assert.False(work.Accepts(Id(1), new TilePos(-1, 8, 0), 0));
        Assert.False(new ReachableWorkFilter(fixture.Pathfinding, new TilePos(20, 8, 0), fixture.Walker).Accepts(Id(1), new TilePos(20, 8, 0), 0));
        Assert.False(haul.CanHaul(new HaulSupply(Id(1), new TilePos(20, 5, 0), Wood, 1), new HaulDemand(Id(2), new TilePos(20, 5, 0), Wood, 1, 0)));
    }

    [Fact]
    public void Seekers_in_parallel_chunk_jobs_see_the_same_reachable_work_on_1_and_4_workers_as_doors_open_and_close()
    {
        var one = RunSeekers(workers: 1, ticks: 12, out _);
        var four = RunSeekers(workers: 4, ticks: 12, out _);

        Assert.Equal(one, four);
        // The door is open on odd Ticks: east items are counted only then.
        Assert.NotEqual(one[0], one[1]);
        Assert.Equal(one[0], one[2]);
        Assert.Equal(one[1], one[3]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Filtered_queries_matching_and_door_toggles_allocate_nothing_in_steady_state(int workers)
    {
        RunSeekers(workers, ticks: 30, out long allocated);

        Assert.Equal(0, allocated);
    }

    private static HaulMatch MatchOne(ReachableHaulFilter filter)
    {
        Assert.Equal(1, Match(filter, out var match));
        return match;
    }

    private static int Match(ReachableHaulFilter filter, out HaulMatch match)
    {
        Span<HaulSupply> supplies = [new(Id(1), EastSupply, Wood, 5), new(Id(2), WestSupply, Wood, 5)];
        Span<HaulDemand> demands = [new(Id(10), Delivery, Wood, 3, 0)];
        Span<HaulMatch> matches = stackalloc HaulMatch[2];
        int count = HaulingMatcher.Match(supplies, demands, matches, filter);
        match = matches[0];
        return count;
    }

    /// <summary>
    /// 600 seekers west of a wall, all work east of it, query the work index from a Simulate chunk job through <see cref="ReachableWorkFilter"/>;
    /// the host toggles a door every Tick and matches hauls with <see cref="ReachableHaulFilter"/>. Returns the
    /// per-Tick sum of found items; <paramref name="allocated"/> covers main and worker threads over the second half.
    /// </summary>
    private static long[] RunSeekers(int workers, int ticks, out long allocated)
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 64, 1), workers, new WorkIndexOptions { InitialCapacity = 4096 }, configure: AddSeekers);
        var world = fixture.World;
        var seeker = world.Schema.Component<Seeker>();
        fixture.WallColumn(20);

        var items = new (TilePos, int)[400];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = (new TilePos(22 + (i % 40), (i * 7) % 64, 0), WorkTestWorld.Mine);
        }

        fixture.SpawnItems(items);
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 600; i++)
        {
            var id = buffer.Create(ComponentMask.Of(fixture.TilePos, seeker));
            buffer.Add(id, fixture.TilePos, new TilePos(i % 19, (i * 13) % 64, 0));
        }

        world.Playback(buffer);
        world.ResetScratch();
        fixture.Pathfinding.EnsureBuilt(fixture.Walker);

        var haul = new ReachableHaulFilter(fixture.Pathfinding, fixture.Walker);
        var supplies = new HaulSupply[2];
        var demands = new HaulDemand[1];
        var matches = new HaulMatch[2];
        var found = new long[ticks];
        long before = 0;
        long workersBefore = 0;
        for (int t = 0; t < ticks; t++)
        {
            if (t == ticks / 2)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
                workersBefore = world.WorkerAllocatedBytes;
            }

            fixture.WallColumn(20, t % 2 == 1 ? DoorY : null);
            world.Tick();
            supplies[0] = new(Id(1), EastSupply, Wood, 5);
            supplies[1] = new(Id(2), WestSupply, Wood, 5);
            demands[0] = new(Id(10), Delivery, Wood, 3, 0);
            HaulingMatcher.Match(supplies, demands, matches, haul);
            found[t] = (Total(world, seeker) * 10) + matches[0].Supply.Index;
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before + (world.WorkerAllocatedBytes - workersBefore);
        return found;
    }

    private static long Total(World world, ComponentType seeker)
    {
        long total = 0;
        foreach (var chunk in world.Query(ComponentMask.Of(seeker)))
        {
            foreach (var s in chunk.Column<Seeker>(seeker))
            {
                total += s.Found;
            }
        }

        return total;
    }

    private static void AddSeekers(WorldBuilder builder, WorkIndexBuilder index)
    {
        builder.AddComponent<Seeker>("test.seeker");
        var seeker = builder.Component<Seeker>();
        var tilePos = index.TilePos;
        var walker = new MovementClass(0); // the fixture's only movement class
        builder.AddSystem(new SystemDefinition
        {
            Name = "test.seek",
            Phase = Phase.Simulate,
            Reads = ComponentMask.Of(tilePos),
            Writes = ComponentMask.Of(seeker),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var workIndex = context.World.WorkIndex();
                var pathfinding = context.World.Pathfinding();
                var positions = chunk.Column<TilePos>(tilePos);
                var seekers = chunk.Column<Seeker>(seeker);
                Span<WorkHit> hits = stackalloc WorkHit[4];
                for (int i = 0; i < positions.Length; i++)
                {
                    var from = positions[i];
                    seekers[i].Found = workIndex.QueryNearest(from, WorkTestWorld.Mine, 64, hits, new ReachableWorkFilter(pathfinding, from, walker));
                }
            },
        });
    }

    public struct Seeker
    {
        public int Found;
    }
}
