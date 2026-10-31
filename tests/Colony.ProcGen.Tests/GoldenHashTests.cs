using System.Globalization;
using System.Text;
using Colony.Hashing;
using Colony.Numerics;

namespace Colony.ProcGen.Tests;

/// <summary>
/// AC #1: committed golden hashes per function and seed. Each entry is the XXH64 of the raw Q16 output over a fixed
/// sample grid. A change here is a change to generated worlds (spec section 9.3: the manifest records a generation
/// version); update the literals only together with that version.
/// </summary>
public sealed class GoldenHashTests
{
    private static readonly ulong[] Seeds = [1UL, 0xC0FFEE_BADF00DUL];

    private static readonly FbmSettings Fbm = new() { Octaves = 4, Basis = NoiseBasis.Simplex };

    /// <summary>(function, seed) to expected hash. Produced by <see cref="Report"/>.</summary>
    private static readonly Dictionary<string, ulong> Expected = new(StringComparer.Ordinal)
    {
        // GOLDEN-TABLE-START
        // GOLDEN-TABLE-END
    };

    public static IEnumerable<TheoryDataRow<string, ulong>> Cases()
    {
        foreach (var seed in Seeds)
        {
            foreach (var name in FunctionNames)
            {
                yield return new TheoryDataRow<string, ulong>(name, seed);
            }
        }
    }

    private static readonly string[] FunctionNames =
    [
        "Value2D", "Value3D", "Gradient2D", "Gradient3D", "Simplex2D", "Simplex3D",
        "CellularF1_2D", "CellularF2_2D", "CellularCell_2D", "CellularF1_3D", "CellularF2_3D", "CellularCell_3D",
        "Fbm2D", "Fbm3D", "WarpX2D", "WarpY2D", "WarpX3D", "WarpY3D", "WarpZ3D", "Spots",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Output_matches_the_committed_golden_hash(string function, ulong seed)
    {
        ulong actual = Compute(function, seed);
        Assert.True(Expected.TryGetValue(Id(function, seed), out ulong expected), $"No golden hash for {Id(function, seed)}; actual 0x{actual:X16}. Run Report to regenerate the table.");
        Assert.True(expected == actual, $"{Id(function, seed)}: expected 0x{expected:X16} got 0x{actual:X16}");
    }

    [Fact]
    public void Report()
    {
        // Prints the full table so the literals above can be regenerated after an intentional change.
        var text = new StringBuilder();
        foreach (var seed in Seeds)
        {
            foreach (var name in FunctionNames)
            {
                text.Append(CultureInfo.InvariantCulture, $"        [\"{Id(name, seed)}\"] = 0x{Compute(name, seed):X16}UL,\n");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(text.ToString());
    }

    private static string Id(string function, ulong seed) => string.Create(CultureInfo.InvariantCulture, $"{function}@{seed:X}");

    private static ulong Compute(string function, ulong seed)
    {
        var key = NoiseKey.Create(seed, "golden");
        var (x, y) = NoiseTestData.Grid2D(64);
        var (x3, y3, z3) = NoiseTestData.Grid3D(16);
        var out2 = new Fixed32[x.Length];
        var out3 = new Fixed32[x3.Length];
        var f2 = new Fixed32[x.Length];
        var cells = new uint[x.Length];
        var f23 = new Fixed32[x3.Length];
        var cells3 = new uint[x3.Length];

        switch (function)
        {
            case "Value2D":
                Noise.Value(key, x, y, out2);
                return Hash64.Compute<Fixed32>(out2);
            case "Value3D":
                Noise.Value(key, x3, y3, z3, out3);
                return Hash64.Compute<Fixed32>(out3);
            case "Gradient2D":
                Noise.Gradient(key, x, y, out2);
                return Hash64.Compute<Fixed32>(out2);
            case "Gradient3D":
                Noise.Gradient(key, x3, y3, z3, out3);
                return Hash64.Compute<Fixed32>(out3);
            case "Simplex2D":
                Noise.Simplex(key, x, y, out2);
                return Hash64.Compute<Fixed32>(out2);
            case "Simplex3D":
                Noise.Simplex(key, x3, y3, z3, out3);
                return Hash64.Compute<Fixed32>(out3);
            case "CellularF1_2D":
                Noise.Cellular(key, x, y, out2, f2, cells);
                return Hash64.Compute<Fixed32>(out2);
            case "CellularF2_2D":
                Noise.Cellular(key, x, y, out2, f2, cells);
                return Hash64.Compute<Fixed32>(f2);
            case "CellularCell_2D":
                Noise.Cellular(key, x, y, out2, f2, cells);
                return Hash64.Compute<uint>(cells);
            case "CellularF1_3D":
                Noise.Cellular(key, x3, y3, z3, out3, f23, cells3);
                return Hash64.Compute<Fixed32>(out3);
            case "CellularF2_3D":
                Noise.Cellular(key, x3, y3, z3, out3, f23, cells3);
                return Hash64.Compute<Fixed32>(f23);
            case "CellularCell_3D":
                Noise.Cellular(key, x3, y3, z3, out3, f23, cells3);
                return Hash64.Compute<uint>(cells3);
            case "Fbm2D":
                Noise.Fbm(key, Fbm, x, y, out2);
                return Hash64.Compute<Fixed32>(out2);
            case "Fbm3D":
                Noise.Fbm(key, Fbm, x3, y3, z3, out3);
                return Hash64.Compute<Fixed32>(out3);
            case "WarpX2D":
            case "WarpY2D":
                Noise.Warp(key, Fixed32.One, x, y);
                return Hash64.Compute<Fixed32>(function == "WarpX2D" ? x : y);
            case "WarpX3D":
            case "WarpY3D":
            case "WarpZ3D":
                Noise.Warp(key, Fixed32.One, x3, y3, z3);
                return Hash64.Compute<Fixed32>(function == "WarpX3D" ? x3 : function == "WarpY3D" ? y3 : z3);
            case "Spots":
                var spots = new Spot[8];
                var state = new Hash64State();
                for (int ry = -2; ry < 2; ry++)
                {
                    for (int rx = -2; rx < 2; rx++)
                    {
                        int count = SpotSampler.Sample(key, rx, ry, Fixed32.FromInt32(32), 1, 8, spots);
                        state.Append(count);
                        for (int i = 0; i < count; i++)
                        {
                            state.Append(spots[i].Position.X.Raw);
                            state.Append(spots[i].Position.Y.Raw);
                            state.Append(spots[i].Hash);
                        }
                    }
                }

                return state.Finish();
            default:
                throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown function");
        }
    }
}
