namespace Colony.Core.Tests;

/// <summary>TASK-35: per-source Input Command permissions, declared at build time and applied at the canonical ordering step.</summary>
public sealed class InputPermissionTests
{
    private static readonly InputSource Host = new(1);
    private static readonly InputSource Guest = new(2);
    private static readonly InputSource Spectator = new(3);

    [Fact]
    public void Without_declarations_every_source_may_issue_every_command_and_the_schema_hash_is_unchanged()
    {
        var plain = PermissionWorld.Build(_ => { });
        Assert.True(plain.Schema.InputPermissions.IsUnrestricted);
        Assert.Equal(InputAccess.Allow, plain.Schema.InputPermissions.DefaultAccess);
        Assert.True(plain.Schema.InputPermissions.IsAllowed(Guest, plain.Spawn));

        var explicitAllow = PermissionWorld.Build(b => b.Builder.DefaultInputAccess(InputAccess.Allow));
        Assert.Equal(plain.Schema.SchemaHash, explicitAllow.Schema.SchemaHash);

        using var world = plain.Schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        world.Inputs.Submit(Guest, new SpawnOrder { Count = 1 });
        world.Tick();
        Assert.Equal(1, world.Resource<PermissionLedger>(plain.Ledger).Spawned);
        Assert.Equal(0, world.Resource<PermissionLedger>(plain.Ledger).Rejected);
    }

