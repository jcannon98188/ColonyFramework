using BenchmarkDotNet.Attributes;
using Colony.Fields;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Benchmarks;

/// <summary>Fields (spec section 5.1): the Field scenario's Tick (double-buffered diffusion plus in-place Life over 40 chunks) and raw accessor writes.</summary>
public class FieldsBenchmarks
{
    private World _world = null!;
    private FieldSet _fields = null!;
    private Field<int> _heat;

    /// <summary>1 = single-threaded; 0 = all physical cores (stencils run chunk-keyed through RunParallel).</summary>
    [Params(1, 0)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var scenario = new FieldScenario();
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var schema = builder.Build();
        _world = schema.CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = Workers });
        scenario.Populate(_world);
        _fields = _world.Fields();
        _heat = _fields.Field<int>("heat");
        for (int i = 0; i < 10; i++)
        {
            _world.Tick();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public bool StencilTick() => _world.Tick();

    /// <summary>Writes every cell of one dense chunk through the accessor (4096 cells).</summary>
    [Benchmark(OperationsPerInvoke = FieldChunk.CellCount)]
    public void AccessorWriteChunk()
    {
        var heat = _fields.Accessor(_heat);
        for (int z = 0; z < FieldChunk.Size; z++)
        {
            for (int y = 0; y < FieldChunk.Size; y++)
            {
                for (int x = 0; x < FieldChunk.Size; x++)
                {
                    heat[x, y, z] = x + y + z;
                }
            }
        }
    }
}
