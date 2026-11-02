namespace Colony.Generated.Tests.ResourceAccess;

[Component]
public partial struct Fuel
{
    public int Units;
}

/// <summary>A value Resource: Fuel burnt per Tick.</summary>
public struct BurnRate
{
    public int Units;
}

/// <summary>A value Resource the serial System writes.</summary>
public struct BurnTotal
{
    public long Units;
}

/// <summary>An event queue: one event per Entity that ran dry.</summary>
public struct RanDry
{
    public long Entity;
}

/// <summary>
/// TASK-42 AC #5: generated Systems declare Resource access ([ReadsResources]/[WritesResources]) and opt out of
/// enable-bit chunk skipping (<see cref="SystemAttribute.IgnoreEnableBits"/>) from attributes, like hand-written
/// <see cref="SystemDefinition"/>s do.
/// </summary>
public static partial class ResourceSystems
{
    /// <summary>Burns fuel at the Resource's rate and emits an event per Entity that ran dry.</summary>
    [System(NamedPhase.Simulate)]
    [ReadsResources(typeof(BurnRate))]
    [WritesResources(typeof(RanDry))]
    public static void Burn(in SystemContext context, EntityId entity, ref Fuel fuel)
    {
        int rate = context.ReadResource<BurnRate>(context.World.Schema.Resource<BurnRate>()).Units;
        if (fuel.Units > 0 && fuel.Units <= rate)
        {
            context.Emit(context.World.Schema.Resource<RanDry>(), new RanDry { Entity = (long)entity.Value });
        }

        fuel.Units = fuel.Units > rate ? fuel.Units - rate : 0;
    }

    /// <summary>Serial: totals the events of the previous Phase into a value Resource.</summary>
    [System(NamedPhase.Late)]
    [ReadsResources(typeof(RanDry))]
    [WritesResources(typeof(BurnTotal))]
    public static void Total(in SystemContext context)
    {
        var schema = context.World.Schema;
        context.Resource<BurnTotal>(schema.Resource<BurnTotal>()).Units += context.Events<RanDry>(schema.Resource<RanDry>()).Length;
    }

    /// <summary>Refuels Entities whose Fuel is disabled: it must see chunks where every row has Fuel disabled.</summary>
    [System(NamedPhase.Begin, IgnoreEnableBits = true)]
    [Writes(typeof(Fuel))]
    public static void Refuel(in SystemContext context, in ChunkView chunk)
    {
        var fuel = context.World.Schema.Component<Fuel>();
        var column = chunk.Column<Fuel>(fuel);
        for (int row = 0; row < chunk.Count; row++)
        {
            if (!chunk.IsEnabled(fuel, row))
            {
                chunk.SetEnabled(fuel, row, enabled: true);
                column[row].Units = 10;
            }
        }
    }
}

public sealed class GeneratedResourceAccessTests
{
    private static WorldSchema Schema()
    {
        var builder = new WorldBuilder();
        builder.AddComponent<Fuel>();
        builder.AddResource<BurnRate>("burn-rate");
        builder.AddResource<BurnTotal>("burn-total");
        builder.AddEvent<RanDry>("ran-dry");
        ResourceSystems.AddSystems(builder);
        return builder.Build();
    }

    [Fact]
    public void Resource_attributes_become_the_definitions_resource_masks()
    {
        var builder = new WorldBuilder();
        builder.AddComponent<Fuel>();
        builder.AddResource<BurnRate>("burn-rate");
        builder.AddResource<BurnTotal>("burn-total");
        builder.AddEvent<RanDry>("ran-dry");

        var burn = ResourceSystems.BurnSystem(builder);
        var total = ResourceSystems.TotalSystem(builder);
        var refuel = ResourceSystems.RefuelSystem(builder);

        Assert.Equal(ResourceMask.Of(builder.Resource<BurnRate>()), burn.ReadsResources);
        Assert.Equal(ResourceMask.Of(builder.Resource<RanDry>()), burn.WritesResources);
        Assert.Equal(ResourceMask.Of(builder.Resource<RanDry>()), total.ReadsResources);
        Assert.Equal(ResourceMask.Of(builder.Resource<BurnTotal>()), total.WritesResources);
        Assert.False(burn.IgnoreEnableBits);
        Assert.True(refuel.IgnoreEnableBits);
        Assert.Equal(ResourceMask.Empty, refuel.ReadsResources);
    }

    [Fact]
    public void Generated_systems_read_write_and_emit_their_declared_resources_and_see_disabled_chunks()
    {
        using var world = Schema().CreateWorld(new WorldOptions { WorkerThreads = 2 });
        var fuel = world.Schema.Component<Fuel>();
        world.Resource<BurnRate>(world.Schema.Resource<BurnRate>()).Units = 3;
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 4; i++)
        {
            var entity = buffer.Create();
            buffer.Add(entity, fuel, new Fuel { Units = 5 + i });
        }

        world.Playback(buffer);
        Assert.True(world.Tick()); // 5..8 -> 2..5, nobody dry
        Assert.True(world.Tick()); // 2..5 -> 0..2: units 2 and 3 ran dry
        Assert.Equal(2, world.Resource<BurnTotal>(world.Schema.Resource<BurnTotal>()).Units);

        // Disable Fuel on every Entity: the chunk now has no enabled Fuel row, and only IgnoreEnableBits visits it.
        foreach (var chunk in world.Query(fuel))
        {
            for (int row = 0; row < chunk.Count; row++)
            {
                world.SetEnabled(chunk.Entities[row], fuel, enabled: false);
            }
        }

        Assert.True(world.Tick());
        foreach (var chunk in world.Query(fuel))
        {
            for (int row = 0; row < chunk.Count; row++)
            {
                Assert.True(chunk.IsEnabled(fuel, row));
                Assert.Equal(7, chunk.Column<Fuel>(fuel)[row].Units); // refuelled to 10 in Begin, burnt 3 in Simulate
            }
        }
    }
}
