using Colony.Persistence;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>TASK-33 AC #4 (spec section 7.4): peers compare build, Plugin set and versions, schema hash, seed and options before lockstep.</summary>
public sealed class HandshakeTests
{
    private const ulong Seed = InputScenario.DefaultSeed;

    [Fact]
    public void Two_peers_built_alike_match()
    {
        var local = Identity(Schema(("farming", "1.0.0", true), ("weather", "2.1.0", true)));
        var remote = Identity(Schema(("farming", "1.0.0", true), ("weather", "2.1.0", true)));

        var result = PeerHandshake.Compare(local, remote);

        Assert.True(result.IsMatch, result.Describe());
        Assert.Empty(result.Mismatches);
        Assert.Equal("The peers match.", result.Describe());
    }

    [Fact]
    public void A_plugin_only_one_peer_has_is_reported_as_missing_on_the_other_side()
    {
        var local = Identity(Schema(("farming", "1.0.0", true)));
        var remote = Identity(Schema(("farming", "1.0.0", true), ("weather", "2.1.0", true)));

        var result = PeerHandshake.Compare(local, remote);

        Assert.False(result.IsMatch);
        var missing = Assert.Single(result.Mismatches, m => m.Kind == HandshakeMismatchKind.MissingPlugin);
        Assert.Equal("weather", missing.Subject);
        Assert.Null(missing.Local);
        Assert.Equal("2.1.0", missing.Remote);
        Assert.Equal("Missing Plugin 'weather': the local peer does not have it, the remote peer has 2.1.0.", missing.Message);

        // The extra Plugin also changes the schema, which is reported on its own line.
        Assert.Contains(result.Mismatches, m => m.Kind == HandshakeMismatchKind.SchemaHash);
    }

    [Fact]
    public void Plugin_versions_determinism_and_load_order_are_compared_one_by_one()
    {
        var local = Identity(Schema(("farming", "1.0.0", true), ("weather", "2.1.0", true)));
        var remote = Identity(Schema(("farming", "1.1.0", true), ("weather", "2.1.0", false)));

        var result = PeerHandshake.Compare(local, remote);

        var version = Assert.Single(result.Mismatches, m => m.Kind == HandshakeMismatchKind.PluginVersion);
        Assert.Equal(("farming", "1.0.0", "1.1.0"), (version.Subject, version.Local, version.Remote));
        var determinism = Assert.Single(result.Mismatches, m => m.Kind == HandshakeMismatchKind.PluginDeterminism);
        Assert.Equal("weather", determinism.Subject);
        Assert.Contains(result.Mismatches, m => m.Kind == HandshakeMismatchKind.NotDeterministic && m.Subject == "remote");

        var reordered = PeerHandshake.Compare(
            Identity(Schema(("farming", "1.0.0", true), ("weather", "2.1.0", true))),
            Identity(Schema(("weather", "2.1.0", true), ("farming", "1.0.0", true))));
        Assert.Contains(reordered.Mismatches, m => m.Kind == HandshakeMismatchKind.PluginOrder);
        Assert.DoesNotContain(reordered.Mismatches, m => m.Kind == HandshakeMismatchKind.MissingPlugin);
    }

