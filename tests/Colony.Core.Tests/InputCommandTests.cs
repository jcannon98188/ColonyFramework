using Colony.Numerics;

namespace Colony.Core.Tests;

public struct SpawnOrder
{
    public int Count;
}

public struct DepositOrder
{
    public int Amount;
}

/// <summary>TASK-15 AC #1 and #4 (spec section 7.3): Input Commands are schema-registered unmanaged structs, submitted from any thread, applied from the Input Phase in canonical order.</summary>
public sealed class InputCommandTests
{
    [Fact]
    public void Input_commands_are_registered_in_the_schema_and_hashed_into_it()
    {
        var builder = new WorldBuilder().AddComponent<Health>("health");
        var spawn = builder.AddInputCommand<SpawnOrder>("spawn");
        var deposit = builder.AddInputCommand<DepositOrder>("deposit");
        var schema = builder.Build();

        Assert.Equal(0, spawn.Id);
        Assert.Equal(1, deposit.Id);
        Assert.Equal(2, schema.InputCommands.Length);
        Assert.Equal(spawn, schema.InputCommand<SpawnOrder>());
        Assert.Equal(deposit, schema.InputCommand("deposit"));
        Assert.Equal("spawn", schema.DescribeInputCommand(spawn).Name);
        Assert.Equal(4, schema.DescribeInputCommand(spawn).Size);
        Assert.True(schema.HasInputCommands);
        Assert.Equal(WorldBuilder.CommandRejectedQueueName, schema.DescribeResource(schema.CommandRejections).Name);

        var without = new WorldBuilder().AddComponent<Health>("health").Build();
        Assert.NotEqual(without.SchemaHash, schema.SchemaHash);
        Assert.False(without.HasInputCommands);
        Assert.Throws<InvalidOperationException>(() => without.CommandRejections);
    }

    [Fact]
    public void Registration_is_validated()
    {
        var builder = new WorldBuilder();
        builder.AddInputCommand<SpawnOrder>("spawn");

        Assert.Throws<ArgumentException>(() => builder.AddInputCommand<DepositOrder>("spawn"));
        Assert.Throws<ArgumentException>(() => builder.AddInputCommand<SpawnOrder>("again"));
        Assert.Throws<ArgumentException>(() => builder.InputCommand<DepositOrder>());
        var schema = builder.Build();
        Assert.Throws<ArgumentException>(() => schema.InputCommand("nothing"));
        Assert.Throws<InvalidOperationException>(() => builder.AddInputCommand<DepositOrder>("late"));
    }

