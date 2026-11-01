using System.Runtime.InteropServices;
using Colony.Fields;
using Colony.Hashing;

namespace Colony.ProcGen;

/// <summary>What the pipeline knows about one stage after ordering.</summary>
/// <param name="Name">Stage name.</param>
/// <param name="Order">Position in execution order.</param>
/// <param name="Stage">The stage instance.</param>
/// <param name="IsBase">True when neighbours' halo reads need this stage (it is then run for neighbour chunks too).</param>
/// <param name="ChunkLocal">The stage's <see cref="GenerationStage.ChunkLocal"/>.</param>
/// <param name="Overridable">Whether a later registration may replace it.</param>
/// <param name="Owner">Registering Plugin id, or null.</param>
public sealed record StageInfo(string Name, int Order, GenerationStage Stage, bool IsBase, bool ChunkLocal, bool Overridable, string? Owner);

/// <summary>
/// The frozen generation pipeline (spec section 9.3): stages in execution order with their Field access
/// resolved, validated and ready to drive a <see cref="ChunkProvider"/>. Built by
/// <see cref="GenerationBuilder.Pipeline"/>. Ordering: Kahn over the stages' <see cref="GenerationStage.After"/>
/// and <see cref="GenerationStage.Before"/> names, ties by registration order. The build fails on unknown
/// names, cycles, undeclared or unknown Fields, two stages touching one Field in an unspecified order, a read
/// with no earlier writer, and a halo read whose base data depends on another halo read.
/// </summary>
public sealed class GenerationPipeline
{
    private readonly StageInfo[] _stages;
    private readonly FieldDescriptor[] _slots;
    private readonly int[] _slotOfField;
    private readonly Dictionary<Type, int> _prePassByType = [];

    internal GenerationPipeline(FieldsBuilder fields, List<GenerationBuilder.StageRegistration> registrations, List<GenerationPrePass> prePasses, int version)
    {
        Size = fields.Size;
        Version = version;
        PrePasses = [.. prePasses];
        for (int i = 0; i < PrePasses.Length; i++)
        {
            _prePassByType.Add(PrePasses[i].ValueType, i);
        }

        int n = registrations.Count;
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            byName.Add(registrations[i].Stage.Name, i);
        }

