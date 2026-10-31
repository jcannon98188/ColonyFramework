namespace Colony;

/// <summary>
/// Handle to a Resource registered in a <see cref="WorldSchema"/>: a World-owned singleton unmanaged
/// struct in a keyed store (spec section 4.1), or a typed event queue (spec section 4.3). Identity is the
/// registered name plus the layout hash; the handle is the schema's dense id.
/// </summary>
public readonly struct ResourceType : IEquatable<ResourceType>
{
    internal ResourceType(int id)
    {
        Id = id;
    }

    /// <summary>Dense id inside the owning schema.</summary>
    public int Id { get; }

    public bool Equals(ResourceType other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is ResourceType other && Equals(other);

    public override int GetHashCode() => Id;

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Resource#{Id}");

    public static bool operator ==(ResourceType left, ResourceType right) => left.Equals(right);

    public static bool operator !=(ResourceType left, ResourceType right) => !left.Equals(right);
}

/// <summary>Registration options for a Resource.</summary>
[Flags]
public enum ResourceOptions
{
    None = 0,

    /// <summary>
    /// The Resource is scratch state: it is neither saved with the World nor part of the World hash.
    /// Event queues are transient unless registered with <see cref="EventOptions.Persistent"/>.
    /// </summary>
    Transient = 1,
}

/// <summary>Registration options for a typed event queue (spec section 4.3).</summary>
[Flags]
public enum EventOptions
{
    None = 0,

    /// <summary>The queue's pending events are saved with the World and hashed; by default a queue is transient.</summary>
    Persistent = 1,
}

/// <summary>What a Resource slot holds.</summary>
public enum ResourceKind
{
    /// <summary>One value of the registered struct.</summary>
    Value = 0,

    /// <summary>A typed event queue of the registered struct, written through per-System buffers (spec section 4.3).</summary>
    EventQueue = 1,
}
