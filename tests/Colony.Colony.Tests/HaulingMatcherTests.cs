using Colony.Fields;

namespace Colony.Jobs.Tests;

/// <summary>AC #3: the hauling matcher pairs supply and demand into haul work items deterministically.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class HaulingMatcherTests
{
    private const int Wood = 1;
    private const int Stone = 2;

    private static EntityId Id(int index) => new(index, 1);

    [Fact]
    public void Each_demand_takes_from_its_nearest_matching_supply_in_priority_order_and_amounts_split_across_supplies()
    {
        Span<HaulSupply> supplies =
        [
            new(Id(1), new TilePos(0, 0, 0), Wood, 5),
            new(Id(2), new TilePos(10, 0, 0), Wood, 5),
            new(Id(3), new TilePos(1, 0, 0), Stone, 9),
        ];
        Span<HaulDemand> demands =
        [
            new(Id(20), new TilePos(2, 0, 0), Wood, 7, Priority: 1),   // lower priority, served second
            new(Id(21), new TilePos(9, 0, 0), Wood, 4, Priority: 5),   // served first: nearest wood is supply 2
            new(Id(22), new TilePos(0, 0, 0), Stone, 3, Priority: 0),
            new(Id(23), new TilePos(0, 0, 0), Wood, 1, Priority: 0),   // nothing left
        ];
        var matches = new HaulMatch[8];

        int count = HaulingMatcher.Match(supplies, demands, matches);

        HaulMatch[] expected =
        [
            new(Id(2), Id(21), new TilePos(10, 0, 0), new TilePos(9, 0, 0), Wood, 4),
            new(Id(1), Id(20), new TilePos(0, 0, 0), new TilePos(2, 0, 0), Wood, 5),
            new(Id(2), Id(20), new TilePos(10, 0, 0), new TilePos(2, 0, 0), Wood, 1),
            new(Id(3), Id(22), new TilePos(1, 0, 0), new TilePos(0, 0, 0), Stone, 3),
        ];
        Assert.Equal(expected, matches[..count]);
    }

    [Fact]
    public void Ties_break_by_entity_id_and_the_input_order_does_not_matter()
    {
        static HaulSupply[] Supplies() => [new(Id(5), new TilePos(3, 0, 0), Wood, 1), new(Id(4), new TilePos(-3, 0, 0), Wood, 1), new(Id(6), new TilePos(3, 0, 0), Wood, 1)];
        static HaulDemand[] Demands() => [new(Id(31), new TilePos(0, 0, 0), Wood, 1, 2), new(Id(30), new TilePos(0, 0, 0), Wood, 1, 2)];

        var matches = new HaulMatch[4];
        int count = HaulingMatcher.Match(Supplies(), Demands(), matches);
        var reversedMatches = new HaulMatch[4];
        int reversedCount = HaulingMatcher.Match(Supplies().Reverse().ToArray(), Demands().Reverse().ToArray(), reversedMatches);

        Assert.Equal(2, count);
        Assert.Equal((Id(4), Id(30)), (matches[0].Supply, matches[0].Demand));
        Assert.Equal((Id(5), Id(31)), (matches[1].Supply, matches[1].Demand));
        Assert.Equal(matches[..count], reversedMatches[..reversedCount]);
    }

    [Fact]
    public void A_filter_excludes_pairs_a_hauler_cannot_serve_and_a_short_match_buffer_truncates()
    {
        Span<HaulSupply> supplies = [new(Id(1), new TilePos(0, 0, 0), Wood, 3), new(Id(2), new TilePos(50, 0, 0), Wood, 3)];
        Span<HaulDemand> demands = [new(Id(20), new TilePos(1, 0, 0), Wood, 6, 0), new(Id(21), new TilePos(51, 0, 0), Wood, 1, 0)];
        var matches = new HaulMatch[4];

        int count = HaulingMatcher.Match(supplies, demands, matches, new WithinDistance(10));

        Assert.Equal(2, count);
        Assert.Equal((Id(1), Id(20), 3), (matches[0].Supply, matches[0].Demand, matches[0].Amount));
        Assert.Equal((Id(2), Id(21), 1), (matches[1].Supply, matches[1].Demand, matches[1].Amount));

        Span<HaulSupply> again = [new(Id(1), new TilePos(0, 0, 0), Wood, 3), new(Id(2), new TilePos(50, 0, 0), Wood, 3)];
        Span<HaulDemand> againDemands = [new(Id(20), new TilePos(1, 0, 0), Wood, 6, 0), new(Id(21), new TilePos(51, 0, 0), Wood, 1, 0)];
        Assert.Equal(1, HaulingMatcher.Match(again, againDemands, matches.AsSpan(0, 1)));
    }

    [Fact]
    public void Matches_become_haul_work_items_carrying_the_match_and_standing_at_the_supply()
    {
        using var fixture = new WorkTestWorld(new WorldSize(32, 32, 1), pathfinding: false, configure: static (builder, _) => builder.AddHauling());
        var hauling = fixture.World.Schema.Hauling();
        var supply = fixture.SpawnItem(new TilePos(3, 3, 0), WorkTestWorld.Mine, available: false);
        var demand = fixture.SpawnItem(new TilePos(8, 8, 0), WorkTestWorld.Build, available: false);
        var match = new HaulMatch(supply, demand, new TilePos(3, 3, 0), new TilePos(8, 8, 0), Wood, 4);

        var buffer = fixture.World.CreateCommandBuffer();
        var deferred = HaulingMatcher.CreateWorkItem(buffer, in match, fixture.Index, hauling, WorkTestWorld.Haul);
        fixture.World.Playback(buffer);
        var job = buffer.Resolve(deferred);
        fixture.World.ResetScratch();
        fixture.World.Tick();

        Assert.Equal(match, fixture.World.Get<HaulMatch>(job, hauling));
        Assert.Equal(WorkItem.Available(WorkTestWorld.Haul), fixture.World.Get<WorkItem>(job, fixture.Index.WorkItem));
        var hits = fixture.Nearest(new TilePos(3, 3, 0), 0, 2, WorkTestWorld.Haul);
        Assert.Equal([job], hits.Select(h => h.Entity).ToArray());
    }

    [Fact]
    public void Matching_allocates_nothing()
    {
        var supplies = new HaulSupply[64];
        var demands = new HaulDemand[64];
        var matches = new HaulMatch[128];
        var scratchSupplies = new HaulSupply[64];
        var scratchDemands = new HaulDemand[64];
        for (int i = 0; i < 64; i++)
        {
            supplies[i] = new(Id(i + 1), new TilePos((i * 7) % 32, (i * 3) % 32, 0), 1 + (i % 3), 1 + (i % 5));
            demands[i] = new(Id(100 + i), new TilePos((i * 5) % 32, (i * 11) % 32, 0), 1 + (i % 3), 1 + (i % 4), i % 6);
        }

        int warm = 0;
        for (int i = 0; i < 20; i++)
        {
            warm += Run(supplies, demands, scratchSupplies, scratchDemands, matches);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int i = 0; i < 20; i++)
        {
            total += Run(supplies, demands, scratchSupplies, scratchDemands, matches);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(total > 0 && warm == total);
    }

    private static int Run(HaulSupply[] supplies, HaulDemand[] demands, HaulSupply[] scratchSupplies, HaulDemand[] scratchDemands, HaulMatch[] matches)
    {
        supplies.CopyTo(scratchSupplies, 0);
        demands.CopyTo(scratchDemands, 0);
        return HaulingMatcher.Match(scratchSupplies, scratchDemands, matches, new WithinDistance(20));
    }

    private readonly struct WithinDistance(int radius) : IHaulFilter
    {
        public bool CanHaul(in HaulSupply supply, in HaulDemand demand) => SpatialIndex.DistanceSquared(supply.Position, demand.Position) <= (long)radius * radius;
    }
}
