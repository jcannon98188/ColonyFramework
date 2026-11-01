using Colony.Diagnostics.Inspection;

namespace Colony.Diagnostics.Tests.Inspection;

/// <summary>TASK-35 AC #4: debug commands go through the same Input permission mechanism as Game commands.</summary>
public sealed class DebugCommandPermissionTests
{
    [Fact]
    public void Debug_commands_are_denied_by_default_and_granted_only_to_the_inspection_source()
    {
        var schema = InspectFixture.BuildSchema();
        Assert.True(schema.TryGetDebugCommands(out var debug));
        Assert.True(schema.InputPermissions.IsAllowed(DebugCommands.Source, debug));
        Assert.False(schema.InputPermissions.IsAllowed(new InputSource(1), debug));

        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1, Seed = 7 });
        var create = DebugCommands.Create([schema.Component<Vitals>()]);
        world.Inputs.Submit(new InputSource(1), in create, world.CurrentTick);
        world.Tick();
        Assert.Equal(0, world.EntityCount);

        world.Inputs.Submit(DebugCommands.Source, in create, world.CurrentTick);
        world.Tick();
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void A_game_can_grant_debug_commands_to_another_source_with_a_pair_rule()
    {
        var builder = new WorldBuilder().AddComponent<Vitals>();
        var debug = builder.AddDebugCommands();
        builder.GrantInput(new InputSource(5), debug);
        var schema = builder.Build();

        Assert.True(schema.InputPermissions.IsAllowed(new InputSource(5), debug));
        Assert.False(schema.InputPermissions.IsAllowed(new InputSource(6), debug));
    }
}
