using Colony.Fields;

namespace Colony.Plugins.Tests;

public struct Crop
{
    public int Growth;
}

public struct Soil
{
    public int Moisture;
}

public struct FarmStats
{
    public long Grown;
    public long Watered;
}

public struct Harvested
{
    public EntityId Crop;
}

/// <summary>A Plugin built from delegates, so each test states exactly what it registers.</summary>
internal sealed class DelegatePlugin : IPlugin
{
    private readonly Action<PluginRegistration> _register;

    public DelegatePlugin(string id, Action<PluginRegistration> register, params string[] dependencies)
        : this(new PluginManifest { Id = id, Version = "1.0.0", Dependencies = dependencies }, register)
    {
    }

    public DelegatePlugin(PluginManifest manifest, Action<PluginRegistration> register)
    {
        Manifest = manifest;
        _register = register;
    }

    public PluginManifest Manifest { get; }

    public void Register(PluginRegistration registration) => _register(registration);
}

/// <summary>
/// The farming Plugin used across the tests: a Crop Component, a Soil Field, a FarmStats Resource, a Harvested
/// event, an "owner" Relation, a counter and two Systems (Grow is Overridable, Water is not).
/// </summary>
internal sealed class FarmingPlugin : IPlugin
{
    public const string Id = "test.farming";

    public PluginManifest Manifest { get; } = new() { Id = Id, Version = "1.0.0" };

    public void Register(PluginRegistration registration)
    {
        registration.AddComponent<Crop>("farming.crop");
        registration.AddResource<FarmStats>("farming.stats");
        registration.AddEvent<Harvested>("farming.harvested");
        registration.AddRelation("farming.owner");
        var moisture = registration.Fields.AddField<int>("farming.moisture", 50);
        registration.AddCounter("grown");
        var crop = registration.Component<Crop>();
        var stats = registration.Resource<FarmStats>();
        var harvested = registration.Resource<Harvested>();

        registration.AddSystem(new SystemDefinition
        {
            Name = "farming.grow",
            Writes = ComponentMask.Of(crop),
            WritesResources = ResourceMask.Of(stats, harvested),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var crops = chunk.Column<Crop>(crop);
                var entities = chunk.Entities;
                for (int i = 0; i < crops.Length; i++)
                {
                    crops[i].Growth += 1;
                    if (crops[i].Growth % 10 == 0)
                    {
                        context.Emit(harvested, new Harvested { Crop = entities[i] });
                    }
                }
            },
            End = (in SystemContext context) =>
            {
                context.Resource<FarmStats>(stats).Grown++;
            },
        }, overridable: true);

        registration.AddSystem(new SystemDefinition
        {
            Name = "farming.water",
            WritesResources = ResourceMask.Of(stats),
            After = ["farming.grow"],
            Begin = (in SystemContext context) =>
            {
                var field = context.World.Fields().Accessor(moisture);
                field[0, 0, 0] = field[0, 0, 0] + 1;
                context.Resource<FarmStats>(stats).Watered++;
            },
        });
    }
}

internal static class PluginWorlds
{
    public static WorldSize Size => new(16, 16, 1);

    /// <summary>A builder with Fields, debug validation off, ready for a <see cref="PluginHost"/>.</summary>
    public static (WorldBuilder Builder, PluginHost Host) Prepare(params IPlugin[] plugins)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var fields = builder.AddFields(Size);
        var host = new PluginHost(new PluginHostOptions { Fields = fields });
        foreach (var plugin in plugins)
        {
            host.Add(plugin);
        }

        return (builder, host);
    }

    public static WorldSchema Build(params IPlugin[] plugins)
    {
        var (builder, host) = Prepare(plugins);
        return host.Build(builder);
    }
}
