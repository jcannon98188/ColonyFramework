using Colony.Numerics;

namespace Colony.ProcGen.Tests;

public sealed class ValueNoiseTests
{
    private static readonly NoiseKey Key = NoiseKey.Create(2024, "value");

    [Fact]
    public void At_lattice_points_the_value_is_the_hashed_corner_value_in_minus_one_to_one()
    {
        for (int y = -3; y <= 3; y++)
        {
            for (int x = -3; x <= 3; x++)
            {
                var sample = Noise.Value(Key, Fixed32.FromInt32(x), Fixed32.FromInt32(y));
                // The corner value is the top 17 bits of the lattice hash mapped onto [-1, 1).
                int expectedRaw = (int)(Key.Hash(x, y) >> 15) - 65536;
                Assert.Equal(expectedRaw, sample.Raw);
                Assert.InRange(sample.Raw, -65536, 65535);
            }
        }
    }

    [Fact]
    public void Values_stay_in_range_and_vary_smoothly_between_lattice_points()
    {
        var (x, y) = NoiseTestData.Grid2D(64, frequencyShift: 5); // step 1/32 lattice unit
        var result = new Fixed32[x.Length];
        Noise.Value(Key, x, y, result);

        int distinct = 0;
        for (int i = 1; i < result.Length; i++)
        {
            Assert.InRange(result[i].Raw, -65536, 65535);
            if (i % 64 != 0)
            {
                // Neighbouring samples 1/32 apart differ by well under 0.2 (the quintic fade bounds the slope).
                Assert.InRange(System.Math.Abs(result[i].Raw - result[i - 1].Raw), 0, 13000);
                distinct += result[i].Raw != result[i - 1].Raw ? 1 : 0;
            }
        }

        Assert.InRange(distinct, result.Length / 2, result.Length);
    }

    [Fact]
    public void The_batch_API_equals_the_point_API_element_for_element_in_any_order()
    {
        var (x, y) = NoiseTestData.Grid2D(37); // not a multiple of any lane width
        var batch = new Fixed32[x.Length];
        Noise.Value(Key, x, y, batch);

        for (int i = batch.Length - 1; i >= 0; i--)
        {
            Assert.Equal(batch[i], Noise.Value(Key, x[i], y[i]));
        }
    }

    [Fact]
    public void All_kernel_paths_produce_identical_bits()
    {
        var (x, y) = NoiseTestData.Grid2D(45);
        var reference = new Fixed32[x.Length];
        var actual = new Fixed32[x.Length];
        Noise.Value(Key, x, y, reference, KernelPath.Scalar);

        foreach (var path in NoiseTestData.AllPaths)
        {
            Noise.Value(Key, x, y, actual, path);
            NoiseTestData.AssertSameRaw(reference, actual, $"Value2D/{path}");
        }
    }

    [Fact]
    public void Different_keys_give_different_fields()
    {
        var (x, y) = NoiseTestData.Grid2D(16);
        var a = new Fixed32[x.Length];
        var b = new Fixed32[x.Length];
        Noise.Value(Key, x, y, a);
        Noise.Value(NoiseKey.Create(2025, "value"), x, y, b);

        int same = 0;
        for (int i = 0; i < a.Length; i++)
        {
            same += a[i] == b[i] ? 1 : 0;
        }

        Assert.InRange(same, 0, a.Length / 50);
    }
}
