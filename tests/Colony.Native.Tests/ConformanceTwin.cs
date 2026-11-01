using System.Runtime.InteropServices;
using Colony.Numerics;
using Colony.Plugins;
using Colony.Testing;

namespace Colony.Native.Tests;

/// <summary>
/// The C# equivalent of the Native Plugin in Conformance/conformance.c (AC #4): the same names, layouts,
/// registration order, Systems and arithmetic, so a World driven by either Plugin hashes identically.
/// </summary>
public sealed class ConformanceTwinPlugin : IPlugin
{
    public const string Json = """
        { "native.species": {
            "hare":     { "speed": 7, "mass": 1.5,   "label": "Hare" },
            "tortoise": { "speed": 2, "mass": 40.25, "label": "Tortoise" }
        } }
        """;

    public PluginManifest Manifest { get; } = new()
    {
        Id = "conformance.native",
        Version = "1.0.0",
        Deterministic = true,
    };

    public void Register(PluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.AddComponent<Position>("native.position");
        registration.AddComponent<Velocity>("native.velocity");
        registration.AddResource<Config>("native.config");
        registration.AddEvent<Moved>("native.moved");
        registration.AddEvent<Summary>("native.summary", EventOptions.Persistent);
        var spawnType = registration.AddInputCommand<Spawn>("native.spawn");
        var species = registration.Definitions.AddType<Species>("native.species", layout => layout
            .Field("speed", (ref Species s) => ref s.Speed)
            .Field("mass", (ref Species s) => ref s.Mass)
            .Field("label", (ref Species s) => ref s.Label));
        registration.Definitions.AddSource(JsonDefinitionSource.FromText(Json, "conformance.c"));

        var position = registration.Component("native.position");
        var velocity = registration.Component("native.velocity");
        var moved = registration.Resource("native.moved");
        var summary = registration.Resource("native.summary");

        registration.AddSystem(new SystemDefinition
        {
            Name = "native.spawn",
            Phase = Phase.Input,
            Begin = (in SystemContext context) =>
            {
                foreach (var entry in context.Inputs<Spawn>(spawnType))
                {
                    var command = entry.Command;
                    if (command.Count > 100)
                    {
                        context.Reject(in entry, 1);
                        continue;
                    }

                    if (command.Count < 0 || species.Count == 0)
                    {
                        context.Reject(in entry, 2);
                        continue;
                    }

                    ref readonly var kind = ref species[command.Species % species.Count];
                    for (int n = 0; n < command.Count; n++)
                    {
                        var entity = context.Commands.Create();
                        var p = new Position { X = entry.Random.Int32At((ulong)(2 * n), -50, 50), Y = entry.Random.Int32At((ulong)(2 * n + 1), -50, 50) };
                        var v = new Velocity { Dx = kind.Speed, Dy = kind.Speed / 2 };
                        context.Commands.Add(entity, position, in p);
                        context.Commands.Add(entity, velocity, in v);
                    }
                }
            },
        });

        registration.AddSystem(new SystemDefinition
        {
            Name = "native.move",
            Reads = ComponentMask.Of(position, velocity),
            WritesResources = ResourceMask.Of(moved),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var positions = chunk.Column<Position>(position);
                var velocities = chunk.Column<Velocity>(velocity);
                var entities = chunk.Entities;
                for (int i = 0; i < chunk.Count; i++)
                {
                    var entity = entities[i];
                    uint draw = context.Random.Derive(entity.Value).UInt32At(0);
                    var p = positions[i];
                    p.X += velocities[i].Dx + ((int)(draw % 3u) - 1);
                    p.Y += velocities[i].Dy;
                    context.Commands.Add(entity, position, in p);
                    context.Emit(moved, new Moved { Entity = entity.Value });
                    if (p.X > 120 || p.X < -120)
                    {
                        context.Commands.Remove(entity, velocity);
                    }

                    if (p.Y > 90 || p.Y < -90)
                    {
                        context.Commands.Destroy(entity);
                    }
                }
            },
        });

        registration.AddSystem(new SystemDefinition
        {
            Name = "native.summarize",
            Phase = Phase.End,
            ReadsResources = ResourceMask.Of(moved),
            WritesResources = ResourceMask.Of(summary),
            Begin = (in SystemContext context) =>
            {
                int count = context.Events<Moved>(moved).Length;
                context.Emit(summary, new Summary { Count = count, Tick = (int)context.Tick });
            },
        });
    }

    public struct Position
    {
        public int X, Y;
    }

    public struct Velocity
    {
        public int Dx, Dy;
    }

    public struct Config
    {
        public int Bound, Pad;
    }

    public struct Moved
    {
        public ulong Entity;
    }

    public struct Summary
    {
        public int Count, Tick;
    }

    public struct Spawn
    {
        public int Count, Species;
    }

    public struct Species
    {
        public int Speed;
        public Fixed32 Mass;
        public StringId Label;
    }
}

/// <summary>Drives a World built from one Plugin (native or twin) with the same Input Commands the C host submits.</summary>
public sealed class ConformanceScenario : Scenario
{
    private readonly Func<IPlugin> _plugin;

    public ConformanceScenario(string name, Func<IPlugin> plugin)
    {
        Name = name;
        _plugin = plugin;
        Seed = 0x5EED;
    }

    public override string Name { get; }

    public override void Compose(WorldBuilder builder)
    {
        var host = new PluginHost();
        host.Add(_plugin());
        host.Register(builder);
    }

    public override void Populate(World world)
    {
    }

    public override void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        long tick = world.CurrentTick;
        // By name and bytes: the native schema registered the Input Command by layout, so no C# type is bound to it.
        var spawn = world.Schema.InputCommand("native.spawn");
        if (tick < 3)
        {
            var command = new ConformanceTwinPlugin.Spawn { Count = 5 + (int)tick, Species = (int)tick };
            world.Inputs.Submit(new InputSource(1), (uint)(tick + 1), spawn, MemoryMarshal.AsBytes(new ReadOnlySpan<ConformanceTwinPlugin.Spawn>(in command)), -1);
        }

        if (tick == 1)
        {
            var rejected = new ConformanceTwinPlugin.Spawn { Count = 500, Species = 0 };
            world.Inputs.Submit(new InputSource(2), 1, spawn, MemoryMarshal.AsBytes(new ReadOnlySpan<ConformanceTwinPlugin.Spawn>(in rejected)), -1);
        }

        world.Tick();
    }
}
