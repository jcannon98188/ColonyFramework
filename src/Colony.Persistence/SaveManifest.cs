using System.Collections.Immutable;

namespace Colony.Persistence;

public readonly record struct SavedComponent(string Name, int Size, int Alignment, ulong LayoutHash, ComponentOptions Flags, int ElementSize, int InlineCapacity);

public readonly record struct SavedResource(string Name, int Size, ulong LayoutHash, ResourceKind Kind, bool IsPersistent);

public readonly record struct SavedExtension(string Name, ulong LayoutHash);

public readonly record struct SavedRelation(string Name, RelationOptions Options);

/// <summary>An Overridable System another Plugin replaced (spec section 8): the System's name and the Plugin that registered the original.</summary>
public readonly record struct SavedSystemOverride(string System, string OriginalPlugin);

/// <summary>
/// One Plugin of the saved World's Plugin set, in load order: its id and version, whether it is deterministic,
/// the Components and Resources it registered (names into <see cref="SaveManifest.Components"/> and
/// <see cref="SaveManifest.Resources"/>), the Systems it replaced, and the C ABI version a Native Plugin was
/// built against (0 for a C# Plugin; spec section 8, versioning).
/// </summary>
public sealed record SavedPlugin(
    string Id,
    string Version,
    bool Deterministic,
    ImmutableArray<string> Components,
    ImmutableArray<string> Resources,
    ImmutableArray<SavedSystemOverride> Overrides,
    int AbiVersion = 0);

/// <summary>
/// The manifest section of a save (spec section 7.1): the embedded schema table (every Component with its
/// layout hash, the Resources, extensions and Relations), the Plugin set the World ran with (spec section 8),
/// the Tick, the World hash and the seed. Readable without loading the World.
/// </summary>
public sealed class SaveManifest
{
    internal SaveManifest(
        int containerVersion,
        ulong schemaHash,
        long tick,
        ulong worldHash,
        ulong seed,
        ImmutableArray<SavedComponent> components,
        ImmutableArray<SavedResource> resources,
        ImmutableArray<SavedExtension> extensions,
        ImmutableArray<SavedRelation> relations,
        ImmutableArray<SavedPlugin> plugins)
    {
        ContainerVersion = containerVersion;
        SchemaHash = schemaHash;
        Tick = tick;
        WorldHash = worldHash;
        Seed = seed;
        Components = components;
        Resources = resources;
        Extensions = extensions;
        Relations = relations;
        Plugins = plugins;
    }

    public int ContainerVersion { get; }

    public ulong SchemaHash { get; }

    public long Tick { get; }

    public ulong WorldHash { get; }

    public ulong Seed { get; }

    /// <summary>Saved Components in saved id order (ids inside the archetype sections index this table).</summary>
    public ImmutableArray<SavedComponent> Components { get; }

    public ImmutableArray<SavedResource> Resources { get; }

    public ImmutableArray<SavedExtension> Extensions { get; }

    public ImmutableArray<SavedRelation> Relations { get; }

    /// <summary>The Plugin set the World ran with, in load order; empty for a World built without Plugins.</summary>
    public ImmutableArray<SavedPlugin> Plugins { get; }

