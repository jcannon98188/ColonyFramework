using Colony.Diagnostics;

namespace Colony.Plugins.Tests;

/// <summary>TASK-29 AC #5: Plugins register inspectors in Register; they reach the schema under the Plugin's id.</summary>
public sealed class PluginInspectorTests
{
    [Fact]
    public void A_plugin_inspector_is_registered_under_the_plugin_id_and_runs_against_the_world()
    {
        var schema = PluginWorlds.Build(new DelegatePlugin("test.economy", r => r.AddInspector("prices", (world, output) =>
        {
            output.Add("bread", 3L);
            output.Add("tick", world.CurrentTick);
        })));

        var inspector = Assert.Single(schema.Inspectors.ToArray());
        Assert.Equal("test.economy.prices", inspector.Name);
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var output = new InspectorOutput();
        inspector.Run(world, output);
        Assert.Equal(3L, output.Entries[0].Whole);
        Assert.Equal("tick", output.Entries[1].Name);
    }

    [Fact]
    public void Two_plugins_may_use_the_same_inspector_name()
    {
        var schema = PluginWorlds.Build(
            new DelegatePlugin("test.a", r => r.AddInspector("state", (_, o) => o.Add("a", 1L))),
            new DelegatePlugin("test.b", r => r.AddInspector("state", (_, o) => o.Add("b", 2L))));

        Assert.Equal(["test.a.state", "test.b.state"], schema.Inspectors.ToArray().Select(i => i.Name));
    }
}
