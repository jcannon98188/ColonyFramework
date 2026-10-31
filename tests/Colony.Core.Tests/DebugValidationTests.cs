namespace Colony.Core.Tests;

/// <summary>AC #6: debug validation checks views against the chunk structural version, poisons freed memory and reports leaks.</summary>
public sealed class DebugValidationTests
{
    [Fact]
    public void A_chunk_view_taken_before_a_structural_change_is_stale_and_throws_in_debug()
    {
        using var world = TestSchemas.ColonistSchema(debug: true).CreateWorld();
        var health = world.Schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 4; i++)
        {
            buffer.Create(ComponentMask.Of(health));
        }

        world.Playback(buffer);

        foreach (var chunk in world.Query(health))
        {
            Assert.True(chunk.IsCurrent);
            buffer.Destroy(chunk.Entities[0]);
            world.Playback(buffer);

            Assert.False(chunk.IsCurrent);
            Assert.Throws<InvalidOperationException>(() => StaleAccess(world, health));
        }
    }

    [Fact]
    public void Without_debug_validation_a_stale_view_is_only_reported_through_IsCurrent()
    {
        using var world = TestSchemas.ColonistSchema(debug: false).CreateWorld();
        Assert.False(world.DebugValidation);
        var health = world.Schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(health));
        buffer.Create(ComponentMask.Of(health));
        world.Playback(buffer);

        foreach (var chunk in world.Query(health))
        {
            buffer.Destroy(chunk.Entities[1]);
            world.Playback(buffer);
            Assert.False(chunk.IsCurrent);

            // Release builds are unchecked: the view is a snapshot and still answers with its old count.
            Assert.Equal(2, chunk.Column<Health>(health).Length);
        }
    }

    [Fact]
    public unsafe void Returned_chunks_are_poisoned()
    {
        using var world = TestSchemas.ColonistSchema(debug: true).CreateWorld();
        byte* chunk = world.Allocator.Rent();
        chunk[0] = 1;
        chunk[SlabAllocator.ChunkSize - 1] = 2;

        world.Allocator.Return(chunk);

        Assert.Equal(SlabAllocator.PoisonByte, chunk[0]);
        Assert.Equal(SlabAllocator.PoisonByte, chunk[SlabAllocator.ChunkSize / 2]);
        Assert.Equal(SlabAllocator.PoisonByte, chunk[SlabAllocator.ChunkSize - 1]);
    }

    [Fact]
    public unsafe void Scratch_reset_poisons_the_used_region()
    {
        using var world = TestSchemas.ColonistSchema(debug: true).CreateWorld();
        byte* scratch = world.Scratch.Alloc(256);
        scratch[0] = 1;
        scratch[255] = 2;

        world.ResetScratch();

        Assert.Equal(ScratchArena.PoisonByte, scratch[0]);
        Assert.Equal(ScratchArena.PoisonByte, scratch[255]);
    }

    [Fact]
    public unsafe void Release_mode_does_not_poison()
    {
        using var world = TestSchemas.ColonistSchema(debug: false).CreateWorld();
        byte* chunk = world.Allocator.Rent();
        chunk[0] = 1;
        world.Allocator.Return(chunk);
        Assert.Equal(1, chunk[0]);
    }

    [Fact]
    public unsafe void Disposing_a_debug_world_with_a_rented_chunk_throws_a_leak_report()
    {
        var world = TestSchemas.ColonistSchema(debug: true).CreateWorld();
        world.Allocator.Rent();

        var error = Assert.Throws<InvalidOperationException>(world.Dispose);
        Assert.Contains("1 chunk", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void Leak_reports_go_to_the_debug_listener_when_one_is_set()
    {
        var listener = new RecordingListener();
        var world = TestSchemas.ColonistSchema(debug: true).CreateWorld(new WorldOptions { DebugListener = listener });
        world.Allocator.Rent();
        world.Allocator.Rent();

        world.Dispose();

        Assert.Equal(new LeakReport(2, 2L * SlabAllocator.ChunkSize), listener.Report);
    }

    [Fact]
    public unsafe void A_world_without_leaks_disposes_silently_and_release_worlds_never_report()
    {
        var clean = TestSchemas.ColonistSchema(debug: true).CreateWorld();
        var buffer = clean.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(clean.Schema.Component<Health>()));
        clean.Playback(buffer);
        clean.Dispose();

        var release = TestSchemas.ColonistSchema(debug: false).CreateWorld();
        release.Allocator.Rent();
        release.Dispose();
    }

    [Fact]
    public void Resetting_scratch_with_pending_commands_throws_in_debug()
    {
        using var world = TestSchemas.ColonistSchema(debug: true).CreateWorld();
        var buffer = world.CreateCommandBuffer();
        buffer.Create();

        Assert.Throws<InvalidOperationException>(world.ResetScratch);
        world.Playback(buffer);
        world.ResetScratch();
    }

    [Fact]
    public void Operations_on_a_disposed_world_throw()
    {
        var world = TestSchemas.ColonistSchema().CreateWorld();
        var buffer = world.CreateCommandBuffer();
        world.Dispose();
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.Playback(buffer));
        Assert.Throws<ObjectDisposedException>(world.CreateCommandBuffer);
    }

    private static void StaleAccess(World world, ComponentType health)
    {
        // Take a view, change the chunk structurally, then touch the view.
        foreach (var chunk in world.Query(health))
        {
            var buffer = world.CreateCommandBuffer();
            buffer.Destroy(chunk.Entities[0]);
            world.Playback(buffer);
            chunk.Column<Health>(health);
        }
    }

    private sealed class RecordingListener : IDebugListener
    {
        public LeakReport Report { get; private set; }

        public void OnLeak(in LeakReport report) => Report = report;
    }
}
