using System.Runtime.CompilerServices;

namespace Colony.Core.Tests.Buffers;

/// <summary>AC #3: buffer Components store elements inline up to the declared capacity, overflow to the World allocator, and the overflow is observable.</summary>
public sealed class BufferComponentTests
{
    private static WorldSchema Schema(out ComponentType inventory, out ComponentType tags)
    {
        var builder = new WorldBuilder().AddComponent<Position>("position").DebugValidation(false);
        builder.AddBufferComponent<int, Inline4<int>>("inventory");
        builder.AddBufferComponent<long, Inline8<long>>("tags", ComponentOptions.NonFragmenting);
        inventory = builder.Component("inventory");
        tags = builder.Component("tags");
        return builder.Build();
    }

    private static EntityId Spawn(World world, CommandBuffer buffer, ComponentMask signature)
    {
        var deferred = buffer.Create(signature);
        world.Playback(buffer);
        return buffer.Resolve(deferred);
    }

    [Fact]
    public void A_buffer_component_declares_its_element_size_and_inline_capacity()
    {
        var schema = Schema(out var inventory, out var tags);
        var descriptor = schema.Describe(inventory);
        Assert.True(descriptor.IsBuffer);
        Assert.Equal(sizeof(int), descriptor.ElementSize);
        Assert.Equal(4, descriptor.InlineCapacity);
        Assert.Equal(Unsafe.SizeOf<BufferComponent<int, Inline4<int>>>(), descriptor.Size);
        Assert.Equal(8 + (4 * sizeof(int)), descriptor.Size);
        Assert.True(schema.Describe(tags).IsNonFragmenting);
        Assert.Equal(8, schema.Describe(tags).InlineCapacity);
        Assert.Throws<ArgumentException>(() => new WorldBuilder().AddComponent<int>("x", ComponentOptions.Buffer));
        Assert.Throws<ArgumentException>(() => new WorldBuilder().AddBufferComponent<long, Inline4<byte>>("odd"));
    }

    [Fact]
    public void Elements_stay_inline_up_to_the_capacity_then_overflow_to_the_world_allocator()
    {
        var schema = Schema(out var inventory, out _);
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var entity = Spawn(world, buffer, ComponentMask.Of(inventory));

        var items = world.GetBuffer<int>(entity, inventory);
        Assert.Equal(0, items.Length);
        Assert.True(items.IsInline);
        Assert.Equal(4, items.Capacity);
        for (int i = 1; i <= 4; i++)
        {
            items.Add(i * 10);
        }

        Assert.True(items.IsInline);
        Assert.Equal(4, items.Length);
        Assert.Equal(0, world.BufferCounters.Overflows);

        items.Add(50);
        Assert.False(items.IsInline);
        Assert.Equal(5, items.Length);
        Assert.True(items.Capacity >= 5);
        Assert.Equal([10, 20, 30, 40, 50], items.AsSpan().ToArray());
        Assert.Equal(1, world.BufferCounters.Overflows);
        Assert.Equal(1, world.BufferCounters.BlocksInUse);

        for (int i = 6; i <= 100; i++)
        {
            items.Add(i * 10);
        }

        Assert.Equal(100, items.Length);
        Assert.Equal(1000, items[99]);
        Assert.True(world.BufferCounters.Grows >= 1);
        Assert.Equal(1, world.BufferCounters.BlocksInUse);

        // The accessor re-taken from the World sees the same storage.
        var again = world.GetBuffer<int>(entity, inventory);
        Assert.Equal(100, again.Length);
        again.RemoveAt(0);
        Assert.Equal(99, again.Length);
        Assert.Equal(20, again[0]);
        again.RemoveAtSwapBack(0);
        Assert.Equal(1000, again[0]);
        again.Clear();
        Assert.Equal(0, again.Length);
        Assert.False(again.IsInline); // the block is kept until the Entity or Component goes
    }

    [Fact]
    public void Destroying_the_entity_or_removing_the_component_frees_the_overflow_block()
    {
        var schema = Schema(out var inventory, out var tags);
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var a = Spawn(world, buffer, ComponentMask.Of(inventory));
        var b = Spawn(world, buffer, ComponentMask.Of(inventory));
        buffer.Add(b, tags);
        world.Playback(buffer);
        for (int i = 0; i < 10; i++)
        {
            world.GetBuffer<int>(a, inventory).Add(i);
            world.GetBuffer<int>(b, inventory).Add(i);
            world.GetBuffer<long>(b, tags).Add(i);
        }

        Assert.Equal(3, world.BufferCounters.BlocksInUse);
        long freesBefore = world.BufferCounters.Frees; // grows free their old block too

        buffer.Remove(b, inventory);
        world.Playback(buffer);
        Assert.Equal(2, world.BufferCounters.BlocksInUse);
        Assert.Equal(freesBefore + 1, world.BufferCounters.Frees);

        buffer.Destroy(b);
        world.Playback(buffer);
        Assert.Equal(1, world.BufferCounters.BlocksInUse);

        buffer.Destroy(a);
        world.Playback(buffer);
        Assert.Equal(0, world.BufferCounters.BlocksInUse);
        Assert.Equal(0, world.BufferCounters.BytesInUse);
        Assert.Equal(freesBefore + 3, world.BufferCounters.Frees);
    }

