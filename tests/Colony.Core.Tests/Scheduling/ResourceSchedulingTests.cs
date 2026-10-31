namespace Colony.Core.Tests.Scheduling;

/// <summary>TASK-8 AC #1: declared Resource access takes part in conflict detection and is enforced at run time.</summary>
public sealed class ResourceSchedulingTests
{
    private static WorldBuilder Builder() => new WorldBuilder()
        .AddComponent<Health>("health")
        .AddResource<ColonyClock>("clock")
        .AddResource<Weather>("weather");

    private static SystemDefinition System(string name, ResourceMask reads = default, ResourceMask writes = default, string[]? after = null)
        => new()
        {
            Name = name,
            ReadsResources = reads,
            WritesResources = writes,
            After = after ?? [],
            Begin = (in SystemContext _) => { },
        };

    [Fact]
    public void A_resource_writer_and_a_reader_without_an_order_fail_at_build_and_an_ordered_pair_takes_two_waves()
    {
        var builder = Builder();
        var clock = builder.Resource<ColonyClock>();
        var weather = builder.Resource<Weather>();
        var ambiguous = Builder()
            .AddSystem(System("Advance", writes: ResourceMask.Of(clock)))
            .AddSystem(System("Display", reads: ResourceMask.Of(clock)));
        var writers = Builder()
            .AddSystem(System("A", writes: ResourceMask.Of(clock)))
            .AddSystem(System("B", writes: ResourceMask.Of(clock)));

        var error = Assert.Throws<InvalidOperationException>(ambiguous.Build);
        Assert.Contains("clock", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(writers.Build);

        var schema = builder
            .AddSystem(System("Advance", writes: ResourceMask.Of(clock)))
            .AddSystem(System("Forecast", writes: ResourceMask.Of(weather)))
            .AddSystem(System("Display", reads: ResourceMask.Of(clock, weather), after: ["Advance", "Forecast"]))
            .AddSystem(System("Readers", reads: ResourceMask.Of(clock), after: ["Advance"]))
            .Build();

        Assert.Equal(0, schema.System("Advance").Wave);
        Assert.Equal(0, schema.System("Forecast").Wave);
        Assert.Equal(1, schema.System("Display").Wave);
        Assert.Equal(1, schema.System("Readers").Wave); // two readers never conflict with each other
    }

    [Fact]
    public void Systems_read_and_write_declared_resources_and_undeclared_access_throws()
    {
        var builder = Builder();
        var clock = builder.Resource<ColonyClock>();
        var weather = builder.Resource<Weather>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Advance",
                WritesResources = ResourceMask.Of(clock),
                Begin = (in SystemContext c) => c.Resource<ColonyClock>(clock).Hour++,
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Forecast",
                ReadsResources = ResourceMask.Of(clock),
                WritesResources = ResourceMask.Of(weather),
                After = ["Advance"],
                Begin = (in SystemContext c) => c.Resource<Weather>(weather).Temperature = c.ReadResource<ColonyClock>(clock).Hour * 10,
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });

        world.Tick();
        world.Tick();

        Assert.Equal(2, world.Resource<ColonyClock>(clock).Hour);
        Assert.Equal(20, world.Resource<Weather>(weather).Temperature);

        var undeclared = Builder();
        var c2 = undeclared.Resource<ColonyClock>();
        using var bad = undeclared
            .AddSystem(new SystemDefinition { Name = "Sneaky", Begin = (in SystemContext c) => c.ReadResource<ColonyClock>(c2) })
            .Build()
            .CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fault = Assert.Throws<SystemFaultException>(() => bad.Tick());
        Assert.IsType<InvalidOperationException>(fault.InnerException);
        Assert.Contains("ReadsResources", fault.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mask_naming_an_unregistered_resource_fails_at_build()
    {
        var builder = Builder().AddSystem(System("A", reads: ResourceMask.Of(new ResourceType(7))));

        Assert.Throws<InvalidOperationException>(builder.Build);
    }
}
