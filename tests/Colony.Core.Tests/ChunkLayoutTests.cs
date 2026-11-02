using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony.Core.Tests;

public sealed class ChunkLayoutTests
{
    // Colonist archetype from the ECS prototype: Entity 8 B + Position 8 + Hunger 4 + Health 4 = 24 B/row,
    // 65536 / 24 = 2730 rounded down to a multiple of 16 = 2720, then stepped down until the columns plus one
    // 64-byte-aligned enable bitset per Component (TASK-8) fit: 2672. Independent of the CPU vector width.
    private const int ColonistCapacity = 2672;

    // ceil(2672 / 8) = 334 bytes, aligned to 64 = 384 bytes per enable bitset; three Components.
    private const int EnableBitsetBytes = 384 * 3;

    [Fact]
    public void Chunk_capacity_is_a_multiple_of_16_fixed_by_the_layout_alone()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var all = ComponentMask.Of(world.Schema.Component<Position>(), world.Schema.Component<Hunger>(), world.Schema.Component<Health>());
        var buffer = world.CreateCommandBuffer();
        buffer.Create(all);
        world.Playback(buffer);

        foreach (var chunk in world.Query(all))
        {
            Assert.Equal(ColonistCapacity, chunk.Capacity);
            Assert.Equal(0, chunk.Capacity % 16);
            Assert.Equal(1, chunk.Count);
            Assert.Equal(16, chunk.PaddedCount);
        }
    }

    [Fact]
    public void Every_column_starts_on_a_64_byte_boundary_and_lies_inside_the_chunk()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var position = world.Schema.Component<Position>();
        var hunger = world.Schema.Component<Hunger>();
        var health = world.Schema.Component<Health>();
        var all = ComponentMask.Of(position, hunger, health);
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 3000; i++)
        {
            buffer.Create(all);
        }

        world.Playback(buffer);

        int chunks = 0;
        foreach (var chunk in world.Query(all))
        {
            chunks++;
            nint entities = AddressOf(chunk.Entities);
            nint positions = AddressOf(chunk.Column<Position>(position));
            nint hungers = AddressOf(chunk.Column<Hunger>(hunger));
            nint healths = AddressOf(chunk.Column<Health>(health));

            Assert.Equal(0, entities % 64);
            Assert.Equal(0, positions % 64);
            Assert.Equal(0, hungers % 64);
            Assert.Equal(0, healths % 64);

            // Entity column, then the enable bitsets, then the Component columns in id order, each padded to Capacity elements.
            Assert.Equal(entities + ColonistCapacity * 8 + EnableBitsetBytes, positions);
            Assert.Equal(positions + ColonistCapacity * 8, hungers);
            Assert.Equal(hungers + ColonistCapacity * 4, healths);
            Assert.True(healths + ColonistCapacity * 4 - entities <= 64 * 1024);
        }

        Assert.Equal(2, chunks);
    }

    [Fact]
    public void Padded_columns_round_the_live_count_up_to_16_and_never_exceed_capacity()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var health = world.Schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 37; i++)
        {
            var e = buffer.Create(ComponentMask.Of(health));
            buffer.Add(e, health, new Health { Value = i });
        }

        world.Playback(buffer);

        foreach (var chunk in world.Query(health))
        {
            var live = chunk.Column<Health>(health);
            var padded = chunk.PaddedColumn<Health>(health);
            Assert.Equal(37, live.Length);
            Assert.Equal(48, padded.Length);
            Assert.True(padded.Length <= chunk.Capacity);
            Assert.Equal(AddressOf(live), AddressOf(padded));
            for (int i = 0; i < 37; i++)
            {
                Assert.Equal(i, padded[i].Value);
            }
        }
    }

    [Fact]
    public void A_Component_too_large_for_16_rows_is_rejected_at_registration()
    {
        var builder = new WorldBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddComponent<Huge>("huge"));
    }

    [Fact]
    public void Column_with_the_wrong_element_type_is_rejected()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var health = world.Schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(health));
        world.Playback(buffer);

        Assert.Throws<ArgumentException>(() => WrongElementType(world, health));
        Assert.Throws<InvalidOperationException>(() => MissingColumn(world, health, world.Schema.Component<Position>()));
    }

    private static void WrongElementType(World world, ComponentType health)
    {
        foreach (var chunk in world.Query(health))
        {
            chunk.Column<Position>(health);
        }
    }

    private static void MissingColumn(World world, ComponentType health, ComponentType position)
    {
        foreach (var chunk in world.Query(health))
        {
            chunk.Column<Position>(position);
        }
    }

    [Fact]
    public void Query_filters_archetypes_by_all_and_none()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var position = world.Schema.Component<Position>();
        var hunger = world.Schema.Component<Hunger>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 5; i++)
        {
            buffer.Create(ComponentMask.Of(position));
        }

        for (int i = 0; i < 3; i++)
        {
            buffer.Create(ComponentMask.Of(position, hunger));
        }

        world.Playback(buffer);

        Assert.Equal(8, world.Query(ComponentMask.Of(position)).EntityCount());
        Assert.Equal(3, world.Query(ComponentMask.Of(position, hunger)).EntityCount());
        Assert.Equal(5, world.Query(ComponentMask.Of(position), ComponentMask.Of(hunger)).EntityCount());
        int seen = 0;
        foreach (var chunk in world.Query(ComponentMask.Of(position), ComponentMask.Of(hunger)))
        {
            seen += chunk.Count;
            Assert.False(chunk.Has(hunger));
        }

        Assert.Equal(5, seen);
    }

    private static unsafe nint AddressOf<T>(ReadOnlySpan<T> span) where T : unmanaged
        => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

    private static unsafe nint AddressOf<T>(Span<T> span) where T : unmanaged
        => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

    [InlineArray(5000)]
    public struct Huge
    {
        private byte _element;
    }
}