    [Fact]
    public void Archetype_moves_and_overwrites_keep_the_elements_and_a_value_owning_overflow_cannot_be_copied()
    {
        var schema = Schema(out var inventory, out _);
        var position = schema.Component<Position>();
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var entity = Spawn(world, buffer, ComponentMask.Of(inventory));
        for (int i = 0; i < 7; i++)
        {
            world.GetBuffer<int>(entity, inventory).Add(i);
        }

        buffer.Add(entity, position, new Position { X = 1 });
        world.Playback(buffer);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], world.GetBuffer<int>(entity, inventory).AsSpan().ToArray());
        Assert.Equal(1, world.BufferCounters.BlocksInUse);

        // Overwriting with a fresh (inline) value frees the old block.
        var fresh = default(BufferComponent<int, Inline4<int>>);
        buffer.Add(entity, inventory, fresh);
        world.Playback(buffer);
        Assert.Equal(0, world.GetBuffer<int>(entity, inventory).Length);
        Assert.Equal(0, world.BufferCounters.BlocksInUse);

        world.GetBuffer<int>(entity, inventory).Add(1);
        world.GetBuffer<int>(entity, inventory).Add(2);
        var copy = world.Get<BufferComponent<int, Inline4<int>>>(entity, inventory);
        buffer.Add(entity, inventory, copy); // inline copies are plain data
        world.Playback(buffer);
        Assert.Equal([1, 2], world.GetBuffer<int>(entity, inventory).AsSpan().ToArray());

        for (int i = 0; i < 5; i++)
        {
            world.GetBuffer<int>(entity, inventory).Add(i);
        }

        var owning = world.Get<BufferComponent<int, Inline4<int>>>(entity, inventory);
        Assert.Throws<ArgumentException>(() => buffer.Add(entity, inventory, owning));
        Assert.Throws<ArgumentException>(() => world.GetBuffer<long>(entity, inventory));
        Assert.Throws<ArgumentException>(() => world.GetBuffer<int>(entity, position));
    }

    [Fact]
    public void The_hash_covers_the_elements_logically_not_the_overflow_handle()
    {
        var schema = Schema(out var inventory, out var tags);
        using var a = schema.CreateWorld();
        using var b = schema.CreateWorld();
        var ea = Spawn(a, a.CreateCommandBuffer(), ComponentMask.Of(inventory));
        var eb = Spawn(b, b.CreateCommandBuffer(), ComponentMask.Of(inventory));
        var ba = a.CreateCommandBuffer();
        var bb = b.CreateCommandBuffer();
        ba.Add(ea, tags);
        bb.Add(eb, tags);
        a.Playback(ba);
        b.Playback(bb);

        // a: overflowed then shrunk back to 3 elements; b: never left inline. Same logical content.
        var items = a.GetBuffer<int>(ea, inventory);
        for (int i = 0; i < 6; i++)
        {
            items.Add(i);
        }

        items.RemoveAt(5);
        items.RemoveAt(4);
        items.RemoveAt(3);
        Assert.False(items.IsInline);
        var other = b.GetBuffer<int>(eb, inventory);
        other.Add(0);
        other.Add(1);
        other.Add(2);
        Assert.True(other.IsInline);
        Assert.Equal(a.Hash(), b.Hash());

        other[2] = 7;
        Assert.NotEqual(a.Hash(), b.Hash());
        other[2] = 2;
        Assert.Equal(a.Hash(), b.Hash());

        // Non-fragmenting buffers hash the same way.
        a.GetBuffer<long>(ea, tags).Add(99);
        Assert.NotEqual(a.Hash(), b.Hash());
        b.GetBuffer<long>(eb, tags).Add(99);
        Assert.Equal(a.Hash(), b.Hash());
    }

    [Fact]
    public void Too_many_elements_for_one_block_is_an_error_not_a_silent_truncation()
    {
        var schema = Schema(out var inventory, out _);
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var entity = Spawn(world, buffer, ComponentMask.Of(inventory));
        var items = world.GetBuffer<int>(entity, inventory);
        Assert.Equal(64 * 1024 / sizeof(int), items.MaxLength);
        items.Resize(items.MaxLength);
        Assert.Equal(items.MaxLength, items.Length);
        Assert.Throws<InvalidOperationException>(() => world.GetBuffer<int>(entity, inventory).Add(1));
    }
}
