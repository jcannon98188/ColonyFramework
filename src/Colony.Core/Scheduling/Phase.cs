namespace Colony;

/// <summary>
/// A named stage of a Tick in which Systems run in a fixed order (spec section 4.3). The six named
/// Phases always exist and run in this order: Streaming, Input, Begin, Simulate, Late, End. Game
/// authors add their own Phases between two named ones with <see cref="WorldBuilder.AddPhase"/>.
/// Command buffers merge at every Phase boundary.
/// </summary>
public readonly struct Phase : IEquatable<Phase>
{
    internal const int NamedCount = 6;

    private static readonly string[] NamedNames = ["Streaming", "Input", "Begin", "Simulate", "Late", "End"];

    private readonly string? _name;

    internal Phase(int id, string name)
    {
        Id = id;
        _name = name;
    }

    /// <summary>Applies completed chunk loads, residency transitions, evictions and catch-up.</summary>
    public static Phase Streaming => new(0, NamedNames[0]);

    /// <summary>Builds the canonical input list; handler Systems apply Input Commands.</summary>
    public static Phase Input => new(1, NamedNames[1]);

    /// <summary>Applies deferred-completion results due this Tick; pathfinding delivery; claim resolution.</summary>
    public static Phase Begin => new(2, NamedNames[2]);

    /// <summary>Main game logic Systems.</summary>
    public static Phase Simulate => new(3, NamedNames[3]);

    /// <summary>Derived-data Systems (spatial index, region upkeep, work index).</summary>
    public static Phase Late => new(4, NamedNames[4]);

    /// <summary>Events cleared, diagnostics record finalised, checkpoint hash if due.</summary>
    public static Phase End => new(5, NamedNames[5]);

    /// <summary>Registration id: 0 to 5 for the named Phases, 6 and up for Game Phases in registration order.</summary>
    public int Id { get; }

    public string Name => _name ?? NamedNames[0];

    public bool IsNamed => Id < NamedCount;

    internal static Phase Named(int id) => new(id, NamedNames[id]);

    internal static bool IsNamedName(string name) => Array.IndexOf(NamedNames, name) >= 0;

    public bool Equals(Phase other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is Phase other && Equals(other);

    public override int GetHashCode() => Id;

    public override string ToString() => Name;

    public static bool operator ==(Phase left, Phase right) => left.Equals(right);

    public static bool operator !=(Phase left, Phase right) => !left.Equals(right);
}
