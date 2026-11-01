using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>
/// Independent oracle for flow Fields: a tile-level multi-source breadth-first search from the goal tiles over
/// the passable tiles of a chunk box (face moves; vertical moves where the lower tile climbs), nothing shared
/// with <see cref="FlowField"/>.
/// </summary>
internal static class FlowOracle
{
    /// <summary>Expected distance of every tile inside the box (flat index <c>x + X * (y + Y * z)</c> over the World), -1 outside the box, impassable or unreached.</summary>
    public static int[] Expected(World world, ReadOnlySpan<TilePos> goals, ChunkCoord min, ChunkCoord max, Func<TilePos, bool> passable, Func<TilePos, bool>? climbs = null)
    {
        var size = world.Fields().Size;
        var distance = new int[size.X * size.Y * size.Z];
        Array.Fill(distance, -1);
        var queue = new Queue<TilePos>();
        foreach (var goal in goals)
        {
            if (Inside(goal, size, min, max) && passable(goal) && distance[Index(size, goal)] < 0)
            {
                distance[Index(size, goal)] = 0;
                queue.Enqueue(goal);
            }
        }

        ReadOnlySpan<(int X, int Y, int Z)> faces = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];
        while (queue.Count > 0)
        {
            var tile = queue.Dequeue();
            int next = distance[Index(size, tile)] + 1;
            foreach (var (dx, dy, dz) in faces)
            {
                var n = new TilePos(tile.X + dx, tile.Y + dy, tile.Z + dz);
                if (!Inside(n, size, min, max) || !passable(n) || distance[Index(size, n)] >= 0)
                {
                    continue;
                }

                if (dz != 0 && climbs is not null && !climbs(dz > 0 ? tile : n))
                {
                    continue;
                }

                distance[Index(size, n)] = next;
                queue.Enqueue(n);
            }
        }

        return distance;
    }

    /// <summary>The flow Field's answer for every tile of the World, -1 where it has none.</summary>
    public static int[] Actual(World world, FlowField field)
    {
        var size = world.Fields().Size;
        var distance = new int[size.X * size.Y * size.Z];
        for (int z = 0; z < size.Z; z++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int x = 0; x < size.X; x++)
                {
                    distance[Index(size, new TilePos(x, y, z))] = field.DistanceTo(new TilePos(x, y, z));
                }
            }
        }

        return distance;
    }

    public static int Index(WorldSize size, TilePos tile) => tile.X + (size.X * (tile.Y + (size.Y * tile.Z)));

    private static bool Inside(TilePos tile, WorldSize size, ChunkCoord min, ChunkCoord max)
    {
        if (!size.Contains(tile))
        {
            return false;
        }

        var c = tile.Chunk;
        return c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y && c.Z >= min.Z && c.Z <= max.Z;
    }
}
