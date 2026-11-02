using Colony.Numerics;

namespace Colony.Core.Tests.Numerics;

/// <summary>
/// Counter-based RNG (Philox4x32-10) addressed by (World seed, stream key, counter). Known-answer vectors come
/// from the Random123 reference distribution; everything else is reproducibility and independence evidence.
/// </summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class RandomSourceTests
{
    [Fact]
    public void Philox4x32_10_matches_the_Random123_known_answer_vectors()
    {
        // counter = 0, key = 0
        var zero = Philox.Generate(0, 0, 0);
        Assert.Equal((0x6627e8d5u, 0xe169c58du, 0xbc57ac4cu, 0x9b00dbd8u), (zero[0], zero[1], zero[2], zero[3]));

        // counter words all 0xffffffff, key words all 0xffffffff
        var ones = Philox.Generate(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
        Assert.Equal((0x408f276du, 0x41c83b0eu, 0xa20bc7c6u, 0x6d5451fdu), (ones[0], ones[1], ones[2], ones[3]));

        // counter = {243f6a88, 85a308d3, 13198a2e, 03707344}, key = {a4093822, 299f31d0} (digits of pi)
        var pi = Philox.Generate(seed: 0x299f31d0a4093822UL, streamKey: 0x0370734413198a2eUL, counter: 0x85a308d3243f6a88UL);
        Assert.Equal((0xd16cfe09u, 0x94fdccebu, 0x5001e420u, 0x24126ea1u), (pi[0], pi[1], pi[2], pi[3]));
    }

    [Fact]
    public void Values_are_addressed_by_seed_key_and_counter_and_are_reproducible()
    {
        var stream = new RandomSource(seed: 42, key: 7);
        var again = new RandomSource(seed: 42, key: 7);
        for (ulong i = 0; i < 100; i++)
        {
            Assert.Equal(stream.UInt32At(i), again.UInt32At(i));
            Assert.Equal(stream.UInt64At(i), again.UInt64At(i));
            Assert.Equal(stream.SingleAt(i), again.SingleAt(i));
            Assert.Equal(stream.DoubleAt(i), again.DoubleAt(i));
        }

        // Committed golden values pin the word layout (seed -> key words, stream key -> upper counter words).
        Assert.Equal(0x6627e8d5u, new RandomSource(0, 0).UInt32At(0));
        Assert.Equal(0xe169c58du, new RandomSource(0, 0).UInt32At(1));
        Assert.Equal(0x9b00dbd8u, new RandomSource(0, 0).UInt32At(3));
        Assert.Equal(0xe169c58d6627e8d5UL, new RandomSource(0, 0).UInt64At(0));
        Assert.Equal(0xe169c58d6627e8d5UL, new RandomSource(0, 0).UInt64At(1)); // odd counters share the aligned pair below
        Assert.Equal(0x9b00dbd8bc57ac4cUL, new RandomSource(0, 0).UInt64At(2));
        Assert.NotEqual(new RandomSource(0, 0).UInt32At(3), new RandomSource(0, 0).UInt32At(4));
    }

    [Fact]
    public void Streams_with_different_keys_or_seeds_are_independent()
    {
        var a = new RandomSource(seed: 1, key: 0);
        var b = new RandomSource(seed: 1, key: 1);
        var c = new RandomSource(seed: 2, key: 0);

        int equalAb = 0;
        int equalAc = 0;
        var seenA = new HashSet<uint>();
        for (ulong i = 0; i < 20_000; i++)
        {
            uint va = a.UInt32At(i);
            equalAb += va == b.UInt32At(i) ? 1 : 0;
            equalAc += va == c.UInt32At(i) ? 1 : 0;
            seenA.Add(va);
        }

        // Chance of any 32-bit collision at the same index over 20k draws is ~5e-6.
        Assert.Equal(0, equalAb);
        Assert.Equal(0, equalAc);
        Assert.Equal(20_000, seenA.Count);
    }

    [Fact]
    public void Adding_a_consumer_never_shifts_another_consumers_values()
    {
        var world = new RandomSource(seed: 123, key: 0);
        var weather = world.Derive("weather");
        var combat = world.Derive("combat");
        var weatherAlone = new RandomSource(seed: 123, key: 0).Derive("weather");

        for (ulong i = 0; i < 1000; i++)
        {
            Assert.Equal(weather.UInt32At(i), weatherAlone.UInt32At(i)); // combat's existence is irrelevant
            Assert.NotEqual(weather.UInt32At(i), combat.UInt32At(i));
        }

        Assert.Equal(weather, world.Derive("weather"));
        Assert.NotEqual(weather, world.Derive("Weather"));
        Assert.Equal(world.Derive(5).Derive(6), world.Derive(5).Derive(6));
        Assert.NotEqual(world.Derive(5).Derive(6), world.Derive(6).Derive(5));
        Assert.NotEqual(world.Derive(5), world.Derive("5"));
        Assert.Equal(123UL, weather.Seed);
    }

    [Fact]
    public void Results_are_identical_regardless_of_thread_count()
    {
        var stream = new RandomSource(seed: 99, key: 3);
        const int count = 100_000;
        var sequential = new uint[count];
        for (int i = 0; i < count; i++)
        {
            sequential[i] = stream.UInt32At((ulong)i);
        }

        foreach (int threads in new[] { 1, 2, 7, 16 })
        {
            var parallel = new uint[count];
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i => parallel[i] = stream.UInt32At((ulong)i));
            Assert.Equal(sequential, parallel);
        }
    }

    [Fact]
    public void Derived_floating_point_values_are_in_the_unit_interval()
    {
        var stream = new RandomSource(seed: 5, key: 5);
        for (ulong i = 0; i < 10_000; i++)
        {
            Assert.InRange(stream.SingleAt(i), 0f, 0.99999994f);
            Assert.InRange(stream.DoubleAt(i), 0d, 0.99999999999999989);
            int ranged = stream.Int32At(i, -3, 4);
            Assert.InRange(ranged, -3, 3);
        }

        // 24 random bits exactly, so 1.0 is never produced and 0 is possible: pin the mapping.
        Assert.Equal(0x6627e8u / 16777216f, new RandomSource(0, 0).SingleAt(0));
        Assert.Equal(5, new RandomSource(0, 0).Int32At(0, 5, 6));
        Assert.Equal(5, new RandomSource(0, 0).Int32At(0, 5, 5)); // empty range yields the minimum
        Assert.True(new RandomSource(0, 0).BoolAt(0)); // 0x6627e8d5 is odd
    }

    [Fact]
    public void Int32At_covers_the_whole_range_roughly_uniformly()
    {
        var stream = new RandomSource(seed: 77, key: 1);
        var counts = new int[10];
        for (ulong i = 0; i < 100_000; i++)
        {
            counts[stream.Int32At(i, 0, 10)]++;
        }

        Assert.All(counts, c => Assert.InRange(c, 9_000, 11_000));
    }

    [Fact]
    public void RandomSequence_walks_the_stream_and_is_restartable_from_its_counter()
    {
        var stream = new RandomSource(seed: 11, key: 2);
        var sequence = new RandomSequence(stream);
        Assert.Equal(stream.UInt32At(0), sequence.NextUInt32());
        Assert.Equal(stream.UInt32At(1), sequence.NextUInt32());
        Assert.Equal(stream.UInt64At(2), sequence.NextUInt64()); // 64-bit draws take the aligned pair (2,3)
        Assert.Equal(4UL, sequence.Counter);
        Assert.Equal(stream.SingleAt(4), sequence.NextSingle());
        Assert.Equal(stream.DoubleAt(6), sequence.NextDouble()); // counter 5 rounds up to the next aligned pair (6,7)
        Assert.Equal(8UL, sequence.Counter);
        Assert.Equal(stream.Int32At(8, 10, 20), sequence.NextInt32(10, 20));
        Assert.Equal(stream.BoolAt(9), sequence.NextBool());
        sequence.Skip(10);
        Assert.Equal(20UL, sequence.Counter);

        var resumed = new RandomSequence(stream, sequence.Counter);
        Assert.Equal(sequence.NextUInt32(), resumed.NextUInt32());
        Assert.Equal(sequence, resumed);
    }

    [Fact]
    public void Random_types_are_unmanaged_and_do_not_allocate()
    {
        Assert.Equal(16, System.Runtime.CompilerServices.Unsafe.SizeOf<RandomSource>());
        Assert.Equal(24, System.Runtime.CompilerServices.Unsafe.SizeOf<RandomSequence>());

        var stream = new RandomSource(1, 2);
        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Run()
        {
            var sequence = new RandomSequence(stream.Derive("x").Derive(9));
            ulong acc = 0;
            for (int i = 0; i < 1000; i++)
            {
                acc += sequence.NextUInt32() + (ulong)sequence.NextInt32(0, 100);
            }

            _ = acc + stream.UInt64At(5);
        }
    }
}
