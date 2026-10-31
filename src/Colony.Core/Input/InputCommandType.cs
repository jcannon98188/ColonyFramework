namespace Colony;

/// <summary>
/// Handle to an Input Command registered in a <see cref="WorldSchema"/> (spec section 7.3): a typed
/// unmanaged struct a player, script or tool submits through <see cref="WorldInputs.Submit{T}(InputSource, in T, long)"/>.
/// Identity is the registered name plus the layout hash; the handle is the schema's dense id.
/// </summary>
public readonly struct InputCommandType : IEquatable<InputCommandType>
{
    internal InputCommandType(int id)
    {
        Id = id;
    }

    /// <summary>Dense id inside the owning schema (registration order).</summary>
    public int Id { get; }

    public bool Equals(InputCommandType other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is InputCommandType other && Equals(other);

    public override int GetHashCode() => Id;

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"InputCommand#{Id}");

    public static bool operator ==(InputCommandType left, InputCommandType right) => left.Equals(right);

    public static bool operator !=(InputCommandType left, InputCommandType right) => !left.Equals(right);
}

/// <summary>A registered Input Command: its handle, stable name, size, alignment and layout hash (name, size and alignment, like a Resource).</summary>
public readonly record struct InputCommandDescriptor(InputCommandType Type, string Name, int Size, int Alignment, ulong LayoutHash);

/// <summary>
/// Who submitted an Input Command (spec section 7.3): a local player, a remote peer in lockstep, a script
/// or a tool. Sources order the canonical list after the effective Tick; every source numbers its own
/// commands. The value is the Game's to assign (a player slot, a peer id).
/// </summary>
public readonly record struct InputSource(uint Value)
{
    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Source#{Value}");
}

/// <summary>
/// The deterministic rejection event (spec section 7.3) a handler System emits with <see cref="SystemContext.Reject{T}"/>:
/// delivered on the built-in <see cref="WorldSchema.CommandRejections"/> queue at the Input Phase boundary, so it
/// is readable from the Begin Phase through the End Phase of the same Tick. <see cref="Reason"/> is a code the Game defines.
/// The rejected command stays in the canonical list and in any replay log.
/// </summary>
public readonly record struct CommandRejected(InputSource Source, uint Sequence, InputCommandType Command, int Reason);

/// <summary>One entry of the canonical input list of a Tick: who, in what order, which command. The payload is read through <see cref="CanonicalInputs"/>.</summary>
public readonly record struct InputRecord(long Tick, InputSource Source, uint Sequence, InputCommandType Command);
