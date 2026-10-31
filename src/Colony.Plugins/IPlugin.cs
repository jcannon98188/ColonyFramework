namespace Colony.Plugins;

/// <summary>
/// A unit of code or data that registers into a World (spec section 8). <see cref="Register"/> is the one entry
/// point: <see cref="PluginHost"/> calls it exactly once, in dependency order, while the <see cref="WorldBuilder"/>
/// is open; everything the Plugin owns (Components, Resources, Relations, Fields, Systems, events, counters,
/// Definitions) is registered there and nothing after. Runtime loading of the assembly is the Game's concern.
/// </summary>
public interface IPlugin
{
    public PluginManifest Manifest { get; }

    public void Register(PluginRegistration registration);
}

/// <summary>A Plugin could not be loaded or registered (spec section 8): duplicate id, missing dependency, cycle, API version, a failing Register, an illegal System replacement.</summary>
public sealed class PluginException : InvalidOperationException
{
    public PluginException()
    {
    }

    public PluginException(string message)
        : base(message)
    {
    }

    public PluginException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PluginException(string pluginId, string message)
        : base(message)
    {
        PluginId = pluginId;
    }

    public PluginException(string pluginId, string message, Exception innerException)
        : base(message, innerException)
    {
        PluginId = pluginId;
    }

    /// <summary>The Plugin at fault, when one can be named.</summary>
    public string? PluginId { get; }
}
