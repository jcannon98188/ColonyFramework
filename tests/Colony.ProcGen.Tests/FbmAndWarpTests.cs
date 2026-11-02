using Colony.Numerics;

namespace Colony.ProcGen.Tests;

/// <summary>Fractal Brownian motion over any basis, and domain warping of coordinates.</summary>
public sealed class FbmAndWarpTests
{
    private static readonly NoiseKey Key = NoiseKey.Create(500, "fbm");

    [Fact]
    public void One_octave_fBm_is_the_basis_evaluated_with_the_first_derived_key()
    {
        var settings = new FbmSettings { Octaves = 1, Basis = NoiseBasis.Gradient };
        var x = Fixed32.FromDouble(1.37);
        var y = Fixed32.FromDouble(-2.21);
        var z = Fixed32.FromDouble(0.6);

        Assert.Equal(Noise.Gradient(Key.Derive(0), x, y), Noise.Fbm(Key, settings, x, y));
        Assert.Equal(Noise.Gradient(Key.Derive(0), x, y, z), Noise.Fbm(Key, settings, x, y, z));
    }

    [Fact]
    public void Octaves_use_independent_keys_so_adding_one_never_changes_the_others()
    {
        var two = new FbmSettings { Octaves = 2, Basis = NoiseBasis.Simplex };
        var x = Fixed32.FromDouble(0.37);
        var y = Fixed32.FromDouble(0.21);

        // Hand-computed two-octave sum with default lacunarity 2 and gain 0.5, normalised by 1 + 0.5.
        var o0 = Noise.Simplex(Key.Derive(0), x, y);
        var o1 = Noise.Simplex(Key.Derive(1), x + x, y + y);
        double expected = (o0.ToDouble() + (0.5 * o1.ToDouble())) / 1.5;

        Assert.InRange(Noise.Fbm(Key, two, x, y).ToDouble(), expected - 0.0005, expected + 0.0005);
    }

    [Theory]
    [InlineData(NoiseBasis.Value)]
    [InlineData(NoiseBasis.Gradient)]
    [InlineData(NoiseBasis.Simplex)]
    public void fBm_stays_in_range_and_is_bit_identical_across_paths_for_every_basis(NoiseBasis basis)
    {
        var settings = new FbmSettings { Octaves = 5, Basis = basis, Lacunarity = Fixed32.FromDouble(2.1), Gain = Fixed32.FromDouble(0.45) };
        var (x, y) = NoiseTestData.Grid2D(45);
        var reference = new Fixed32[x.Length];
        var actual = new Fixed32[x.Length];
        Noise.Fbm(Key, settings, x, y, reference, KernelPath.Scalar);
        for (int i = 0; i < reference.Length; i += 11)
        {
            Assert.InRange(reference[i].Raw, -65536, 65536);
            Assert.Equal(reference[i], Noise.Fbm(Key, settings, x[i], y[i]));
        }

        foreach (var path in NoiseTestData.AllPaths)
        {
            Noise.Fbm(Key, settings, x, y, actual, path);
            NoiseTestData.AssertSameRaw(reference, actual, $"Fbm2D/{basis}/{path}");
        }

        var (x3, y3, z3) = NoiseTestData.Grid3D(11);
        var reference3 = new Fixed32[x3.Length];
        var actual3 = new Fixed32[x3.Length];
        Noise.Fbm(Key, settings, x3, y3, z3, reference3, KernelPath.Scalar);
        Assert.Equal(reference3[17], Noise.Fbm(Key, settings, x3[17], y3[17], z3[17]));
        foreach (var path in NoiseTestData.AllPaths)
        {
            Noise.Fbm(Key, settings, x3, y3, z3, actual3, path);
            NoiseTestData.AssertSameRaw(reference3, actual3, $"Fbm3D/{basis}/{path}");
        }
    }

    [Fact]
    public void Domain_warp_offsets_each_axis_by_strength_times_gradient_noise_of_its_own_key()
    {
        var x = Fixed32.FromDouble(3.3);
        var y = Fixed32.FromDouble(1.1);
        var strength = Fixed32.FromDouble(0.75);
        var (wx, wy) = Noise.Warp(Key, strength, x, y);

        var expectedX = x + (strength * Noise.Gradient(Key.Derive(0), x, y));
        var expectedY = y + (strength * Noise.Gradient(Key.Derive(1), x, y));
        Assert.InRange(wx.Raw, expectedX.Raw - 2, expectedX.Raw + 2);
        Assert.InRange(wy.Raw, expectedY.Raw - 2, expectedY.Raw + 2);
        Assert.NotEqual(x, wx);
    }

    [Fact]
    public void Domain_warp_batches_in_place_and_are_bit_identical_across_paths_in_2D_and_3D()
    {
        var strength = Fixed32.FromDouble(1.5);
        var (x, y) = NoiseTestData.Grid2D(45);
        var rx = (Fixed32[])x.Clone();
        var ry = (Fixed32[])y.Clone();
        Noise.Warp(Key, strength, rx, ry, KernelPath.Scalar);
        var (px, py) = Noise.Warp(Key, strength, x[100], y[100]);
        Assert.Equal(px, rx[100]);
        Assert.Equal(py, ry[100]);

        foreach (var path in NoiseTestData.AllPaths)
        {
            var ax = (Fixed32[])x.Clone();
            var ay = (Fixed32[])y.Clone();
            Noise.Warp(Key, strength, ax, ay, path);
            NoiseTestData.AssertSameRaw(rx, ax, $"WarpX/{path}");
            NoiseTestData.AssertSameRaw(ry, ay, $"WarpY/{path}");
        }

        var (x3, y3, z3) = NoiseTestData.Grid3D(11);
        var rx3 = (Fixed32[])x3.Clone();
        var ry3 = (Fixed32[])y3.Clone();
        var rz3 = (Fixed32[])z3.Clone();
        Noise.Warp(Key, strength, rx3, ry3, rz3, KernelPath.Scalar);
        var (qx, qy, qz) = Noise.Warp(Key, strength, x3[100], y3[100], z3[100]);
        Assert.Equal((qx, qy, qz), (rx3[100], ry3[100], rz3[100]));
        foreach (var path in NoiseTestData.AllPaths)
        {
            var ax = (Fixed32[])x3.Clone();
            var ay = (Fixed32[])y3.Clone();
            var az = (Fixed32[])z3.Clone();
            Noise.Warp(Key, strength, ax, ay, az, path);
            NoiseTestData.AssertSameRaw(rx3, ax, $"WarpX3/{path}");
            NoiseTestData.AssertSameRaw(ry3, ay, $"WarpY3/{path}");
            NoiseTestData.AssertSameRaw(rz3, az, $"WarpZ3/{path}");
        }
    }
}
