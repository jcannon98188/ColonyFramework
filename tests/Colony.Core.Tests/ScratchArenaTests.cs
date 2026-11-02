namespace Colony.Core.Tests;

public sealed class ScratchArenaTests
{
    [Fact]
    public unsafe void Allocations_are_aligned_and_reset_rewinds_without_renting_more_chunks()
    {
        using var allocator = new SlabAllocator(poison: false);
        using var arena = new ScratchArena(allocator, poison: false);

        byte* a = arena.Alloc(10);
        byte* b = arena.Alloc(100, 64);
        byte* c = arena.Alloc(SlabAllocator.ChunkSize); // does not fit the first chunk: spills to a second
        Assert.Equal(0, (nint)a % 16);
        Assert.Equal(0, (nint)b % 64);
        Assert.True(b >= a + 10);
        Assert.Equal(0, (nint)c % 64);
        Assert.True(allocator.Owns(c), "a chunk-sized allocation starts a fresh chunk");
        Assert.Equal(2, arena.ChunkCount);
        Assert.Equal(2, allocator.ChunksInUse);

        uint generation = arena.Generation;
        arena.Reset();
        Assert.Equal(generation + 1, arena.Generation);
        Assert.Equal(0, arena.UsedBytes);

        byte* again = arena.Alloc(10);
        arena.Alloc(SlabAllocator.ChunkSize);
        Assert.True(again == a);
        Assert.Equal(2, arena.ChunkCount);
        Assert.Equal(2, allocator.ChunksInUse);
    }

    [Fact]
    public unsafe void Concurrent_allocations_from_worker_threads_never_overlap()
    {
        // Chunk jobs record into per-job command buffers from any worker, so page rentals race on the arena.
        using var allocator = new SlabAllocator(poison: false);
        using var arena = new ScratchArena(allocator, poison: false);
        const int threads = 8;
        const int perThread = 2000;
        var slices = new nint[threads * perThread];
        using var start = new ManualResetEventSlim();
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int thread = t;
            workers[t] = new Thread(() =>
            {
                start.Wait();
                for (int i = 0; i < perThread; i++)
                {
                    byte* p = arena.Alloc(48 + ((thread * 7 + i) % 5 * 16));
                    slices[(thread * perThread) + i] = (nint)p;
                    new Span<byte>(p, 48).Fill((byte)thread);
                }
            });
            workers[t].Start();
        }

        start.Set();
        foreach (var worker in workers)
        {
            worker.Join();
        }

        for (int t = 0; t < threads; t++)
        {
            for (int i = 0; i < perThread; i++)
            {
                Assert.Equal((byte)t, *(byte*)slices[(t * perThread) + i]);
            }
        }

        var sorted = (nint[])slices.Clone();
        Array.Sort(sorted);
        for (int i = 1; i < sorted.Length; i++)
        {
            Assert.True(sorted[i] - sorted[i - 1] >= 48, "two allocations overlap");
        }
    }

    [Fact]
    public unsafe void Allocations_larger_than_a_chunk_are_rejected()
    {
        using var allocator = new SlabAllocator(poison: false);
        using var arena = new ScratchArena(allocator, poison: false);

        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Alloc(SlabAllocator.ChunkSize + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Alloc(0));
    }

    [Fact]
    public void Command_buffers_draw_their_storage_from_the_arena_and_reset_frees_it()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var health = world.Schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        Assert.Equal(0, world.Scratch.UsedBytes);

        for (int i = 0; i < 5000; i++)
        {
            buffer.Add(EntityId.Null, health, new Health { Value = i });
        }

        long used = world.Scratch.UsedBytes;
        Assert.True(used >= 5000 * 13, $"5000 Add commands should take at least 65000 arena bytes, took {used}");
        world.Playback(buffer);
        world.ResetScratch();
        Assert.Equal(0, world.Scratch.UsedBytes);
        int chunks = world.Scratch.ChunkCount;

        for (int i = 0; i < 5000; i++)
        {
            buffer.Add(EntityId.Null, health, new Health { Value = i });
        }

        world.Playback(buffer);
        world.ResetScratch();
        Assert.Equal(chunks, world.Scratch.ChunkCount);
    }

    [Fact]
    public void Resolving_a_deferred_handle_after_the_scratch_reset_throws()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create();
        Assert.Throws<InvalidOperationException>(() => buffer.Resolve(deferred));

        world.Playback(buffer);
        Assert.False(buffer.Resolve(deferred).IsDeferred);

        world.ResetScratch();
        Assert.Throws<InvalidOperationException>(() => buffer.Resolve(deferred));
    }

    [Fact]
    public void A_buffer_recorded_before_a_scratch_reset_cannot_be_played_back_after_it()
    {
        using var world = TestSchemas.ColonistSchema(debug: false).CreateWorld();
        var buffer = world.CreateCommandBuffer();
        buffer.Create();
        world.ResetScratch();

        Assert.Throws<InvalidOperationException>(() => world.Playback(buffer));
        buffer.Clear();
        buffer.Create();
        world.Playback(buffer);
        Assert.Equal(1, world.EntityCount);
    }
}
