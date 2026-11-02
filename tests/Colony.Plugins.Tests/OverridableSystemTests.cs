using Colony.Persistence;

namespace Colony.Plugins.Tests;

/// <summary>AC #3: an Overridable System is replaced by a dependent Plugin and the replacement is recorded in the save manifest; a non-Overridable one cannot be replaced.</summary>
public sealed class OverridableSystemTests
{
    private static SystemDefinition FastGrow(PluginRegistration r)
    {
        var crop = r.Component<Crop>();
        var stats = r.Resource<FarmStats>();
        return new SystemDefinition
        {
            Name = "farming.grow",
            Writes = ComponentMask.Of(crop),
            WritesResources = ResourceMask.Of(stats),
            ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
            {
                foreach (ref var c in chunk.Column<Crop>(crop))
                {
                    c.Growth += 5;
                }
            },
            End = (in SystemContext context) => context.Resource<FarmStats>(stats).Grown += 100,
        };
    }

    [Fact]
    public void A_dependent_plugin_replaces_an_overridable_system_and_the_save_manifest_records_it()
    {
        var mod = new DelegatePlugin("test.fastfarm", r => r.ReplaceSystem(FastGrow(r)), FarmingPlugin.Id);
        var schema = PluginWorlds.Build(new FarmingPlugin(), mod);
        using var world = schema.CreateWorld(new WorldOptions { Seed = 1, WorkerThreads = 1 });
        var crop = schema.Component<Crop>();
        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(crop));
        world.Playback(buffer);

        world.Tick();

        // The replacement kept the original's name, id and position; its body ran instead of the original's.
        Assert.Equal(2, schema.Systems.Length);
        Assert.Equal(0, schema.System("farming.grow").Id);
        Assert.Equal(100, world.Resource<FarmStats>(schema.Resource<FarmStats>()).Grown);
        var replaced = Assert.Single(schema.SystemOverrides.ToArray());
        Assert.Equal(new SystemOverride("farming.grow", FarmingPlugin.Id, "test.fastfarm"), replaced);

        var manifest = WorldPersistence.ReadManifest(world.SaveToBytes());
        Assert.Equal(new[] { FarmingPlugin.Id, "test.fastfarm" }, manifest.Plugins.Select(p => p.Id).ToArray());
        var saved = Assert.Single(manifest.Plugins[1].Overrides);
        Assert.Equal(("farming.grow", FarmingPlugin.Id), (saved.System, saved.OriginalPlugin));
        Assert.Contains("farming.crop", manifest.Plugins[0].Components);
        Assert.True(manifest.IsDeterministic);
    }

    [Fact]
    public void A_non_overridable_system_cannot_be_replaced()
    {
        var mod = new DelegatePlugin("test.nowater", r => r.ReplaceSystem(new SystemDefinition { Name = "farming.water", Begin = (in SystemContext _) => { } }), FarmingPlugin.Id);

        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(new FarmingPlugin(), mod));

        Assert.Equal("test.nowater", error.PluginId);
        Assert.Contains("not Overridable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replacing_without_depending_on_the_owner_fails()
    {
        var mod = new DelegatePlugin("test.stranger", r => r.ReplaceSystem(new SystemDefinition { Name = "farming.grow", Begin = (in SystemContext _) => { } }));

        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(new FarmingPlugin(), mod));

        Assert.Contains("dependency", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replacing_an_unknown_system_or_registering_a_taken_name_fails()
    {
        var ghost = new DelegatePlugin("test.ghost", r => r.ReplaceSystem(new SystemDefinition { Name = "farming.nothing", Begin = (in SystemContext _) => { } }), FarmingPlugin.Id);
        Assert.Throws<PluginException>(() => PluginWorlds.Build(new FarmingPlugin(), ghost));

        var squatter = new DelegatePlugin("test.squatter", r => r.AddSystem(new SystemDefinition { Name = "farming.grow", Begin = (in SystemContext _) => { } }), FarmingPlugin.Id);
        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(new FarmingPlugin(), squatter));
        Assert.Contains("ReplaceSystem", error.Message, StringComparison.Ordinal);
    }
}
