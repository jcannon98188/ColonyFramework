using Colony.Fields;
using Colony.Pathfinding;

namespace Colony.Jobs.Tests;

/// <summary>
/// Shared fixture for the work index: a small World with a byte "terrain" Field (0 = floor, 1 = wall), a walker
/// movement class blocked by walls, TilePos grid Entities and the work index. Host-side helpers spawn work items
/// between Ticks; a Tick runs the Late-Phase update.
/// </summary>
internal sealed class WorkTestWorld : IDisposable
{
    public const int Haul = 0;
    public const int Mine = 1;
    public const int Build = 2;

    public WorkTestWorld(WorldSize size, int workers = 1, WorkIndexOptions? options = null, bool pathfinding = true)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var fields = builder.AddFields(size);
        Terrain = fields.AddField<byte>("terrain");
        TilePos = builder.AddComponent<TilePos>("tile.pos").Component<TilePos>();
        if (pathfinding)
        {
            var paths = builder.AddPathfinding(fields);
            Walker = paths.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(Terrain, static (byte t) => t == 0)).DependsOn(Terrain));
        }

        Index = builder.AddWorkIndex(fields, TilePos, options);
        Schema = builder.Build();
        World = Schema.CreateWorld(new WorldOptions { WorkerThreads = workers, Seed = 11 });
    }

    public WorldSchema Schema { get; }

    public World World { get; }

    public Field<byte> Terrain { get; }

    public ComponentType TilePos { get; }

    public MovementClass Walker { get; }

    public WorkIndexBuilder Index { get; }

    public WorkIndex WorkIndex => World.WorkIndex();

    public PathfindingService Pathfinding => World.Pathfinding();

    /// <summary>Spawns one work item per (position, type), available; returns ids in input order. Indexed at the next Tick.</summary>
    public EntityId[] SpawnItems(ReadOnlySpan<(TilePos Position, int Type)> items, bool available = true)
    {
        var buffer = World.CreateCommandBuffer();
        var mask = ComponentMask.Of(TilePos, Index.WorkItem);
        var deferred = new EntityId[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            deferred[i] = buffer.Create(mask);
            buffer.Add(deferred[i], TilePos, items[i].Position);
            buffer.Add(deferred[i], Index.WorkItem, available ? WorkItem.Available(items[i].Type) : WorkItem.Taken(items[i].Type));
        }

        World.Playback(buffer);
        var ids = new EntityId[deferred.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = buffer.Resolve(deferred[i]);
        }

        World.ResetScratch();
        return ids;
    }

    public EntityId SpawnItem(TilePos position, int type, bool available = true)
        => SpawnItems([(position, type)], available)[0];

    /// <summary>A full-height wall across the World at <paramref name="x"/>, optionally with one door tile.</summary>
    public void WallColumn(int x, int? doorY = null)
    {
        var fields = World.Fields();
        var accessor = fields.Accessor(Terrain);
        for (int z = 0; z < fields.Size.Z; z++)
        {
            for (int y = 0; y < fields.Size.Y; y++)
            {
                accessor[x, y, z] = (byte)(y == doorY ? 0 : 1);
            }
        }
    }

    public WorkHit[] Nearest(TilePos origin, int radius, int count, params int[] types)
    {
        var hits = new WorkHit[count];
        int n = WorkIndex.QueryNearest(origin, types, radius, hits);
        return hits[..n];
    }

    public void Dispose() => World.Dispose();
}

/// <summary>The Game-side adapter of spec 9.2 "reachability filters candidates": accepts items a walker standing at <see cref="From"/> can reach.</summary>
internal readonly struct ReachableFrom(PathfindingService pathfinding, TilePos from, MovementClass movementClass) : IWorkFilter
{
    public TilePos From => from;

    public bool Accepts(EntityId entity, in TilePos position, int type) => pathfinding.Reachable(from, position, movementClass);
}
