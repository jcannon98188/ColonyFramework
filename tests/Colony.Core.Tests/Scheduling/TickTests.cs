namespace Colony.Core.Tests.Scheduling;

public sealed class TickTests
{
    [Fact]
    public void A_tick_runs_every_phase_in_order_and_merges_command_buffers_at_each_boundary()
    {
        var log = new List<string>();
        var builder = new WorldBuilder().AddComponent<Health>("health");
        var health = new ComponentType(0);
        var weather = builder.AddPhase("Weather", after: Phase.Begin);
        int seenInWeather = -1;
        int seenInSimulate = -1;
        int seenInEnd = -1;

        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Spawn",
                Phase = Phase.Begin,
                Begin = (in SystemContext context) =>
                {
                    log.Add("Spawn");
                    var e = context.Commands.Create(ComponentMask.Of(health));
                    context.Commands.Add(e, health, new Health { Value = 7 });
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Observe",
                Phase = Phase.Simulate,
                Reads = ComponentMask.Of(health),
                Begin = (in SystemContext context) =>
                {
                    log.Add("Observe");
                    seenInSimulate = context.World.Query(health).EntityCount();
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Stream",
                Phase = Phase.Streaming,
                Begin = (in SystemContext context) => log.Add("Stream"),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Finish",
                Phase = Phase.End,
                Reads = ComponentMask.Of(health),
                Begin = (in SystemContext context) =>
                {
                    log.Add("Finish");
                    seenInEnd = context.World.Query(health).EntityCount();
                    foreach (var chunk in context.World.Query(health))
                    {
                        foreach (var entity in chunk.Entities)
                        {
                            context.Commands.Destroy(entity);
                        }
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Weather",
                Phase = weather,
                Begin = (in SystemContext context) =>
                {
                    log.Add("Weather");
                    seenInWeather = context.World.Query(health).EntityCount();
                },
            })
            .Build();

        Assert.Equal(health, schema.Component<Health>());
        using var world = schema.CreateWorld();
        Assert.Equal(0, world.CurrentTick);

        Assert.True(world.Tick());

        Assert.Equal(["Stream", "Spawn", "Weather", "Observe", "Finish"], log);
        Assert.Equal(1, seenInWeather);
        Assert.Equal(1, seenInSimulate);
        Assert.Equal(1, seenInEnd);
        Assert.Equal(0, world.EntityCount);
        Assert.Equal(1, world.CurrentTick);
        Assert.Equal(1, world.Counters.Creates);
        Assert.Equal(1, world.Counters.Destroys);
    }
}
