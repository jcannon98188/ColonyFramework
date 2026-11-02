using BenchmarkDotNet.Attributes;
using Colony.Fields;
using Colony.Pathfinding;
using Colony.Pathfinding.Tests.Scenarios;

namespace Colony.Benchmarks;

/// <summary>
/// Pathfinding regions (spec section 9.1, TASK-19 AC #3 and #4) on the prototype World sizes, small 256x256x32 and
/// big 512x512x64, over <see cref="PrototypeTerrain"/> (rooms, doors, rock, stairs, sealed pockets): the
/// from-scratch rebuild a load performs, an incremental sync after one door toggle, and 1024 Reachable queries
/// (constant time: the same cost on both sizes).
/// TASK-39 ToggleDoorAndSync (1 / 8 workers, after the setup Ticks): BFS relabelling of every affected region
/// 508 / 482 us small, 4,623 / 4,431 us big; union-find merge fast path and bounded split search 118 / 91 us
/// small, 124 / 98 us big.
/// </summary>
public class PathfindingBenchmarks
{
    private const int Queries = 1024;

    private World _world = null!;
    private PathfindingService _service = null!;
    private MovementClass _walker;
    private Field<byte> _terrain;
    private TilePos _door;
    private TilePos[] _from = [];
    private TilePos[] _to = [];

    [Params("small", "big")]
    public string World { get; set; } = "small";

    [Params(1, 8)]
    public int Workers { get; set; } = 1;

    [GlobalSetup]
    public void Setup()
    {
        var size = World == "big" ? new WorldSize(512, 512, 64) : new WorldSize(256, 256, 32);
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(size);
        _terrain = fields.AddField<byte>("terrain");
        _walker = builder.AddPathfinding(fields).AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(_terrain, PrototypeTerrain.Walkable))
        {
            Vertical = Passability.OfField(_terrain, PrototypeTerrain.Climbable),
        }.DependsOn(_terrain));
        _world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = Workers });
        byte[] tiles = PrototypeTerrain.Generate(size);
        PrototypeTerrain.Write(_world, _terrain, tiles);
        _service = _world.Pathfinding();
        _service.EnsureBuilt(_walker);

        // Two Ticks move the change feed past the terrain written above. Without them every chunk carries the
        // running version, so each Sync re-evaluates the whole World (host writes at the running version stay
        // dirty until a Tick passes), which is what the TASK-19 numbers (13 ms small, 143 ms big) measured.
        _world.Tick();
        _world.Tick();

        int door = Array.FindIndex(tiles, static t => t is PrototypeTerrain.OpenDoor or PrototypeTerrain.ClosedDoor);
        _door = new TilePos(door % size.X, door / size.X % size.Y, door / (size.X * size.Y));
        _from = new TilePos[Queries];
        _to = new TilePos[Queries];
        var random = _world.Random.Derive("queries");
        for (int i = 0; i < Queries; i++)
        {
            ulong k = (ulong)i * 6;
            _from[i] = new TilePos(random.Int32At(k, 0, size.X), random.Int32At(k + 1, 0, size.Y), random.Int32At(k + 2, 0, size.Z));
            _to[i] = new TilePos(random.Int32At(k + 3, 0, size.X), random.Int32At(k + 4, 0, size.Y), random.Int32At(k + 5, 0, size.Z));
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>Labels every chunk from scratch: what loading a save does for each built class.</summary>
    [Benchmark]
    public void Rebuild() => _service.Rebuild(_walker);

    /// <summary>One door flips, then the change feed is applied (one chunk relabelled, affected regions re-searched).</summary>
    [Benchmark]
    public void ToggleDoorAndSync()
    {
        var accessor = _world.Fields().Accessor(_terrain);
        accessor[_door] = accessor[_door] == PrototypeTerrain.OpenDoor ? PrototypeTerrain.ClosedDoor : PrototypeTerrain.OpenDoor;
        _service.Sync();
    }

    [Benchmark(OperationsPerInvoke = Queries)]
    public int Reachable()
    {
        int reachable = 0;
        for (int i = 0; i < Queries; i++)
        {
            reachable += _service.Reachable(_from[i], _to[i], _walker) ? 1 : 0;
        }

        return reachable;
    }
}