    [Fact]
    public void A_denied_pair_is_rejected_at_the_tick_boundary_handlers_never_see_it_and_it_stays_in_the_canonical_list()
    {
        var setup = PermissionWorld.Build(b => b.Builder.DenyInput(Guest, b.Spawn));
        using var world = setup.Schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });

        world.Inputs.Submit(Host, new SpawnOrder { Count = 2 });
        world.Inputs.Submit(Guest, new SpawnOrder { Count = 5 });
        world.Inputs.Submit(Guest, new DepositOrder { Amount = 7 });
        world.Tick();

        ref readonly var ledger = ref world.Resource<PermissionLedger>(setup.Ledger);
        Assert.Equal(2, ledger.Spawned);
        Assert.Equal(7, ledger.Deposited);
        Assert.Equal(1, ledger.Rejected);
        Assert.Equal(Guest.Value, ledger.LastSource);
        Assert.Equal(0u, ledger.LastSequence);
        Assert.Equal(setup.Spawn.Id, ledger.LastCommand);
        Assert.Equal(CommandRejected.PermissionDenied, ledger.LastReason);

        var list = world.Inputs.Current;
        Assert.Equal(3, list.Count);
        Assert.False(list.IsDenied(0));
        Assert.True(list.IsDenied(1));
        Assert.False(list.IsDenied(2));
        Assert.Equal(Guest, list[1].Source);
    }

    [Fact]
    public void Deny_by_default_admits_only_granted_pairs()
    {
        var setup = PermissionWorld.Build(b =>
        {
            b.Builder.DefaultInputAccess(InputAccess.Deny);
            b.Builder.GrantInput(Host, b.Spawn);
            b.Builder.GrantInput(Guest, b.Deposit);
        });
        var permissions = setup.Schema.InputPermissions;
        Assert.False(permissions.IsUnrestricted);
        Assert.True(permissions.IsAllowed(Host, setup.Spawn));
        Assert.False(permissions.IsAllowed(Host, setup.Deposit));
        Assert.True(permissions.IsAllowed(Guest, setup.Deposit));
        Assert.False(permissions.IsAllowed(Guest, setup.Spawn));
        Assert.False(permissions.IsAllowed(Spectator, setup.Spawn));

        using var world = setup.Schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        world.Inputs.Submit(Host, new SpawnOrder { Count = 1 });
        world.Inputs.Submit(Host, new DepositOrder { Amount = 100 });
        world.Inputs.Submit(Guest, new DepositOrder { Amount = 3 });
        world.Inputs.Submit(Spectator, new SpawnOrder { Count = 9 });
        world.Tick();

        ref readonly var ledger = ref world.Resource<PermissionLedger>(setup.Ledger);
        Assert.Equal(1, ledger.Spawned);
        Assert.Equal(3, ledger.Deposited);
        Assert.Equal(2, ledger.Rejected);
    }

    [Fact]
    public void A_pair_rule_beats_the_source_and_command_defaults_and_between_defaults_deny_wins()
    {
        var setup = PermissionWorld.Build(b =>
        {
            b.Builder.SetInputAccess(Spectator, InputAccess.Deny);
            b.Builder.SetInputAccess(b.Deposit, InputAccess.Deny);
            b.Builder.SetInputAccess(Host, InputAccess.Allow);
            b.Builder.GrantInput(Spectator, b.Spawn);
        });
        var p = setup.Schema.InputPermissions;

        Assert.True(p.IsAllowed(Spectator, setup.Spawn));     // pair grant beats the source default
        Assert.False(p.IsAllowed(Spectator, setup.Deposit));  // source deny
        Assert.False(p.IsAllowed(Host, setup.Deposit));       // command deny beats source allow
        Assert.True(p.IsAllowed(Host, setup.Spawn));
        Assert.True(p.IsAllowed(Guest, setup.Spawn));         // global default
        Assert.False(p.IsAllowed(Guest, setup.Deposit));
    }

    [Fact]
    public void Permissions_fold_into_the_schema_hash_independently_of_declaration_order()
    {
        var plain = PermissionWorld.Build(_ => { });
        var a = PermissionWorld.Build(b =>
        {
            b.Builder.DenyInput(Guest, b.Spawn);
            b.Builder.GrantInput(Host, b.Deposit);
        });
        var b2 = PermissionWorld.Build(b =>
        {
            b.Builder.GrantInput(Host, b.Deposit);
            b.Builder.DenyInput(Guest, b.Spawn);
        });
        var other = PermissionWorld.Build(b => b.Builder.DenyInput(Guest, b.Deposit));
        var denyAll = PermissionWorld.Build(b => b.Builder.DefaultInputAccess(InputAccess.Deny));

        Assert.NotEqual(plain.Schema.SchemaHash, a.Schema.SchemaHash);
        Assert.Equal(a.Schema.SchemaHash, b2.Schema.SchemaHash);
        Assert.Equal(a.Schema.InputPermissions.Hash, b2.Schema.InputPermissions.Hash);
        Assert.NotEqual(a.Schema.SchemaHash, other.Schema.SchemaHash);
        Assert.NotEqual(plain.Schema.SchemaHash, denyAll.Schema.SchemaHash);
    }

    [Fact]
    public void Contradicting_declarations_fail_and_repeating_one_is_harmless()
    {
        var builder = new WorldBuilder();
        var spawn = builder.AddInputCommand<SpawnOrder>("spawn");
        builder.DenyInput(Guest, spawn);
        builder.DenyInput(Guest, spawn);
        var pair = Assert.Throws<ArgumentException>(() => builder.GrantInput(Guest, spawn));
        Assert.Contains("spawn", pair.Message, StringComparison.Ordinal);

        builder.DefaultInputAccess(InputAccess.Deny);
        Assert.Throws<ArgumentException>(() => builder.DefaultInputAccess(InputAccess.Allow));
        builder.SetInputAccess(spawn, InputAccess.Deny);
        Assert.Throws<ArgumentException>(() => builder.SetInputAccess(spawn, InputAccess.Allow));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.SetInputAccess(Host, (InputAccess)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.GrantInput(Host, InputCommandTypeFor(5)));

        builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.GrantInput(Host, spawn));
    }

    [Fact]
    public void Rules_are_listed_in_canonical_order_and_described_by_name()
    {
        var setup = PermissionWorld.Build(b =>
        {
            b.Builder.GrantInput(Host, b.Deposit);
            b.Builder.SetInputAccess(b.Spawn, InputAccess.Deny);
            b.Builder.SetInputAccess(Spectator, InputAccess.Deny);
        });
        var rules = setup.Schema.InputPermissions.Rules.ToArray();

        Assert.Equal(
            [
                new InputPermissionRule(InputPermissionScope.Source, Spectator, default, InputAccess.Deny),
                new InputPermissionRule(InputPermissionScope.Command, default, setup.Spawn, InputAccess.Deny),
                new InputPermissionRule(InputPermissionScope.Pair, Host, setup.Deposit, InputAccess.Allow),
            ],
            rules);
        Assert.Equal(
            ["default allow", "source 3: deny", "command 'spawn': deny", "source 1, command 'deposit': allow"],
            setup.Schema.InputPermissions.Describe());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(0)]
    public void Denials_hash_identically_on_any_worker_count(int workers)
    {
        var setup = PermissionWorld.Build(b =>
        {
            b.Builder.DefaultInputAccess(InputAccess.Deny);
            b.Builder.GrantInput(Host, b.Spawn);
            b.Builder.GrantInput(Host, b.Deposit);
            b.Builder.GrantInput(Guest, b.Deposit);
        });

        Assert.Equal(Run(setup.Schema, 1), Run(setup.Schema, workers));

        static ulong[] Run(WorldSchema schema, int workers)
        {
            using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = workers, Seed = 11 });
            var hashes = new ulong[20];
            for (int t = 0; t < hashes.Length; t++)
            {
                for (uint s = 1; s <= 3; s++)
                {
                    world.Inputs.Submit(new InputSource(s), new SpawnOrder { Count = (t + (int)s) % 3 });
                    world.Inputs.Submit(new InputSource(s), new DepositOrder { Amount = t });
                }

                world.Tick();
                hashes[t] = world.Hash();
            }

            return hashes;
        }
    }

    private static InputCommandType InputCommandTypeFor(int id)
    {
        var builder = new WorldBuilder();
        InputCommandType last = default;
        for (int i = 0; i <= id; i++)
        {
            last = i switch
            {
                0 => builder.AddInputCommand<SpawnOrder>("a"),
                1 => builder.AddInputCommand<DepositOrder>("b"),
                2 => builder.AddInputCommand<PermissionLedger>("c"),
                3 => builder.AddInputCommand<Health>("d"),
                4 => builder.AddInputCommand<long>("e"),
                _ => builder.AddInputCommand<short>("f"),
            };
        }

        return last;
    }
}

