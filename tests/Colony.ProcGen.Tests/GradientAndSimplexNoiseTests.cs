using Colony.Numerics;

namespace Colony.ProcGen.Tests;

/// <summary>
/// Gradient (Perlin) and simplex noise in 2D and 3D, plus 3D value noise: range, lattice behaviour, smoothness,
/// point/batch agreement and bit-identity across kernel paths.
/// </summary>
public sealed class GradientAndSimplexNoiseTests
{
    private static readonly NoiseKey Key = NoiseKey.Create(77, "gradient");

    public delegate Fixed32 Point2(NoiseKey key, Fixed32 x, Fixed32 y);

    public delegate Fixed32 Point3(NoiseKey key, Fixed32 x, Fixed32 y, Fixed32 z);

    internal delegate void Batch2(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result, KernelPath path);

    internal delegate void Batch3(NoiseKey key, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result, KernelPath path);

    private static readonly (string Name, Point2 Point, Batch2 Batch)[] Functions2D =
    [
        ("Gradient2D", Noise.Gradient, Noise.Gradient),
        ("Simplex2D", Noise.Simplex, Noise.Simplex),
    ];

    private static readonly (string Name, Point3 Point, Batch3 Batch)[] Functions3D =
    [
        ("Value3D", Noise.Value, Noise.Value),
        ("Gradient3D", Noise.Gradient, Noise.Gradient),
        ("Simplex3D", Noise.Simplex, Noise.Simplex),
    ];

    [Fact]
    public void Gradient_noise_is_zero_at_every_lattice_point()
    {
        for (int y = -4; y <= 4; y++)
        {
            for (int x = -4; x <= 4; x++)
            {
                Assert.Equal(Fixed32.Zero, Noise.Gradient(Key, Fixed32.FromInt32(x), Fixed32.FromInt32(y)));
                Assert.Equal(Fixed32.Zero, Noise.Gradient(Key, Fixed32.FromInt32(x), Fixed32.FromInt32(y), Fixed32.FromInt32(1 - x)));
            }
        }
    }

    [Fact]
    public void Simplex_noise_is_zero_at_the_origin_and_nonzero_nearby()
    {
        Assert.Equal(Fixed32.Zero, Noise.Simplex(Key, Fixed32.Zero, Fixed32.Zero));
        Assert.Equal(Fixed32.Zero, Noise.Simplex(Key, Fixed32.Zero, Fixed32.Zero, Fixed32.Zero));
        Assert.NotEqual(Fixed32.Zero, Noise.Simplex(Key, Fixed32.Half, Fixed32.FromDouble(0.3)));
        Assert.NotEqual(Fixed32.Zero, Noise.Simplex(Key, Fixed32.Half, Fixed32.FromDouble(0.3), Fixed32.FromDouble(0.2)));
    }

    [Fact]
    public void Two_dimensional_fields_stay_in_range_use_the_range_and_are_smooth()
    {
        var (x, y) = NoiseTestData.Grid2D(64, frequencyShift: 5);
        var result = new Fixed32[x.Length];
        foreach (var (name, _, batch) in Functions2D)
        {
            batch(Key, x, y, result, KernelPath.Scalar);
            AssertFieldQuality(result, 64, name);
        }
    }

    [Fact]
    public void Three_dimensional_fields_stay_in_range_use_the_range_and_are_smooth()
    {
        var (x, y, z) = NoiseTestData.Grid3D(24, frequencyShift: 4);
        var result = new Fixed32[x.Length];
        foreach (var (name, _, batch) in Functions3D)
        {
            batch(Key, x, y, z, result, KernelPath.Scalar);
            AssertFieldQuality(result, 24, name);
        }
    }

    [Fact]
    public void Batch_equals_point_API_and_all_paths_are_bit_identical_in_2D()
    {
        var (x, y) = NoiseTestData.Grid2D(45);
        var reference = new Fixed32[x.Length];
        var actual = new Fixed32[x.Length];
        foreach (var (name, point, batch) in Functions2D)
        {
            batch(Key, x, y, reference, KernelPath.Scalar);
            for (int i = reference.Length - 1; i >= 0; i -= 7)
            {
                Assert.Equal(reference[i], point(Key, x[i], y[i]));
            }

            foreach (var path in NoiseTestData.AllPaths)
            {
                batch(Key, x, y, actual, path);
                NoiseTestData.AssertSameRaw(reference, actual, $"{name}/{path}");
            }
        }
    }

    [Fact]
    public void Batch_equals_point_API_and_all_paths_are_bit_identical_in_3D()
    {
        var (x, y, z) = NoiseTestData.Grid3D(13);
        var reference = new Fixed32[x.Length];
        var actual = new Fixed32[x.Length];
        foreach (var (name, point, batch) in Functions3D)
        {
            batch(Key, x, y, z, reference, KernelPath.Scalar);
            for (int i = reference.Length - 1; i >= 0; i -= 7)
            {
                Assert.Equal(reference[i], point(Key, x[i], y[i], z[i]));
            }

            foreach (var path in NoiseTestData.AllPaths)
            {
                batch(Key, x, y, z, actual, path);
                NoiseTestData.AssertSameRaw(reference, actual, $"{name}/{path}");
            }
        }
    }

    private static void AssertFieldQuality(Fixed32[] result, int side, string name)
    {
        int min = int.MaxValue;
        int max = int.MinValue;
        int maxStep = 0;
        for (int i = 0; i < result.Length; i++)
        {
            Assert.InRange(result[i].Raw, -65536, 65536);
            min = System.Math.Min(min, result[i].Raw);
            max = System.Math.Max(max, result[i].Raw);
            if (i % side != 0)
            {
                maxStep = System.Math.Max(maxStep, System.Math.Abs(result[i].Raw - result[i - 1].Raw));
            }
        }

        // Normalised noise should reach at least +/- 0.5 over a few hundred cells.
        Assert.True(min < -32768, $"{name}: minimum {min} never reaches -0.5");
        Assert.True(max > 32768, $"{name}: maximum {max} never reaches 0.5");
        // Adjacent samples at most 1/16 lattice unit apart: slope bounded, so steps are well under 0.35.
        Assert.True(maxStep < 23000, $"{name}: step {maxStep} between neighbours is not smooth");
    }
}
