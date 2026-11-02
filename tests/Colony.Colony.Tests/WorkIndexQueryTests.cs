using Colony.Fields;

namespace Colony.Jobs.Tests;

/// <summary>AC #1: nearest-N available work items of given types within a radius, in deterministic order, filtered by Reachable.</summary>
public sealed class WorkIndexQueryTests
{
    [Fact]
    public void Nearest_items_of_the_requested_types_come_back_by_distance_then_entity_id_within_the_radius()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 64, 1));
        var origin = new TilePos(10, 10, 0);
        var ids = fixture.SpawnItems(
        [
            (new TilePos(10, 13, 0), WorkTestWorld.Mine),   // d2 = 9
            (new TilePos(12, 10, 0), WorkTestWorld.Mine),   // d2 = 4
            (new TilePos(10, 12, 0), WorkTestWorld.Haul),   // d2 = 4, same distance: later id loses the tie
            (new TilePos(40, 40, 0), WorkTestWorld.Mine),   // out of radius
            (new TilePos(11, 10, 0), WorkTestWorld.Build),  // d2 = 1 but not a requested type
            (new TilePos(10, 10, 0), WorkTestWorld.Haul),   // d2 = 0
        ]);
        fixture.World.Tick();

        var hits = fixture.Nearest(origin, 20, 8, WorkTestWorld.Haul, WorkTestWorld.Mine);

        Assert.Equal([ids[5], ids[1], ids[2], ids[0]], hits.Select(h => h.Entity).ToArray());
        Assert.Equal([0L, 4L, 4L, 9L], hits.Select(h => h.DistanceSquared).ToArray());
        Assert.Equal([WorkTestWorld.Haul, WorkTestWorld.Mine, WorkTestWorld.Haul, WorkTestWorld.Mine], hits.Select(h => h.Type).ToArray());
        Assert.Equal(new TilePos(10, 13, 0), hits[3].Position);
    }

    [Fact]
    public void A_short_result_buffer_keeps_the_nearest_items()
    {
        using var fixture = new WorkTestWorld(new WorldSize(64, 64, 1));
        var origin = new TilePos(0, 0, 0);
        var ids = fixture.SpawnItems(
        [
            (new TilePos(5, 0, 0), WorkTestWorld.Mine),
            (new TilePos(3, 0, 0), WorkTestWorld.Mine),
            (new TilePos(1, 0, 0), WorkTestWorld.Mine),
            (new TilePos(4, 0, 0), WorkTestWorld.Mine),
        ]);
        fixture.World.Tick();

        var hits = fixture.Nearest(origin, 100, 2, WorkTestWorld.Mine);

        Assert.Equal([ids[2], ids[1]], hits.Select(h => h.Entity).ToArray());
    }

    [Fact]
    public void Items_across_chunk_boundaries_are_found_in_distance_order_and_a_radius_of_zero_means_the_origin_tile()
    {
        using var fixture = new WorkTestWorld(new WorldSize(96, 96, 32));
        var origin = new TilePos(15, 15, 15);
        var ids = fixture.SpawnItems(
        [
            (new TilePos(16, 15, 15), WorkTestWorld.Haul), // next chunk in x, d2 = 1
            (new TilePos(15, 16, 15), WorkTestWorld.Haul), // next chunk in y, d2 = 1
            (new TilePos(15, 15, 16), WorkTestWorld.Haul), // next chunk in z, d2 = 1
            (new TilePos(13, 15, 15), WorkTestWorld.Haul), // same chunk, d2 = 4
            (new TilePos(15, 15, 15), WorkTestWorld.Haul), // d2 = 0
            (new TilePos(80, 80, 20), WorkTestWorld.Haul), // far
        ]);
        fixture.World.Tick();

        Assert.Equal([ids[4], ids[0], ids[1], ids[2], ids[3], ids[5]], fixture.Nearest(origin, int.MaxValue, 8, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());
        Assert.Equal([ids[4]], fixture.Nearest(origin, 0, 8, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());
        Assert.Equal([ids[4], ids[0], ids[1], ids[2]], fixture.Nearest(origin, 1, 8, WorkTestWorld.Haul).Select(h => h.Entity).ToArray());
    }
}
