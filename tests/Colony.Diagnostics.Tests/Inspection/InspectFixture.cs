using System.Collections.Immutable;
using Colony.Diagnostics.Inspection;
using Colony.Fields;
using Colony.Persistence;

namespace Colony.Diagnostics.Tests.Inspection;

/// <summary>A Component with a recorded field layout, as the source generator would write it.</summary>
public struct Vitals : IComponent<Vitals>
{
    public int Health;
    public float Hunger;

    public static ComponentLayout Layout { get; } = new(
        "vitals",
        ComponentOptions.None,
        [new ComponentField("Health", "int", 0, 4), new ComponentField("Hunger", "float", 4, 4)]);
}

public struct Tally
{
    public long Ticks;
}

public struct Ping
{
    public int Value;
}

/// <summary>
/// A small inspectable World: Vitals + TilePos Entities, a Tally Resource bumped every Tick, a Ping event queue,
/// one Field, the spatial index, debug commands and an inspector. The host side of the server tests.
/// </summary>
internal sealed class InspectFixture : IDisposable
{
    public const string Token = "test-token-1234";

    private InspectFixture(WorldSchema schema, World world, TickGate gate, ReplayRecorder recorder, InspectionServer server)
    {
        Schema = schema;
        World = world;
        Gate = gate;
        Recorder = recorder;
        Server = server;
    }

    public WorldSchema Schema { get; }

    public World World { get; }

    public TickGate Gate { get; }

    public ReplayRecorder Recorder { get; }

    public InspectionServer Server { get; }

    public static WorldSchema BuildSchema(bool debugCommands = true)
    {
        var builder = new WorldBuilder()
            .AddComponent<Vitals>()
            .AddComponent<TilePos>("tile.pos")
            .AddResource<Tally>("tally")
            .AddEvent<Ping>("ping")
            .DebugValidation(false);
        var tally = builder.Resource<Tally>();
        var ping = builder.Resource<Ping>();
        var fields = builder.AddFields(new WorldSize(32, 32, 1));
        fields.AddField<short>("temperature", 20);
        builder.AddSpatialIndex(fields, builder.Component<TilePos>());
        builder.AddSystem(new SystemDefinition
        {
            Name = "Count",
            WritesResources = ResourceMask.Of(tally, ping),
            Begin = (in SystemContext context) =>
            {
                ref var t = ref context.Resource<Tally>(tally);
                t.Ticks++;
                context.Emit(ping, new Ping { Value = (int)t.Ticks });
            },
        });
        builder.AddInspector("tally", (world, output) =>
        {
            output.Add("ticks", world.Resource<Tally>(world.Schema.Resource<Tally>()).Ticks);
            output.Add("name", "fixture");
        });
        if (debugCommands)
        {
            builder.AddDebugCommands();
        }

        return builder.Build();
    }

    public static InspectFixture Create(bool allowMutation = false, bool debugCommands = true, int entities = 3)
    {
        var schema = BuildSchema(debugCommands);
        var gate = new TickGate();
        var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1, Seed = 7, TickGate = gate });
        var vitals = schema.Component<Vitals>();
        var tile = schema.Component<TilePos>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < entities; i++)
        {
            var e = buffer.Create(ComponentMask.Of(vitals, tile));
            buffer.Add(e, vitals, new Vitals { Health = 10 + i, Hunger = 0.5f * i });
            buffer.Add(e, tile, new TilePos(2 + i, 3, 0));
        }

        world.Playback(buffer);
        world.ResetScratch();
        var recorder = new ReplayRecorder(world, new ReplayOptions { CheckpointEvery = 1 });
        var server = new InspectionServer(world, new InspectionServerOptions { Token = Token, AllowMutation = allowMutation, Replay = recorder });
        return new InspectFixture(schema, world, gate, recorder, server);
    }

    /// <summary>Runs <paramref name="ticks"/> recorded Ticks on the calling thread (the gate must be open or have steps).</summary>
    public void Run(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            Assert.True(Recorder.Tick());
        }
    }

    /// <summary>
    /// The host loop on a background thread: pump requests at every Tick boundary, Tick through the recorder when
    /// the gate lets it, block briefly on the server when held. Stops on <c>shutdown</c> or <paramref name="ct"/>.
    /// </summary>
    public Thread StartHostLoop(CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            while (!Server.ShutdownRequested && !ct.IsCancellationRequested)
            {
                Server.Pump();
                if (!Recorder.Tick())
                {
                    Server.WaitForRequest(TimeSpan.FromMilliseconds(20));
                }
            }

            Server.Pump();
        })
        { IsBackground = true, Name = "fixture host" };
        thread.Start();
        return thread;
    }

    public void Dispose()
    {
        Server.Dispose();
        World.Dispose();
    }
}
