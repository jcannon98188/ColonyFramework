namespace Colony;

/// <summary>
/// The build-time scheduling plan (spec section 4.3): per Phase, the Systems in stable topological order
/// (After/Before constraints, ties by System id) and the wave each runs in. Built once by
/// <see cref="WorldBuilder.Build"/>, validated there, shared by every World of the schema.
/// </summary>
internal sealed class SystemSchedule
{
    private SystemSchedule(SystemDefinition[] definitions, SystemDescriptor[] descriptors, PhasePlan[] phases, ComponentMask[] queries)
    {
        Definitions = definitions;
        Descriptors = descriptors;
        Phases = phases;
        Queries = queries;
    }

    /// <summary>By System id.</summary>
    public SystemDefinition[] Definitions { get; }

    public SystemDescriptor[] Descriptors { get; }

    /// <summary>In Tick order, parallel to <see cref="WorldSchema.Phases"/>.</summary>
    public PhasePlan[] Phases { get; }

    /// <summary>Resolved "All" query mask per System id.</summary>
    public ComponentMask[] Queries { get; }

    public int SystemCount => Definitions.Length;

    public static SystemSchedule Build(List<SystemDefinition> definitions, List<Phase> phases, int componentCount)
    {
        var defs = definitions.ToArray();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < defs.Length; i++)
        {
            byName.Add(defs[i].Name, i);
        }

        var descriptors = new SystemDescriptor[defs.Length];
        var queries = new ComponentMask[defs.Length];
        var plans = new PhasePlan[phases.Count];
        for (int p = 0; p < phases.Count; p++)
        {
            plans[p] = BuildPhase(phases[p], defs, byName, descriptors);
        }

        for (int i = 0; i < defs.Length; i++)
        {
            var def = defs[i];
            if (!phases.Contains(def.Phase))
            {
                throw new InvalidOperationException($"System '{def.Name}' is in Phase '{def.Phase.Name}', which this builder did not define.");
            }

            ValidateMask(def.All, componentCount, def.Name, "All");
            ValidateMask(def.None, componentCount, def.Name, "None");
            ValidateMask(def.Reads, componentCount, def.Name, "Reads");
            ValidateMask(def.Writes, componentCount, def.Name, "Writes");
            ValidateMask(def.ChangeFilter, componentCount, def.Name, "ChangeFilter");
            queries[i] = def.All.IsEmpty ? def.Reads.Union(def.Writes) : def.All;
        }

