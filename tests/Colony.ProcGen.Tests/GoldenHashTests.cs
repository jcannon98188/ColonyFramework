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
        ["Value2D@1"] = 0x1C7221B572E8BC9AUL,
        ["Value3D@1"] = 0xDB7165C5F49F878EUL,
        ["Gradient2D@1"] = 0x9F3F02B6E5B02BD3UL,
        ["Gradient3D@1"] = 0x3015D22A41D63477UL,
        ["Simplex2D@1"] = 0xA437DAFDFB1D2E83UL,
        ["Simplex3D@1"] = 0xE2AE15DE7753947DUL,
        ["CellularF1_2D@1"] = 0xBDB1BE74FD635E6FUL,
        ["CellularF2_2D@1"] = 0x948A02B8D9D42846UL,
        ["CellularCell_2D@1"] = 0x27E86E10621AFF8FUL,
        ["CellularF1_3D@1"] = 0x3757FE556DC0C358UL,
        ["CellularF2_3D@1"] = 0x152AFD8B4ADA797EUL,
        ["CellularCell_3D@1"] = 0x766AEA3E92421D56UL,
        ["Fbm2D@1"] = 0x08D0F182E4B3AA1CUL,
        ["Fbm3D@1"] = 0xCACC672A0E0EC7C7UL,
        ["WarpX2D@1"] = 0x68AA3F8A40F118E0UL,
        ["WarpY2D@1"] = 0x9B0CBC474BE876FDUL,
        ["WarpX3D@1"] = 0x78FEA225E460717EUL,
        ["WarpY3D@1"] = 0x69730911E3FA4605UL,
        ["WarpZ3D@1"] = 0x8E66D7E87704DF92UL,
        ["Spots@1"] = 0x9449C30DE979D547UL,
        ["Value2D@C0FFEEBADF00D"] = 0xB5D4B5EC749771F9UL,
        ["Value3D@C0FFEEBADF00D"] = 0xDF0FC9C15C0140C0UL,
        ["Gradient2D@C0FFEEBADF00D"] = 0x0D4BC0DE5F6028ADUL,
        ["Gradient3D@C0FFEEBADF00D"] = 0x0ED5FE21BD16E5F9UL,
        ["Simplex2D@C0FFEEBADF00D"] = 0x7F251A1B50195F81UL,
        ["Simplex3D@C0FFEEBADF00D"] = 0x257E644F3A8F90E2UL,
        ["CellularF1_2D@C0FFEEBADF00D"] = 0x4CB4CD2636EB6239UL,
        ["CellularF2_2D@C0FFEEBADF00D"] = 0xC72061FB325CD6DEUL,
        ["CellularCell_2D@C0FFEEBADF00D"] = 0xC96E0C493528F627UL,
        ["CellularF1_3D@C0FFEEBADF00D"] = 0x0FAF4A1152E0693DUL,
        ["CellularF2_3D@C0FFEEBADF00D"] = 0x75BF91EFA38E436FUL,
        ["CellularCell_3D@C0FFEEBADF00D"] = 0x6E4F033230A1F924UL,
        ["Fbm2D@C0FFEEBADF00D"] = 0xE6BB3DDDD90CC031UL,
        ["Fbm3D@C0FFEEBADF00D"] = 0xBBD63AE5E3E03FE7UL,
        ["WarpX2D@C0FFEEBADF00D"] = 0x817004C4448A1803UL,
        ["WarpY2D@C0FFEEBADF00D"] = 0xBB393716BBDC9D53UL,
        ["WarpX3D@C0FFEEBADF00D"] = 0x29333D623F3A4080UL,
        ["WarpY3D@C0FFEEBADF00D"] = 0x0FB1876ED4E71C75UL,
        ["WarpZ3D@C0FFEEBADF00D"] = 0xE751519780BA5754UL,
        ["Spots@C0FFEEBADF00D"] = 0xE7CEFCA504405DA8UL,
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
                var state = new Hash64State(0);
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
