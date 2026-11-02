namespace Colony.Core.Tests.Tables;

/// <summary>AC #4: blobs are immutable once stored; the store is saved World state.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class BlobStoreTests
{
    private static World NewWorld() => new WorldBuilder().AddComponent<Position>("position").DebugValidation(false).Build().CreateWorld();

    [Fact]
    public void A_stored_blob_is_a_copy_the_caller_cannot_change_afterwards()
    {
        using var world = NewWorld();
        int[] source = [1, 2, 3, 4, 5];
        var blob = world.Blobs.Store<int>(source);
        source[0] = 99;

        Assert.False(blob.IsNull);
        Assert.Equal([1, 2, 3, 4, 5], world.Blobs.Get(blob).ToArray());
        Assert.Equal(5, world.Blobs.Get(blob).Length);
        Assert.Equal(1, world.Blobs.Count);
        Assert.Equal(5 * sizeof(int), world.Blobs.Bytes);
    }

    [Fact]
    public void Blobs_get_dense_ids_in_store_order_and_identical_sequences_match_across_worlds()
    {
        using var a = NewWorld();
        using var b = NewWorld();
        for (int i = 0; i < 50; i++)
        {
            var data = new long[i + 1];
            for (int j = 0; j < data.Length; j++)
            {
                data[j] = i * 1_000 + j;
            }

            var ra = a.Blobs.Store<long>(data);
            var rb = b.Blobs.Store<long>(data);
            Assert.Equal(ra, rb);
            Assert.Equal((uint)(i + 1), ra.Id);
        }

        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.Blobs.Get(new BlobRef<long>(7)).ToArray(), b.Blobs.Get(new BlobRef<long>(7)).ToArray());
    }

    [Fact]
    public void Storing_the_same_bytes_twice_yields_two_blobs()
    {
        using var world = NewWorld();
        var first = world.Blobs.Store<byte>([1, 2, 3]);
        var second = world.Blobs.Store<byte>([1, 2, 3]);
        Assert.NotEqual(first, second);
        Assert.Equal(2, world.Blobs.Count);
    }

    [Fact]
    public void Empty_blobs_are_allowed_and_the_null_ref_reads_as_empty()
    {
        using var world = NewWorld();
        var empty = world.Blobs.Store<Position>([]);
        Assert.False(empty.IsNull);
        Assert.Equal(0, world.Blobs.Get(empty).Length);
        Assert.Equal(0, world.Blobs.Get(default(BlobRef<Position>)).Length);
        Assert.True(default(BlobRef<Position>).IsNull);
    }

    [Fact]
    public void Reading_a_blob_with_the_wrong_element_type_or_an_unknown_id_throws()
    {
        using var world = NewWorld();
        var blob = world.Blobs.Store<int>([1, 2, 3, 4]);
        Assert.Throws<ArgumentException>(() => world.Blobs.Get(new BlobRef<long>(blob.Id)));
        Assert.Throws<ArgumentException>(() => world.Blobs.Get(new BlobRef<int>(42)));
        Assert.Equal([1, 2, 3, 4], world.Blobs.Get(new BlobRef<int>(blob.Id)).ToArray());
    }

    [Fact]
    public void Structs_keep_their_alignment_and_large_blobs_survive_growth()
    {
        using var world = NewWorld();
        var refs = new BlobRef<Position>[40];
        for (int i = 0; i < refs.Length; i++)
        {
            var data = new Position[1 + i * 97];
            for (int j = 0; j < data.Length; j++)
            {
                data[j] = new Position { X = i, Y = j };
            }

            refs[i] = world.Blobs.Store<Position>(data);
        }

        for (int i = 0; i < refs.Length; i++)
        {
            var span = world.Blobs.Get(refs[i]);
            Assert.Equal(1 + i * 97, span.Length);
            Assert.Equal(new Position { X = i, Y = span.Length - 1 }, span[^1]);
        }
    }

    [Fact]
    public void Storing_a_blob_changes_the_world_hash()
    {
        using var world = NewWorld();
        ulong before = world.Hash();
        world.Blobs.Store<int>([1]);
        ulong after = world.Hash();
        Assert.NotEqual(before, after);
        Assert.Equal(after, world.Hash());
    }

    [Fact]
    public void Reading_a_blob_allocates_nothing()
    {
        using var world = NewWorld();
        var blob = world.Blobs.Store<int>([1, 2, 3, 4, 5, 6, 7, 8]);
        long sum = 0;
        for (int i = 0; i < 100; i++)
        {
            sum += world.Blobs.Get(blob)[i % 8];
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            sum += world.Blobs.Get(blob)[i % 8];
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(sum > 0);
    }
}
