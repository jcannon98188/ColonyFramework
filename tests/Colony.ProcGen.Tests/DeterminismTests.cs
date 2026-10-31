using Colony.Numerics;

namespace Colony.ProcGen.Tests;

/// <summary>
/// AC #2: evaluating any coordinate in any order on any thread yields identical results. One sequential batch is the
/// reference; the same positions are then evaluated as shuffled chunks on many threads, as single points in reverse,
/// and as a window inside a larger batch (different lane alignment), and every bit must agree.
/// </summary>
public sealed class DeterminismTests
{
    private static readonly NoiseKey Key = NoiseKey.Create(0xD15EA5E, "determinism");
    private static readonly FbmSettings Fbm = new() { Octaves = 3, Basis = NoiseBasis.Simplex };

    internal delegate void Batch2(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result);

    private static readonly (string Name, Batch2 Batch)[] Batches =
    [
        ("Value", Noise.Value),
        ("Gradient", Noise.Gradient),
        ("Simplex", Noise.Simplex),
        ("Fbm", (key, x, y, r) => Noise.Fbm(key, Fbm, x, y, r)),
        ("CellularF1", (key, x, y, r) =>
        {
            var f2 = new Fixed32[x.Length];
            var cells = new uint[x.Length];
            Noise.Cellular(key, x, y, r, f2, cells);
        }),
        ("WarpX", (key, x, y, r) =>
        {
            x.CopyTo(r);
            var wy = y.ToArray();
            Noise.Warp(key, Fixed32.Half, r, wy);
        }),
    ];

    [Fact]
    public void Shuffled_chunks_on_many_threads_reproduce_the_sequential_batch()
    {
        var (x, y) = NoiseTestData.Grid2D(128);
        const int chunk = 37; // not a lane multiple, so chunk starts fall on every alignment
        int chunks = (x.Length + chunk - 1) / chunk;
        var order = Enumerable.Range(0, chunks).ToArray();
        new Random(99).Shuffle(order);

        foreach (var (name, batch) in Batches)
        {
            var reference = new Fixed32[x.Length];
            batch(Key, x, y, reference);

            var parallel = new Fixed32[x.Length];
            Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, c =>
            {
                int start = c * chunk;
                int length = Math.Min(chunk, x.Length - start);
                batch(Key, x.AsSpan(start, length), y.AsSpan(start, length), parallel.AsSpan(start, length));
            });

            NoiseTestData.AssertSameRaw(reference, parallel, $"{name}/parallel");
        }
    }

    [Fact]
    public void Single_points_in_reverse_order_reproduce_the_batch()
    {
        var (x, y) = NoiseTestData.Grid2D(32);
        var reference = new Fixed32[x.Length];
        Noise.Fbm(Key, Fbm, x, y, reference);
        for (int i = x.Length - 1; i >= 0; i--)
        {
            Assert.Equal(reference[i], Noise.Fbm(Key, Fbm, x[i], y[i]));
        }

        var f1 = new Fixed32[x.Length];
        var f2 = new Fixed32[x.Length];
        var cells = new uint[x.Length];
        Noise.Cellular(Key, x, y, f1, f2, cells);
        for (int i = x.Length - 1; i >= 0; i -= 3)
        {
            Assert.Equal(new CellularSample(f1[i], f2[i], cells[i]), Noise.Cellular(Key, x[i], y[i]));
        }
    }

    [Fact]
    public void A_window_inside_a_larger_batch_reproduces_the_standalone_batch()
    {
        var (x, y) = NoiseTestData.Grid2D(40);
        foreach (var (name, batch) in Batches)
        {
            var whole = new Fixed32[x.Length];
            batch(Key, x, y, whole);
            for (int offset = 1; offset <= 9; offset += 4)
            {
                var window = new Fixed32[301];
                batch(Key, x.AsSpan(offset, 301), y.AsSpan(offset, 301), window);
                NoiseTestData.AssertSameRaw(whole.AsSpan(offset, 301), window, $"{name}/window+{offset}");
            }
        }
    }

    [Fact]
    public void The_active_path_is_a_real_vector_path()
    {
        Assert.Equal(System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated ? KernelPath.Vector256 : KernelPath.Vector128, Noise.ActivePath);
    }
}
