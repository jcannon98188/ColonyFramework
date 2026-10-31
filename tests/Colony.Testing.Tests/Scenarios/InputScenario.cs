using Colony.Numerics;
using Colony.Testing;

namespace Colony.Testing.Tests.Scenarios;

/// <summary>
/// TASK-15's input oracle (spec section 7.3): a colony of workers driven entirely by Input Commands from
/// several sources. Every Tick a seeded script submits a batch of commands per source (spawn workers, deposit
/// stock, dismiss workers; some deliberately invalid so handlers reject them), either sequentially or from
/// one thread per source, then the World Ticks. Handlers draw randomness from the command's keyed stream;
/// a Simulate System burns worker energy and destroys the exhausted. The World hash must not depend on the
/// submission interleaving, the thread count, or on whether the commands come live or from a replay log.
/// </summary>
public sealed class InputScenario : Scenario
{
    public const ulong DefaultSeed = 0x1A5E_C0DE_0000_0015UL;

    public struct Worker
    {
        public int Energy;
    }

    public struct Stock
    {
        public long Wood;
        public long Stone;
        public int Workers;
        public int Rejections;
        public int Dismissed;
    }

    public struct Spawn
    {
        public int Count;
    }

    public struct Deposit
    {
        public int Wood;
        public int Stone;
    }

    public struct Dismiss
    {
        public int Count;
    }

    public const int RejectNegativeDeposit = 1;
    public const int RejectTooMany = 2;

    public override string Name => "inputs";

    public override ulong Seed { get; init; } = DefaultSeed;

    /// <summary>How many Input sources (players) submit every Tick.</summary>
    public int Sources { get; init; } = 4;

    /// <summary>Commands each source submits per Tick.</summary>
    public int CommandsPerTick { get; init; } = 6;

    /// <summary>True submits every source from its own thread, concurrently; false submits sequentially in source order.</summary>
    public bool Concurrent { get; init; }

    public override int InitialEntityCapacity => 4096;

    public override void Compose(WorldBuilder builder) => ComposeStatic(builder);

    public override void Populate(World world)
    {
    }

