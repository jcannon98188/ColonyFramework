namespace Colony.Core.Tests;

/// <summary>
/// Spec section 6: the World hash excludes padding. Padding bytes of a Component whose data bytes are known
/// (a field layout, or data ranges a Native Plugin declares) are zeroed where values enter the World, so a value
/// recorded with poisoned padding hashes like one with clean padding.
/// </summary>
public sealed class PaddingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void Raw_values_with_poisoned_padding_hash_like_clean_ones(bool nonFragmenting)
    {
        ulong clean = HashAfterRawAdd(0x00, nonFragmenting, out var cleanRead);
        ulong poisoned = HashAfterRawAdd(0xAB, nonFragmenting, out var poisonedRead);

        Assert.Equal(clean, poisoned);
        Assert.Equal(cleanRead, poisonedRead);
        Assert.Equal(new byte[] { 7, 0, 0, 0, 0x78, 0x56, 0x34, 0x12 }, poisonedRead);
    }

    [Fact]
    public void Typed_adds_of_a_component_with_a_field_layout_zero_its_padding_too()
    {
        ulong Run(byte poison)
        {
            var builder = new WorldBuilder();
            builder.DebugValidation(false);
            builder.AddComponent<Padded>();
            var type = builder.Component<Padded>();
            using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
            var value = new Padded { Tag = 7, Value = 0x12345678 };
            unsafe
            {
                var bytes = (byte*)&value;
                bytes[1] = bytes[2] = bytes[3] = poison;
            }

            var buffer = world.CreateCommandBuffer();
            var e = buffer.Create(ComponentMask.Of(type));
            buffer.Add(e, type, value);
            world.Playback(buffer);
            return world.Hash();
        }

        Assert.Equal(Run(0x00), Run(0xCD));
    }

    [Fact]
    public void Data_ranges_must_lie_inside_the_component_and_do_not_change_its_identity()
    {
        var a = new WorldBuilder();
        var typeA = a.AddComponentLayout("native.padded", 8, 4, ComponentOptions.None);
        var b = new WorldBuilder();
        var typeB = b.AddComponentLayout("native.padded", 8, 4, ComponentOptions.None);
        b.DeclareComponentData(typeB, [(0, 1), (4, 4)]);

        Assert.Throws<ArgumentException>(() => b.DeclareComponentData(typeB, [(6, 4)]));
        Assert.Equal(a.Build().SchemaHash, b.Build().SchemaHash);
    }

    private static unsafe ulong HashAfterRawAdd(byte poison, bool nonFragmenting, out byte[] read)
    {
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var type = builder.AddComponentLayout("native.padded", 8, 4, nonFragmenting ? ComponentOptions.NonFragmenting : ComponentOptions.None);
        builder.DeclareComponentData(type, [(0, 1), (4, 4)]);
        builder.AddComponent<Position>("pos");
        var pos = builder.Component<Position>();
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        byte* value = stackalloc byte[8];
        value[0] = 7;
        value[1] = value[2] = value[3] = poison;
        *(int*)(value + 4) = 0x12345678;
        var buffer = world.CreateCommandBuffer();
        var e = buffer.Create(ComponentMask.Of(pos));
        buffer.AddRaw(e, type, value, 8);
        world.Playback(buffer);
        EntityId real = default;
        foreach (var chunk in world.Query(ComponentMask.Of(pos)))
        {
            real = chunk.Entities[0];
        }

        read = new ReadOnlySpan<byte>(world.ComponentAddress(real, type), 8).ToArray();
        return world.Hash();
    }

    public struct Padded : IComponent<Padded>
    {
        private static readonly ComponentLayout PaddedLayout = new("padded.typed", ComponentOptions.None, [new("Tag", "uint8", 0, 1), new("Value", "int32", 4, 4)]);

        static ComponentLayout IComponent<Padded>.Layout => PaddedLayout;

        public byte Tag;
        public int Value;
    }
}