        return new SystemSchedule(defs, descriptors, plans, queries);
    }

    private static void ValidateMask(ComponentMask mask, int componentCount, string system, string what)
    {
        if (mask.Count == 0)
        {
            return;
        }

        Span<int> ids = stackalloc int[mask.Count];
        mask.CopyIds(ids);
        if (ids[^1] >= componentCount)
        {
            throw new InvalidOperationException($"System '{system}' names Component id {ids[^1]} in {what}, but the schema registers only {componentCount} Components.");
        }
    }

    private static PhasePlan BuildPhase(Phase phase, SystemDefinition[] defs, Dictionary<string, int> byName, SystemDescriptor[] descriptors)
    {
        var members = new List<int>();
        for (int i = 0; i < defs.Length; i++)
        {
            if (defs[i].Phase == phase)
            {
                members.Add(i);
            }
        }

        int n = members.Count;
        var edges = new bool[n, n]; // edges[a, b]: a must run before b (local indices)
        var local = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            local[members[i]] = i;
        }

        for (int i = 0; i < n; i++)
        {
            var def = defs[members[i]];
            foreach (string name in def.After)
            {
                edges[Resolve(name, def, phase, byName, defs, local), i] = true;
            }

            foreach (string name in def.Before)
            {
                edges[i, Resolve(name, def, phase, byName, defs, local)] = true;
            }
        }

        // Stable topological sort: Kahn's algorithm always taking the ready node with the smallest System id.
        var order = new int[n];
        var placed = new bool[n];
        var position = new int[n];
        for (int k = 0; k < n; k++)
        {
            int pick = -1;
            for (int i = 0; i < n; i++)
            {
                if (placed[i])
                {
                    continue;
                }

                bool ready = true;
                for (int a = 0; a < n && ready; a++)
                {
                    ready = placed[a] || !edges[a, i];
                }

                if (ready)
                {
                    pick = i;
                    break;
                }
            }

            if (pick < 0)
            {
                throw new InvalidOperationException($"After/Before constraints among Systems in Phase '{phase.Name}' form a cycle: {DescribeUnplaced(members, placed, defs)}.");
            }

            placed[pick] = true;
            order[k] = pick;
            position[pick] = k;
        }

        // Transitive ordering, then conflict checks and wave levels in topological order.
        var reach = new bool[n, n]; // reach[a, b]: a is ordered before b by constraints
        for (int k = 0; k < n; k++)
        {
            int u = order[k];
            for (int v = 0; v < n; v++)
            {
                if (!edges[u, v])
                {
                    continue;
                }

                reach[u, v] = true;
                for (int w = 0; w < n; w++)
                {
                    reach[w, v] |= reach[w, u];
                }
            }
        }

        var level = new int[n];
        int waves = 0;
        for (int kb = 0; kb < n; kb++)
        {
            int b = order[kb];
            var bd = defs[members[b]];
            for (int ka = 0; ka < kb; ka++)
            {
                int a = order[ka];
                var ad = defs[members[a]];
                bool conflicts = Conflicts(ad, bd);
                if (conflicts && !reach[a, b])
                {
                    throw new InvalidOperationException(
                        $"Systems '{ad.Name}' and '{bd.Name}' in Phase '{phase.Name}' both touch {DescribeConflict(ad, bd)} but no After/Before constraint orders them; declare one so the result cannot depend on scheduling.");
                }

                if (conflicts || reach[a, b])
                {
                    level[b] = Math.Max(level[b], level[a] + 1);
                }
            }

            waves = Math.Max(waves, level[b] + 1);
        }

        var systems = new int[n];
        var waveOf = new int[n];
        for (int k = 0; k < n; k++)
        {
            int id = members[order[k]];
            systems[k] = id;
            waveOf[k] = level[order[k]];
            var def = defs[id];
            if (def.Every < 1)
            {
                throw new InvalidOperationException($"System '{def.Name}' has Every = {def.Every}; it must be at least 1.");
            }

            if (def.Offset >= def.Every)
            {
                throw new InvalidOperationException($"System '{def.Name}' has Offset = {def.Offset}, which is not below Every = {def.Every}.");
            }

            int offset = def.Offset < 0 ? id % def.Every : def.Offset;
            descriptors[id] = new SystemDescriptor(id, def.Name, phase, k, level[order[k]], def.Reads, def.Writes, def.Every, offset);
        }

        return new PhasePlan(phase, systems, waveOf, waves);
    }

    private static int Resolve(string name, SystemDefinition def, Phase phase, Dictionary<string, int> byName, SystemDefinition[] defs, Dictionary<int, int> local)
    {
        if (!byName.TryGetValue(name, out int id))
        {
            throw new InvalidOperationException($"System '{def.Name}' orders itself against '{name}', which is not registered.");
        }

        if (id == byName[def.Name])
        {
            throw new InvalidOperationException($"System '{def.Name}' orders itself against itself.");
        }

        if (!local.TryGetValue(id, out int index))
        {
            throw new InvalidOperationException($"System '{def.Name}' (Phase '{phase.Name}') orders itself against '{name}' (Phase '{defs[id].Phase.Name}'); constraints work within one Phase, Phases already order the rest.");
        }

        return index;
    }

    internal static bool Conflicts(SystemDefinition a, SystemDefinition b)
        => a.Writes.Intersects(b.Reads) || a.Writes.Intersects(b.Writes) || b.Writes.Intersects(a.Reads);

    private static string DescribeConflict(SystemDefinition a, SystemDefinition b)
    {
        var shared = a.Writes.Intersect(b.Reads.Union(b.Writes)).Union(b.Writes.Intersect(a.Reads));
        Span<int> ids = stackalloc int[shared.Count];
        shared.CopyIds(ids);
        return ids.Length == 1
            ? $"Component {ids[0]}"
            : $"Components {string.Join(", ", ids.ToArray())}";
    }

    private static string DescribeUnplaced(List<int> members, bool[] placed, SystemDefinition[] defs)
    {
        var names = new List<string>();
        for (int i = 0; i < placed.Length; i++)
        {
            if (!placed[i])
            {
                names.Add(defs[members[i]].Name);
            }
        }

        return string.Join(", ", names);
    }

    /// <summary>One Phase's Systems in stable order with the wave of each.</summary>
    internal sealed class PhasePlan
    {
        public PhasePlan(Phase phase, int[] systems, int[] waveOf, int waveCount)
        {
            Phase = phase;
            Systems = systems;
            WaveOf = waveOf;
            WaveCount = waveCount;
        }

        public Phase Phase { get; }

        /// <summary>System ids in stable topological order.</summary>
        public int[] Systems { get; }

        /// <summary>Wave of the System at the same index of <see cref="Systems"/>; waves are non-decreasing in this order.</summary>
        public int[] WaveOf { get; }

        public int WaveCount { get; }
    }
}
