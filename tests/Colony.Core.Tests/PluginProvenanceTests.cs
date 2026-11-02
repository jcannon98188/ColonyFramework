namespace Colony.Core.Tests;

/// <summary>
/// TASK-16: Core records which Plugin registered what (spec sections 7.1 and 8) so the save manifest can carry
/// the Plugin set and the World can report whether every Plugin declared itself Deterministic. Core knows
/// Plugins only as provenance data; the loader lives in Colony.Plugins.
/// </summary>
public sealed class PluginProvenanceTests
{
    [Fact]
    public void Registrations_between_BeginPlugin_and_EndPlugin_are_attributed_to_that_plugin()
    {
        var builder = new WorldBuilder().AddComponent<Position>("game.position");
        builder.BeginPlugin("acme.farming", "1.2.0", deterministic: true);
        builder.AddComponent<Hunger>("farming.hunger").AddResource<Health>("farming.stats");
        builder.EndPlugin();
        builder.AddComponent<Health>("game.health");

        var schema = builder.Build();

        var plugin = Assert.Single(schema.Plugins.ToArray());
        Assert.Equal("acme.farming", plugin.Id);
        Assert.Equal("1.2.0", plugin.Version);
        Assert.True(plugin.Deterministic);
        Assert.Equal(0, schema.ComponentOwner(schema.Component("farming.hunger")));
        Assert.Equal(-1, schema.ComponentOwner(schema.Component("game.position")));
        Assert.Equal(-1, schema.ComponentOwner(schema.Component("game.health")));
        Assert.Equal(0, schema.ResourceOwner(schema.Resource("farming.stats")));
        Assert.Equal(-1, schema.ResourceOwner(schema.StructuralFailures));
    }

    [Fact]
    public void A_world_is_deterministic_only_when_every_plugin_declares_it()
    {
        var deterministic = new WorldBuilder();
        deterministic.BeginPlugin("a", "1.0.0", deterministic: true);
        deterministic.EndPlugin();
        using var world = deterministic.Build().CreateWorld();
        Assert.True(world.IsDeterministic);
        Assert.True(new WorldBuilder().Build().IsDeterministic);

        var mixed = new WorldBuilder();
        mixed.BeginPlugin("a", "1.0.0", deterministic: true);
        mixed.EndPlugin();
        mixed.BeginPlugin("b", "1.0.0", deterministic: false);
        mixed.EndPlugin();
        var schema = mixed.Build();
        Assert.False(schema.IsDeterministic);
        using var other = schema.CreateWorld();
        Assert.False(other.IsDeterministic);
    }

    [Fact]
    public void Plugin_scopes_do_not_nest_and_duplicate_ids_are_rejected()
    {
        var builder = new WorldBuilder();
        builder.BeginPlugin("a", "1.0.0", deterministic: true);
        Assert.Throws<InvalidOperationException>(() => builder.BeginPlugin("b", "1.0.0", deterministic: true));
        builder.EndPlugin();
        Assert.Throws<InvalidOperationException>(builder.EndPlugin);
        Assert.Throws<ArgumentException>(() => builder.BeginPlugin("a", "2.0.0", deterministic: true));
    }

    [Fact]
    public void System_overrides_are_recorded_on_the_schema()
    {
        var builder = new WorldBuilder();
        builder.BeginPlugin("base", "1.0.0", deterministic: true);
        builder.EndPlugin();
        builder.BeginPlugin("mod", "1.0.0", deterministic: true);
        builder.EndPlugin();
        builder.RecordSystemOverride("base.grow", "base", "mod");

        var schema = builder.Build();

        var replaced = Assert.Single(schema.SystemOverrides.ToArray());
        Assert.Equal(("base.grow", "base", "mod"), (replaced.System, replaced.OriginalPlugin, replaced.ReplacingPlugin));
    }

    [Fact]
    public void Plugin_provenance_does_not_change_the_schema_hash()
    {
        var plain = new WorldBuilder().AddComponent<Position>("position").Build();
        var builder = new WorldBuilder();
        builder.BeginPlugin("a", "1.0.0", deterministic: false);
        builder.AddComponent<Position>("position");
        builder.EndPlugin();

        Assert.Equal(plain.SchemaHash, builder.Build().SchemaHash);
    }
}
