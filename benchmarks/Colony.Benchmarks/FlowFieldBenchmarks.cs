using BenchmarkDotNet.Attributes;
using Colony.Fields;
using Colony.Pathfinding;
using Colony.Pathfinding.Tests.Scenarios;

namespace Colony.Benchmarks;

/// <summary>
/// Flow Fields (spec section 9.1, TASK-21 AC #2): on the small prototype World (256x256x32,
/// <see cref="PrototypeTerrain"/>) a Field of three goals bounded by two chunks around them (5x5x2 chunks). The
/// repair benchmark toggles one door inside the bound and repairs: with <see cref="RebuildFraction"/> 0.0 every
/// repair is a full rebuild, with 1.0 the incremental path runs (affected chunks plus whatever their faces moved);
/// the two rows measure the rebuild-versus-incremental threshold the option sets. Steps are 1024 flow reads.
/// </summary>
public class FlowFieldBenchmarks
{
    private const int Steps = 1024;

    private World _world = null!;
    private PathfindingService _service = null!;
    private MovementClass _walker;
    private Field<byte> _terrain;
    private FlowField _field = null!;
    private TilePos _door;
    private TilePos[] _from = [];

    [Params(0.0, 1.0)]
    public double RebuildFraction { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var size = new WorldSize(256, 256, 32);
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(size);
        _terrain = fields.AddField<byte>("terrain");
        _walker = builder.AddPathfinding(fields, new PathfindingOptions { FlowRebuildFraction = RebuildFraction }).AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(_terrain, PrototypeTerrain.Walkable))
        {
            Vertical = Passability.OfField(_terrain, PrototypeTerrain.Climbable),
        }.DependsOn(_terrain));
        _world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        byte[] tiles = PrototypeTerrain.Generate(size);
        PrototypeTerrain.Write(_world, _terrain, tiles);
        _service = _world.Pathfinding();
        _service.EnsureBuilt(_walker);

        // A door near the middle of the World and three floor goals in chunks around it.
        int door = Array.FindIndex(tiles, PrototypeTerrain.Index(size, 128, 128, 0), static t => t is PrototypeTerrain.OpenDoor or PrototypeTerrain.ClosedDoor);
        _door = TileAt(size, door);
        var goals = new TilePos[3];
        int found = 0;
        for (int i = PrototypeTerrain.Index(size, 100, 100, 0); found < 3; i += 997)
        {
            if (tiles[i] == PrototypeTerrain.Floor)
            {
                goals[found++] = TileAt(size, i);
            }
        }

        _field = _service.FlowField(goals, _walker, radiusChunks: 2);
        var random = _world.Random.Derive("steps");
        _from = new TilePos[Steps];
        var min = _field.Bounds.Min.Origin;
        for (int i = 0; i < Steps; i++)
        {
            ulong k = (ulong)i * 3;
            _from[i] = new TilePos(min.X + random.Int32At(k, 0, _field.Bounds.SizeX * FieldChunk.Size), min.Y + random.Int32At(k + 1, 0, _field.Bounds.SizeY * FieldChunk.Size), min.Z + random.Int32At(k + 2, 0, _field.Bounds.SizeZ * FieldChunk.Size));
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>One door inside the bound flips, the change feed is applied and the Field is repaired (rebuild or incremental by <see cref="RebuildFraction"/>).</summary>
    [Benchmark]
    public void ToggleDoorAndRepair()
    {
        var accessor = _world.Fields().Accessor(_terrain);
        accessor[_door] = accessor[_door] == PrototypeTerrain.OpenDoor ? PrototypeTerrain.ClosedDoor : PrototypeTerrain.OpenDoor;
        _service.Sync();
        _service.Repair(_field);
    }

    [Benchmark(OperationsPerInvoke = Steps)]
    public int Step()
    {
        int steps = 0;
        for (int i = 0; i < Steps; i++)
        {
            steps += _field.TryStep(_from[i], out _) == FlowStepStatus.Step ? 1 : 0;
        }

        return steps;
    }

    private static TilePos TileAt(WorldSize size, int index) => new(index % size.X, index / size.X % size.Y, index / (size.X * size.Y));
}