    [Fact]
    public void A_different_seed_build_tick_rate_and_settings_each_get_their_own_reason()
    {
        var schema = Schema();
        var local = PeerIdentity.Of(schema, Seed, new PeerIdentityOptions
        {
            Build = "game-1.4.2+abc",
            TickRate = 30,
            Settings = [new SessionSetting("input-delay", "3"), new SessionSetting("map", "river")],
        });
        var remote = PeerIdentity.Of(schema, Seed + 1, new PeerIdentityOptions
        {
            Build = "game-1.4.3+def",
            TickRate = 20,
            Settings = [new SessionSetting("input-delay", "4"), new SessionSetting("difficulty", "hard")],
        });

        var result = PeerHandshake.Compare(local, remote);

        Assert.Equal(
            [
                HandshakeMismatchKind.Build,
                HandshakeMismatchKind.Seed,
                HandshakeMismatchKind.TickRate,
                HandshakeMismatchKind.MissingSetting,
                HandshakeMismatchKind.SettingValue,
                HandshakeMismatchKind.MissingSetting,
            ],
            result.Mismatches.Select(m => m.Kind));
        Assert.Equal("Different seed: the local peer has 0x1A5EC0DE00000015, the remote peer has 0x1A5EC0DE00000016.", result.Mismatches[1].Message);
        Assert.Equal(("difficulty", null, "hard"), (result.Mismatches[3].Subject, result.Mismatches[3].Local, result.Mismatches[3].Remote));
        Assert.Equal(("input-delay", "3", "4"), (result.Mismatches[4].Subject, result.Mismatches[4].Local, result.Mismatches[4].Remote));
        Assert.Equal(("map", "river", null), (result.Mismatches[5].Subject, result.Mismatches[5].Local, result.Mismatches[5].Remote));
        Assert.DoesNotContain(result.Mismatches, m => m.Kind == HandshakeMismatchKind.SchemaHash);
    }

    [Fact]
    public void A_different_schema_with_the_same_plugins_is_a_schema_hash_mismatch()
    {
        var other = new WorldBuilder();
        InputScenario.ComposeStatic(other);
        other.AddResource<ExtraState>("extra");
        var result = PeerHandshake.Compare(Identity(Schema()), Identity(other.Build()));

        var mismatch = Assert.Single(result.Mismatches);
        Assert.Equal(HandshakeMismatchKind.SchemaHash, mismatch.Kind);
    }

    [Fact]
    public void An_identity_round_trips_through_its_wire_form_and_a_world_reports_its_own()
    {
        var schema = Schema(("farming", "1.0.0", true));
        using var world = schema.CreateWorld(new WorldOptions { Seed = Seed });
        var identity = PeerIdentity.Of(world, new PeerIdentityOptions { Build = "b", TickRate = 60, Settings = [new SessionSetting("z", "1"), new SessionSetting("a", "2")] });

        var decoded = PeerIdentity.Decode(identity.Encode());

        Assert.True(PeerHandshake.Compare(identity, decoded).IsMatch);
        Assert.Equal(identity.Encode(), decoded.Encode());
        Assert.Equal(Seed, decoded.Seed);
        Assert.Equal(schema.SchemaHash, decoded.SchemaHash);
        Assert.Equal(PeerIdentity.CurrentFrameworkVersion, decoded.FrameworkVersion);
        Assert.Equal(["a", "z"], decoded.Settings.Select(s => s.Name));
        Assert.Equal("farming", Assert.Single(decoded.Plugins).Id);
        Assert.Throws<SaveFormatException>(() => PeerIdentity.Decode(identity.Encode().AsSpan(0, 10)));
    }

    [Fact]
    public void The_framework_version_constant_tracks_the_package_version()
    {
        var attribute = typeof(PeerIdentity).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single();
        Assert.StartsWith(PeerIdentity.CurrentFrameworkVersion, attribute.InformationalVersion, StringComparison.Ordinal);
    }

    public struct ExtraState
    {
        public int Value;
    }

    private static PeerIdentity Identity(WorldSchema schema) => PeerIdentity.Of(schema, Seed);

    internal static WorldSchema Schema(params (string Id, string Version, bool Deterministic)[] plugins)
    {
        var builder = new WorldBuilder();
        InputScenario.ComposeStatic(builder);
        foreach (var (id, version, deterministic) in plugins)
        {
            builder.BeginPlugin(id, version, deterministic);
            // A Relation needs no CLR type, so each Plugin can own one and change the schema hash.
            builder.AddRelation("rel." + id);
            builder.EndPlugin();
        }

        builder.DebugValidation(false);
        return builder.Build();
    }
}