public struct PermissionLedger
{
    public int Spawned;
    public int Deposited;
    public int Rejected;
    public uint LastSource;
    public uint LastSequence;
    public int LastCommand;
    public int LastReason;
}

/// <summary>A two-command schema whose handlers and rejection reader write a <see cref="PermissionLedger"/> Resource.</summary>
internal sealed class PermissionWorld
{
    private PermissionWorld(WorldBuilder builder)
    {
        Builder = builder;
        Ledger = builder.AddResource<PermissionLedger>("ledger").Resource<PermissionLedger>();
        Spawn = builder.AddInputCommand<SpawnOrder>("spawn");
        Deposit = builder.AddInputCommand<DepositOrder>("deposit");
    }

    public WorldBuilder Builder { get; }

    public WorldSchema Schema { get; private set; } = null!;

    public ResourceType Ledger { get; }

    public InputCommandType Spawn { get; }

    public InputCommandType Deposit { get; }

    public static PermissionWorld Build(Action<PermissionWorld> declare)
    {
        var setup = new PermissionWorld(new WorldBuilder());
        declare(setup);
        setup.Schema = AddSystems(setup).Build();
        return setup;
    }

    private static WorldBuilder AddSystems(PermissionWorld setup)
    {
        var builder = setup.Builder;
        var ledger = setup.Ledger;
        var spawn = setup.Spawn;
        var deposit = setup.Deposit;
        builder.AddSystem(new SystemDefinition
        {
            Name = "apply-orders",
            Phase = Phase.Input,
            WritesResources = ResourceMask.Of(ledger),
            Begin = (in SystemContext context) =>
            {
                ref var state = ref context.Resource<PermissionLedger>(ledger);
                foreach (var entry in context.Inputs<SpawnOrder>(spawn))
                {
                    state.Spawned += entry.Command.Count;
                }

                foreach (var entry in context.Inputs<DepositOrder>(deposit))
                {
                    state.Deposited += entry.Command.Amount;
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "count-rejections",
            Phase = Phase.Begin,
            ReadsResources = ResourceMask.Of(builder.CommandRejections),
            WritesResources = ResourceMask.Of(ledger),
            Begin = (in SystemContext context) =>
            {
                ref var state = ref context.Resource<PermissionLedger>(ledger);
                foreach (var rejected in context.Events<CommandRejected>(context.World.Schema.CommandRejections))
                {
                    state.Rejected++;
                    state.LastSource = rejected.Source.Value;
                    state.LastSequence = rejected.Sequence;
                    state.LastCommand = rejected.Command.Id;
                    state.LastReason = rejected.Reason;
                }
            },
        });
        return builder;
    }
}
