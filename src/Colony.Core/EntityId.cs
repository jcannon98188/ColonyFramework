namespace Colony;

/// <summary>
/// 64-bit identity of an Entity: the low 32 bits are the slot index in the World's Entity index, the
/// high 32 bits the generation of that slot (spec section 4.1). Generations start at 1, so the value 0
/// is never alive and serves as <see cref="Null"/>. The id is stable across save/load; a destroyed
/// Entity's id fails the generation check for ever after.
/// </summary>
/// <remarks>
/// A deferred handle (returned by <see cref="CommandBuffer.Create()"/>) carries the
/// <see cref="IsDeferred"/> flag in the top bit and is only meaningful to the buffer that produced it
/// until that buffer is played back. Real generations therefore stay below 2^31.
/// </remarks>
public readonly struct EntityId : IEquatable<EntityId>
{
    internal const ulong DeferredBit = 1UL << 63;
    internal const uint MaxGeneration = 0x7FFF_FFFF;

    public EntityId(ulong value)
    {
        Value = value;
    }

    public EntityId(int index, uint generation)
    {
        Value = (uint)index | ((ulong)generation << 32);
    }

    public static EntityId Null => default;

    public ulong Value { get; }

    /// <summary>Slot in the World's Entity index (low 32 bits).</summary>
    public int Index => (int)(uint)Value;

    /// <summary>Generation of the slot (high 32 bits). 0 is never alive.</summary>
    public uint Generation => (uint)(Value >> 32);

    public bool IsNull => Value == 0;

    /// <summary>True for a handle handed out by <see cref="CommandBuffer.Create()"/> before playback.</summary>
    public bool IsDeferred => (Value & DeferredBit) != 0;

    internal static EntityId Deferred(int ordinal) => new(DeferredBit | (uint)ordinal);

    internal int DeferredOrdinal => (int)(uint)Value;

    public bool Equals(EntityId other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => IsDeferred
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Deferred#{DeferredOrdinal}")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"E{Index}v{Generation}");

    public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);

    public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
}
