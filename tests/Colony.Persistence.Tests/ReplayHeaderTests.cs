using Colony.Hashing;
using Colony.Persistence;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>
/// Spec section 7.2: the replay header records the versions, the Plugin set, the generation version, the seed and
/// the tick rate, and playback refuses a World that differs in any of them (spec section 6: a log recorded with a
/// non-deterministic Plugin carries no guarantee). The identity is the lockstep <see cref="PeerIdentity"/>.
/// </summary>
public sealed class ReplayHeaderTests
{
    private const ulong Seed = InputScenario.DefaultSeed;

    [Fact]
    public void The_header_records_the_identity_and_the_generation_version_and_survives_a_round_trip()
    {
        var schema = Schema("7", ("farming", "1.0.0", true));
        var log = Record(schema, Seed, new ReplayOptions { TickRate = 30, Build = "game 1.2" });
        var reopened = ReplayLog.Open(log.Encode());

        foreach (var l in new[] { log, reopened })
        {
            Assert.Equal([new PeerPlugin("farming", "1.0.0", true, 0)], l.Plugins);
            Assert.Equal(PeerIdentity.CurrentFrameworkVersion, l.Identity.FrameworkVersion);
            Assert.Equal(SaveContainer.Version, l.Identity.SaveFormatVersion);
            Assert.Equal(ReplayLog.Version, l.Identity.ReplayFormatVersion);
            Assert.Equal("game 1.2", l.Identity.Build);
            Assert.Equal(Seed, l.Seed);
            Assert.Equal(schema.SchemaHash, l.SchemaHash);
            Assert.Equal(30u, l.TickRate);
            Assert.True(l.IsDeterministic);
            Assert.Equal([("generated", "7")], l.ExtensionVersions.Select(e => (e.Name, e.Version!)));
        }

        Assert.Equal(log.Encode(), reopened.Encode());
    }

    [Fact]
    public void Playback_on_a_matching_world_is_accepted()
    {
        var schema = Schema("7", ("farming", "1.0.0", true));
        var log = Record(schema, Seed, ReplayOptions.Default);
        using var world = schema.CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = 1 });
        var player = new ReplayPlayer(log, world);
        player.RunToEnd();
        Assert.True(player.VerifiedCheckpoints > 0);
    }

    [Fact]
    public void Playback_refuses_a_world_with_another_plugin_version()
    {
        var log = Record(Schema("7", ("farming", "1.0.0", true)), Seed, ReplayOptions.Default);
        using var world = Schema("7", ("farming", "1.1.0", true)).CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = 1 });

        var e = Assert.Throws<InvalidOperationException>(() => new ReplayPlayer(log, world));
        Assert.Contains("farming", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Playback_refuses_a_world_with_another_seed()
    {
        var schema = Schema("7");
        var log = Record(schema, Seed, ReplayOptions.Default);
        using var world = schema.CreateWorld(new WorldOptions { Seed = Seed + 1, WorkerThreads = 1 });

        var e = Assert.Throws<InvalidOperationException>(() => new ReplayPlayer(log, world));
        Assert.Contains("seed", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Playback_refuses_a_world_with_another_generation_version()
    {
        var log = Record(Schema("7"), Seed, ReplayOptions.Default);
        using var world = Schema("8").CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = 1 });

        var e = Assert.Throws<InvalidOperationException>(() => new ReplayPlayer(log, world));
        Assert.Contains("generated", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_recorded_with_a_non_deterministic_plugin_is_marked_and_playback_refuses_it()
    {
        var schema = Schema("7", ("weather", "2.1.0", false));
        var log = Record(schema, Seed, ReplayOptions.Default);
        Assert.False(ReplayLog.Open(log.Encode()).IsDeterministic);

        using var world = schema.CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = 1 });
        var e = Assert.Throws<InvalidOperationException>(() => new ReplayPlayer(log, world));
        Assert.Contains("weather", e.Message, StringComparison.Ordinal);
    }

    private static ReplayLog Record(WorldSchema schema, ulong seed, ReplayOptions options)
    {
        using var world = schema.CreateWorld(new WorldOptions { Seed = seed, WorkerThreads = 1 });
        var recorder = new ReplayRecorder(world, options);
        for (int t = 0; t < 12; t++)
        {
            Assert.True(recorder.Tick());
        }

        return recorder.Finish();
    }

    private static WorldSchema Schema(string generationVersion, params (string Id, string Version, bool Deterministic)[] plugins)
    {
        var builder = new WorldBuilder();
        InputScenario.ComposeStatic(builder);
        builder.AddExtension(new GeneratedDefinition(generationVersion));
        foreach (var (id, version, deterministic) in plugins)
        {
            builder.BeginPlugin(id, version, deterministic);
            builder.AddRelation("rel." + id);
            builder.EndPlugin();
        }

        builder.DebugValidation(false);
        return builder.Build();
    }

    /// <summary>An extension with no state whose content version stands in for a generator's (spec section 9.3).</summary>
    private sealed class GeneratedDefinition(string version) : WorldExtensionDefinition
    {
        public override string Name => "generated";

        public override ulong LayoutHash => 0x4745_4E00_0000_0001;

        public override string? Version => version;

        public override IWorldExtension Create(World world) => new Generated();
    }

    private sealed class Generated : IWorldExtension
    {
        public void BeginTick(long tick)
        {
        }

        public void EndPhase(Phase phase)
        {
        }

        public void EndTick(long tick)
        {
        }

        public void Hash(ref Hash64State state)
        {
        }

        public void Save(ExtensionStateWriter writer)
        {
        }

        public void Load(ExtensionStateReader reader)
        {
        }

        public void Dispose()
        {
        }
    }
}
