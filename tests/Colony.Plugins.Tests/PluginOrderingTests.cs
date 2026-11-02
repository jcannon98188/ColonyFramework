namespace Colony.Plugins.Tests;

/// <summary>AC #2: load order from the dependency graph with Plugin-id ties; duplicates, cycles, missing dependencies and unorderable writers fail at World build.</summary>
public sealed class PluginOrderingTests
{
    private static DelegatePlugin Marker(string id, params string[] dependencies)
        => new(id, r => r.AddComponent<Soil>(id + ".marker"), dependencies);

    private static DelegatePlugin Empty(string id, params string[] dependencies) => new(id, _ => { }, dependencies);

    [Fact]
    public void Plugins_load_in_dependency_order_with_ties_broken_by_id()
    {
        // Added out of order on purpose: c and b depend on a; d depends on c. Expected: a, b, c, d.
        var (builder, host) = PluginWorlds.Prepare(
            new DelegatePlugin("d", r => r.AddComponent<Harvested>("d.marker"), "c"),
            new DelegatePlugin("c", r => r.AddComponent<Crop>("c.marker"), "a"),
            new DelegatePlugin("b", r => r.AddComponent<FarmStats>("b.marker"), "a"),
            Marker("a"));

        var schema = host.Build(builder);

        Assert.Equal(["a", "b", "c", "d"], host.LoadOrder.Select(p => p.Id).ToArray());
        Assert.Equal(["a", "b", "c", "d"], schema.Plugins.ToArray().Select(p => p.Id).ToArray());
        Assert.Equal(["a.marker", "b.marker", "c.marker", "d.marker"], schema.Components.ToArray().Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Independent_plugins_load_in_id_order_regardless_of_add_order()
    {
        var (builder, host) = PluginWorlds.Prepare(Empty("zeta"), Empty("alpha"), Empty("mid"));

        host.Build(builder);

        Assert.Equal(["alpha", "mid", "zeta"], host.LoadOrder.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void A_duplicate_plugin_id_fails_at_build()
    {
        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(Empty("dup"), Empty("dup")));

        Assert.Equal("dup", error.PluginId);
        Assert.Contains("twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_component_name_across_plugins_fails_naming_the_second_plugin()
    {
        var first = new DelegatePlugin("first", r => r.AddComponent<Crop>("shared.name"));
        var second = new DelegatePlugin("second", r => r.AddComponent<Soil>("shared.name"));

        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(first, second));

        Assert.Equal("second", error.PluginId);
        Assert.Contains("shared.name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dependency_cycle_fails_at_build()
    {
        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(Empty("a", "b"), Empty("b", "c"), Empty("c", "a"), Empty("free")));

        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a", error.Message, StringComparison.Ordinal);
        Assert.Contains("c", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_dependency_fails_at_build()
    {
        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(Empty("needy", "absent")));

        Assert.Equal("needy", error.PluginId);
        Assert.Contains("absent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_requiring_a_newer_api_fails_at_build()
    {
        var manifest = new PluginManifest { Id = "future", Version = "1.0.0", RequiredApiVersion = ColonyApi.Version + 1 };

        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(new DelegatePlugin(manifest, _ => { })));

        Assert.Equal("future", error.PluginId);
        Assert.Contains("API", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unorderable_writers_from_two_plugins_fail_at_build()
    {
        var owner = new DelegatePlugin("owner", r => r.AddComponent<Crop>("crop"));
        var left = new DelegatePlugin("left", r => r.AddSystem(Writer("left.write", r.Component<Crop>())), "owner");
        var right = new DelegatePlugin("right", r => r.AddSystem(Writer("right.write", r.Component<Crop>())), "owner");

        var error = Assert.Throws<InvalidOperationException>(() => PluginWorlds.Build(owner, left, right));

        Assert.Contains("left.write", error.Message, StringComparison.Ordinal);
        Assert.Contains("right.write", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Writers_ordered_across_plugins_by_name_build_and_run()
    {
        var owner = new DelegatePlugin("owner", r => r.AddComponent<Crop>("crop"));
        var left = new DelegatePlugin("left", r => r.AddSystem(Writer("left.write", r.Component<Crop>())), "owner");
        var right = new DelegatePlugin("right", r => r.AddSystem(Writer("right.write", r.Component<Crop>(), after: "left.write")), "owner", "left");

        var schema = PluginWorlds.Build(owner, left, right);
        using var world = schema.CreateWorld();

        Assert.True(world.Tick());
        Assert.True(schema.System("left.write").Order < schema.System("right.write").Order);
    }

    private static SystemDefinition Writer(string name, ComponentType crop, string? after = null) => new()
    {
        Name = name,
        Writes = ComponentMask.Of(crop),
        After = after is null ? [] : [after],
        ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
        {
            foreach (ref var c in chunk.Column<Crop>(crop))
            {
                c.Growth++;
            }
        },
    };
}
