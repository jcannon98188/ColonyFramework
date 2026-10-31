using Colony.Fields;

namespace Colony.Fields.Tests.Scenario;

/// <summary>
/// AC #2: the incrementally updated index agrees with a naive grid (the live TilePos columns) for every query
/// type after random moves, creates and destroys. AC #4: rebuild on demand and hash exclusion.
/// </summary>
public sealed class SpatialIndexModelTests
{
    private const int Ticks = 40;
    private const int ProbesPerTick = 12;

    private sealed record Model(EntityId Entity, TilePos Position);

    private static List<Model> Naive(World world, ComponentType tilePos)
    {
        var model = new List<Model>();
        foreach (var chunk in world.Query(tilePos))
        {
            var entities = chunk.Entities;
            var positions = chunk.Column<TilePos>(tilePos);
            for (int i = 0; i < entities.Length; i++)
            {
                if (SpatialScenario.Size.Contains(positions[i]))
                {
                    model.Add(new Model(entities[i], positions[i]));
                }
            }
        }

        return model;
    }

    private static SpatialHit[] Expected(IEnumerable<Model> candidates, TilePos origin, int take, Func<Model, bool>? predicate = null)
        => candidates
            .Where(m => predicate is null || predicate(m))
            .Select(m => new SpatialHit(m.Entity, m.Position, SpatialIndex.DistanceSquared(origin, m.Position)))
            .OrderBy(h => h.DistanceSquared)
            .ThenBy(h => h.Entity.Value)
            .Take(take)
            .ToArray();

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(987654321UL)]
    public void Every_query_matches_the_naive_grid_after_random_moves_creates_and_destroys(ulong seed)
    {
        var scenario = SpatialScenario.Build(probe: false);
        using var world = scenario.CreateWorld(workers: 3, seed);
        var tilePos = scenario.TilePos;
        var index = world.SpatialIndex();
        var probeRandom = world.Random.Derive("probes");
        var hits = new SpatialHit[64];
        long destroysSeen = 0;
        bool sawOutside = false;

        for (int tick = 0; tick < Ticks; tick++)
        {
            world.Tick();
            var model = Naive(world, tilePos);
            Assert.Equal(model.Count, index.Count);
            sawOutside |= model.Count < world.Query(tilePos).EntityCount();

            for (int p = 0; p < ProbesPerTick; p++)
            {
                ulong c = ((ulong)tick * 1000) + ((ulong)p * 8);
                var origin = new TilePos(
                    probeRandom.Int32At(c, -4, SpatialScenario.Size.X + 4),
                    probeRandom.Int32At(c + 1, -4, SpatialScenario.Size.Y + 4),
                    probeRandom.Int32At(c + 2, -4, SpatialScenario.Size.Z + 4));
                int radius = probeRandom.Int32At(c + 3, 0, 24);
                int k = probeRandom.Int32At(c + 4, 1, 40);
                var box = new TileBox(origin, origin + new TilePos(probeRandom.Int32At(c + 5, 0, 30), probeRandom.Int32At(c + 6, 0, 30), probeRandom.Int32At(c + 7, 0, 10)));
                var even = new SpatialScenario.EvenIndex();

                // tile
                int n = index.QueryTile(origin, hits);
                Assert.Equal(Expected(model.Where(m => m.Position == origin), origin, hits.Length), hits[..n]);

                // radius, full and truncated to k, with and without predicate
                long r2 = (long)radius * radius;
                n = index.QueryRadius(origin, radius, hits);
                Assert.Equal(Expected(model.Where(m => SpatialIndex.DistanceSquared(origin, m.Position) <= r2), origin, hits.Length), hits[..n]);
                n = index.QueryRadius(origin, radius, hits.AsSpan(0, k), even);
                Assert.Equal(Expected(model.Where(m => SpatialIndex.DistanceSquared(origin, m.Position) <= r2), origin, k, m => (m.Entity.Index & 1) == 0), hits[..n]);

                // box
                n = index.QueryBox(in box, origin, hits);
                Assert.Equal(Expected(model.Where(m => box.Contains(m.Position)), origin, hits.Length), hits[..n]);
                n = index.QueryBox(in box, origin, hits.AsSpan(0, k), even);
                Assert.Equal(Expected(model.Where(m => box.Contains(m.Position)), origin, k, m => (m.Entity.Index & 1) == 0), hits[..n]);

                // nearest-k, plain, with predicate, with radius cap
                n = index.QueryNearest(origin, hits.AsSpan(0, k));
                Assert.Equal(Expected(model, origin, k), hits[..n]);
                n = index.QueryNearest(origin, hits.AsSpan(0, k), even);
                Assert.Equal(Expected(model, origin, k, m => (m.Entity.Index & 1) == 0), hits[..n]);
                n = index.QueryNearest(origin, hits.AsSpan(0, k), default(AnyEntity), maxRadius: radius);
                Assert.Equal(Expected(model.Where(m => SpatialIndex.DistanceSquared(origin, m.Position) <= r2), origin, k), hits[..n]);
            }

            destroysSeen = world.Counters.Destroys;
        }

        Assert.True(destroysSeen > 0, "the scenario destroys Entities");
        Assert.True(sawOutside, "the scenario places some Entities outside the World");
        Assert.True(world.Counters.Creates > SpatialScenario.TargetPopulation, "the scenario creates Entities");
    }

    [Fact]
    public void Rebuild_on_demand_reproduces_the_incrementally_maintained_index()
    {
        var scenario = SpatialScenario.Build(probe: false);
        using var world = scenario.CreateWorld(workers: 2, seed: 7);
        var index = world.SpatialIndex();
        for (int i = 0; i < 10; i++)
        {
            world.Tick();
        }

        var before = Snapshot(index);
        index.Clear();
        Assert.Equal(0, index.Count);
        Assert.Empty(Snapshot(index));
        index.Rebuild();
        Assert.Equal(before, Snapshot(index));
        Assert.Equal(Naive(world, scenario.TilePos).Count, index.Count);
    }

    [Fact]
    public void The_index_is_excluded_from_the_world_hash()
    {
        var scenario = SpatialScenario.Build(probe: false);
        using var world = scenario.CreateWorld(workers: 2, seed: 7);
        var index = world.SpatialIndex();
        for (int i = 0; i < 5; i++)
        {
            world.Tick();
        }

        Assert.True(index.Count > 0);
        ulong populated = world.Hash();
        index.Clear();
        Assert.Equal(populated, world.Hash());
        index.Rebuild();
        Assert.Equal(populated, world.Hash());
    }

    private static SpatialHit[] Snapshot(SpatialIndex index)
    {
        var hits = new SpatialHit[4096];
        int n = index.QueryBox(new TileBox(new TilePos(0, 0, 0), new TilePos(SpatialScenario.Size.X - 1, SpatialScenario.Size.Y - 1, SpatialScenario.Size.Z - 1)), new TilePos(0, 0, 0), hits);
        return hits[..n];
    }
}
