using Colony.Fields;

namespace Colony.Plugins.Tests;

/// <summary>AC #1: a Plugin registers through Register and the World runs its Systems under the scheduler; registration closes at Build.</summary>
public sealed class PluginRegistrationTests
{
    [Fact]
    public void A_plugin_registers_components_resources_events_relations_fields_systems_and_counters_and_the_world_runs_them()
    {
        var schema = PluginWorlds.Build(new FarmingPlugin());
        using var world = schema.CreateWorld(new WorldOptions { Seed = 1, WorkerThreads = 1 });
        var crop = schema.Component<Crop>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 20; i++)
        {
            buffer.Create(ComponentMask.Of(crop));
        }

        world.Playback(buffer);

        for (int tick = 0; tick < 10; tick++)
        {
            Assert.True(world.Tick());
        }

        var stats = world.Resource<FarmStats>(schema.Resource<FarmStats>());
        Assert.Equal(10, stats.Grown);
        Assert.Equal(10, stats.Watered);
        Assert.Equal(10, world.Fields().Accessor(world.Fields().Field<int>("farming.moisture"))[0, 0, 0] - 50);
        Assert.True(schema.TryGetRelation("farming.owner", out _));
        Assert.True(schema.TryGetResource("farming.harvested", out _));
        Assert.Contains("test.farming.grown", schema.Counters.ToArray());
        Assert.Equal(2, schema.Systems.Length);

        var plugin = Assert.Single(schema.Plugins.ToArray());
        Assert.Equal(FarmingPlugin.Id, plugin.Id);
        Assert.Equal(0, schema.ComponentOwner(crop));
        Assert.True(world.IsDeterministic);
    }

    public struct Plant
    {
        public int Seeds;
    }

    private struct Planted
    {
        public long Seeds;
    }

    [Fact]
    public void A_plugin_registers_input_commands_and_handlers_in_the_input_phase_apply_them()
    {
        var schema = PluginWorlds.Build(new FarmingPlugin(), new DelegatePlugin("test.planting", r =>
        {
            var plant = r.AddInputCommand<Plant>("farming.plant");
            r.AddResource<Planted>("farming.planted");
            var planted = r.Resource<Planted>();
            r.AddSystem(new SystemDefinition
            {
                Name = "farming.plant",
                Phase = Phase.Input,
                WritesResources = ResourceMask.Of(planted),
                Begin = (in SystemContext context) =>
                {
                    foreach (var entry in context.Inputs<Plant>(plant))
                    {
                        context.Resource<Planted>(planted).Seeds += entry.Command.Seeds;
                    }
                },
            });
        }, FarmingPlugin.Id));
        using var world = schema.CreateWorld(new WorldOptions { Seed = 1, WorkerThreads = 1 });

        world.Inputs.Submit(new InputSource(1), new Plant { Seeds = 3 });
        world.Inputs.Submit(new InputSource(2), new Plant { Seeds = 4 });
        world.Tick();
        world.Tick();

        Assert.Equal(7, world.Resource<Planted>(schema.Resource<Planted>()).Seeds);
        Assert.Equal(schema.InputCommand<Plant>(), schema.InputCommand("farming.plant"));
    }

    [Fact]
    public void Registration_after_build_fails()
    {
        PluginRegistration? captured = null;
        var schema = PluginWorlds.Build(new DelegatePlugin("test.capture", r => captured = r));

        Assert.NotNull(captured);
        Assert.Throws<InvalidOperationException>(() => captured.AddComponent<Crop>("late.crop"));
        Assert.Throws<InvalidOperationException>(() => captured.AddSystem(new SystemDefinition { Name = "late", Begin = (in SystemContext _) => { } }));
        Assert.Throws<InvalidOperationException>(() => captured.Builder.AddResource<FarmStats>("late.stats"));
        Assert.Equal(0, schema.ComponentCount);
    }

    [Fact]
    public void A_plugin_that_throws_in_register_fails_the_build_naming_the_plugin()
    {
        var plugin = new DelegatePlugin("test.broken", _ => throw new ArgumentException("bad registration"));

        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(plugin));

        Assert.Equal("test.broken", error.PluginId);
        Assert.Contains("bad registration", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_deterministic_plugin_makes_the_world_report_non_deterministic()
    {
        var manifest = new PluginManifest { Id = "test.wild", Version = "0.1.0", Deterministic = false };
        var schema = PluginWorlds.Build(new FarmingPlugin(), new DelegatePlugin(manifest, _ => { }));
        using var world = schema.CreateWorld();

        Assert.False(schema.IsDeterministic);
        Assert.False(world.IsDeterministic);
    }
}
