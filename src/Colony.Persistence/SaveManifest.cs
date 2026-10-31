using System.Collections.Immutable;

namespace Colony.Persistence;

public readonly record struct SavedComponent(string Name, int Size, int Alignment, ulong LayoutHash, ComponentOptions Flags, int ElementSize, int InlineCapacity);

public readonly record struct SavedResource(string Name, int Size, ulong LayoutHash, ResourceKind Kind, bool IsPersistent);

public readonly record struct SavedExtension(string Name, ulong LayoutHash);

public readonly record struct SavedRelation(string Name, RelationOptions Options);

/// <summary>
/// The manifest section of a save (spec section 7.1): the embedded schema table (every Component with its
/// layout hash, the Resources, extensions and Relations), the Plugin set (a placeholder until Plugins exist),
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
        ImmutableArray<string> plugins)
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

    /// <summary>The Plugin set the World ran with; empty until the Plugins package exists.</summary>
    public ImmutableArray<string> Plugins { get; }

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
            ImmutableArray<string>.Empty);
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

        w.WriteInt32(Plugins.Length);
        foreach (var p in Plugins)
        {
            w.WriteString(p);
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
        var plugins = ImmutableArray.CreateBuilder<string>(count);
        for (int i = 0; i < count; i++)
        {
            plugins.Add(r.ReadString());
        }

        r.ExpectEnd();
        return new SaveManifest(containerVersion, schemaHash, tick, worldHash, seed, components.MoveToImmutable(), resources.MoveToImmutable(), extensions.MoveToImmutable(), relations.MoveToImmutable(), plugins.MoveToImmutable());
    }
}
