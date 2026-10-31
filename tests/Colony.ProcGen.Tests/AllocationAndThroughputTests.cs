using System.Diagnostics;
using System.Globalization;
using Colony.Numerics;

namespace Colony.ProcGen.Tests;

/// <summary>
/// Zero allocation in steady state (spec section 2) and AC #4: a throughput report per utility. The report is a
/// test that prints samples per second for the active SIMD path and the scalar path; benchmarks/ holds only a
/// README stub until TASK-30 lands BenchmarkDotNet, so this is the committed throughput evidence for now.
/// </summary>
public sealed class AllocationAndThroughputTests
{
    private const int BatchSize = 4096;
    private static readonly NoiseKey Key = NoiseKey.Create(3, "perf");
    private static readonly FbmSettings Fbm = new() { Octaves = 4, Basis = NoiseBasis.Gradient };

    [Fact]
    public void Steady_state_evaluation_allocates_nothing()
    {
        var (x, y) = NoiseTestData.Grid2D(64);
        var (x3, y3, z3) = NoiseTestData.Grid3D(16);
        var r = new Fixed32[x.Length];
        var r3 = new Fixed32[x3.Length];
        var f2 = new Fixed32[x.Length];
        var cells = new uint[x.Length];
        var f23 = new Fixed32[x3.Length];
        var cells3 = new uint[x3.Length];
        var w3 = new Fixed32[x3.Length];
        var spots = new Spot[8];

        void Tick()
        {
            Noise.Value(Key, x, y, r);
            Noise.Gradient(Key, x, y, r);
            Noise.Simplex(Key, x, y, r);
            Noise.Cellular(Key, x, y, r, f2, cells);
            Noise.Fbm(Key, Fbm, x, y, r);
            Noise.Warp(Key, Fixed32.Half, r, f2);
            Noise.Value(Key, x3, y3, z3, r3);
            Noise.Gradient(Key, x3, y3, z3, r3);
            Noise.Simplex(Key, x3, y3, z3, r3);
            Noise.Cellular(Key, x3, y3, z3, r3, f23, cells3);
            Noise.Fbm(Key, Fbm, x3, y3, z3, r3);
            Noise.Warp(Key, Fixed32.Half, r3, f23, w3);
            for (int i = 0; i < 64; i++)
            {
                _ = Noise.Value(Key, x[i], y[i]) + Noise.Gradient(Key, x[i], y[i]) + Noise.Simplex(Key, x[i], y[i], x[i]);
                _ = Noise.Cellular(Key, x[i], y[i]).F1 + Noise.Fbm(Key, Fbm, x[i], y[i]) + Noise.Warp(Key, Fixed32.One, x[i], y[i]).X;
                _ = SpotSampler.Sample(Key, i, -i, Fixed32.FromInt32(32), 1, 8, spots);
            }
        }

        Tick();
        Tick();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            Tick();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Throughput_report()
    {
        var (x, y) = NoiseTestData.Grid2D(64);
        var (x3, y3, z3) = NoiseTestData.Grid3D(16);
        var r = new Fixed32[BatchSize];
        var f2 = new Fixed32[BatchSize];
        var cells = new uint[BatchSize];
        var output = TestContext.Current.TestOutputHelper;

        Report(output, "Value2D", path => Noise.Value(Key, x, y, r, path));
        Report(output, "Gradient2D", path => Noise.Gradient(Key, x, y, r, path));
        Report(output, "Simplex2D", path => Noise.Simplex(Key, x, y, r, path));
        Report(output, "Cellular2D", path => Noise.Cellular(Key, x, y, r, f2, cells, path));
        Report(output, "Fbm2D(4 octaves)", path => Noise.Fbm(Key, Fbm, x, y, r, path));
        Report(output, "Warp2D", path =>
        {
            x.CopyTo(r, 0);
            y.CopyTo(f2, 0);
            Noise.Warp(Key, Fixed32.Half, r, f2, path);
        });
        Report(output, "Value3D", path => Noise.Value(Key, x3, y3, z3, r, path));
        Report(output, "Gradient3D", path => Noise.Gradient(Key, x3, y3, z3, r, path));
        Report(output, "Simplex3D", path => Noise.Simplex(Key, x3, y3, z3, r, path));
        Report(output, "Cellular3D", path => Noise.Cellular(Key, x3, y3, z3, r, f2, cells, path));
        Report(output, "Fbm3D(4 octaves)", path => Noise.Fbm(Key, Fbm, x3, y3, z3, r, path));

        var spots = new Spot[8];
        var watch = Stopwatch.StartNew();
        int total = 0;
        for (int i = 0; i < 100_000; i++)
        {
            total += SpotSampler.Sample(Key, i, -i, Fixed32.FromInt32(32), 1, 8, spots);
        }

        watch.Stop();
        output?.WriteLine(string.Format(CultureInfo.InvariantCulture, "Spots: {0:N0} regions/s ({1:N0} spots)", 100_000 / watch.Elapsed.TotalSeconds, total));
    }

    private static void Report(ITestOutputHelper? output, string name, Action<KernelPath> run)
    {
        // Warm up past the tiered-JIT call threshold on every path, give the background tier-1 compile time to land,
        // then time a fixed number of batches per path.
        for (int round = 0; round < 2; round++)
        {
            foreach (var path in NoiseTestData.AllPaths)
            {
                for (int i = 0; i < 40; i++)
                {
                    run(path);
                }
            }

            Thread.Sleep(200);
        }

        const int iterations = 100;
        var line = new System.Text.StringBuilder(name).Append(": ");
        foreach (var path in new[] { Noise.ActivePath, KernelPath.Scalar })
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                run(path);
            }

            watch.Stop();
            double samplesPerSecond = (double)iterations * BatchSize / watch.Elapsed.TotalSeconds;
            line.Append(CultureInfo.InvariantCulture, $"{path} {samplesPerSecond / 1e6:F1} Msamples/s  ");
        }

        output?.WriteLine(line.ToString());
    }
}
