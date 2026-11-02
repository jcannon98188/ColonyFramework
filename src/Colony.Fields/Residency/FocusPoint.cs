namespace Colony.Fields;

/// <summary>
/// An Input Command of the residency manager (spec section 5.3): the chunks within <see cref="Radius"/> chunks
/// (Chebyshev distance, in all three axes) of <see cref="Center"/>'s chunk are Active, the ring one chunk
/// further out Hibernating. Submitting a point with an existing <see cref="Id"/> moves it; a negative
/// <see cref="Radius"/> removes it. Applied in the Streaming Phase of the Tick after the one that received it.
/// </summary>
public record struct FocusPoint(uint Id, TilePos Center, int Radius);

/// <summary>
/// Built-in non-fragmenting Component of a chunk-owned Entity whose chunk is not Active: every Component's
/// enable bit is off and <see cref="Enabled"/> remembers which were on, so thawing restores the Entity exactly.
/// Systems honouring enable bits never see a frozen Entity; one with <see cref="SystemDefinition.IgnoreEnableBits"/>
/// must check <see cref="ChunkResidencyManager.IsFrozen"/> itself.
/// </summary>
public record struct Frozen(ComponentMask Enabled);

/// <summary>
/// Built-in non-fragmenting marker of an Entity the residency manager never freezes (spec section 5.3): a pinned
/// Entity keeps simulating when its chunk unloads and may stand in unloaded space.
/// </summary>
public record struct Pinned(byte Marker);

/// <summary>Options of <see cref="ResidencyRegistration.AddResidency"/>.</summary>
public sealed record ResidencyOptions
{
    /// <summary>Focus points the manager tracks at most; a further one is rejected with <see cref="ResidencyBuilder.RejectTooManyFocusPoints"/>.</summary>
    public int MaxFocusPoints { get; init; } = 16;

    /// <summary>When true, chunks outside every focus ring stay Hibernating instead of Unloaded (everything stays resident, nothing simulates far away).</summary>
    public bool HibernateInsteadOfUnload { get; init; }

    /// <summary>
    /// The residency of every chunk in a fresh World and whenever no focus point exists: <see cref="ChunkResidency.Active"/>
    /// (the default, the never-unload policy of a World without streaming) or <see cref="ChunkResidency.Unloaded"/> (a
    /// streaming World whose chunks are generated on demand as focus points arrive, spec section 5.3).
    /// </summary>
    public ChunkResidency InitialResidency { get; init; } = ChunkResidency.Active;

    /// <summary>
    /// Width of the Hibernating ring around the Active chunks, in chunks (default 1). A wider ring loads chunks
    /// earlier, so a moving focus with <see cref="LoadDelayTicks"/> &gt; 0 finds its next ring already resident.
    /// </summary>
    public int RingWidth { get; init; } = 1;

    /// <summary>
    /// Loads of Unloaded chunks run on background workers and apply exactly this many Ticks after the Streaming
    /// Phase that requested them (spec section 5.3); 0 (the default) loads synchronously in the requesting Phase. A load
    /// not finished by its Tick stalls that Tick (counted in <see cref="StreamingCounters.LoadStalls"/> and the
    /// <c>residency.load-stalls</c> diagnostics counter); the resulting state never depends on load timing.
    /// </summary>
    public int LoadDelayTicks { get; init; }

    /// <summary>Requested loads not yet applied at most (the bounded queue of spec section 5.3); further chunks wait for a later Streaming Phase.</summary>
    public int MaxLoadsInFlight { get; init; } = 64;

    /// <summary>
    /// Resident (Active or Hibernating) chunks at most; 0 (the default) means no budget. When focus points demand more,
    /// the Streaming Phase evicts deterministically (farthest from the nearest focus first, then Hibernating before
    /// Active, then ascending chunk ordinal), demotes Active chunks left bordering unloaded space to Hibernating and
    /// emits a <see cref="BudgetPressure"/> event on <see cref="ResidencyBuilder.BudgetPressureQueueName"/>.
    /// </summary>
    public int ResidentChunkBudget { get; init; }

    /// <summary>Ticks a catch-up function (<see cref="ResidencyBuilder.AddCatchUp{T}"/>) replays at most for one thawing Entity (spec section 5.3).</summary>
    public long CatchUpMaxTicks { get; init; } = 256;

    /// <summary>
    /// Entities thawed with catch-up per Streaming Phase at most, when catch-up functions are registered. Chunks whose
    /// thaw would exceed it stay Hibernating until a later Tick (at least one chunk thaws per Tick), so a focus jump
    /// spreads the replay over several Ticks instead of one.
    /// </summary>
    public int CatchUpWorkBudget { get; init; } = 4096;
}

/// <summary>Chunk counts per residency state.</summary>
public readonly record struct ResidencyCounts(int Active, int Hibernating, int Unloaded);

/// <summary>
/// Event of the Streaming Phase (queue <see cref="ResidencyBuilder.BudgetPressureQueueName"/>, registered only with a
/// <see cref="ResidencyOptions.ResidentChunkBudget"/>): the focus points demanded <see cref="Demanded"/> resident
/// chunks against a budget of <see cref="Budget"/>, so <see cref="Evicted"/> chunks were unloaded this Tick.
/// </summary>
public readonly record struct BudgetPressure(int Demanded, int Budget, int Evicted);

/// <summary>Built-in non-fragmenting Component of a frozen Entity when catch-up functions are registered: the Tick its chunk left Active.</summary>
public record struct FrozenAt(long Tick);

/// <summary>
/// A catch-up function (spec section 5.3): a deterministic function of the Component's state and the Ticks the
/// Entity was frozen (capped by <see cref="ResidencyOptions.CatchUpMaxTicks"/>), applied as its chunk becomes
/// Active again, before the Tick's Simulate Phase.
/// </summary>
public delegate void CatchUpAction<T>(ref T state, long elapsedTicks) where T : unmanaged;

/// <summary>Running totals of the Streaming Phase (diagnostics only, never hashed); the same values feed the <c>residency.*</c> counters of the Tick records.</summary>
public readonly record struct StreamingCounters(
    long Loads,
    long LoadStalls,
    long LoadsInFlight,
    long Evictions,
    long CatchUps,
    long CatchUpTicks);
