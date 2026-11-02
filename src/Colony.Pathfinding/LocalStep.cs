using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>
/// The local A* of the two-level scheme (spec section 9.1): the single face move that takes a unit from a tile
/// toward a target in the same chunk along a shortest in-chunk route (breadth-first distances from the target;
/// ties by neighbour index -x, +x, -y, +y, -z, +z). Pure function of the chunk's passable and vertical bits.
/// </summary>
internal static class LocalStep
{
    /// <summary>
    /// The next tile toward <paramref name="target"/> (same chunk as <paramref name="from"/>, or a face-adjacent tile
    /// across a border, which is taken directly). False when no in-chunk route exists or the target is impassable.
    /// </summary>
    public static bool Toward(RegionMap map, TilePos from, TilePos target, Span<int> dist, Span<ushort> queue, out TilePos next)
    {
        next = from;
        if (from == target || !map.IsPassable(target) || !map.IsPassable(from))
        {
            return false;
        }

        var fields = map.Fields;
        if (from.Chunk != target.Chunk)
        {
            var d = target - from;
            if (Math.Abs(d.X) + Math.Abs(d.Y) + Math.Abs(d.Z) != 1)
            {
                return false;
            }

            if (d.Z != 0)
            {
                var lower = d.Z > 0 ? from : target;
                if (!ChunkGraph.Bit(map.UpOf(fields.OrdinalOf(lower.Chunk)), lower.CellIndex))
                {
                    return false;
                }
            }

            next = target;
            return true;
        }

        int ordinal = fields.OrdinalOf(from.Chunk);
        var passable = map.PassableOf(ordinal);
        var up = map.UpOf(ordinal);
        ChunkGraph.Distances(passable, up, target.CellIndex, dist, queue);
        int cell = from.CellIndex;
        if (dist[cell] < 0)
        {
            return false;
        }

        int x = cell & FieldChunk.Mask;
        int y = (cell >> FieldChunk.Shift) & FieldChunk.Mask;
        int z = cell >> (2 * FieldChunk.Shift);
        int best = -1;
        int bestDist = dist[cell];
        Consider(dist, x > 0, cell - 1, ref best, ref bestDist);
        Consider(dist, x < FieldChunk.Mask, cell + 1, ref best, ref bestDist);
        Consider(dist, y > 0, cell - FieldChunk.Size, ref best, ref bestDist);
        Consider(dist, y < FieldChunk.Mask, cell + FieldChunk.Size, ref best, ref bestDist);
        Consider(dist, z > 0 && ChunkGraph.Bit(up, cell - (FieldChunk.Size * FieldChunk.Size)), cell - (FieldChunk.Size * FieldChunk.Size), ref best, ref bestDist);
        Consider(dist, z < FieldChunk.Mask && ChunkGraph.Bit(up, cell), cell + (FieldChunk.Size * FieldChunk.Size), ref best, ref bestDist);
        if (best < 0)
        {
            return false;
        }

        var (nx, ny, nz) = FieldChunk.CellCoordinates(best);
        var origin = from.Chunk.Origin;
        next = new TilePos(origin.X + nx, origin.Y + ny, origin.Z + nz);
        return true;
    }

    private static void Consider(ReadOnlySpan<int> dist, bool inside, int neighbour, ref int best, ref int bestDist)
    {
        if (inside && dist[neighbour] >= 0 && dist[neighbour] < bestDist)
        {
            bestDist = dist[neighbour];
            best = neighbour;
        }
    }
}