        // Fields: slots are the Fields any stage touches, in Field id order.
        var touched = new SortedDictionary<int, FieldDescriptor>();
        var reads = new List<int>[n];
        var writes = new List<int>[n];
        var halos = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            var stage = registrations[i].Stage;
            reads[i] = Resolve(fields, stage, stage.Reads, "reads", touched);
            writes[i] = Resolve(fields, stage, stage.Writes, "writes", touched);
            halos[i] = Resolve(fields, stage, stage.HaloReads, "halo-reads", touched);
        }

        _slots = [.. touched.Values];
        int maxId = _slots.Length == 0 ? -1 : _slots[^1].Id;
        _slotOfField = new int[maxId + 1];
        Array.Fill(_slotOfField, -1);
        for (int s = 0; s < _slots.Length; s++)
        {
            _slotOfField[_slots[s].Id] = s;
        }

        // Ordering edges and Kahn sort (ties: registration order).
        var successors = new List<int>[n];
        var indegree = new int[n];
        for (int i = 0; i < n; i++)
        {
            successors[i] = [];
        }

        for (int i = 0; i < n; i++)
        {
            var stage = registrations[i].Stage;
            foreach (string after in stage.After)
            {
                successors[Lookup(byName, stage, after, "After")].Add(i);
            }

            foreach (string before in stage.Before)
            {
                successors[i].Add(Lookup(byName, stage, before, "Before"));
            }
        }

        for (int i = 0; i < n; i++)
        {
            foreach (int j in successors[i])
            {
                indegree[j]++;
            }
        }

        var order = new List<int>(n);
        var ready = new SortedSet<int>();
        for (int i = 0; i < n; i++)
        {
            if (indegree[i] == 0)
            {
                ready.Add(i);
            }
        }

        while (ready.Count > 0)
        {
            int next = ready.Min;
            ready.Remove(next);
            order.Add(next);
            foreach (int j in successors[next])
            {
                if (--indegree[j] == 0)
                {
                    ready.Add(j);
                }
            }
        }

        if (order.Count != n)
        {
            var cycle = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (indegree[i] > 0)
                {
                    cycle.Add(registrations[i].Stage.Name);
                }
            }

            throw new InvalidOperationException($"Generation stages form an ordering cycle: {string.Join(", ", cycle)}.");
        }

        // Reachability over the constraint graph (stage counts are small).
        var reach = new bool[n, n];
        for (int k = n - 1; k >= 0; k--)
        {
            int i = order[k];
            foreach (int j in successors[i])
            {
                reach[i, j] = true;
                for (int m = 0; m < n; m++)
                {
                    reach[i, m] |= reach[j, m];
                }
            }
        }

        var position = new int[n];
        for (int k = 0; k < n; k++)
        {
            position[order[k]] = k;
        }

        // Access conflicts: writer vs reader/writer/halo-reader of one Field must be ordered.
        for (int a = 0; a < n; a++)
        {
            for (int b = a + 1; b < n; b++)
            {
                foreach (int field in Conflicts(writes[a], reads[b], writes[b], halos[b]).Concat(Conflicts(writes[b], reads[a], writes[a], halos[a])))
                {
                    if (!reach[a, b] && !reach[b, a])
                    {
                        throw new InvalidOperationException(
                            $"Generation stages '{registrations[a].Stage.Name}' and '{registrations[b].Stage.Name}' both use Field '{FieldName(field)}' and at least one writes it; order them with After or Before.");
                    }
                }
            }
        }

        // Reads need an earlier writer; halo reads need every writer earlier.
        for (int i = 0; i < n; i++)
        {
            foreach (int field in reads[i].Concat(halos[i]))
            {
                bool earlier = false;
                for (int w = 0; w < n; w++)
                {
                    if (w != i && writes[w].Contains(field) && position[w] < position[i])
                    {
                        earlier = true;
                    }
                }

                if (!earlier && !writes[i].Contains(field))
                {
                    throw new InvalidOperationException($"Generation stage '{registrations[i].Stage.Name}' reads Field '{FieldName(field)}' but no earlier stage writes it.");
                }
            }

            foreach (int field in halos[i])
            {
                for (int w = 0; w < n; w++)
                {
                    if (writes[w].Contains(field) && position[w] > position[i])
                    {
                        throw new InvalidOperationException(
                            $"Generation stage '{registrations[w].Stage.Name}' writes Field '{FieldName(field)}' after '{registrations[i].Stage.Name}' reads it through a halo; halo reads see base data only.");
                    }

                    if (w == i && writes[w].Contains(field))
                    {
                        throw new InvalidOperationException($"Generation stage '{registrations[i].Stage.Name}' both writes Field '{FieldName(field)}' and reads it through a halo.");
                    }
                }
            }
        }

        // Base closure: the stages a neighbour's halo-read Fields depend on, run for neighbour chunks.
        var isBase = new bool[n];
        var work = new Stack<(int Field, int Before)>();
        for (int i = 0; i < n; i++)
        {
            foreach (int field in halos[i])
            {
                work.Push((field, position[i]));
            }
        }

        while (work.Count > 0)
        {
            var (field, before) = work.Pop();
            for (int w = 0; w < n; w++)
            {
                if (position[w] < before && writes[w].Contains(field) && !isBase[w])
                {
                    if (halos[w].Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"Generation stage '{registrations[w].Stage.Name}' produces base data for a halo read but itself reads a halo; halo reads see neighbours' base data only.");
                    }

                    isBase[w] = true;
                    foreach (int read in reads[w].Concat(writes[w]))
                    {
                        work.Push((read, position[w]));
                    }
                }
            }
        }

        _stages = new StageInfo[n];
        ReadSlots = new bool[n][];
        WriteSlots = new bool[n][];
        HaloSlots = new bool[n][];
        StageKeys = new ulong[n];
        BaseStages = [.. order.Where(i => isBase[i]).Select(i => position[i])];
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            var registration = registrations[i];
            _stages[k] = new StageInfo(registration.Stage.Name, k, registration.Stage, isBase[i], registration.Stage.ChunkLocal, registration.Overridable, registration.Owner);
            ReadSlots[k] = Mask(reads[i]);
            WriteSlots[k] = Mask(writes[i]);
            HaloSlots[k] = Mask(halos[i]);
            StageKeys[k] = KeyOf(registration.Stage.Name);
        }

        HasHalo = Array.Exists(HaloSlots, m => Array.IndexOf(m, true) >= 0);
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>The World size the pipeline generates for.</summary>
    public WorldSize Size { get; }

    /// <summary>The generation version (<see cref="GenerationBuilder.GenerationVersion"/>).</summary>
    public int Version { get; }

    /// <summary>Stages in execution order.</summary>
    public IReadOnlyList<StageInfo> Stages => _stages;

    /// <summary>The Fields any stage touches, in Field id order: the cells a <see cref="GeneratedChunk"/> holds.</summary>
    public IReadOnlyList<FieldDescriptor> Fields => _slots;

    /// <summary>XXH64 over the version, stage names in order and the Fields' layout hashes: identifies the pipeline's shape (not its output).</summary>
    public ulong Fingerprint { get; }

    internal GenerationPrePass[] PrePasses { get; }

    internal bool[][] ReadSlots { get; }

    internal bool[][] WriteSlots { get; }

    internal bool[][] HaloSlots { get; }

    /// <summary>Hash64 of each stage name (UTF-16), the stage half of the randomness key.</summary>
    internal ulong[] StageKeys { get; }

    /// <summary>Execution-order indices of the base stages.</summary>
    internal int[] BaseStages { get; }

    internal bool HasHalo { get; }

    internal int SlotCount => _slots.Length;

    /// <summary>A provider generating chunks of this pipeline for one seed; computes the pre-passes once.</summary>
    public ChunkProvider CreateProvider(ulong seed, ChunkProviderOptions? options = null) => new(this, seed, options ?? new ChunkProviderOptions());

    /// <summary>Position of a stage in execution order, or -1.</summary>
    public int IndexOf(string stageName)
    {
        ArgumentNullException.ThrowIfNull(stageName);
        for (int i = 0; i < _stages.Length; i++)
        {
            if (string.Equals(_stages[i].Name, stageName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    internal int SlotOf(int fieldId) => (uint)fieldId < (uint)_slotOfField.Length ? _slotOfField[fieldId] : -1;

    internal FieldDescriptor SlotDescriptor(int slot) => _slots[slot];

    internal int PrePassIndex(Type type) => _prePassByType.TryGetValue(type, out int index) ? index : -1;

    internal static ulong KeyOf(string name) => Hash64.Compute(MemoryMarshal.AsBytes(name.AsSpan()));

    private bool[] Mask(List<int> fieldIds)
    {
        var mask = new bool[_slots.Length];
        foreach (int id in fieldIds)
        {
            mask[_slotOfField[id]] = true;
        }

        return mask;
    }

    private string FieldName(int fieldId) => _slots[_slotOfField[fieldId]].Name;

    private ulong ComputeFingerprint()
    {
        var state = new Hash64State(0x4745_4E46UL);
        state.Append((ulong)(uint)Version);
        foreach (var stage in _stages)
        {
            state.Append(MemoryMarshal.AsBytes(stage.Name.AsSpan()));
        }

        foreach (var field in _slots)
        {
            state.Append(field.LayoutHash);
        }

        return state.Finish();
    }

    private static IEnumerable<int> Conflicts(List<int> writes, List<int> reads, List<int> otherWrites, List<int> halos)
        => writes.Where(f => reads.Contains(f) || otherWrites.Contains(f) || halos.Contains(f)).Distinct();

    private static int Lookup(Dictionary<string, int> byName, GenerationStage stage, string name, string relation)
        => byName.TryGetValue(name, out int index)
            ? index
            : throw new InvalidOperationException($"Generation stage '{stage.Name}' orders itself {relation} '{name}', which is not a registered stage.");

    private static List<int> Resolve(FieldsBuilder fields, GenerationStage stage, IReadOnlyList<string> names, string verb, SortedDictionary<int, FieldDescriptor> touched)
    {
        var ids = new List<int>(names.Count);
        foreach (string name in names)
        {
            if (!fields.TryDescribe(name, out var descriptor))
            {
                throw new InvalidOperationException($"Generation stage '{stage.Name}' {verb} Field '{name}', which is not registered.");
            }

            if (!ids.Contains(descriptor.Id))
            {
                ids.Add(descriptor.Id);
            }

            touched[descriptor.Id] = descriptor;
        }

        return ids;
    }
}
