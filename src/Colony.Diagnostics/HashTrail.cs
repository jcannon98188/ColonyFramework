using System.Globalization;

namespace Colony.Diagnostics;

/// <summary>
/// The hashes of one Tick as the hash trail recorded them (<see cref="DiagnosticsOptions.HashTrail"/>):
/// the World hash after every System, after every Phase boundary and at the end of the Tick, plus every
/// archetype's hash at each Phase boundary.
/// </summary>
public sealed class TickHashes
{
    internal TickHashes(long tick, ulong hash, ulong[] phases, ulong[] systems, ulong[] archetypes, int archetypeStride)
    {
        Tick = tick;
        Hash = hash;
        PhaseHashesArray = phases;
        SystemHashesArray = systems;
        ArchetypeHashesArray = archetypes;
        ArchetypeStride = archetypeStride;
    }

    public long Tick { get; }

    public ulong Hash { get; }

    /// <summary>By Phase id.</summary>
    public ReadOnlySpan<ulong> PhaseHashes => PhaseHashesArray;

    /// <summary>By System id (0 for Systems that did not run this Tick).</summary>
    public ReadOnlySpan<ulong> SystemHashes => SystemHashesArray;

    /// <summary>Per Phase, <see cref="ArchetypeStride"/> entries: <c>phase * stride + archetype</c>.</summary>
    public ReadOnlySpan<ulong> ArchetypeHashes => ArchetypeHashesArray;

    public int ArchetypeStride { get; }

    internal ulong[] PhaseHashesArray { get; }

    internal ulong[] SystemHashesArray { get; }

    internal ulong[] ArchetypeHashesArray { get; }

    internal static TickHashes From(in TickRecordView record)
    {
        var phases = new ulong[record.Phases.Length];
        for (int p = 0; p < phases.Length; p++)
        {
            phases[p] = record.Phases[p].Hash;
        }

        var systems = new ulong[record.Systems.Length];
        for (int s = 0; s < systems.Length; s++)
        {
            systems[s] = record.Systems[s].Hash;
        }

        return new TickHashes(record.Tick.Tick, record.Tick.Hash, phases, systems, record.ArchetypeHashes.ToArray(), record.ArchetypeHashStride);
    }
}

/// <summary>
/// Collects a World's hash trail Tick by Tick through <see cref="IDiagnosticsListener"/>, so two runs
/// (different thread counts, machines or builds) can be compared with <see cref="HashTrail.Compare(IReadOnlyList{TickHashes}, IReadOnlyList{TickHashes}, WorldSchema)"/>.
/// Recording copies each Tick's hashes into a growing list on the Tick thread; it is a diagnostic tool,
/// not a steady-state path, and only costs anything when the World's <see cref="DiagnosticsOptions.HashTrail"/> is on.
/// </summary>
public sealed class HashTrailRecorder : IDiagnosticsListener
{
    private readonly List<TickHashes> _ticks = [];

    public IReadOnlyList<TickHashes> Ticks => _ticks;

    public void OnTickRecorded(World world, in TickRecordView record)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Diagnostics.Options.HashTrail)
        {
            throw new InvalidOperationException("The World records no hash trail; set DiagnosticsOptions.HashTrail.");
        }

        _ticks.Add(TickHashes.From(in record));
    }
}

/// <summary>Where two hash trails first differ (spec section 10.1): the Tick, the Phase, the System and the archetype.</summary>
public readonly record struct HashDivergence(long Tick, int PhaseId, int SystemId, int Archetype)
{
    /// <summary>True when the Phase's System hashes all agree and only the Phase boundary (command playback) differs.</summary>
    public bool AtPhaseBoundary => SystemId < 0;

    /// <summary>True when the divergence is not inside any one recorded archetype (non-fragmenting data, RNG state, or an archetype beyond the trail's stride).</summary>
    public bool OutsideRecordedArchetypes => Archetype < 0;

    public string Describe(WorldSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        string phase = PhaseId >= 0 && PhaseId < schema.Phases.Length ? schema.Phases[PhaseId].Name : "?";
        string system = SystemId >= 0 && SystemId < schema.Systems.Length ? schema.Systems[SystemId].Name : "(Phase boundary)";
        string archetype = Archetype >= 0 ? string.Create(CultureInfo.InvariantCulture, $"archetype {Archetype}") : "outside recorded archetypes";
        return string.Create(CultureInfo.InvariantCulture, $"Tick {Tick}, Phase {phase}, System {system}, {archetype}");
    }
}

