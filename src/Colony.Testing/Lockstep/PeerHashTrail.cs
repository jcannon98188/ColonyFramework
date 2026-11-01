using Colony.Diagnostics;

namespace Colony.Testing;

/// <summary>
/// A peer's hash trail (<see cref="DiagnosticsOptions.HashTrail"/>) copied Tick by Tick, and the comparison that
/// finds the first diverging (Tick, Phase, System) between two peers. Colony.Testing may not reference
/// Colony.Diagnostics (spec section 3), so this is a small copy of <c>HashTrail.Compare</c> over Core's records:
/// Systems are examined in the order the Phase ran them (wave, then order).
/// </summary>
internal sealed class PeerHashTrail : IDiagnosticsListener
{
    private readonly List<Entry> _ticks = [];

    public int Count => _ticks.Count;

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

        _ticks.Add(new Entry(record.Tick.Tick, phases, systems));
    }

    /// <summary>The first (Tick, Phase, System) where the trails differ; System -1 = the Phase boundary. Null when they agree.</summary>
    public static (long Tick, int Phase, int System)? Compare(PeerHashTrail a, PeerHashTrail b, WorldSchema schema)
    {
        int common = Math.Min(a._ticks.Count, b._ticks.Count);
        for (int t = 0; t < common; t++)
        {
            var x = a._ticks[t];
            var y = b._ticks[t];
            int phases = Math.Min(x.Phases.Length, y.Phases.Length);
            for (int p = 0; p < phases; p++)
            {
                int system = FirstDivergingSystem(x, y, schema, p);
                if (system >= 0 || x.Phases[p] != y.Phases[p])
                {
                    return (x.Tick, p, system);
                }
            }
        }

        return null;
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
