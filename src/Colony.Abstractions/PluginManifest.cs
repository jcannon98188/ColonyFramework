namespace Colony.Plugins;

/// <summary>The version numbers of the Framework's extension surfaces (spec section 8): recorded in manifests and saves, separate from the package version.</summary>
public static class ColonyApi
{
    /// <summary>The C# API version a Plugin compiles against (<see cref="PluginManifest.RequiredApiVersion"/>). No compatibility promise during 0.x.</summary>
    public const int Version = 1;

    /// <summary>The C ABI version (spec section 8): the <c>COLONY_ABI_VERSION</c> of <c>colony.h</c>, hosted by Colony.Plugins and exported by Colony.Native.</summary>
    public const int AbiVersion = 1;
}

/// <summary>
/// What a Plugin declares about itself (spec section 8): a stable <see cref="Id"/>, its <see cref="Version"/>,
/// the C# API version it was written against, the Plugins it must load after, and whether it keeps the
/// determinism rules (a NonDeterministic Plugin makes every World it registers into report
/// non-deterministic). Pure data: Plugins compile against this without a Core reference.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Stable identity across versions and saves (for example <c>acme.farming</c>); compared ordinally.</summary>
    public required string Id { get; init; }

    /// <summary>The Plugin's own version string, recorded in saves; the Framework does not interpret it.</summary>
    public required string Version { get; init; }

    /// <summary>The <see cref="ColonyApi.Version"/> the Plugin needs; a newer requirement than the host fails at World build.</summary>
    public int RequiredApiVersion { get; init; } = ColonyApi.Version;

    /// <summary>
    /// The <see cref="ColonyApi.AbiVersion"/> a Native Plugin was built against (its <c>colony_plugin_desc.abi_version</c>),
    /// or 0 for a C# Plugin. Recorded in the schema and in saves; a newer version than the host fails at World build.
    /// </summary>
    public int AbiVersion { get; init; }

    /// <summary>Ids of Plugins that must register before this one; they may be replaced or patched by it.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>True when the Plugin's Systems follow the determinism rules (spec section 6). Default true.</summary>
    public bool Deterministic { get; init; } = true;
}