/// <summary>Comparison of two recorded hash trails.</summary>
public static class HashTrail
{
    /// <summary>
    /// The first point where <paramref name="a"/> and <paramref name="b"/> diverge, or null when they agree over
    /// their common length. Systems are examined in the order the Phase ran them (wave, then order), so the
    /// reported System is the first whose output differed.
    /// </summary>
    public static HashDivergence? Compare(IReadOnlyList<TickHashes> a, IReadOnlyList<TickHashes> b, WorldSchema schema)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(schema);
        var order = new List<SystemOrder>(schema.Systems.Length);
        foreach (ref readonly var descriptor in schema.Systems)
        {
            order.Add(new SystemOrder(descriptor.Id, descriptor.Phase.Id, descriptor.Wave, descriptor.Order));
        }

        return Compare(a, b, order);
    }

    /// <summary>The schema-free form: Systems described by id, Phase, wave and order (as a hash trail file stores them).</summary>
    internal static HashDivergence? Compare(IReadOnlyList<TickHashes> a, IReadOnlyList<TickHashes> b, IReadOnlyList<SystemOrder> systems)
    {
        int ticks = Math.Min(a.Count, b.Count);
        for (int t = 0; t < ticks; t++)
        {
            var x = a[t];
            var y = b[t];
            if (x.Tick != y.Tick)
            {
                throw new ArgumentException($"The trails are not aligned: entry {t} is Tick {x.Tick} in one and Tick {y.Tick} in the other.", nameof(b));
            }

            if (x.Hash == y.Hash && x.PhaseHashesArray.AsSpan().SequenceEqual(y.PhaseHashesArray) && x.SystemHashesArray.AsSpan().SequenceEqual(y.SystemHashesArray))
            {
                continue;
            }

            int phases = Math.Min(x.PhaseHashesArray.Length, y.PhaseHashesArray.Length);
            for (int p = 0; p < phases; p++)
            {
                int system = FirstDivergingSystem(x, y, systems, p);
                if (x.PhaseHashesArray[p] == y.PhaseHashesArray[p] && system < 0)
                {
                    continue;
                }

                return new HashDivergence(x.Tick, p, system, FirstDivergingArchetype(x, y, p));
            }

            return new HashDivergence(x.Tick, phases - 1, -1, -1);
        }

        return null;
    }

    private static int FirstDivergingSystem(TickHashes x, TickHashes y, IReadOnlyList<SystemOrder> systems, int phaseId)
    {
        int best = -1;
        int bestWave = int.MaxValue;
        int bestOrder = int.MaxValue;
        foreach (var descriptor in systems)
        {
            if (descriptor.Phase != phaseId || descriptor.Id >= x.SystemHashesArray.Length || descriptor.Id >= y.SystemHashesArray.Length)
            {
                continue;
            }

            if (x.SystemHashesArray[descriptor.Id] == y.SystemHashesArray[descriptor.Id])
            {
                continue;
            }

            if (descriptor.Wave < bestWave || (descriptor.Wave == bestWave && descriptor.Order < bestOrder))
            {
                best = descriptor.Id;
                bestWave = descriptor.Wave;
                bestOrder = descriptor.Order;
            }
        }

        return best;
    }

    private static int FirstDivergingArchetype(TickHashes x, TickHashes y, int phaseId)
    {
        int stride = Math.Min(x.ArchetypeStride, y.ArchetypeStride);
        for (int a = 0; a < stride; a++)
        {
            int ix = (phaseId * x.ArchetypeStride) + a;
            int iy = (phaseId * y.ArchetypeStride) + a;
            if (ix < x.ArchetypeHashesArray.Length && iy < y.ArchetypeHashesArray.Length && x.ArchetypeHashesArray[ix] != y.ArchetypeHashesArray[iy])
            {
                return a;
            }
        }

        return -1;
    }
}

/// <summary>Where a System runs: enough of the schema to order a hash-trail comparison.</summary>
internal readonly record struct SystemOrder(int Id, int Phase, int Wave, int Order);
