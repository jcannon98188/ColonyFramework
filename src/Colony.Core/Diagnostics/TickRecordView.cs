namespace Colony.Diagnostics;

/// <summary>
/// One Tick's complete record: the Tick struct plus its per-Phase, per-System, per-worker and (Detailed)
/// per-job spans, viewed in place inside the ring. Valid until the next <see cref="World.Tick"/>, which may
/// overwrite the slot; copy what must outlive it. Reading allocates nothing.
/// </summary>
public readonly ref struct TickRecordView
{
    private readonly ref readonly TickRecord _tick;

    internal TickRecordView(
        ref readonly TickRecord tick,
        ReadOnlySpan<PhaseRecord> phases,
        ReadOnlySpan<SystemRecord> systems,
        ReadOnlySpan<WorkerRecord> workers,
        ReadOnlySpan<JobRecord> jobs,
        ReadOnlySpan<long> counters,
        ReadOnlySpan<ulong> archetypeHashes,
        int archetypeHashStride)
    {
        _tick = ref tick;
        Phases = phases;
        Systems = systems;
        Workers = workers;
        Jobs = jobs;
        Counters = counters;
        ArchetypeHashes = archetypeHashes;
        ArchetypeHashStride = archetypeHashStride;
    }

    public ref readonly TickRecord Tick => ref _tick;

    /// <summary>By Phase id, parallel to <see cref="WorldSchema.Phases"/>.</summary>
    public ReadOnlySpan<PhaseRecord> Phases { get; }

    /// <summary>By System id, parallel to <see cref="WorldSchema.Systems"/>.</summary>
    public ReadOnlySpan<SystemRecord> Systems { get; }

    /// <summary>By worker index; worker 0 is the calling thread.</summary>
    public ReadOnlySpan<WorkerRecord> Workers { get; }

    /// <summary>Per-job trace in completion order; empty below <see cref="DiagnosticsLevel.Detailed"/>.</summary>
    public ReadOnlySpan<JobRecord> Jobs { get; }

    /// <summary>Registered counters (<see cref="WorldBuilder.AddCounter"/>) by <see cref="CounterId"/>, summed over every Tick thread.</summary>
    public ReadOnlySpan<long> Counters { get; }

    /// <summary>Hash-trail archetype hashes, <see cref="ArchetypeHashStride"/> per Phase: entry <c>phase * stride + archetype</c>.</summary>
    public ReadOnlySpan<ulong> ArchetypeHashes { get; }

    public int ArchetypeHashStride { get; }

    /// <summary>The hash of one archetype after one Phase (0 when the trail is off or the archetype was not hashed).</summary>
    public ulong ArchetypeHash(int phaseId, int archetype)
        => archetype < ArchetypeHashStride && ArchetypeHashes.Length > 0 ? ArchetypeHashes[(phaseId * ArchetypeHashStride) + archetype] : 0;
}
