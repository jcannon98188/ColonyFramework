using Colony.Numerics;

namespace Colony.ProcGen.Tests;

/// <summary>
/// Region-keyed spot sampling (spec section 9.3 "region-keyed placement for large features"): the spots of a
/// region are drawn from the counter-based RNG stream of (seed, stage, region coordinate), use index by use index.
/// </summary>
public sealed class SpotSamplingTests
{
    private static readonly NoiseKey Key = NoiseKey.Create(11, "placement.ruins");
    private static readonly Fixed32 RegionSize = Fixed32.FromInt32(64);

    [Fact]
    public void Spots_lie_inside_their_region_and_the_count_respects_the_bounds()
    {
        var spots = new Spot[8];
        for (int ry = -2; ry <= 2; ry++)
        {
            for (int rx = -2; rx <= 2; rx++)
            {
                int count = SpotSampler.Sample(Key, rx, ry, RegionSize, 2, 8, spots);
                Assert.InRange(count, 2, 8);
                for (int i = 0; i < count; i++)
                {
                    Assert.InRange(spots[i].Position.X.ToDouble(), rx * 64.0, (rx * 64.0) + 64.0);
                    Assert.InRange(spots[i].Position.Y.ToDouble(), ry * 64.0, (ry * 64.0) + 64.0);
                    Assert.True(spots[i].Position.X.ToDouble() < (rx * 64.0) + 64.0);
                }
            }
        }
    }

    [Fact]
    public void Each_spot_is_a_pure_function_of_key_region_and_index()
    {
        var source = Key.RegionSource(3, -5);
        var spots = new Spot[6];
        int count = SpotSampler.Sample(Key, 3, -5, RegionSize, 1, 6, spots);

        Assert.Equal(source.Int32At(0, 1, 7), count);
        for (int i = 0; i < count; i++)
        {
            // Counter layout: 0 = count, then (x, y, hash) per spot; the hash is the spot's own randomness.
            ulong counter = 1 + (3UL * (ulong)i);
            Assert.Equal(source.UInt32At(counter + 2), spots[i].Hash);
            var expectedX = Fixed32.FromInt32(3 * 64) + (RegionSize * Fixed32.FromRaw((int)(source.UInt32At(counter) >> 16)));
            Assert.Equal(expectedX, spots[i].Position.X);
            Assert.Equal(spots[i], SpotSampler.SpotAt(Key, 3, -5, RegionSize, i));
        }
    }

    [Fact]
    public void A_capped_output_span_truncates_without_changing_the_first_spots()
    {
        var all = new Spot[16];
        var few = new Spot[2];
        int countAll = SpotSampler.Sample(Key, 0, 0, RegionSize, 5, 16, all);
        int countFew = SpotSampler.Sample(Key, 0, 0, RegionSize, 5, 16, few);

        Assert.InRange(countAll, 5, 16);
        Assert.Equal(2, countFew);
        Assert.Equal(all[0], few[0]);
        Assert.Equal(all[1], few[1]);
    }

    [Fact]
    public void Neighbouring_regions_and_other_stages_draw_different_spots()
    {
        var a = new Spot[4];
        var b = new Spot[4];
        SpotSampler.Sample(Key, 0, 0, RegionSize, 4, 4, a);
        SpotSampler.Sample(Key, 1, 0, RegionSize, 4, 4, b);
        Assert.NotEqual(a[0].Hash, b[0].Hash);

        SpotSampler.Sample(NoiseKey.Create(11, "placement.lakes"), 0, 0, RegionSize, 4, 4, b);
        Assert.NotEqual(a[0].Hash, b[0].Hash);
    }
}
