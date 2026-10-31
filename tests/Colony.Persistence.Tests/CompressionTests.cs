using System.Text;
using Colony.Persistence;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>TASK-14 AC #4 (compression half): per-section LZ4 is optional, deterministic and transparent to the load.</summary>
public sealed class CompressionTests
{
    [Fact]
    public void Compression_is_per_section_optional_and_loads_to_the_same_world()
    {
        var scenario = new ColonistHarnessScenario { Colonists = 2_000, Food = 100 };
        using var world = ScenarioWorlds.Create(scenario);
        for (int i = 0; i < 3; i++)
        {
            world.Tick();
        }

        byte[] plain = world.SaveToBytes(new SaveOptions { Compression = SaveCompression.None });
        byte[] packed = world.SaveToBytes(new SaveOptions { Compression = SaveCompression.Lz4 });

        Assert.True(packed.Length < plain.Length, $"compressed {packed.Length} vs plain {plain.Length}");
        Assert.False(WorldPersistence.IsSectionCompressed(plain, "archetype/0"));
        Assert.True(WorldPersistence.IsSectionCompressed(packed, "archetype/0"));
        Assert.False(WorldPersistence.IsSectionCompressed(packed, "world"), "sections under the threshold stay raw");
        Assert.Equal(WorldPersistence.SectionNames(plain), WorldPersistence.SectionNames(packed));

        using var fromPlain = WorldPersistence.Load(plain, ScenarioWorlds.Schema(new ColonistHarnessScenario { Colonists = 2_000, Food = 100 }));
        using var fromPacked = WorldPersistence.Load(packed, ScenarioWorlds.Schema(new ColonistHarnessScenario { Colonists = 2_000, Food = 100 }));
        Assert.Equal(world.Hash(), fromPlain.Hash());
        Assert.Equal(world.Hash(), fromPacked.Hash());
    }

    [Fact]
    public void Saving_the_same_world_twice_yields_identical_bytes()
    {
        using var world = TablesWorld.Compose().Create();
        Assert.Equal(world.SaveToBytes(), world.SaveToBytes());
        Assert.Equal(world.SaveToBytes(new SaveOptions { Compression = SaveCompression.None }), world.SaveToBytes(new SaveOptions { Compression = SaveCompression.None }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(64)]
    [InlineData(5_000)]
    [InlineData(70_000)]
    public void The_lz4_block_codec_round_trips_zeros_text_and_noise(int length)
    {
        Check(new byte[length]);

        var text = new byte[length];
        var phrase = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog; ");
        for (int i = 0; i < length; i++)
        {
            text[i] = phrase[i % phrase.Length];
        }

        Check(text);

        var noise = new byte[length];
        uint x = 0x9E37_79B9;
        for (int i = 0; i < length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            noise[i] = (byte)x;
        }

        Check(noise);
    }

    [Fact]
    public void The_lz4_block_decoder_rejects_malformed_input()
    {
        var destination = new byte[32];
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decompress([0xF0, 0x01], destination));
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decompress([0x10, 0x41, 0xFF, 0xFF], destination));
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decompress([0x10, 0x41], new byte[2]));
    }

    private static void Check(byte[] input)
    {
        var compressed = new byte[Lz4Block.MaxCompressedLength(input.Length)];
        int length = Lz4Block.Compress(input, compressed);
        Assert.InRange(length, 1, compressed.Length);

        var output = new byte[input.Length];
        Lz4Block.Decompress(compressed.AsSpan(0, length), output);
        Assert.Equal(input, output);

        var again = new byte[compressed.Length];
        Assert.Equal(length, Lz4Block.Compress(input, again));
        Assert.Equal(compressed.AsSpan(0, length).ToArray(), again.AsSpan(0, length).ToArray());
    }
}
