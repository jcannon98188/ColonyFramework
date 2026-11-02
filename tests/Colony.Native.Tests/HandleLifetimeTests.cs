using Colony.Plugins.Native;

namespace Colony.Native.Tests;

/// <summary>
/// Review #7 (spec section 3, no process-global state): the C ABI's handle table is process-wide because C holds
/// plain integers, but every handle a World's Tick hands out (the World itself and its per-job command buffers) is
/// owned by that World and released when it is disposed, so a disposed harness World is no longer rooted.
/// </summary>
[Collection(NativeTestGroup.Name)]
public sealed class HandleLifetimeTests
{
    [Fact]
    public void Handles_a_native_system_hands_out_during_ticks_are_released_when_the_world_is_disposed()
    {
        string library = ConformanceTests.RequireLibrary();
        var scenario = new ConformanceScenario("conformance.native", () => NativePlugin.Load(library));
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 2 });
        scenario.Populate(world);
        for (int t = 0; t < 4; t++)
        {
            scenario.Advance(world);
            Assert.True(world.Tick());
        }

        Assert.True(NativeHandles.TryGetHandle(world, out nint handle));
        Assert.True(NativeHandles.OwnedCount(world) > 1, "the World and at least one per-job command buffer");

        world.Dispose();

        Assert.False(NativeHandles.TryGetHandle(world, out _));
        Assert.Equal(0, NativeHandles.OwnedCount(world));
        Assert.Throws<ArgumentException>(() => NativeHandles.Get<World>(handle, "colony_world"));
    }

    [Fact]
    public void A_command_buffer_the_host_creates_through_the_abi_is_owned_by_its_world()
    {
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        nint commands = NativeHandles.GetOrAdd(world.CreateCommandBuffer(), world);
        Assert.Equal(1, NativeHandles.OwnedCount(world));

        world.Dispose();

        Assert.Throws<ArgumentException>(() => NativeHandles.Get<CommandBuffer>(commands, "colony_commands"));
    }
}
