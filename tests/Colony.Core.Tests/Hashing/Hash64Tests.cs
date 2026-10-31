using System.Text;
using Colony.Hashing;

namespace Colony.Core.Tests.Hashing;

/// <summary>
/// Hash64 is the XXH64 algorithm (the published 64-bit xxHash). Published reference values pin the algorithm;
/// committed golden values for Framework-shaped inputs pin our implementation against future refactors.
/// </summary>
public sealed class Hash64Tests
{
    [Theory]
    [InlineData("", 0UL, 0xEF46DB3751D8E999UL)]
    [InlineData("abc", 0UL, 0x44BC2CF5AD770999UL)]
    [InlineData("Nobody inspects the spammish repetition", 0UL, 0xFBCEA83C8A378BF1UL)]
    public void Matches_published_XXH64_reference_values(string text, ulong seed, ulong expected)
    {
        Assert.Equal(expected, Hash64.Compute(Encoding.ASCII.GetBytes(text), seed));
    }

    [Theory]
    [InlineData(0, 0UL, 0xEF46DB3751D8E999UL)]
    [InlineData(1, 0UL, 0xA96C7F0CE858BBB7UL)]
    [InlineData(4, 0UL, 0xC60D15B1E3FF8F04UL)]
    [InlineData(8, 0UL, 0x3DA5C7AA269683E0UL)]
    [InlineData(31, 0UL, 0x4A74F3A1A39AD4A1UL)]
    [InlineData(32, 0UL, 0x8D57D6A4671CC43DUL)]
    [InlineData(33, 7UL, 0x338DACB2402DBBBFUL)]
    [InlineData(1000, 12345UL, 0xCBD42AE414E71A03UL)]
    public void Committed_golden_values_for_fixed_byte_sequences(int length, ulong seed, ulong expected)
    {
        Assert.Equal(expected, Hash64.Compute(Sequence(length), seed));
    }

    [Fact]
    public void Incremental_hashing_matches_one_shot_for_every_split_of_a_fixed_input()
    {
        var data = Sequence(300);
        ulong expected = Hash64.Compute(data, 99);
        for (int split = 0; split <= data.Length; split += 7)
        {
            var state = new Hash64State(99);
            state.Append(data.AsSpan(0, split));
            state.Append(data.AsSpan(split));
            Assert.Equal(expected, state.Finish());
            Assert.Equal((ulong)data.Length, state.Length);
        }

        // Byte at a time exercises the buffer across every stripe boundary.
        var byteWise = new Hash64State(99);
        foreach (var b in data)
        {
            byteWise.Append([b]);
        }

        Assert.Equal(expected, byteWise.Finish());
    }

    [Fact]
    public void Finish_does_not_consume_the_state()
    {
        var state = new Hash64State(0);
        state.Append(Sequence(40));
        ulong first = state.Finish();
        Assert.Equal(first, state.Finish());
        state.Append(Sequence(3));
        Assert.NotEqual(first, state.Finish());
    }

    [Fact]
    public void Typed_spans_and_values_hash_their_little_endian_bytes()
    {
        int[] ints = [1, 2, 3];
        byte[] bytes = [1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0];
        ulong expected = Hash64.Compute(bytes);
        Assert.True(expected == Hash64.Compute<int>(ints), "typed span");
        Assert.True(Hash64.Compute(bytes.AsSpan(0, 4)) == Hash64.Compute(1), "single value");
        // A plain struct (not a ValueTuple: the runtime may auto-lay-out generic structs).
        Assert.True(expected == Hash64.Compute(new Triple(1, 2, 3)), "struct value");

        var state = new Hash64State(0);
        state.Append<int>(ints);
        Assert.True(expected == state.Finish(), "incremental typed span");

        var stateByValue = new Hash64State(0);
        stateByValue.Append(1);
        stateByValue.Append(2);
        stateByValue.Append(3);
        Assert.True(expected == stateByValue.Finish(), "incremental values");
    }

    [Fact]
    public void Seed_changes_the_hash()
    {
        var data = Sequence(50);
        Assert.NotEqual(Hash64.Compute(data, 0), Hash64.Compute(data, 1));
    }

    [Fact]
    public void Combine_mixes_two_values_order_sensitively()
    {
        Assert.NotEqual(Hash64.Combine(1, 2), Hash64.Combine(2, 1));
        Assert.NotEqual(0UL, Hash64.Combine(0, 0));
        Assert.Equal(Hash64.Combine(5, 6), Hash64.Combine(5, 6));
        Assert.Equal(0xD3A4FB20201C198EUL, Hash64.Combine(0xDEADBEEF, 42));
    }

    [Fact]
    public void Hashing_does_not_allocate()
    {
        var data = Sequence(4096);
        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Run()
        {
            var state = new Hash64State(1);
            state.Append(data.AsSpan(0, 100));
            state.Append(data.AsSpan(100));
            state.Append(7);
            _ = state.Finish() ^ Hash64.Compute(data) ^ Hash64.Compute(ReadOnlySpan<int>.Empty);
        }
    }

    private readonly struct Triple
    {
        public Triple(int a, int b, int c)
        {
            A = a;
            B = b;
            C = c;
        }

        public int A { get; }

        public int B { get; }

        public int C { get; }
    }

    private static byte[] Sequence(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 31 + 7);
        }

        return data;
    }
}
