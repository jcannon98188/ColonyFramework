namespace Colony.Core.Tests.Tables;

/// <summary>AC #4: interning yields identical StringIds for identical insertion sequences; the table is saved World state.</summary>
public sealed class StringTableTests
{
    private static World NewWorld() => new WorldBuilder().AddComponent<Position>("position").DebugValidation(false).Build().CreateWorld();

    [Fact]
    public void Interning_the_same_text_twice_yields_the_same_id_and_ids_are_dense_in_insertion_order()
    {
        using var world = NewWorld();
        var table = world.Strings;
        Assert.Equal(0, table.Count);

        var wood = table.Intern("wood");
        var stone = table.Intern("stone");
        var woodAgain = table.Intern("wood".AsSpan());

        Assert.Equal(wood, woodAgain);
        Assert.NotEqual(wood, stone);
        Assert.Equal(1u, wood.Value);
        Assert.Equal(2u, stone.Value);
        Assert.Equal(2, table.Count);
        Assert.False(wood.IsNull);
        Assert.True(StringId.Null.IsNull);
        Assert.True(default(StringId).IsNull);
    }

    [Fact]
    public void Identical_insertion_sequences_yield_identical_ids_across_worlds()
    {
        string[] names = ["wood", "stone", "", "iron", "wood", "copper", "a much longer name with spaces", "stone"];
        using var a = NewWorld();
        using var b = NewWorld();
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(a.Strings.Intern(names[i]), b.Strings.Intern(names[i]));
        }

        Assert.Equal(a.Strings.Count, b.Strings.Count);
        Assert.Equal(a.Hash(), b.Hash());
    }

    [Fact]
    public void Get_returns_the_interned_text_and_TryGetId_finds_it_without_interning()
    {
        using var world = NewWorld();
        var table = world.Strings;
        var id = table.Intern("plank");
        Assert.Equal("plank", table.Get(id).ToString());
        Assert.True(table.TryGetId("plank", out var found));
        Assert.Equal(id, found);
        Assert.False(table.TryGetId("beam", out var missing));
        Assert.True(missing.IsNull);
        Assert.Equal(1, table.Count);
        Assert.Equal(0, table.Get(StringId.Null).Length);
        Assert.Throws<ArgumentException>(() => table.Get(new StringId(99)));
    }

    [Fact]
    public void The_empty_string_is_a_distinct_interned_entry()
    {
        using var world = NewWorld();
        var empty = world.Strings.Intern("");
        Assert.False(empty.IsNull);
        Assert.Equal(0, world.Strings.Get(empty).Length);
        Assert.Equal(empty, world.Strings.Intern(ReadOnlySpan<char>.Empty));
    }

    [Fact]
    public void Many_strings_survive_growth_of_the_lookup_and_the_storage()
    {
        using var world = NewWorld();
        var ids = new StringId[5_000];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = world.Strings.Intern($"item-{i}-{new string('x', i % 37)}");
        }

        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal((uint)(i + 1), ids[i].Value);
            Assert.Equal($"item-{i}-{new string('x', i % 37)}", world.Strings.Get(ids[i]).ToString());
            Assert.Equal(ids[i], world.Strings.Intern($"item-{i}-{new string('x', i % 37)}"));
        }

        Assert.Equal(ids.Length, world.Strings.Count);
    }

    [Fact]
    public void Interning_changes_the_world_hash_and_different_orders_differ()
    {
        using var a = NewWorld();
        using var b = NewWorld();
        ulong before = a.Hash();
        a.Strings.Intern("wood");
        Assert.NotEqual(before, a.Hash());

        a.Strings.Intern("stone");
        b.Strings.Intern("stone");
        b.Strings.Intern("wood");
        Assert.NotEqual(a.Hash(), b.Hash());
    }

    [Fact]
    public void Looking_up_an_existing_string_allocates_nothing()
    {
        using var world = NewWorld();
        var id = world.Strings.Intern("wood");
        ReadOnlySpan<char> text = "wood";
        for (int i = 0; i < 100; i++)
        {
            world.Strings.Intern(text);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        StringId found = default;
        int length = 0;
        for (int i = 0; i < 1_000; i++)
        {
            found = world.Strings.Intern(text);
            length += world.Strings.Get(found).Length;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(id, found);
        Assert.Equal(4_000, length);
    }
}