    /// <summary>False when any Plugin declared itself non-deterministic: the World's hash and replay carry no guarantee (<see cref="World.IsDeterministic"/>).</summary>
    public bool IsDeterministic
    {
        get
        {
            foreach (var plugin in Plugins)
            {
                if (!plugin.Deterministic)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal static SaveManifest Capture(World world, ulong worldHash)
    {
        var schema = world.Schema;
        var components = ImmutableArray.CreateBuilder<SavedComponent>(schema.ComponentCount);
        foreach (ref readonly var c in schema.Components)
        {
            components.Add(new SavedComponent(c.Name, c.Size, c.Alignment, c.LayoutHash, c.Flags, c.ElementSize, c.InlineCapacity));
        }

        var resources = ImmutableArray.CreateBuilder<SavedResource>(schema.ResourceCount);
        foreach (ref readonly var r in schema.Resources)
        {
            resources.Add(new SavedResource(r.Name, r.Size, r.LayoutHash, r.Kind, r.IsPersistent));
        }

        var extensions = ImmutableArray.CreateBuilder<SavedExtension>(schema.Extensions.Length);
        foreach (var e in schema.Extensions)
        {
            extensions.Add(new SavedExtension(e.Name, e.LayoutHash));
        }

        var relations = ImmutableArray.CreateBuilder<SavedRelation>(schema.RelationCount);
        foreach (ref readonly var r in schema.Relations)
        {
            relations.Add(new SavedRelation(r.Name, r.Options));
        }

        return new SaveManifest(
            SaveContainer.Version,
            schema.SchemaHash,
            world.CurrentTick,
            worldHash,
            world.Seed,
            components.MoveToImmutable(),
            resources.MoveToImmutable(),
            extensions.MoveToImmutable(),
            relations.MoveToImmutable(),
            CapturePlugins(schema));
    }

    private static ImmutableArray<SavedPlugin> CapturePlugins(WorldSchema schema)
    {
        var plugins = ImmutableArray.CreateBuilder<SavedPlugin>(schema.Plugins.Length);
        foreach (var plugin in schema.Plugins)
        {
            var components = ImmutableArray.CreateBuilder<string>();
            foreach (ref readonly var c in schema.Components)
            {
                if (schema.ComponentOwner(c.Type) == plugin.Index)
                {
                    components.Add(c.Name);
                }
            }

            var resources = ImmutableArray.CreateBuilder<string>();
            foreach (ref readonly var r in schema.Resources)
            {
                if (schema.ResourceOwner(schema.Resource(r.Name)) == plugin.Index)
                {
                    resources.Add(r.Name);
                }
            }

            var overrides = ImmutableArray.CreateBuilder<SavedSystemOverride>();
            foreach (var o in schema.SystemOverrides)
            {
                if (string.Equals(o.ReplacingPlugin, plugin.Id, StringComparison.Ordinal))
                {
                    overrides.Add(new SavedSystemOverride(o.System, o.OriginalPlugin));
                }
            }

            plugins.Add(new SavedPlugin(plugin.Id, plugin.Version, plugin.Deterministic, components.ToImmutable(), resources.ToImmutable(), overrides.ToImmutable(), plugin.AbiVersion));
        }

        return plugins.MoveToImmutable();
    }

    internal byte[] Encode()
    {
        var w = new ByteWriter(1024);
        w.WriteUInt64(SchemaHash);
        w.WriteInt64(Tick);
        w.WriteUInt64(WorldHash);
        w.WriteUInt64(Seed);
        w.WriteInt32(Components.Length);
        foreach (var c in Components)
        {
            w.WriteString(c.Name);
            w.WriteInt32(c.Size);
            w.WriteInt32(c.Alignment);
            w.WriteUInt64(c.LayoutHash);
            w.WriteUInt32((uint)c.Flags);
            w.WriteInt32(c.ElementSize);
            w.WriteInt32(c.InlineCapacity);
        }

        w.WriteInt32(Resources.Length);
        foreach (var r in Resources)
        {
            w.WriteString(r.Name);
            w.WriteInt32(r.Size);
            w.WriteUInt64(r.LayoutHash);
            w.WriteUInt32((uint)r.Kind);
            w.WriteByte(r.IsPersistent ? (byte)1 : (byte)0);
        }

        w.WriteInt32(Extensions.Length);
        foreach (var e in Extensions)
        {
            w.WriteString(e.Name);
            w.WriteUInt64(e.LayoutHash);
        }

        w.WriteInt32(Relations.Length);
        foreach (var r in Relations)
        {
            w.WriteString(r.Name);
            w.WriteUInt32((uint)r.Options);
        }

        // Everything about a Plugin sits inside its entry, so a save without Plugins encodes exactly as before.
        w.WriteInt32(Plugins.Length);
        foreach (var p in Plugins)
        {
            w.WriteString(p.Id);
            w.WriteString(p.Version);
            w.WriteByte(p.Deterministic ? (byte)1 : (byte)0);
            w.WriteInt32(p.Components.Length);
            foreach (var name in p.Components)
            {
                w.WriteString(name);
            }

            w.WriteInt32(p.Resources.Length);
            foreach (var name in p.Resources)
            {
                w.WriteString(name);
            }

            w.WriteInt32(p.Overrides.Length);
            foreach (var o in p.Overrides)
            {
                w.WriteString(o.System);
                w.WriteString(o.OriginalPlugin);
            }

            w.WriteInt32(p.AbiVersion);
        }

        return w.ToArray();
    }

    internal static SaveManifest Decode(ReadOnlySpan<byte> payload, int containerVersion)
    {
        var r = new ByteReader(payload, SaveContainer.ManifestSection);
        ulong schemaHash = r.ReadUInt64();
        long tick = r.ReadInt64();
        ulong worldHash = r.ReadUInt64();
        ulong seed = r.ReadUInt64();

        int count = r.ReadCount(1);
        var components = ImmutableArray.CreateBuilder<SavedComponent>(count);
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            int size = r.ReadInt32();
            int alignment = r.ReadInt32();
            ulong layout = r.ReadUInt64();
            var flags = (ComponentOptions)r.ReadUInt32();
            int elementSize = r.ReadInt32();
            int inline = r.ReadInt32();
            components.Add(new SavedComponent(name, size, alignment, layout, flags, elementSize, inline));
        }

        count = r.ReadCount(1);
        var resources = ImmutableArray.CreateBuilder<SavedResource>(count);
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            int size = r.ReadInt32();
            ulong layout = r.ReadUInt64();
            var kind = (ResourceKind)r.ReadUInt32();
            bool persistent = r.ReadByte() != 0;
            resources.Add(new SavedResource(name, size, layout, kind, persistent));
        }

        count = r.ReadCount(1);
        var extensions = ImmutableArray.CreateBuilder<SavedExtension>(count);
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            extensions.Add(new SavedExtension(name, r.ReadUInt64()));
        }

        count = r.ReadCount(1);
        var relations = ImmutableArray.CreateBuilder<SavedRelation>(count);
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            relations.Add(new SavedRelation(name, (RelationOptions)r.ReadUInt32()));
        }

