using BenchmarkDotNet.Attributes;
using Colony.Hashing;
using Colony.Numerics;
using Colony.Scenario;

namespace Colony.Benchmarks;

/// <summary>Hashing and RNG (spec section 6): XXH64 over small and chunk-sized buffers, Philox4x32-10 blocks, and the World hash of 100k colonists.</summary>
public class HashingBenchmarks
{
    public const int Blocks = 1024;

    private readonly byte[] _small = new byte[64];
    private readonly byte[] _page = new byte[4096];
    private readonly byte[] _chunk = new byte[65536];
    private readonly RandomSource _random = new(12345, 7);
    private World _world = null!;

    [GlobalSetup]
    public void Setup()
    {
        var fill = new RandomSource(1, 1);
        for (int i = 0; i < _chunk.Length; i++)
        {
            _chunk[i] = (byte)fill.UInt32At((ulong)i);
        }

        _chunk.AsSpan(0, _page.Length).CopyTo(_page);
        _chunk.AsSpan(0, _small.Length).CopyTo(_small);
        _world = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), workers: 1);
        _world.Tick();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public ulong Hash64Of64Bytes() => Hash64.Compute(_small);

    [Benchmark]
    public ulong Hash64Of4KiB() => Hash64.Compute(_page);

    [Benchmark]
    public ulong Hash64Of64KiB() => Hash64.Compute(_chunk);

    [Benchmark(OperationsPerInvoke = Blocks)]
    public uint Philox1024Blocks()
    {
        uint acc = 0;
        for (ulong i = 0; i < Blocks; i++)
        {
            acc ^= Philox.Generate(12345, 7, i)[0];
        }

        return acc;
    }

    [Benchmark(OperationsPerInvoke = Blocks)]
    public uint RandomSource1024Draws()
    {
        uint acc = 0;
        for (ulong i = 0; i < Blocks; i++)
        {
            acc ^= _random.UInt32At(i);
        }

        return acc;
    }

    [Benchmark]
    public ulong WorldHashOf100kColonists() => _world.Hash();
}
