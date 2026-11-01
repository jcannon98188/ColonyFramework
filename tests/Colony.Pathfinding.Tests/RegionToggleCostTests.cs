using Colony.Fields;
using Colony.Pathfinding.Tests.Scenarios;

namespace Colony.Pathfinding.Tests;

/// <summary>
/// TASK-39 AC #2: a door toggle costs work proportional to the change (union-find merge fast path, bounded split
/// search), not to the size of the connected colony around it, and the ids still equal the oracle.
/// </summary>
public sealed class RegionToggleCostTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Toggling_doors_in_a_large_colony_visits_a_small_part_of_the_region_graph_and_matches_the_oracle(int workers)
    {
        var size = new WorldSize(192, 192, 4); // 12 x 12 chunks, one big connected colony of rooms
        var tiles = PrototypeTerrainWorld.Create(size, out var world, out var walker, out var terrain, workers);
        using (world)
        {
            var service = world.Pathfinding();
            service.EnsureBuilt(walker);
            var map = service.Regions(walker);
            int nodes = 0;
            for (int o = 0; o < map.ChunkCount; o++)
            {
                nodes += map.ComponentCount(o);
            }

            var accessor = world.Fields().Accessor(terrain);
            int toggled = 0;
            long worst = 0;
            for (int i = 0; i < tiles.Length && toggled < 60; i += 2459)
            {
                int door = Array.FindIndex(tiles, i, static t => t is PrototypeTerrain.OpenDoor or PrototypeTerrain.ClosedDoor);
                if (door < 0)
                {
                    break;
                }

                var tile = new TilePos(door % size.X, door / size.X % size.Y, door / (size.X * size.Y));
                tiles[door] = tiles[door] == PrototypeTerrain.OpenDoor ? PrototypeTerrain.ClosedDoor : PrototypeTerrain.OpenDoor;
                accessor[tile] = tiles[door];
                long before = map.Counters.NodesVisited;
                world.Tick();
                worst = Math.Max(worst, map.Counters.NodesVisited - before);
                toggled++;
                if (toggled % 10 == 0)
                {
                    Assert.Equal(Expected(world, size, tiles), RegionOracle.Actual(world, walker));
                }
            }

            Assert.True(toggled >= 30, $"only {toggled} doors toggled");
            Assert.Equal(Expected(world, size, tiles), RegionOracle.Actual(world, walker));
            Assert.Equal(0, map.Counters.FullRelabels);
            Assert.True(worst * 8 < nodes, $"one toggle visited {worst} of {nodes} region graph nodes");
        }
    }

    private static long[] Expected(World world, WorldSize size, byte[] tiles)
        => RegionOracle.Expected(
            world,
            t => PrototypeTerrain.Walkable(tiles[PrototypeTerrain.Index(size, t.X, t.Y, t.Z)]),
            t => PrototypeTerrain.Climbable(tiles[PrototypeTerrain.Index(size, t.X, t.Y, t.Z)]));
}
