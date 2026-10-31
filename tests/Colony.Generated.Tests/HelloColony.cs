using Colony.Numerics;

namespace Colony.Generated.Tests.Hello;

// The hello-colony sketch of spec section 11.1 without Fields and Save (later tickets). Components are attributed
// partial structs and the System is an attributed static method; everything else is generated.

[Component]
public partial struct Hunger
{
    public Fixed32 Value;
}

[Component]
public partial struct Food
{
    public int Units;
}

public static partial class ColonySystems
{
    [System(NamedPhase.Simulate, Every = 1)]
    public static void Starve(ref Hunger hunger) => hunger.Value += Fixed32.FromRatio(1, 600);
}

public static class HelloColony
{
    public const int Colonists = 100;
    public const int Ticks = 1000;
    public const ulong Seed = 12345;

    public static WorldSchema BuildSchema()
    {
        var builder = new WorldBuilder()
            .AddComponent<Hunger>()
            .AddComponent<Food>()
            .DebugValidation(false);
        ColonySystems.AddSystems(builder);
        return builder.Build();
    }

    public static World CreateWorld(WorldSchema schema, int workers)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var world = schema.CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = workers });
        var buffer = world.CreateCommandBuffer();
        var hunger = ComponentMask.Of(schema.Component<Hunger>());
        for (int i = 0; i < Colonists; i++)
        {
            buffer.Create(hunger);
        }

        world.Playback(buffer);
        world.ResetScratch();
        return world;
    }

    /// <summary>Builds the World, runs 1000 Ticks and returns the World hash, like the sample's final line.</summary>
    public static ulong Run(int workers)
    {
        using var world = CreateWorld(BuildSchema(), workers);
        for (int tick = 0; tick < Ticks; tick++)
        {
            world.Tick();
        }

        return world.Hash();
    }
}
