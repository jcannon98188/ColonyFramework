using Colony.Numerics;

namespace Colony.ProcGen.Tests;

/// <summary>Shared sample grids and helpers for the noise tests.</summary>
internal static class NoiseTestData
{
    internal static readonly KernelPath[] AllPaths = [KernelPath.Scalar, KernelPath.Vector128, KernelPath.Vector256];

    /// <summary>A grid of <paramref name="side"/>² sample positions over [-side/8, side/8) in lattice units, SoA.</summary>
    internal static (Fixed32[] X, Fixed32[] Y) Grid2D(int side, int frequencyShift = 3)
    {
        var x = new Fixed32[side * side];
        var y = new Fixed32[side * side];
        int half = side / 2;
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                // Step 1/8 lattice unit plus an irregular sub-step so fractions are not all multiples of 1/8.
                x[(j * side) + i] = Fixed32.FromRaw(((i - half) << (16 - frequencyShift)) + ((i * 37) & 1023));
                y[(j * side) + i] = Fixed32.FromRaw(((j - half) << (16 - frequencyShift)) + ((j * 53) & 1023));
            }
        }

        return (x, y);
    }

    internal static (Fixed32[] X, Fixed32[] Y, Fixed32[] Z) Grid3D(int side, int frequencyShift = 3)
    {
        var x = new Fixed32[side * side * side];
        var y = new Fixed32[side * side * side];
        var z = new Fixed32[side * side * side];
        int half = side / 2;
        for (int k = 0; k < side; k++)
        {
            for (int j = 0; j < side; j++)
            {
                for (int i = 0; i < side; i++)
                {
                    int n = (((k * side) + j) * side) + i;
                    x[n] = Fixed32.FromRaw(((i - half) << (16 - frequencyShift)) + ((i * 37) & 1023));
                    y[n] = Fixed32.FromRaw(((j - half) << (16 - frequencyShift)) + ((j * 53) & 1023));
                    z[n] = Fixed32.FromRaw(((k - half) << (16 - frequencyShift)) + ((k * 71) & 1023));
                }
            }
        }

        return (x, y, z);
    }

    internal static void AssertSameRaw(ReadOnlySpan<Fixed32> expected, ReadOnlySpan<Fixed32> actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].Raw == actual[i].Raw, $"{what}[{i}]: expected raw {expected[i].Raw} got {actual[i].Raw}");
        }
    }
}
