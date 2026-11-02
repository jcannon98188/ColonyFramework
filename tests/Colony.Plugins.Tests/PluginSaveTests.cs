using Colony.Persistence;

namespace Colony.Plugins.Tests;

/// <summary>AC #5: saves record the Plugin set; loading with a missing Plugin fails by default and succeeds in the opt-in drop-unknown-Components mode.</summary>
public sealed class PluginSaveTests
{
    private struct TagTotals
    {
        public long Count;
    }

    private static readonly DelegatePlugin Tags = new("test.tags", r =>
    {
        r.AddComponent<Soil>("tags.soil", ComponentOptions.NonFragmenting);
        r.AddComponent<Harvested>("tags.mark");
        r.AddResource<TagTotals>("tags.totals");
        r.Builder.AddBufferComponent<int, Inline4<int>>("tags.list");
    });

    private static World Populate(WorldSchema schema)
    {
        var world = schema.CreateWorld(new WorldOptions { Seed = 7, WorkerThreads = 1 });
        var crop = schema.Component("farming.crop");
        var soil = schema.Component("tags.soil");
        var mark = schema.Component("tags.mark");
        var list = schema.Component("tags.list");
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 40; i++)
        {
            // Mixed archetypes: {crop}, {crop, mark}, {crop, mark, list}, {mark} so dropping collapses some of them.
            var mask = (i % 4) switch
            {
                0 => ComponentMask.Of(crop),
                1 => ComponentMask.Of(crop, mark),
                2 => ComponentMask.Of(crop, mark, list),
                _ => ComponentMask.Of(mark),
            };
            buffer.Create(mask);
        }

        world.Playback(buffer);
        buffer.Clear();
        int n = 0;
        foreach (var chunk in world.Query(ComponentMask.Of(crop)))
        {
            foreach (var e in chunk.Entities)
            {
                if (n++ % 3 == 0)
                {
                    buffer.Add(e, soil, new Soil { Moisture = n });
                }
            }
        }

        world.Playback(buffer);
        world.Resource<TagTotals>(schema.Resource("tags.totals")).Count = 42;
        for (int i = 0; i < 3; i++)
        {
            world.Tick();
        }

        return world;
    }

    [Fact]
    public void The_manifest_records_the_plugin_set_with_versions_and_owned_components()
    {
        var schema = PluginWorlds.Build(new FarmingPlugin(), Tags);
        using var world = Populate(schema);

        var manifest = WorldPersistence.ReadManifest(world.SaveToBytes());

        Assert.Equal(new[] { FarmingPlugin.Id, "test.tags" }, manifest.Plugins.Select(p => p.Id).ToArray());
        Assert.Equal("1.0.0", manifest.Plugins[0].Version);
        Assert.Equal<string>(["tags.soil", "tags.mark", "tags.list"], manifest.Plugins[1].Components);
        Assert.Equal<string>(["tags.totals"], manifest.Plugins[1].Resources);
        Assert.True(manifest.Plugins[0].Deterministic);
    }

    [Fact]
    public void The_C_ABI_version_of_a_native_plugin_is_recorded_in_the_schema_and_the_save()
    {
        var native = new DelegatePlugin(new PluginManifest { Id = "test.native", Version = "2.0.0", AbiVersion = ColonyApi.AbiVersion }, r => r.AddComponent<Soil>("native.soil"));
        var schema = PluginWorlds.Build(new FarmingPlugin(), native);
        using var world = schema.CreateWorld();
        world.Tick();

        var manifest = WorldPersistence.ReadManifest(world.SaveToBytes());

        Assert.Equal([0, ColonyApi.AbiVersion], schema.Plugins.ToArray().Select(p => p.AbiVersion).ToArray());
        Assert.Equal([0, ColonyApi.AbiVersion], manifest.Plugins.Select(p => p.AbiVersion).ToArray());
    }

    [Fact]
    public void A_plugin_built_against_a_newer_C_ABI_fails_at_build()
    {
        var future = new DelegatePlugin(new PluginManifest { Id = "test.future", Version = "1.0.0", AbiVersion = ColonyApi.AbiVersion + 1 }, _ => { });

        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(future));

        Assert.Contains("C ABI", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_with_a_missing_plugin_fails_by_default()
    {
        var schema = PluginWorlds.Build(new FarmingPlugin(), Tags);
        using var world = Populate(schema);
        byte[] bytes = world.SaveToBytes();

        var without = PluginWorlds.Build(new FarmingPlugin());
        var error = Assert.Throws<MissingPluginException>(() => WorldPersistence.Load(bytes, without));

        Assert.Equal("test.tags", error.PluginId);
        Assert.Contains("DropUnknownComponents", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_plugin_without_components_still_fails_by_default()
    {
        var systemsOnly = new DelegatePlugin("test.quiet", r => r.AddSystem(new SystemDefinition { Name = "quiet", Begin = (in SystemContext _) => { } }));
        var schema = PluginWorlds.Build(new FarmingPlugin(), systemsOnly);
        using var world = schema.CreateWorld();
        world.Tick();
        byte[] bytes = world.SaveToBytes();

        Assert.Throws<MissingPluginException>(() => WorldPersistence.Load(bytes, PluginWorlds.Build(new FarmingPlugin())));
        using var loaded = WorldPersistence.Load(bytes, PluginWorlds.Build(new FarmingPlugin()), new LoadOptions { DropUnknownComponents = true });
        Assert.Equal(1, loaded.CurrentTick);
    }

    [Fact]
    public void Drop_unknown_components_mode_loads_the_rest_and_continues()
    {
        var schema = PluginWorlds.Build(new FarmingPlugin(), Tags);
        using var world = Populate(schema);
        byte[] bytes = world.SaveToBytes();
        var crop = schema.Component("farming.crop");

        var without = PluginWorlds.Build(new FarmingPlugin());
        using var loaded = WorldPersistence.Load(bytes, without, new LoadOptions { DropUnknownComponents = true });

        Assert.Equal(world.CurrentTick, loaded.CurrentTick);
        Assert.Equal(world.EntityCount, loaded.EntityCount);
        var expected = new List<(EntityId, int)>();
        foreach (var chunk in world.Query(ComponentMask.Of(crop)))
        {
            for (int i = 0; i < chunk.Count; i++)
            {
                expected.Add((chunk.Entities[i], chunk.Column<Crop>(crop)[i].Growth));
            }
        }

        var actual = new List<(EntityId, int)>();
        var loadedCrop = without.Component("farming.crop");
        foreach (var chunk in loaded.Query(ComponentMask.Of(loadedCrop)))
        {
            for (int i = 0; i < chunk.Count; i++)
            {
                actual.Add((chunk.Entities[i], chunk.Column<Crop>(loadedCrop)[i].Growth));
            }
        }

        Assert.Equal(expected.OrderBy(p => p.Item1.Value).ToList(), actual.OrderBy(p => p.Item1.Value).ToList());
        Assert.Equal(world.Resource<FarmStats>(schema.Resource<FarmStats>()).Grown, loaded.Resource<FarmStats>(without.Resource<FarmStats>()).Grown);

        // The loaded World keeps ticking with the Plugins it has.
        loaded.Tick();
        Assert.Equal(world.CurrentTick + 1, loaded.CurrentTick);
    }
}
