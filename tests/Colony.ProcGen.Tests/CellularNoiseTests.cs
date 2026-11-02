using Colony.Numerics;

namespace Colony.ProcGen.Tests;

/// <summary>Cellular (Worley) noise: F1 and F2 feature distances and the nearest cell's hash, in 2D and 3D.</summary>
public sealed class CellularNoiseTests
{
    private static readonly NoiseKey Key = NoiseKey.Create(31, "cellular");

    [Fact]
    public void F1_is_at_most_F2_and_both_are_bounded_by_the_search_neighbourhood()
    {
        var (x, y) = NoiseTestData.Grid2D(48, frequencyShift: 4);
        var f1 = new Fixed32[x.Length];
        var f2 = new Fixed32[x.Length];
        var cells = new uint[x.Length];
        Noise.Cellular(Key, x, y, f1, f2, cells);

        for (int i = 0; i < x.Length; i++)
        {
            Assert.True(f1[i] <= f2[i], $"F1 {f1[i]} > F2 {f2[i]} at {i}");
            Assert.InRange(f1[i].ToDouble(), 0, 1.5);
            Assert.InRange(f2[i].ToDouble(), 0, 2.3);
        }
    }

    [Fact]
    public void F1_is_the_distance_to_the_nearest_feature_point_recomputed_independently()
    {
        var sample = Noise.Cellular(Key, Fixed32.FromDouble(2.3), Fixed32.FromDouble(-1.7));

        // Recompute from the public lattice hash: one feature point per cell, jittered by the hash words.
        double best = double.MaxValue;
        uint bestCell = 0;
        for (int cy = -3; cy <= -1; cy++)
        {
            for (int cx = 1; cx <= 3; cx++)
            {
                uint h = Key.Hash(cx, cy);
                double px = cx + ((h & 0xFFFF) / 65536.0);
                double py = cy + ((h >> 16) / 65536.0);
                double d = System.Math.Sqrt(((px - 2.3) * (px - 2.3)) + ((py + 1.7) * (py + 1.7)));
                if (d < best)
                {
                    best = d;
                    bestCell = h;
                }
            }
        }

        Assert.Equal(bestCell, sample.Cell);
        Assert.InRange(sample.F1.ToDouble(), best - 0.0002, best + 0.0002);
    }

    [Fact]
    public void The_nearest_cell_hash_is_constant_inside_a_cell_region_and_changes_across_edges()
    {
        var (x, y) = NoiseTestData.Grid2D(64, frequencyShift: 5);
        var f1 = new Fixed32[x.Length];
        var f2 = new Fixed32[x.Length];
        var cells = new uint[x.Length];
        Noise.Cellular(Key, x, y, f1, f2, cells);

        int changes = 0;
        for (int i = 1; i < cells.Length; i++)
        {
            changes += cells[i] != cells[i - 1] ? 1 : 0;
        }

        // 64 rows over two lattice units: a few cell boundaries per row, far fewer than one per sample.
        Assert.InRange(changes, 64, cells.Length / 4);
    }

    [Fact]
    public void Three_dimensional_cellular_noise_is_ordered_and_bounded()
    {
        var (x, y, z) = NoiseTestData.Grid3D(16, frequencyShift: 3);
        var f1 = new Fixed32[x.Length];
        var f2 = new Fixed32[x.Length];
        var cells = new uint[x.Length];
        Noise.Cellular(Key, x, y, z, f1, f2, cells);

        for (int i = 0; i < x.Length; i++)
        {
            Assert.True(f1[i] <= f2[i]);
            Assert.InRange(f1[i].ToDouble(), 0, 1.8);
            var point = Noise.Cellular(Key, x[i], y[i], z[i]);
            Assert.Equal(f1[i], point.F1);
            Assert.Equal(f2[i], point.F2);
            Assert.Equal(cells[i], point.Cell);
        }
    }

    [Fact]
    public void Batch_equals_point_API_and_all_paths_are_bit_identical()
    {
        var (x, y) = NoiseTestData.Grid2D(45);
        var f1 = new Fixed32[x.Length];
        var f2 = new Fixed32[x.Length];
        var cells = new uint[x.Length];
        Noise.Cellular(Key, x, y, f1, f2, cells, KernelPath.Scalar);
        for (int i = 0; i < x.Length; i += 5)
        {
            var point = Noise.Cellular(Key, x[i], y[i]);
            Assert.Equal(f1[i], point.F1);
            Assert.Equal(f2[i], point.F2);
            Assert.Equal(cells[i], point.Cell);
        }

        var g1 = new Fixed32[x.Length];
        var g2 = new Fixed32[x.Length];
        var gcells = new uint[x.Length];
        foreach (var path in NoiseTestData.AllPaths)
        {
            Noise.Cellular(Key, x, y, g1, g2, gcells, path);
            NoiseTestData.AssertSameRaw(f1, g1, $"F1/{path}");
            NoiseTestData.AssertSameRaw(f2, g2, $"F2/{path}");
            Assert.Equal(cells, gcells);
        }

        var (x3, y3, z3) = NoiseTestData.Grid3D(11);
        var h1 = new Fixed32[x3.Length];
        var h2 = new Fixed32[x3.Length];
        var hcells = new uint[x3.Length];
        Noise.Cellular(Key, x3, y3, z3, h1, h2, hcells, KernelPath.Scalar);
        var k1 = new Fixed32[x3.Length];
        var k2 = new Fixed32[x3.Length];
        var kcells = new uint[x3.Length];
        foreach (var path in NoiseTestData.AllPaths)
        {
            Noise.Cellular(Key, x3, y3, z3, k1, k2, kcells, path);
            NoiseTestData.AssertSameRaw(h1, k1, $"F1-3D/{path}");
            NoiseTestData.AssertSameRaw(h2, k2, $"F2-3D/{path}");
            Assert.Equal(hcells, kcells);
        }
    }
}
