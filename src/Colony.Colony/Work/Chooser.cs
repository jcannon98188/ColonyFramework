namespace Colony.Jobs;

/// <summary>
/// The keys of a lexicographic choice over candidates of <typeparamref name="T"/>: <see cref="Key"/> 0 is compared
/// first, lower wins, and only equal keys fall through to the next. A struct, so choosing allocates nothing.
/// </summary>
public interface ILexicographicKeys<T>
{
    public int KeyCount { get; }

    /// <summary>Whether the candidate may be chosen at all.</summary>
    public bool Accepts(in T candidate);

    /// <summary>The <paramref name="index"/>-th key of a candidate; negate a value to prefer higher.</summary>
    public long Key(in T candidate, int index);
}

/// <summary>The lexicographic chooser of spec section 9.2 (optional helper).</summary>
public static class Lexicographic
{
    /// <summary>
    /// The index of the accepted candidate with the lexicographically smallest keys; full ties resolve to the
    /// earliest position, so the result depends only on the span's contents and order. -1 when nothing is accepted.
    /// </summary>
    public static int Choose<T, TKeys>(ReadOnlySpan<T> candidates, TKeys keys)
        where TKeys : struct, ILexicographicKeys<T>
    {
        int best = -1;
        for (int i = 0; i < candidates.Length; i++)
        {
            ref readonly var candidate = ref candidates[i];
            if (!keys.Accepts(in candidate))
            {
                continue;
            }

            if (best < 0 || Precedes(in candidate, in candidates[best], keys))
            {
                best = i;
            }
        }

        return best;
    }

    private static bool Precedes<T, TKeys>(in T a, in T b, TKeys keys)
        where TKeys : struct, ILexicographicKeys<T>
    {
        int count = keys.KeyCount;
        for (int k = 0; k < count; k++)
        {
            long ka = keys.Key(in a, k);
            long kb = keys.Key(in b, k);
            if (ka != kb)
            {
                return ka < kb;
            }
        }

        return false;
    }
}

/// <summary>
/// An agent's priority table (spec section 9.2): a rank per work type for <see cref="Capacity"/> types, four bits
/// each. Rank <see cref="Never"/> (0) forbids the type; otherwise 1 is the most urgent and <see cref="MaxRank"/> the
/// least. A plain saved Component when stored on agents.
/// </summary>
public readonly record struct PriorityTable(ulong Word0, ulong Word1, ulong Word2, ulong Word3)
{
    /// <summary>Work types a table ranks.</summary>
    public const int Capacity = 64;

    /// <summary>The rank that forbids a type.</summary>
    public const int Never = 0;

    /// <summary>The lowest allowed urgency.</summary>
    public const int MaxRank = 15;

    /// <summary>Every type forbidden.</summary>
    public static PriorityTable Empty => default;

    /// <summary>The rank of a work type; <see cref="Never"/> when forbidden.</summary>
    public int Rank(int type)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(type, Capacity);
        return (int)((Word(type >> 4) >> ((type & 15) << 2)) & 0xF);
    }

    public bool Allows(int type) => Rank(type) != Never;

    /// <summary>The same table with <paramref name="type"/> ranked <paramref name="rank"/> (0..<see cref="MaxRank"/>).</summary>
    public PriorityTable With(int type, int rank)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(type, Capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(rank);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rank, MaxRank);
        int shift = (type & 15) << 2;
        ulong word = (Word(type >> 4) & ~(0xFUL << shift)) | ((ulong)rank << shift);
        return (type >> 4) switch
        {
            0 => this with { Word0 = word },
            1 => this with { Word1 = word },
            2 => this with { Word2 = word },
            _ => this with { Word3 = word },
        };
    }

    private ulong Word(int index) => index switch { 0 => Word0, 1 => Word1, 2 => Word2, _ => Word3 };
}

/// <summary>The priority-table chooser: picks among <see cref="WorkHit"/> candidates by rank, then distance, then Entity id.</summary>
public static class WorkChooser
{
    /// <summary>
    /// The index of the candidate with the most urgent rank in <paramref name="table"/> (forbidden types skipped),
    /// nearest among equals, lowest Entity id among those. -1 when no candidate is allowed. The candidates'
    /// order does not matter unless two share Entity id, rank and distance.
    /// </summary>
    public static int Choose(in PriorityTable table, ReadOnlySpan<WorkHit> candidates)
        => Lexicographic.Choose(candidates, new PriorityKeys(table));

    private readonly struct PriorityKeys(PriorityTable table) : ILexicographicKeys<WorkHit>
    {
        public int KeyCount => 3;

        public bool Accepts(in WorkHit candidate) => table.Allows(candidate.Type);

        public long Key(in WorkHit candidate, int index) => index switch
        {
            0 => table.Rank(candidate.Type),
            1 => candidate.DistanceSquared,
            _ => (long)candidate.Entity.Value,
        };
    }
}
