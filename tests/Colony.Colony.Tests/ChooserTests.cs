using Colony.Fields;
using Colony.Numerics;

namespace Colony.Jobs.Tests;

/// <summary>AC #4: the lexicographic priority-table chooser and the utility scoring helpers are allocation free and behave as specified.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class ChooserTests
{
    private static EntityId Id(int index) => new(index, 1);

    private static WorkHit Hit(int id, int type, long distanceSquared) => new(Id(id), new TilePos(0, 0, 0), type, distanceSquared);

    [Fact]
    public void A_priority_table_holds_a_rank_per_work_type_and_zero_means_never()
    {
        var table = PriorityTable.Empty.With(3, 1).With(0, 4).With(63, PriorityTable.MaxRank);

        Assert.Equal(1, table.Rank(3));
        Assert.Equal(4, table.Rank(0));
        Assert.Equal(PriorityTable.MaxRank, table.Rank(63));
        Assert.Equal(0, table.Rank(7));
        Assert.True(table.Allows(3));
        Assert.False(table.Allows(7));
        Assert.Equal(0, table.With(3, PriorityTable.Never).Rank(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.With(64, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.With(1, PriorityTable.MaxRank + 1));
    }

    [Fact]
    public void The_chooser_picks_the_best_rank_then_the_nearest_then_the_lowest_entity_id_and_skips_forbidden_types()
    {
        var table = PriorityTable.Empty.With(WorkTestWorld.Haul, 2).With(WorkTestWorld.Mine, 1);
        ReadOnlySpan<WorkHit> candidates =
        [
            Hit(10, WorkTestWorld.Build, 0),   // forbidden (rank 0) even though nearest
            Hit(11, WorkTestWorld.Haul, 1),    // rank 2
            Hit(12, WorkTestWorld.Mine, 9),    // rank 1, farther
            Hit(14, WorkTestWorld.Mine, 4),    // rank 1, nearest of rank 1, but higher id than 13
            Hit(13, WorkTestWorld.Mine, 4),    // winner
        ];

        var empty = PriorityTable.Empty;
        Assert.Equal(4, WorkChooser.Choose(in table, candidates));
        Assert.Equal(-1, WorkChooser.Choose(in empty, candidates));
        Assert.Equal(-1, WorkChooser.Choose(in table, ReadOnlySpan<WorkHit>.Empty));
    }

    [Fact]
    public void The_generic_lexicographic_chooser_compares_keys_in_order_and_breaks_full_ties_by_position()
    {
        // Urgency 3 beats everything; among the three, cost 3 beats 9; the two equal candidates resolve to the earlier one.
        ReadOnlySpan<(int Urgency, int Cost)> candidates = [(2, 1), (3, 9), (3, 3), (3, 3), (1, 0)];

        Assert.Equal(2, Lexicographic.Choose(candidates, default(UrgencyThenCost)));
        Assert.Equal(-1, Lexicographic.Choose(ReadOnlySpan<(int, int)>.Empty, default(UrgencyThenCost)));
    }

    [Fact]
    public void Utility_helpers_normalise_shape_and_combine_scores_in_fixed_point()
    {
        Assert.Equal(Fixed32.FromRatio(1, 2), Utility.Normalize(Fixed32.FromInt32(5), Fixed32.FromInt32(0), Fixed32.FromInt32(10)));
        Assert.Equal(Fixed32.Zero, Utility.Normalize(Fixed32.FromInt32(-5), Fixed32.FromInt32(0), Fixed32.FromInt32(10)));
        Assert.Equal(Fixed32.One, Utility.Normalize(Fixed32.FromInt32(50), Fixed32.FromInt32(0), Fixed32.FromInt32(10)));
        Assert.Equal(Fixed32.One, Utility.Normalize(Fixed32.FromInt32(3), Fixed32.FromInt32(3), Fixed32.FromInt32(3)));
        Assert.Equal(Fixed32.FromRatio(3, 4), Utility.Invert(Fixed32.FromRatio(1, 4)));
        Assert.Equal(Fixed32.FromRatio(1, 4), Utility.Quadratic(Fixed32.FromRatio(1, 2)));
        Assert.Equal(Fixed32.FromRatio(1, 2), Utility.Smooth(Fixed32.FromRatio(1, 2)));
        Assert.Equal(Fixed32.One, Utility.Smooth(Fixed32.FromInt32(7)));

        // Weighted sum: 0.5 * 2 + 1 * 1 over total weight 3 = 2/3.
        Assert.Equal(Fixed32.FromRatio(2, 3), Utility.WeightedSum([Fixed32.FromRatio(1, 2), Fixed32.One], [Fixed32.FromInt32(2), Fixed32.One]));
        Assert.Equal(Fixed32.Zero, Utility.WeightedSum(ReadOnlySpan<Fixed32>.Empty, ReadOnlySpan<Fixed32>.Empty));
        Assert.Throws<ArgumentException>(() => Utility.WeightedSum([Fixed32.One], ReadOnlySpan<Fixed32>.Empty));

        // Compensated product (infinite-axis style): with one consideration the score is itself; with two 0.5s
        // the raw product 0.25 is lifted by (1 - 0.25) * (1 - 1/2) * 0.25 = 0.09375 to 0.34375; a zero kills it.
        Assert.Equal(Fixed32.FromRatio(1, 2), Utility.Combine([Fixed32.FromRatio(1, 2)]));
        Assert.Equal(Fixed32.FromRatio(11, 32), Utility.Combine([Fixed32.FromRatio(1, 2), Fixed32.FromRatio(1, 2)]));
        Assert.Equal(Fixed32.Zero, Utility.Combine([Fixed32.One, Fixed32.Zero]));
        Assert.Equal(Fixed32.Zero, Utility.Combine(ReadOnlySpan<Fixed32>.Empty));

        Assert.Equal(1, Utility.Best([Fixed32.FromRatio(1, 4), Fixed32.FromRatio(3, 4), Fixed32.FromRatio(3, 4)]));
        Assert.Equal(-1, Utility.Best(ReadOnlySpan<Fixed32>.Empty));
    }

    [Fact]
    public void Choosers_and_utility_helpers_allocate_nothing()
    {
        var table = PriorityTable.Empty.With(0, 2).With(1, 1);
        var candidates = new WorkHit[32];
        var scores = new Fixed32[32];
        var weights = new Fixed32[32];
        var pairs = new (int, int)[32];
        for (int i = 0; i < 32; i++)
        {
            candidates[i] = Hit(i + 1, i % 3, (i * 7) % 11);
            scores[i] = Fixed32.FromRatio(i, 32);
            weights[i] = Fixed32.FromInt32(1 + (i % 4));
            pairs[i] = (i % 5, (i * 3) % 7);
        }

        long sink = Exercise(table, candidates, scores, weights, pairs);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            sink += Exercise(table, candidates, scores, weights, pairs);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.NotEqual(0, sink);
    }

    private static long Exercise(PriorityTable table, WorkHit[] candidates, Fixed32[] scores, Fixed32[] weights, (int, int)[] pairs)
    {
        long sink = WorkChooser.Choose(in table, candidates);
        sink += Lexicographic.Choose<(int, int), UrgencyThenCost>(pairs, default);
        sink += Utility.Combine(scores.AsSpan(1, 4)).Raw;
        sink += Utility.WeightedSum(scores, weights).Raw;
        sink += Utility.Best(scores);
        sink += Utility.Smooth(Utility.Normalize(scores[3], Fixed32.Zero, Fixed32.One)).Raw;
        return sink;
    }

    private readonly struct UrgencyThenCost : ILexicographicKeys<(int Urgency, int Cost)>
    {
        public int KeyCount => 2;

        public bool Accepts(in (int Urgency, int Cost) candidate) => true;

        /// <summary>Higher urgency first (negated), then lower cost.</summary>
        public long Key(in (int Urgency, int Cost) candidate, int index) => index == 0 ? -candidate.Urgency : candidate.Cost;
    }
}
