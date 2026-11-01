namespace Colony.Testing;

/// <summary>
/// The conditions a <see cref="TestNetwork"/> simulates, in network steps (one step = one
/// <see cref="TestNetwork.Advance"/>): a base one-way latency, uniform jitter on top, a chance that a packet is
/// held back long enough to arrive behind later ones, and drop and duplicate rates. Every random decision is
/// drawn from the network's seeded counter-based stream, so a profile plus a seed is a reproducible run.
/// </summary>
public sealed record NetworkProfile
{
    /// <summary>Delivered on the next step, in send order, nothing lost.</summary>
    public static NetworkProfile Perfect { get; } = new() { Name = "perfect", LatencySteps = 1 };

    /// <summary>Short latency with a little jitter.</summary>
    public static NetworkProfile Lan { get; } = new() { Name = "lan", LatencySteps = 1, JitterSteps = 1 };

    /// <summary>Long latency with jitter larger than the latency itself.</summary>
    public static NetworkProfile Jittery { get; } = new() { Name = "jittery", LatencySteps = 3, JitterSteps = 4 };

    /// <summary>A quarter of the packets are held back past later ones.</summary>
    public static NetworkProfile Reordering { get; } = new() { Name = "reordering", LatencySteps = 2, JitterSteps = 2, ReorderRate = 0.25, ReorderDelaySteps = 6 };

    /// <summary>Three packets in ten are lost.</summary>
    public static NetworkProfile Lossy { get; } = new() { Name = "lossy", LatencySteps = 2, DropRate = 0.3 };

    /// <summary>Three packets in ten arrive twice.</summary>
    public static NetworkProfile Duplicating { get; } = new() { Name = "duplicating", LatencySteps = 2, JitterSteps = 1, DuplicateRate = 0.3 };

    /// <summary>Every fault at once.</summary>
    public static NetworkProfile Hostile { get; } = new()
    {
        Name = "hostile",
        LatencySteps = 3,
        JitterSteps = 3,
        ReorderRate = 0.15,
        ReorderDelaySteps = 5,
        DropRate = 0.2,
        DuplicateRate = 0.15,
    };

    /// <summary>The built-in profiles, mildest first.</summary>
    public static IReadOnlyList<NetworkProfile> All { get; } = [Perfect, Lan, Jittery, Reordering, Lossy, Duplicating, Hostile];

    /// <summary>A built-in profile by <see cref="Name"/> (for test data rows).</summary>
    public static NetworkProfile Named(string name)
    {
        foreach (var profile in All)
        {
            if (string.Equals(profile.Name, name, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        throw new ArgumentException($"No built-in network profile is named '{name}'.", nameof(name));
    }

    public required string Name { get; init; }

    /// <summary>Steps between send and delivery before jitter; 0 makes a packet receivable in the step it was sent.</summary>
    public int LatencySteps { get; init; }

    /// <summary>Extra delay drawn uniformly from [0, JitterSteps] per packet.</summary>
    public int JitterSteps { get; init; }

    /// <summary>Chance in [0, 1] that a packet is delayed by a further <see cref="ReorderDelaySteps"/>.</summary>
    public double ReorderRate { get; init; }

    public int ReorderDelaySteps { get; init; } = 4;

    /// <summary>Chance in [0, 1] that a packet is lost.</summary>
    public double DropRate { get; init; }

    /// <summary>Chance in [0, 1] that a delivered packet arrives a second time (with its own delay).</summary>
    public double DuplicateRate { get; init; }

    public override string ToString() => Name;

    internal void Validate()
    {
        CheckSteps(LatencySteps, nameof(LatencySteps));
        CheckSteps(JitterSteps, nameof(JitterSteps));
        CheckSteps(ReorderDelaySteps, nameof(ReorderDelaySteps));
        CheckRate(ReorderRate, nameof(ReorderRate));
        CheckRate(DropRate, nameof(DropRate));
        CheckRate(DuplicateRate, nameof(DuplicateRate));
    }

    /// <summary>A rate as parts per million, compared against a uniform draw in [0, 1e6).</summary>
    internal static int PartsPerMillion(double rate) => (int)((rate * 1_000_000d) + 0.5d);

    private static void CheckSteps(int value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Step counts must not be negative.");
        }
    }

    private static void CheckRate(double value, string name)
    {
        if (!(value >= 0d && value <= 1d))
        {
            throw new ArgumentOutOfRangeException(name, value, "Rates must lie in [0, 1].");
        }
    }
}
