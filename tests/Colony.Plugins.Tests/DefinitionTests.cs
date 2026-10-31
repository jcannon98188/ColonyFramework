using Colony.Numerics;

namespace Colony.Plugins.Tests;

public struct ItemDefinition
{
    public int Weight;
    public Fixed32 Value;
    public bool Edible;
    public StringId Category;
}

/// <summary>AC #4: Definitions load from the JSON source, are patched by a later Plugin, bake into blobs and are looked up by interned name without allocation during Ticks.</summary>
public sealed class DefinitionTests
{
    private const string BaseItems = """
        {
          // comments and trailing commas are accepted
          "item": {
            "apple": { "weight": 1, "value": 2.5, "edible": true, "category": "food" },
            "stone": { "weight": 10, "value": 0.25, "edible": false, "category": "material" },
          }
        }
        """;

    private static DelegatePlugin ItemsPlugin(Action<PluginRegistration>? extra = null) => new("test.items", r =>
    {
        r.Definitions.AddType<ItemDefinition>("item", layout => layout
            .Field("weight", (ref ItemDefinition d) => ref d.Weight)
            .Field("value", (ref ItemDefinition d) => ref d.Value)
            .Field("edible", (ref ItemDefinition d) => ref d.Edible)
            .Field("category", (ref ItemDefinition d) => ref d.Category));
        r.Definitions.AddSource(JsonDefinitionSource.FromText(BaseItems, "items.json"));
        extra?.Invoke(r);
    });

    [Fact]
    public void Definitions_load_from_json_and_are_read_by_interned_name()
    {
        var schema = PluginWorlds.Build(ItemsPlugin());
        using var world = schema.CreateWorld();
        var items = world.Definitions().Table<ItemDefinition>("item");

        Assert.Equal(2, items.Count);
        ref readonly var apple = ref items["apple"];
        Assert.Equal(1, apple.Weight);
        Assert.Equal(Fixed32.FromDouble(2.5), apple.Value);
        Assert.True(apple.Edible);
        Assert.Equal("food", items.Registry.Strings.Get(apple.Category).ToString());
        Assert.True(items.TryFind("stone", out int stone));
        Assert.Equal("stone", items.NameOf(stone).ToString());
        Assert.Equal(10, items[stone].Weight);
        Assert.False(items.TryFind("gold", out _));
        Assert.Equal(1, items.Registry.Blobs.Count);
    }

    [Fact]
    public void A_later_plugin_patches_definitions_from_json_and_code_and_adds_new_ones()
    {
        const string patch = """{ "item": { "apple": { "weight": 3 }, "gold": { "weight": 50, "value": 1000, "category": "material" } } }""";
        var mod = new DelegatePlugin("test.tuning", r =>
        {
            r.Definitions.AddSource(JsonDefinitionSource.FromText(patch, "tuning.json"));
            r.Definitions.Patch<ItemDefinition>("item", "stone", (ref ItemDefinition d) => d.Edible = true);
            r.Definitions.Add("item", "iron", new ItemDefinition { Weight = 20, Category = r.Definitions.Intern("material") });
        }, "test.items");

        var schema = PluginWorlds.Build(ItemsPlugin(), mod);
        using var world = schema.CreateWorld();
        var items = world.Definitions().Table<ItemDefinition>("item");

        Assert.Equal(4, items.Count);
        Assert.Equal(3, items["apple"].Weight);
        Assert.True(items["apple"].Edible); // untouched fields survive a patch
        Assert.True(items["stone"].Edible);
        Assert.Equal(50, items["gold"].Weight);
        Assert.Equal(items["stone"].Category, items["gold"].Category);
        Assert.Equal(items["gold"].Category, items["iron"].Category);
    }

    [Fact]
    public void Lookups_during_ticks_allocate_nothing()
    {
        var crop = default(ComponentType);
        DefinitionTable<ItemDefinition>? items = null;
        long total = 0;
        var schema = PluginWorlds.Build(ItemsPlugin(r =>
        {
            r.AddComponent<Crop>("crop");
            crop = r.Component<Crop>();
            items = r.Definitions.Registry.Table<ItemDefinition>("item");
            r.AddSystem(new SystemDefinition
            {
                Name = "weigh",
                Reads = ComponentMask.Of(crop),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    long sum = 0;
                    foreach (ref readonly var c in chunk.Column<Crop>(crop))
                    {
                        string name = c.Growth % 2 == 0 ? "apple" : "stone";
                        items!.TryFind(name.AsSpan(), out int index);
                        sum += items[index].Weight + items[items.IndexOf("apple")].Weight;
                    }

                    Interlocked.Add(ref total, sum);
                },
            });
        }));
        using var world = schema.CreateWorld(new WorldOptions { Seed = 1, WorkerThreads = 1 });
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 100; i++)
        {
            buffer.Create(ComponentMask.Of(crop));
        }

        world.Playback(buffer);
        world.Tick();
        world.Tick();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            world.Tick();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(7 * 100 * 2, total);
    }

    [Fact]
    public void Unknown_types_fields_and_mistyped_values_fail_naming_the_plugin_and_source()
    {
        var unknownType = new DelegatePlugin("test.bad", r => r.Definitions.AddSource(JsonDefinitionSource.FromText("""{ "creature": { "wolf": { "legs": 4 } } }""", "wolves.json")));
        var e1 = Assert.Throws<PluginException>(() => PluginWorlds.Build(unknownType));
        Assert.Equal("test.bad", e1.PluginId);
        Assert.Contains("creature", e1.Message, StringComparison.Ordinal);

        var unknownField = ItemsPlugin(r => r.Definitions.AddSource(JsonDefinitionSource.FromText("""{ "item": { "apple": { "colour": "red" } } }""", "colours.json")));
        var e2 = Assert.Throws<PluginException>(() => PluginWorlds.Build(unknownField));
        Assert.Contains("colours.json", e2.Message, StringComparison.Ordinal);
        Assert.Contains("colour", e2.Message, StringComparison.Ordinal);

        var mistyped = ItemsPlugin(r => r.Definitions.AddSource(JsonDefinitionSource.FromText("""{ "item": { "apple": { "weight": "heavy" } } }""", "heavy.json")));
        var e3 = Assert.Throws<PluginException>(() => PluginWorlds.Build(mistyped));
        Assert.Contains("weight", e3.Message, StringComparison.Ordinal);

        var patchMissing = ItemsPlugin(r => r.Definitions.Patch<ItemDefinition>("item", "gold", (ref ItemDefinition d) => d.Weight = 1));
        Assert.Throws<PluginException>(() => PluginWorlds.Build(patchMissing));
    }

    [Fact]
    public void Nested_json_values_are_rejected()
    {
        var source = JsonDefinitionSource.FromText("""{ "item": { "apple": { "tags": ["a"] } } }""");

        Assert.Throws<FormatException>(() => source.Read().ToList());
    }

    [Fact]
    public void Definition_type_layouts_are_part_of_the_schema_hash_but_content_is_not()
    {
        ulong a = PluginWorlds.Build(ItemsPlugin()).SchemaHash;
        ulong b = PluginWorlds.Build(ItemsPlugin(r => r.Definitions.Patch<ItemDefinition>("item", "apple", (ref ItemDefinition d) => d.Weight = 99))).SchemaHash;
        ulong c = PluginWorlds.Build(new DelegatePlugin("test.items", r => r.Definitions.AddType<Crop>("item", l => l.Field("growth", (ref Crop d) => ref d.Growth)))).SchemaHash;

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
