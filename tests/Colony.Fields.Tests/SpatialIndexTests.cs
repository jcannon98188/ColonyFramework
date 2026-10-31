using Colony.Fields;

namespace Colony.Fields.Tests;

/// <summary>Spatial index over grid Entities (spec section 5.1): registration, per-query semantics and ordering.</summary>
public sealed class SpatialIndexTests
{
    private static readonly WorldSize Size = new(80, 48, 32);

    private static (WorldSchema Schema, ComponentType TilePos) BuildSchema()
    {
        var builder = new WorldBuilder().AddComponent<TilePos>("tile.pos").DebugValidation(false);
        var fields = builder.AddFields(Size);
        builder.AddSpatialIndex(fields, builder.Component<TilePos>());
        var schema = builder.Build();
        return (schema, schema.Component<TilePos>());
    }

    private static EntityId Spawn(World world, CommandBuffer buffer, ComponentType tilePos, TilePos at)
    {
        var deferred = buffer.Create(ComponentMask.Of(tilePos));
        buffer.Add(deferred, tilePos, at);
        return deferred;
    }

    [Fact]
    public void An_entity_created_before_the_first_tick_is_found_by_its_tile_after_the_tick()
    {
        var (schema, tilePos) = BuildSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var buffer = world.CreateCommandBuffer();
        Spawn(world, buffer, tilePos, new TilePos(20, 5, 3));
        world.Playback(buffer);
        world.ResetScratch();
        var index = world.SpatialIndex();

        Span<SpatialHit> hits = stackalloc SpatialHit[4];
        Assert.Equal(0, index.QueryTile(new TilePos(20, 5, 3), hits));

        world.Tick();

        Assert.Equal(1, index.Count);
        Assert.Equal(1, index.QueryTile(new TilePos(20, 5, 3), hits));
        Assert.Equal(new TilePos(20, 5, 3), hits[0].Position);
        Assert.True(world.IsAlive(hits[0].Entity));
        Assert.Equal(0, hits[0].DistanceSquared);
        Assert.Equal(0, index.QueryTile(new TilePos(21, 5, 3), hits));
    }

    /// <summary>Spawns a fixed layout around (40, 24, 16) crossing chunk borders and returns the ids in creation order.</summary>
    private static (World World, SpatialIndex Index, EntityId[] Ids) Arrange(ComponentType tilePos, WorldSchema schema, params TilePos[] tiles)
    {
        var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        var buffer = world.CreateCommandBuffer();
        var deferred = new EntityId[tiles.Length];
        for (int i = 0; i < tiles.Length; i++)
        {
            deferred[i] = Spawn(world, buffer, tilePos, tiles[i]);
        }

        world.Playback(buffer);
        var ids = new EntityId[tiles.Length];
        for (int i = 0; i < tiles.Length; i++)
        {
            ids[i] = buffer.Resolve(deferred[i]);
        }

        world.ResetScratch();
        world.Tick();
        return (world, world.SpatialIndex(), ids);
    }

    private readonly struct EvenIndexOnly : ISpatialPredicate
    {
        public bool Matches(EntityId entity, in TilePos position) => (entity.Index & 1) == 0;
    }

    [Fact]
    public void Results_are_ordered_by_distance_then_entity_id_and_a_short_buffer_keeps_the_nearest()
    {
        var (schema, tilePos) = BuildSchema();
        var center = new TilePos(40, 24, 16);
        using var owner = new WorldOwner();
        var (world, index, ids) = Arrange(tilePos, schema,
            center + new TilePos(3, 0, 0),   // d2 = 9
            center + new TilePos(0, 0, 1),   // d2 = 1
            center,                          // d2 = 0
            center + new TilePos(-1, 0, 0),  // d2 = 1 (same distance as [1]; higher id -> after it)
            center + new TilePos(2, 2, 0),   // d2 = 8
            center + new TilePos(30, 0, 0)); // d2 = 900, in a far chunk
        owner.World = world;

        Span<SpatialHit> hits = stackalloc SpatialHit[8];
        int n = index.QueryNearest(center, hits);
        Assert.Equal(6, n);
        Assert.Equal([ids[2], ids[1], ids[3], ids[4], ids[0], ids[5]], hits[..n].ToArray().Select(h => h.Entity).ToArray());
        Assert.Equal([0L, 1, 1, 8, 9, 900], hits[..n].ToArray().Select(h => h.DistanceSquared).ToArray());

        // nearest-3 is the head of the same order
        n = index.QueryNearest(center, hits[..3]);
        Assert.Equal([ids[2], ids[1], ids[3]], hits[..n].ToArray().Select(h => h.Entity).ToArray());

        // radius 3 excludes d2 = 900 and includes d2 = 9 (inclusive)
        n = index.QueryRadius(center, 3, hits);
        Assert.Equal([ids[2], ids[1], ids[3], ids[4], ids[0]], hits[..n].ToArray().Select(h => h.Entity).ToArray());

        // a box ordered from its origin; a short buffer keeps the nearest by that order
        n = index.QueryBox(TileBox.Around(center, 2), center, hits[..2]);
        Assert.Equal([ids[2], ids[1]], hits[..n].ToArray().Select(h => h.Entity).ToArray());

        // predicate filters before ranking
        n = index.QueryNearest(center, hits, new EvenIndexOnly());
        Assert.All(hits[..n].ToArray(), h => Assert.Equal(0, h.Entity.Index & 1));
        Assert.Equal(ids.Where(id => (id.Index & 1) == 0).Count(), n);
        n = index.QueryRadius(center, 3, hits, new EvenIndexOnly());
        Assert.All(hits[..n].ToArray(), h => Assert.True(h.DistanceSquared <= 9 && (h.Entity.Index & 1) == 0));

        // nearest with a radius cap
        n = index.QueryNearest(center, hits, default(AnyEntity), maxRadius: 3);
        Assert.Equal(5, n);

        // an empty buffer finds nothing and does not throw
        Assert.Equal(0, index.QueryNearest(center, Span<SpatialHit>.Empty));
    }

    private sealed class WorldOwner : IDisposable
    {
        public World? World { get; set; }

        public void Dispose() => World?.Dispose();
    }
}
