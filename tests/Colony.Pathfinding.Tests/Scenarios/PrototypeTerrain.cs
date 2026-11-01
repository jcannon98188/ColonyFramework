using Colony.Fields;
using Colony.Hashing;

namespace Colony.Pathfinding.Tests.Scenarios;

/// <summary>
/// A deterministic colony-like terrain for the pathfinding scenario and benchmarks: a grid of 12x12 rooms per
/// storey (shifted per storey) with a door in the middle of every wall segment, blobs of rock, scattered stairs
/// and, in every fifth room, a sealed 3x3 pocket ringed by wall. Hash-based, no RNG, no test-framework dependency
/// (the benchmarks link this file).
/// </summary>
public static class PrototypeTerrain
{
    public const byte Floor = 0;
    public const byte Wall = 1;
    public const byte ClosedDoor = 2;
    public const byte OpenDoor = 3;
    public const byte Stairs = 4;

    private const int Room = 12;

    /// <summary>The walker's rule: everything but walls and closed doors.</summary>
    public static bool Walkable(byte t) => t is not (Wall or ClosedDoor);

    /// <summary>The walker climbs only from stairs.</summary>
    public static bool Climbable(byte t) => t == Stairs;

    /// <summary>The terrain as a flat array indexed <c>x + X * (y + Y * z)</c>; <paramref name="pockets"/> (same length) receives the sealed pocket interiors.</summary>
    public static byte[] Generate(WorldSize size, bool[]? pockets = null)
    {
        var tiles = new byte[size.X * size.Y * size.Z];
        for (int z = 0; z < size.Z; z++)
        {
            int shift = z * 5;
            for (int y = 0; y < size.Y; y++)
            {
                for (int x = 0; x < size.X; x++)
                {
                    int rx = (x + shift) % Room;
                    int ry = (y + shift) % Room;
                    byte t = Floor;
                    if (rx == 0 || ry == 0)
                    {
                        t = (rx == 0 && ry == Room / 2) || (ry == 0 && rx == Room / 2)
                            ? (Hash(x, y, z, 1) % 3 == 0 ? ClosedDoor : OpenDoor)
                            : Wall;
                    }
                    else if (Hash(x >> 3, y >> 3, z, 2) % 9 == 0)
                    {
                        t = Wall; // rock blob
                    }
                    else if (z + 1 < size.Z && Hash(x, y, z, 3) % 61 == 0)
                    {
                        t = Stairs;
                    }

                    tiles[Index(size, x, y, z)] = t;
                }
            }

            // Sealed pockets, stamped last so rock never opens them.
            for (int cy = Room / 2; cy + 2 < size.Y; cy += Room)
            {
                for (int cx = Room / 2; cx + 2 < size.X; cx += Room)
                {
                    int px = cx - (shift % Room);
                    int py = cy - (shift % Room);
                    if (px < 2 || py < 2 || Hash(px / Room, py / Room, z, 4) % 5 != 0)
                    {
                        continue;
                    }

                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            bool ring = Math.Abs(dx) == 2 || Math.Abs(dy) == 2;
                            int i = Index(size, px + dx, py + dy, z);
                            tiles[i] = ring ? Wall : Floor;
                            if (!ring && pockets is not null)
                            {
                                pockets[i] = true;
                            }
                        }
                    }

                    // Nothing climbs into a pocket from below.
                    if (z > 0)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                ref byte below = ref tiles[Index(size, px + dx, py + dy, z - 1)];
                                if (below == Stairs)
                                {
                                    below = Floor;
                                }
                            }
                        }
                    }
                }
            }
        }

        return tiles;
    }

    public static void Write(World world, Field<byte> terrain, byte[] tiles)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(tiles);
        var fields = world.Fields();
        var size = fields.Size;
        var accessor = fields.Accessor(terrain);
        for (int z = 0; z < size.Z; z++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int x = 0; x < size.X; x++)
                {
                    byte t = tiles[Index(size, x, y, z)];
                    if (t != 0)
                    {
                        accessor[x, y, z] = t;
                    }
                }
            }
        }
    }

    public static int Index(WorldSize size, int x, int y, int z) => x + (size.X * (y + (size.Y * z)));

    private static ulong Hash(int a, int b, int c, int salt)
        => Hash64.Combine(Hash64.Combine(Hash64.Combine((ulong)salt, (ulong)a), (ulong)b), (ulong)c);
}
