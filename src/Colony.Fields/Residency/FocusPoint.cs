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
}

/// <summary>Chunk counts per residency state.</summary>
public readonly record struct ResidencyCounts(int Active, int Hibernating, int Unloaded);
