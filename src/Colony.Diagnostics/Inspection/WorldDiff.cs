namespace Colony.Diagnostics.Inspection;

/// <summary>One difference between two Worlds (or a World and a save).</summary>
/// <param name="Kind"><c>tick</c>, <c>entity-only-left</c>, <c>entity-only-right</c>, <c>component-only-left</c>, <c>component-only-right</c>, <c>component</c>, <c>enabled</c> or <c>resource</c>.</param>
/// <param name="Entity">The Entity, or <see cref="EntityId.Null"/> for World-level differences.</param>
/// <param name="Name">The Component or Resource name, or null.</param>
public readonly record struct WorldDifference(string Kind, EntityId Entity, string? Name);

/// <summary>The outcome of <see cref="WorldDiff.Compare"/>.</summary>
public sealed class WorldDiffReport
{
    internal WorldDiffReport(long leftTick, long rightTick, ulong leftHash, ulong rightHash, IReadOnlyList<WorldDifference> differences, long total)
    {
        LeftTick = leftTick;
        RightTick = rightTick;
        LeftHash = leftHash;
        RightHash = rightHash;
        Differences = differences;
        Total = total;
    }

    public long LeftTick { get; }

    public long RightTick { get; }

    public ulong LeftHash { get; }

    public ulong RightHash { get; }

    /// <summary>The first differences in Entity id, then schema order (at most the requested limit).</summary>
    public IReadOnlyList<WorldDifference> Differences { get; }

    /// <summary>All differences found, including those past the limit.</summary>
    public long Total { get; }

    public bool Identical => Total == 0;

    public InspectValue ToValue()
    {
        var list = new List<InspectValue>(Differences.Count);
        foreach (var d in Differences)
        {
            list.Add(InspectValue.Map(
                new("kind", d.Kind),
                new("entity", d.Entity == EntityId.Null ? InspectValue.Null : InspectValue.From(d.Entity.Value)),
                new("name", d.Name is null ? InspectValue.Null : InspectValue.From(d.Name))));
        }

        return InspectValue.Map(
            new("identical", Identical),
            new("leftTick", LeftTick),
            new("rightTick", RightTick),
            new("leftHash", InspectValue.From(LeftHash)),
            new("rightHash", InspectValue.From(RightHash)),
            new("total", Total),
            new("differences", InspectValue.List(list)));
    }
}

/// <summary>
/// Entity-level comparison of two Worlds built from the same schema (spec section 10.2, the <c>diff</c> command):
/// which Entities exist on one side only, which Components differ in presence, enabled state or bytes, and which
/// Resources differ. Load a save with <c>WorldPersistence.Load</c> to compare a World with a save, or two saves.
/// </summary>
public static class WorldDiff
{
    public static WorldDiffReport Compare(World left, World right, int limit = 64)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Schema.SchemaHash != right.Schema.SchemaHash)
        {
            throw new ArgumentException("The Worlds were built from different schemas.", nameof(right));
        }

        var schema = left.Schema;
        var found = new List<WorldDifference>();
        long total = 0;
        void Report(string kind, EntityId entity, string? name)
        {
            if (found.Count < limit)
            {
                found.Add(new WorldDifference(kind, entity, name));
            }

            total++;
        }

        if (left.CurrentTick != right.CurrentTick)
        {
            Report("tick", EntityId.Null, null);
        }

        var leftIds = LiveEntities(left);
        var rightIds = LiveEntities(right);
        var all = new SortedSet<ulong>(leftIds);
        all.UnionWith(rightIds);
        foreach (ulong raw in all)
        {
            var id = new EntityId(raw);
            bool inLeft = leftIds.Contains(raw);
            bool inRight = rightIds.Contains(raw);
            if (!inRight)
            {
                Report("entity-only-left", id, null);
                continue;
            }

            if (!inLeft)
            {
                Report("entity-only-right", id, null);
                continue;
            }

            foreach (ref readonly var c in schema.Components)
            {
                bool l = left.Has(id, c.Type);
                bool r = right.Has(id, c.Type);
                if (l != r)
                {
                    Report(l ? "component-only-left" : "component-only-right", id, c.Name);
                }
                else if (l)
                {
                    if (left.IsEnabled(id, c.Type) != right.IsEnabled(id, c.Type))
                    {
                        Report("enabled", id, c.Name);
                    }

                    if (!left.ComponentBytes(id, c.Type).SequenceEqual(right.ComponentBytes(id, c.Type)))
                    {
                        Report("component", id, c.Name);
                    }
                }
            }
        }

        foreach (ref readonly var r in schema.Resources)
        {
            bool same = r.IsEventQueue
                ? left.EventBytes(r.Type).SequenceEqual(right.EventBytes(r.Type))
                : left.ResourceBytes(r.Type).SequenceEqual(right.ResourceBytes(r.Type));
            if (!same)
            {
                Report("resource", EntityId.Null, r.Name);
            }
        }

        return new WorldDiffReport(left.CurrentTick, right.CurrentTick, left.Hash(), right.Hash(), found, total);
    }

    private static HashSet<ulong> LiveEntities(World world)
    {
        var ids = new HashSet<ulong>();
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            int chunks = world.DescribeArchetype(a).ChunkCount;
            for (int c = 0; c < chunks; c++)
            {
                foreach (var id in world.ArchetypeEntities(a, c))
                {
                    ids.Add(id.Value);
                }
            }
        }

        return ids;
    }
}
