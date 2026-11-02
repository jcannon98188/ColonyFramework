using BenchmarkDotNet.Attributes;
using Colony.Persistence;
using Colony.Scenario;

namespace Colony.Benchmarks;

/// <summary>Save/load (spec section 7.1): snapshot and encode 10k colonists to bytes, and load them back into a fresh World.</summary>
public class PersistenceBenchmarks
{
    public const int Colonists = 10_000;

    private WorldSchema _schema = null!;
    private World _world = null!;
    private SaveOptions _options = null!;
    private byte[] _saved = [];

    [Params(SaveCompression.Lz4, SaveCompression.None)]
    public SaveCompression Compression { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _schema = ColonistScenario.BuildSchema(foodTarget: 500);
        _world = ColonistScenario.CreateWorld(_schema, workers: 1, colonists: Colonists, food: 500);
        for (int i = 0; i < 10; i++)
        {
            _world.Tick();
        }

        _options = new SaveOptions { Compression = Compression, BackupGenerations = 0 };
        _saved = _world.SaveToBytes(_options);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public byte[] SaveToBytes() => _world.SaveToBytes(_options);

    [Benchmark]
    public ulong LoadAndHash()
    {
        using var loaded = WorldPersistence.Load(_saved, _schema, new LoadOptions { VerifyWorldHash = false });
        return loaded.Hash();
    }

    /// <summary>Bytes the saved image takes with this compression setting (reported as a return value, measured once).</summary>
    [Benchmark]
    public int SavedBytes() => _saved.Length;
}
