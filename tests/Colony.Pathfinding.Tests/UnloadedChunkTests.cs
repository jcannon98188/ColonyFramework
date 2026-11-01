using Colony.Fields;
using Colony.Persistence;

namespace Colony.Pathfinding.Tests;

/// <summary>TASK-27 AC #4: an Unloaded chunk keeps its pathfinding summary (passable bits, hence labels and portals), so Reachable answers across it; a path into it reports NeedsLoading with the chunk coordinate.</summary>
public sealed class UnloadedChunkTests
{
    // 4 x 1 x 1 chunks; chunk 1 holds a wall with one door.
    private static readonly WorldSize Size = new(64, 16, 16);
    private static readonly ChunkCoord Middle = new(1, 0, 0);

    private static World Create(out Field<byte> terrain, out MovementClass walker)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        builder.AddComponent<TilePos>("tile.pos");
        var fields = builder.AddFields(Size);
        terrain = fields.AddField<byte>("terrain");
        builder.AddResidency(fields, builder.Component<TilePos>());
        var pathfinding = builder.AddPathfinding(fields);
        walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        return builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2 });
    }

    [Fact]
    public void Reachable_answers_across_an_Unloaded_chunk_from_its_retained_summary_even_after_a_rebuild()
    {
        using var world = Create(out var terrain, out var walker);
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 5);
        var service = world.Pathfinding();
        var from = new TilePos(0, 0, 0);
        var to = new TilePos(63, 0, 0);
        Assert.True(service.Reachable(from, to, walker));

        int ordinal = world.Fields().OrdinalOf(Middle);
        world.Residency().ForceResidency(ordinal, ChunkResidency.Unloaded);
        Assert.Equal(ChunkResidency.Unloaded, world.Fields().ResidencyOf(ordinal));

        Assert.True(service.Reachable(from, to, walker));
        service.Rebuild(walker);
        Assert.True(service.Reachable(from, to, walker)); // the cells are gone; the summary is the source now

        var query = service.FindPath(from, to, walker, stackalloc TilePos[16]);
        Assert.Equal(PathStatus.NeedsLoading, query.Status);
        Assert.Equal(Middle, query.MissingChunk);

        world.Tick(); // no focus points: the never-unload policy reloads the chunk
        Assert.Equal(ChunkResidency.Active, world.Fields().ResidencyOf(ordinal));
        Assert.True(service.Reachable(from, to, walker));
        Assert.Equal(PathStatus.Found, service.FindPath(from, to, walker, stackalloc TilePos[16]).Status);
    }

    [Fact]
    public void A_closed_door_stays_closed_in_the_summary_and_reopening_is_seen_when_the_chunk_reloads()
    {
        using var world = Create(out var terrain, out var walker);
        TestWorlds.WallColumn(world, terrain, x: 20);
        var service = world.Pathfinding();
        var from = new TilePos(0, 0, 0);
        var to = new TilePos(63, 0, 0);
        Assert.False(service.Reachable(from, to, walker));

        int ordinal = world.Fields().OrdinalOf(Middle);
        world.Residency().ForceResidency(ordinal, ChunkResidency.Unloaded);
        service.Rebuild(walker);
        Assert.False(service.Reachable(from, to, walker));

        world.Residency().ForceResidency(ordinal, ChunkResidency.Active);
        TestWorlds.Set(world, terrain, 20, 5, 0, 0);
        service.Sync();
        Assert.True(service.Reachable(from, to, walker));
    }

    [Fact]
    public void The_summary_of_Unloaded_chunks_is_saved_and_loading_answers_Reachable_across_them()
    {
        using var world = Create(out var terrain, out var walker);
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 5);
        world.Pathfinding().EnsureBuilt(walker);
        world.Tick();
        int ordinal = world.Fields().OrdinalOf(Middle);
        world.Residency().ForceResidency(ordinal, ChunkResidency.Unloaded);

        byte[] image = world.SaveToBytes();
        var sections = WorldPersistence.SectionNames(image).Where(n => n.StartsWith("ext/pathfinding/", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(["ext/pathfinding/built", "ext/pathfinding/summary/0"], sections);

        using var loaded = WorldPersistence.Load(image, world.Schema);
        Assert.Equal(ChunkResidency.Unloaded, loaded.Fields().ResidencyOf(ordinal));
        Assert.True(loaded.Pathfinding().Reachable(new TilePos(0, 0, 0), new TilePos(63, 0, 0), walker));
        Assert.Equal(RegionOracle.Actual(world, walker), RegionOracle.Actual(loaded, walker));
    }
}
