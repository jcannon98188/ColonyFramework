using Colony.Persistence;

namespace Colony.Diagnostics.Inspection;

/// <summary>
/// Diffs of saves (spec section 10.2, the <c>diff</c> command). <see cref="CompareManifests"/> needs no schema:
/// it compares what the two save manifests declare (Tick, hashes, seed, the Component, Resource, extension and
/// Plugin lists). With the Game's schema, <see cref="Compare(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, WorldSchema, int)"/>
/// loads both saves and compares them Entity by Entity through <see cref="WorldDiff"/>; a live World against a
/// save goes through the server's <c>diff</c> op, which has the schema.
/// </summary>
public static class SaveDiff
{
    private static KeyValuePair<string, InspectValue> P(string key, InspectValue value) => new(key, value);

    public static InspectValue CompareManifests(SaveManifest left, SaveManifest right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var changed = new List<InspectValue>();
        void Check(bool same, string what)
        {
            if (!same)
            {
                changed.Add(what);
            }
        }

        Check(left.SchemaHash == right.SchemaHash, "schemaHash");
        Check(left.Tick == right.Tick, "tick");
        Check(left.WorldHash == right.WorldHash, "worldHash");
        Check(left.Seed == right.Seed, "seed");
        Check(left.ContainerVersion == right.ContainerVersion, "containerVersion");

        var components = NamedDiff(left.Components.Select(c => (c.Name, (object)c)), right.Components.Select(c => (c.Name, (object)c)));
        var resources = NamedDiff(left.Resources.Select(r => (r.Name, (object)r)), right.Resources.Select(r => (r.Name, (object)r)));
        var extensions = NamedDiff(left.Extensions.Select(e => (e.Name, (object)e)), right.Extensions.Select(e => (e.Name, (object)e)));
        var plugins = NamedDiff(left.Plugins.Select(p => (p.Id, (object)p)), right.Plugins.Select(p => (p.Id, (object)p)));
        Check(components.Items.Count == 0, "components");
        Check(resources.Items.Count == 0, "resources");
        Check(extensions.Items.Count == 0, "extensions");
        Check(plugins.Items.Count == 0, "plugins");

        return InspectValue.Map(
            P("identical", changed.Count == 0),
            P("sameSchema", left.SchemaHash == right.SchemaHash),
            P("changed", InspectValue.List(changed)),
            P("left", Summary(left)),
            P("right", Summary(right)),
            P("components", components),
            P("resources", resources),
            P("extensions", extensions),
            P("plugins", plugins));
    }

    /// <summary>Loads both saves with <paramref name="schema"/> and compares them Entity by Entity.</summary>
    public static WorldDiffReport Compare(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right, WorldSchema schema, int limit = 64)
    {
        var options = new LoadOptions { VerifyWorldHash = false, WorldOptions = new WorldOptions { WorkerThreads = 1 } };
        using var a = WorldPersistence.Load(left, schema, options);
        using var b = WorldPersistence.Load(right, schema, options);
        return WorldDiff.Compare(a, b, limit);
    }

    private static InspectValue Summary(SaveManifest m) => InspectValue.Map(
        P("tick", m.Tick),
        P("worldHash", InspectValue.From(m.WorldHash)),
        P("schemaHash", InspectValue.From(m.SchemaHash)),
        P("seed", InspectValue.From(m.Seed)),
        P("components", m.Components.Length),
        P("resources", m.Resources.Length));

    /// <summary>Entries present on one side only, or present on both with a different declaration, by name.</summary>
    private static InspectValue NamedDiff(IEnumerable<(string Name, object Value)> left, IEnumerable<(string Name, object Value)> right)
    {
        var l = left.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        var r = right.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        var differences = new List<InspectValue>();
        foreach (string name in l.Keys.Union(r.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            bool inLeft = l.TryGetValue(name, out var a);
            bool inRight = r.TryGetValue(name, out var b);
            string? kind = !inRight ? "only-left" : !inLeft ? "only-right" : Equals(a, b) ? null : "changed";
            if (kind is not null)
            {
                differences.Add(InspectValue.Map(P("name", name), P("kind", kind)));
            }
        }

        return InspectValue.List(differences);
    }
}
