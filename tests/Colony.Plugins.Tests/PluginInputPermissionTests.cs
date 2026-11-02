namespace Colony.Plugins.Tests;

public struct PlantOrder
{
    public int Crop;
}

/// <summary>TASK-35: Plugins declare Input Command permissions through their registration.</summary>
public sealed class PluginInputPermissionTests
{
    private static readonly InputSource Player = new(1);
    private static readonly InputSource Spectator = new(9);

    [Fact]
    public void A_plugin_declares_permissions_for_its_own_and_other_plugins_commands()
    {
        var schema = PluginWorlds.Build(
            new DelegatePlugin("farming", r =>
            {
                var plant = r.AddInputCommand<PlantOrder>("farming.plant");
                r.SetInputAccess(plant, InputAccess.Deny).GrantInput(Player, plant);
            }),
            new DelegatePlugin("spectators", r => r.SetInputAccess(Spectator, InputAccess.Deny), "farming"));

        var plant = schema.InputCommand<PlantOrder>();
        Assert.True(schema.InputPermissions.IsAllowed(Player, plant));
        Assert.False(schema.InputPermissions.IsAllowed(Spectator, plant));
        Assert.False(schema.InputPermissions.IsAllowed(new InputSource(2), plant));
        Assert.Equal(3, schema.InputPermissions.Rules.Length);
    }

    [Fact]
    public void Contradicting_plugins_fail_naming_both()
    {
        var error = Assert.Throws<PluginException>(() => PluginWorlds.Build(
            new DelegatePlugin("farming", r =>
            {
                var plant = r.AddInputCommand<PlantOrder>("farming.plant");
                r.GrantInput(Spectator, plant);
            }),
            new DelegatePlugin("strict", r => r.DenyInput(Spectator, r.InputCommand<PlantOrder>()), "farming")));

        Assert.Equal("strict", error.PluginId);
        Assert.Contains("Plugin 'farming'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Plugin 'strict'", error.Message, StringComparison.Ordinal);
    }
}
