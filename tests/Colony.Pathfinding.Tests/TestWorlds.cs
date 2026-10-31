using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>Small Worlds with a byte "terrain" Field (0 = floor, 1 = wall), a walker class blocked by walls and a flyer class that ignores them.</summary>
internal static class TestWorlds
{
    public static readonly PassabilityRule AllPassable = static (in PassabilityInput input, Span<ulong> passable) => passable.Fill(ulong.MaxValue);

    public static World Create(WorldSize size, out Field<byte> terrain, out MovementClass walker, out MovementClass flyer, int workers = 1, PathfindingOptions? options = null)
    {
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(size);
        terrain = fields.AddField<byte>("terrain");
        var pathfinding = builder.AddPathfinding(fields, options);
        walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        flyer = pathfinding.AddMovementClass(new MovementClassDefinition("flyer", AllPassable));
        return builder.Build().CreateWorld(new WorldOptions { WorkerThreads = workers });
    }

    /// <summary>A full-height wall across the World at <paramref name="x"/>, optionally with one door tile.</summary>
    public static void WallColumn(World world, Field<byte> terrain, int x, int? doorY = null)
    {
        var fields = world.Fields();
        var accessor = fields.Accessor(terrain);
        for (int z = 0; z < fields.Size.Z; z++)
        {
            for (int y = 0; y < fields.Size.Y; y++)
            {
                accessor[x, y, z] = (byte)(y == doorY ? 0 : 1);
            }
        }
    }

    public static void Set(World world, Field<byte> terrain, int x, int y, int z, byte value)
    {
        var accessor = world.Fields().Accessor(terrain);
        accessor[x, y, z] = value;
    }
}
