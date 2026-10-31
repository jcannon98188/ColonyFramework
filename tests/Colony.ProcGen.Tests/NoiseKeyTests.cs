using Colony.Hashing;

namespace Colony.ProcGen.Tests;

/// <summary>
/// Spec section 9.3: randomness is keyed by (seed, stage name hash, coordinate, use index). A NoiseKey is the
/// (seed, stage) part; every lattice hash and spot sample derives from it and a coordinate alone, so adding a
/// stage or a consumer never shifts another's numbers.
/// </summary>
public sealed class NoiseKeyTests
{
    [Fact]
    public void A_key_is_a_pure_function_of_seed_and_stage_name()
    {
        var a = NoiseKey.Create(42, "terrain.height");
        var b = NoiseKey.Create(42, "terrain.height");
        var otherStage = NoiseKey.Create(42, "terrain.moisture");
        var otherSeed = NoiseKey.Create(43, "terrain.height");

        Assert.Equal(a, b);
        Assert.NotEqual(a, otherStage);
        Assert.NotEqual(a, otherSeed);
        Assert.Equal(42UL, a.Seed);
        Assert.Equal(Hash64.Compute("terrain.height".AsSpan()), a.Stage);
    }

    [Fact]
    public void Lattice_hashes_depend_on_seed_stage_and_every_coordinate()
    {
        var key = NoiseKey.Create(7, "stage");
        var other = NoiseKey.Create(8, "stage");

        Assert.Equal(key.Hash(3, 4), key.Hash(3, 4));
        Assert.NotEqual(key.Hash(3, 4), key.Hash(4, 3));
        Assert.NotEqual(key.Hash(3, 4), key.Hash(3, 5));
        Assert.NotEqual(key.Hash(3, 4), other.Hash(3, 4));
        Assert.NotEqual(key.Hash(3, 4, 0), key.Hash(3, 4, 1));
        Assert.NotEqual(key.Hash(0, 0), key.Hash(0, 0, 0));
    }

    [Fact]
    public void Lattice_hashes_are_well_distributed_over_a_grid()
    {
        var key = NoiseKey.Create(1, "distribution");
        var buckets = new int[64];
        int ones = 0;
        for (int y = -32; y < 32; y++)
        {
            for (int x = -32; x < 32; x++)
            {
                uint h = key.Hash(x, y);
                buckets[h & 63]++;
                ones += System.Numerics.BitOperations.PopCount(h);
            }
        }

        // 4096 samples into 64 buckets: expect 64 each, allow +/- 50%.
        Assert.All(buckets, count => Assert.InRange(count, 32, 96));
        // Half of the 32 bits set on average, within 2%.
        Assert.InRange(ones, 4096 * 16 * 98 / 100, 4096 * 16 * 102 / 100);
    }

    [Fact]
    public void Use_index_derivation_yields_an_independent_key_with_the_same_seed()
    {
        var key = NoiseKey.Create(5, "fbm");
        var octave1 = key.Derive(1);
        var octave2 = key.Derive(2);

        Assert.Equal(key.Seed, octave1.Seed);
        Assert.NotEqual(key, octave1);
        Assert.NotEqual(octave1, octave2);
        Assert.Equal(octave1, key.Derive(1));
        Assert.NotEqual(key.Hash(0, 0), octave1.Hash(0, 0));
    }

    [Fact]
    public void A_region_source_is_the_counter_based_RNG_keyed_by_seed_stage_and_region_coordinate()
    {
        var key = NoiseKey.Create(9, "placement");
        var source = key.RegionSource(10, -3);

        Assert.Equal(9UL, source.Seed);
        Assert.Equal(source, key.RegionSource(10, -3));
        Assert.NotEqual(source, key.RegionSource(-3, 10));
        Assert.NotEqual(source.UInt32At(0), key.RegionSource(10, -2).UInt32At(0));
    }
}