    public override void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SubmitTick(world, world.CurrentTick, Seed, Sources, CommandsPerTick, Concurrent);
        world.Tick();
    }

    /// <summary>Registers the Components, Input Commands and Systems of the scenario on any builder.</summary>
    public static void ComposeStatic(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddComponent<Worker>("worker");
        builder.AddResource<Stock>("stock");
        var spawn = builder.AddInputCommand<Spawn>("spawn");
        var deposit = builder.AddInputCommand<Deposit>("deposit");
        var dismiss = builder.AddInputCommand<Dismiss>("dismiss");
        var worker = builder.Component<Worker>();
        var stock = builder.Resource<Stock>();

        builder.AddSystem(new SystemDefinition
        {
            Name = "inputs.spawn",
            Phase = Phase.Input,
            WritesResources = ResourceMask.Of(stock),
            Begin = (in SystemContext context) =>
            {
                foreach (var entry in context.Inputs<Spawn>(spawn))
                {
                    if (entry.Command.Count > 8)
                    {
                        context.Reject(in entry, RejectTooMany);
                        continue;
                    }

                    for (int i = 0; i < entry.Command.Count; i++)
                    {
                        var id = context.Commands.Create(ComponentMask.Of(worker));
                        context.Commands.Add(id, worker, new Worker { Energy = entry.Random.Int32At((ulong)i, 3, 40) });
                    }

                    context.Resource<Stock>(stock).Workers += entry.Command.Count;
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "inputs.deposit",
            Phase = Phase.Input,
            WritesResources = ResourceMask.Of(stock),
            After = ["inputs.spawn"],
            Begin = (in SystemContext context) =>
            {
                foreach (var entry in context.Inputs<Deposit>(deposit))
                {
                    if (entry.Command.Wood < 0 || entry.Command.Stone < 0)
                    {
                        context.Reject(in entry, RejectNegativeDeposit);
                        continue;
                    }

                    ref var s = ref context.Resource<Stock>(stock);
                    s.Wood += entry.Command.Wood;
                    s.Stone += entry.Command.Stone;
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "inputs.dismiss",
            Phase = Phase.Input,
            WritesResources = ResourceMask.Of(stock),
            After = ["inputs.deposit"],
            Begin = (in SystemContext context) =>
            {
                foreach (var entry in context.Inputs<Dismiss>(dismiss))
                {
                    int left = entry.Command.Count;
                    foreach (var chunk in context.World.Query(worker))
                    {
                        var entities = chunk.Entities;
                        for (int row = 0; row < entities.Length && left > 0; row++, left--)
                        {
                            context.Commands.Destroy(entities[row]);
                        }
                    }

                    context.Resource<Stock>(stock).Dismissed += entry.Command.Count - left;
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "inputs.rejections",
            Phase = Phase.Begin,
            ReadsResources = ResourceMask.Of(builder.CommandRejections),
            WritesResources = ResourceMask.Of(stock),
            Begin = (in SystemContext context) =>
            {
                context.Resource<Stock>(stock).Rejections += context.Events<CommandRejected>(context.World.Schema.CommandRejections).Length;
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "inputs.work",
            Phase = Phase.Simulate,
            Writes = ComponentMask.Of(worker),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var energy = chunk.Column<Worker>(worker);
                var entities = chunk.Entities;
                for (int row = 0; row < energy.Length; row++)
                {
                    energy[row].Energy -= 1 + context.Random.Int32At(entities[row].Value, 0, 3);
                    if (energy[row].Energy <= 0)
                    {
                        context.Commands.Destroy(entities[row]);
                    }
                }
            },
        });
    }

    /// <summary>The scripted commands of one source for one Tick: a deterministic function of (seed, tick, source), independent of any World.</summary>
    public static void SubmitSource(World world, long tick, ulong seed, uint source, int commandsPerTick, long effectiveTick = -1)
    {
        ArgumentNullException.ThrowIfNull(world);
        var script = new RandomSource(seed, 0x5343_5250UL).Derive((ulong)tick).Derive(source);
        var id = new InputSource(source);
        for (int i = 0; i < commandsPerTick; i++)
        {
            ulong at = (ulong)i * 4;
            switch (script.Int32At(at, 0, 10))
            {
                case 0:
                case 1:
                    world.Inputs.Submit(id, new Spawn { Count = script.Int32At(at + 1, 1, 11) }, effectiveTick);
                    break;
                case 2:
                    world.Inputs.Submit(id, new Dismiss { Count = script.Int32At(at + 1, 1, 4) }, effectiveTick);
                    break;
                default:
                    world.Inputs.Submit(id, new Deposit { Wood = script.Int32At(at + 1, -2, 20), Stone = script.Int32At(at + 2, 0, 9) }, effectiveTick);
                    break;
            }
        }
    }

    /// <summary>Submits every source's commands for <paramref name="tick"/>, sequentially or from one thread per source.</summary>
    public static void SubmitTick(World world, long tick, ulong seed, int sources, int commandsPerTick, bool concurrent)
    {
        if (!concurrent)
        {
            for (int s = 0; s < sources; s++)
            {
                SubmitSource(world, tick, seed, (uint)s, commandsPerTick);
            }

            return;
        }

        var threads = new Thread[sources];
        using var start = new ManualResetEventSlim();
        for (int s = 0; s < sources; s++)
        {
            uint source = (uint)s;
            threads[s] = new Thread(() =>
            {
                start.Wait();
                SubmitSource(world, tick, seed, source, commandsPerTick);
            });
            threads[s].Start();
        }

        start.Set();
        foreach (var thread in threads)
        {
            thread.Join();
        }
    }
}
