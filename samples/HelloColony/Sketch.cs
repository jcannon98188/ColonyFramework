#pragma warning disable IDE0055, IDE0011 // keep the spec's one-line layout (braces, formatting) so the doc test can diff it line by line
using Colony.Fields;
using Colony.Numerics;
using Colony.Persistence;

namespace Colony.Samples.Hello;

// The hello-colony sketch of spec section 11.1 as it compiles today. The lines between the <sketch> markers
// are diffed against the spec's code block by HelloColony.Tests/SpecSketchDocTests; the documented
// deviations there are the spec follow-up (NamedPhase in attributes, AddSystems(builder), Fields registered
// by name on the builder, the seed in WorldOptions, Entities created through a command buffer).

// <sketch>
[Component] public partial struct Hunger { public Fixed32 Value; }
[Component] public partial struct Food   { public int Units; }

public struct Heat { public Fixed32 Value; }

public static partial class ColonySystems
{
    [System(NamedPhase.Simulate, Every = 1)]
    public static void Starve(ref Hunger hunger)
        => hunger.Value += Fixed32.FromRatio(1, 600);
}
// </sketch>

/// <summary>The sketch's statements, verbatim where the API allows. Writes <c>colony.save</c> into the current directory, as the spec does.</summary>
public static class Sketch
{
    public static ulong Run()
    {
        // <sketch>
        var builder = new WorldBuilder()
            .AddComponent<Hunger>().AddComponent<Food>();
        builder.AddFields(new WorldSize(64, 64, 1)).AddField<Heat>("heat");
        ColonySystems.AddSystems(builder);
        var schema = builder.Build();                                  // validates, freezes the schema

        using var world = schema.CreateWorld(new WorldOptions { Seed = 12345 });
        var commands = world.CreateCommandBuffer();
        for (int i = 0; i < 100; i++) commands.Create(ComponentMask.Of(schema.Component<Hunger>()));
        world.Playback(commands);

        for (int tick = 0; tick < 1000; tick++) world.Tick();

        world.Save("colony.save", SaveOptions.Default);
        Console.WriteLine(world.Hash());               // identical on every machine
        return world.Hash();
        // </sketch>
    }
}