        count = r.ReadCount(1);
        var plugins = ImmutableArray.CreateBuilder<SavedPlugin>(count);
        for (int i = 0; i < count; i++)
        {
            string id = r.ReadString();
            string version = r.ReadString();
            bool deterministic = r.ReadByte() != 0;
            var ownedComponents = ReadNames(ref r);
            var ownedResources = ReadNames(ref r);
            var overrides = ReadOverrides(ref r);
            plugins.Add(new SavedPlugin(id, version, deterministic, ownedComponents, ownedResources, overrides, r.ReadInt32()));
        }

        r.ExpectEnd();
        return new SaveManifest(containerVersion, schemaHash, tick, worldHash, seed, components.MoveToImmutable(), resources.MoveToImmutable(), extensions.MoveToImmutable(), relations.MoveToImmutable(), plugins.MoveToImmutable());
    }

    private static ImmutableArray<string> ReadNames(ref ByteReader r)
    {
        int count = r.ReadCount(1);
        var names = ImmutableArray.CreateBuilder<string>(count);
        for (int i = 0; i < count; i++)
        {
            names.Add(r.ReadString());
        }

        return names.MoveToImmutable();
    }

    private static ImmutableArray<SavedSystemOverride> ReadOverrides(ref ByteReader r)
    {
        int count = r.ReadCount(1);
        var overrides = ImmutableArray.CreateBuilder<SavedSystemOverride>(count);
        for (int i = 0; i < count; i++)
        {
            string system = r.ReadString();
            overrides.Add(new SavedSystemOverride(system, r.ReadString()));
        }

        return overrides.MoveToImmutable();
    }
}
