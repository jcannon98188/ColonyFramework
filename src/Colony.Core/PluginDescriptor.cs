namespace Colony;

/// <summary>
/// What a <see cref="WorldSchema"/> records about one Plugin that registered into it (spec sections 7.1 and 8):
/// the stable id, the version and whether it declared itself Deterministic. Core keeps this as provenance only;
/// the Plugin loader, manifests and ordering live in Colony.Plugins. <see cref="Index"/> is the order in which
/// Plugins registered, which is the load order.
/// </summary>
public readonly record struct PluginDescriptor(int Index, string Id, string Version, bool Deterministic);

/// <summary>A System of one Plugin replaced by another Plugin's definition (spec section 8); recorded in the save manifest.</summary>
public readonly record struct SystemOverride(string System, string OriginalPlugin, string ReplacingPlugin);