    [Fact]
    public void The_canonical_list_is_sorted_by_effective_tick_source_then_sequence_whatever_the_submission_order()
    {
        var builder = new WorldBuilder().AddComponent<Health>("health");
        var spawn = builder.AddInputCommand<SpawnOrder>("spawn");
        var schema = builder.Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var a = new InputSource(1);
        var b = new InputSource(2);

        // Sequences are per source and assigned at submission; effective Ticks may be out of order.
        Assert.Equal(0u, world.Inputs.Submit(b, new SpawnOrder { Count = 20 }, effectiveTick: 0));
        Assert.Equal(0u, world.Inputs.Submit(a, new SpawnOrder { Count = 10 }, effectiveTick: 1));
        Assert.Equal(1u, world.Inputs.Submit(a, new SpawnOrder { Count = 11 }, effectiveTick: 0));
        Assert.Equal(2u, world.Inputs.Submit(a, new SpawnOrder { Count = 12 })); // next boundary = Tick 0
        Assert.Equal(1u, world.Inputs.Submit(b, new SpawnOrder { Count = 21 }, effectiveTick: 1));
        Assert.Equal(5, world.Inputs.PendingCount);
        Assert.Equal(0, world.Inputs.NextTick);

        world.Tick();

        var list = world.Inputs.Current;
        Assert.Equal(0, list.Tick);
        Assert.Equal(3, list.Count);
        Assert.Equal((1u, 1u, 11), (list[0].Source.Value, list[0].Sequence, list.Command<SpawnOrder>(0).Count));
        Assert.Equal((1u, 2u, 12), (list[1].Source.Value, list[1].Sequence, list.Command<SpawnOrder>(1).Count));
        Assert.Equal((2u, 0u, 20), (list[2].Source.Value, list[2].Sequence, list.Command<SpawnOrder>(2).Count));
        Assert.Equal(spawn, list[0].Command);
        Assert.Equal(2, world.Inputs.PendingCount);
        Assert.Equal(1, world.Inputs.NextTick);

        world.Tick();

        list = world.Inputs.Current;
        Assert.Equal(1, list.Tick);
        Assert.Equal(2, list.Count);
        Assert.Equal((1u, 0u, 10), (list[0].Source.Value, list[0].Sequence, list.Command<SpawnOrder>(0).Count));
        Assert.Equal((2u, 1u, 21), (list[1].Source.Value, list[1].Sequence, list.Command<SpawnOrder>(1).Count));
        Assert.Equal(0, world.Inputs.PendingCount);

        // A Tick that already started no longer accepts input; a sequence may never go backwards.
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Inputs.Submit(a, new SpawnOrder(), effectiveTick: 1));
        Assert.Throws<ArgumentException>(() => world.Inputs.Submit(a, 1u, new SpawnOrder(), effectiveTick: 5));
    }

    [Fact]
    public void Handlers_apply_commands_through_command_buffers_reject_deterministically_and_draw_keyed_random()
    {
        var schema = BuildHandlerSchema(out var spawn, out var deposit);
        var health = schema.Component<Health>();
        var seen = schema.Resource<RejectionsSeen>();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 2, Seed = 7 });
        var a = new InputSource(5);

        world.Inputs.Submit(a, new SpawnOrder { Count = 3 });
        world.Inputs.Submit(a, new DepositOrder { Amount = -1 }); // rejected: negative
        world.Inputs.Submit(a, new DepositOrder { Amount = 4 });
        world.Tick();

        Assert.Equal(3, world.EntityCount);
        var expected = world.Random.Derive("input").Derive(5UL).Derive(0UL);
        var values = new List<int>();
        foreach (var chunk in world.Query(health))
        {
            foreach (var h in chunk.Column<Health>(health))
            {
                values.Add(h.Value);
            }
        }

        values.Sort();
        var expectedValues = new List<int> { expected.Int32At(0, 1, 100), expected.Int32At(1, 1, 100), expected.Int32At(2, 1, 100) };
        expectedValues.Sort();
        Assert.Equal(expectedValues, values);

        ref readonly var rejections = ref world.Resource<RejectionsSeen>(seen);
        Assert.Equal(1, rejections.Count);
        Assert.Equal(5u, rejections.LastSource);
        Assert.Equal(1u, rejections.LastSequence);
        Assert.Equal(deposit, rejections.LastCommand);
        Assert.Equal(42, rejections.LastReason);
        Assert.Equal(4, rejections.Deposited);
        _ = spawn;
    }

    [Fact]
    public void Concurrent_submission_from_several_sources_yields_the_same_canonical_list_and_hash_as_sequential_submission()
    {
        const int sources = 4;
        const int perSource = 50;
        var schema = BuildHandlerSchema(out var spawn, out var deposit);

        ulong sequentialHash;
        byte[] sequentialList;
        using (var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 2, Seed = 3 }))
        {
            for (int s = 0; s < sources; s++)
            {
                SubmitScript(world, new InputSource((uint)s), perSource);
            }

            world.Tick();
            sequentialList = Snapshot(world.Inputs.Current);
            sequentialHash = world.Hash();
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 2, Seed = 3 });
            var threads = new Thread[sources];
            using var start = new ManualResetEventSlim();
            for (int s = 0; s < sources; s++)
            {
                var source = new InputSource((uint)s);
                threads[s] = new Thread(() =>
                {
                    start.Wait();
                    SubmitScript(world, source, perSource);
                });
                threads[s].Start();
            }

            start.Set();
            foreach (var t in threads)
            {
                t.Join();
            }

            world.Tick();
            Assert.Equal(sequentialList, Snapshot(world.Inputs.Current));
            Assert.Equal(sequentialHash, world.Hash());
        }

        _ = spawn;
        _ = deposit;
    }

    private static void SubmitScript(World world, InputSource source, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if ((i & 3) == 0)
            {
                world.Inputs.Submit(source, new SpawnOrder { Count = 1 + (i % 3) });
            }
            else
            {
                world.Inputs.Submit(source, new DepositOrder { Amount = (i % 5) - 1 });
            }
        }
    }

    private static byte[] Snapshot(in CanonicalInputs list)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < list.Count; i++)
        {
            var record = list[i];
            bytes.AddRange(BitConverter.GetBytes(record.Source.Value));
            bytes.AddRange(BitConverter.GetBytes(record.Sequence));
            bytes.AddRange(BitConverter.GetBytes(record.Command.Id));
            bytes.AddRange(list.Payload(i).ToArray());
        }

        return [.. bytes];
    }

    public struct RejectionsSeen
    {
        public int Count;
        public uint LastSource;
        public uint LastSequence;
        public InputCommandType LastCommand;
        public int LastReason;
        public int Deposited;
    }

    private static WorldSchema BuildHandlerSchema(out InputCommandType spawn, out InputCommandType deposit)
    {
        var builder = new WorldBuilder().AddComponent<Health>("health").AddResource<RejectionsSeen>("seen");
        var spawnType = spawn = builder.AddInputCommand<SpawnOrder>("spawn");
        var depositType = deposit = builder.AddInputCommand<DepositOrder>("deposit");
        var health = builder.Component<Health>();
        var seen = builder.Resource<RejectionsSeen>();
        builder.AddSystem(new SystemDefinition
        {
            Name = "handle-spawn",
            Phase = Phase.Input,
            Begin = (in SystemContext context) =>
            {
                foreach (var entry in context.Inputs<SpawnOrder>(spawnType))
                {
                    for (int i = 0; i < entry.Command.Count; i++)
                    {
                        var id = context.Commands.Create(ComponentMask.Of(health));
                        context.Commands.Add(id, health, new Health { Value = entry.Random.Int32At((ulong)i, 1, 100) });
                    }
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "handle-deposit",
            Phase = Phase.Input,
            WritesResources = ResourceMask.Of(seen),
            Begin = (in SystemContext context) =>
            {
                foreach (var entry in context.Inputs<DepositOrder>(depositType))
                {
                    if (entry.Command.Amount < 0)
                    {
                        context.Reject(in entry, reason: 42);
                    }
                    else
                    {
                        context.Resource<RejectionsSeen>(seen).Deposited += entry.Command.Amount;
                    }
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "count-rejections",
            Phase = Phase.Begin,
            ReadsResources = ResourceMask.Of(builder.CommandRejections),
            WritesResources = ResourceMask.Of(seen),
            Begin = (in SystemContext context) =>
            {
                foreach (var rejected in context.Events<CommandRejected>(context.World.Schema.CommandRejections))
                {
                    ref var state = ref context.Resource<RejectionsSeen>(seen);
                    state.Count++;
                    state.LastSource = rejected.Source.Value;
                    state.LastSequence = rejected.Sequence;
                    state.LastCommand = rejected.Command;
                    state.LastReason = rejected.Reason;
                }
            },
        });
        return builder.Build();
    }
}
