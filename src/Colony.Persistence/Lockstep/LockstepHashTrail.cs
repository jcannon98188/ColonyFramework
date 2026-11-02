using Colony.Diagnostics;

namespace Colony.Persistence;

/// <summary>Where two hash trails first disagree: the Tick, the Phase and the System (-1 when only the Phase boundary differed).</summary>
public readonly record struct HashTrailDivergence(long Tick, int PhaseId, int SystemId);

/// <summary>
/// A peer's hash trail (<see cref="DiagnosticsOptions.HashTrail"/>) kept Tick by Tick for lockstep desync
/// diagnosis (spec section 7.4): pass one instance as <see cref="DiagnosticsOptions.Listener"/> when creating
/// the World and as the lockstep session's <c>HashTrail</c> option. The last <see cref="Capacity"/> Ticks are
/// kept. <see cref="Compare"/> finds the first diverging (Tick, Phase, System) between two trails, examining
/// a Phase's Systems in the order they ran (wave, then order); the session sends the relevant Ticks of its
/// trail to the peer it disagrees with, so both sides can run the comparison.
/// </summary>
public sealed class LockstepHashTrail : IDiagnosticsListener
{
    private readonly List<Entry> _ticks = [];

    public LockstepHashTrail(int capacity = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>Most Ticks kept; older ones are dropped.</summary>
    public int Capacity { get; }

    public int Count => _ticks.Count;

    /// <summary>The oldest Tick still held, or -1 when empty.</summary>
    public long FirstTick => _ticks.Count == 0 ? -1 : _ticks[0].Tick;

    /// <summary>The newest Tick held, or -1 when empty.</summary>
    public long LastTick => _ticks.Count == 0 ? -1 : _ticks[^1].Tick;

    public bool Holds(long tick) => IndexOf(tick) >= 0;

    public void OnTickRecorded(World world, in TickRecordView record)
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

        Add(new Entry(record.Tick.Tick, phases, systems));
    }

    /// <summary>The first (Tick, Phase, System) where the trails differ over the Ticks both hold; null when they agree.</summary>
    public static HashTrailDivergence? Compare(LockstepHashTrail a, LockstepHashTrail b, WorldSchema schema)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(schema);
        foreach (var x in a._ticks)
        {
            int j = b.IndexOf(x.Tick);
            if (j < 0)
            {
                continue;
            }

            var y = b._ticks[j];
            int phases = Math.Min(x.Phases.Length, y.Phases.Length);
            for (int p = 0; p < phases; p++)
            {
                int system = FirstDivergingSystem(x, y, schema, p);
                if (system >= 0 || x.Phases[p] != y.Phases[p])
                {
                    return new HashTrailDivergence(x.Tick, p, system);
                }
            }
        }

        return null;
    }

    /// <summary>A trail holding only the Ticks of this one in <c>[from, to]</c> (for the wire).</summary>
    internal LockstepHashTrail Slice(long from, long to)
    {
        var slice = new LockstepHashTrail(Capacity);
        foreach (var entry in _ticks)
        {
            if (entry.Tick >= from && entry.Tick <= to)
            {
                slice._ticks.Add(entry);
            }
        }

        return slice;
    }

    internal void Write(ByteWriter w)
    {
        w.WriteInt32(_ticks.Count);
        foreach (var entry in _ticks)
        {
            w.WriteInt64(entry.Tick);
            w.WriteInt32(entry.Phases.Length);
            foreach (ulong hash in entry.Phases)
            {
                w.WriteUInt64(hash);
            }

            w.WriteInt32(entry.Systems.Length);
            foreach (ulong hash in entry.Systems)
            {
                w.WriteUInt64(hash);
            }
        }
    }

    internal static LockstepHashTrail Read(ref ByteReader r)
    {
        int count = r.ReadCount(16);
        var trail = new LockstepHashTrail(Math.Max(1, count));
        for (int i = 0; i < count; i++)
        {
            long tick = r.ReadInt64();
            var phases = new ulong[r.ReadCount(8)];
            for (int p = 0; p < phases.Length; p++)
            {
                phases[p] = r.ReadUInt64();
            }

            var systems = new ulong[r.ReadCount(8)];
            for (int s = 0; s < systems.Length; s++)
            {
                systems[s] = r.ReadUInt64();
            }

            trail._ticks.Add(new Entry(tick, phases, systems));
        }

        return trail;
    }

    private void Add(Entry entry)
    {
        _ticks.Add(entry);
        if (_ticks.Count > Capacity)
        {
            _ticks.RemoveAt(0);
        }
    }

    private int IndexOf(long tick)
    {
        for (int i = 0; i < _ticks.Count; i++)
        {
            if (_ticks[i].Tick == tick)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FirstDivergingSystem(Entry x, Entry y, WorldSchema schema, int phaseId)
    {
        int best = -1;
        int bestWave = int.MaxValue;
        int bestOrder = int.MaxValue;
        foreach (ref readonly var system in schema.Systems)
        {
            if (system.Phase.Id != phaseId || system.Id >= x.Systems.Length || system.Id >= y.Systems.Length || x.Systems[system.Id] == y.Systems[system.Id])
            {
                continue;
            }

            if (system.Wave < bestWave || (system.Wave == bestWave && system.Order < bestOrder))
            {
                best = system.Id;
                bestWave = system.Wave;
                bestOrder = system.Order;
            }
        }

        return best;
    }

    private sealed record Entry(long Tick, ulong[] Phases, ulong[] Systems);
}
