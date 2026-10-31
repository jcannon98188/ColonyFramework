namespace Colony.Jobs.Tests;

/// <summary>AC #4: the default anti-thrash policy's three rules, and that a Game can replace the policy.</summary>
public sealed class ClaimPolicyTests
{
    private static readonly EntityId Target = new((1UL << 32) | 7);
    private static readonly EntityId Other = new((1UL << 32) | 8);

    [Fact]
    public void A_grant_commits_the_agent_for_the_commitment_window()
    {
        var policy = ClaimPolicy.WithOptions(new ClaimPolicyOptions { CommitmentWindow = 5, CooldownTicks = 0, BreakerMaxStarts = 100 });
        var state = default(ClaimAgentState);
        Assert.True(policy.MayPropose(in state, Target, 10));

        policy.OnGranted(ref state, Target, 10);
        policy.OnReleased(ref state, Target, 11);
        Assert.False(policy.MayPropose(in state, Other, 11));
        Assert.False(policy.MayPropose(in state, Other, 14));
        Assert.True(policy.MayPropose(in state, Other, 15));
    }

    [Fact]
    public void A_denial_cools_down_that_target_only_and_the_four_newest_targets_are_remembered()
    {
        var policy = ClaimPolicy.WithOptions(new ClaimPolicyOptions { CommitmentWindow = 0, CooldownTicks = 20 });
        var state = default(ClaimAgentState);
        policy.OnDenied(ref state, Target, 0);

        Assert.False(policy.MayPropose(in state, Target, 0));
        Assert.False(policy.MayPropose(in state, Target, 19));
        Assert.True(policy.MayPropose(in state, Target, 20));
        Assert.True(policy.MayPropose(in state, Other, 0));

        for (uint i = 0; i < ClaimAgentState.CooldownEntries; i++)
        {
            policy.OnDenied(ref state, new EntityId((1UL << 32) | (100 + i)), 1);
        }

        // The oldest entry (Target, until 20) was evicted by the four newer ones.
        Assert.True(policy.MayPropose(in state, Target, 2));
        Assert.False(policy.MayPropose(in state, new EntityId((1UL << 32) | 103), 2));
    }

    [Fact]
    public void Too_many_job_starts_in_a_window_trip_the_circuit_breaker()
    {
        var policy = ClaimPolicy.WithOptions(new ClaimPolicyOptions { CommitmentWindow = 0, BreakerMaxStarts = 2, BreakerWindow = 10, BreakerBarTicks = 30 });
        var state = default(ClaimAgentState);
        policy.OnGranted(ref state, Target, 1);
        policy.OnGranted(ref state, Target, 3);
        Assert.True(policy.MayPropose(in state, Target, 4));

        policy.OnGranted(ref state, Target, 5); // third start inside the window
        Assert.False(policy.MayPropose(in state, Target, 5));
        Assert.False(policy.MayPropose(in state, Other, 34));
        Assert.True(policy.MayPropose(in state, Other, 35));

        // Starts spread wider than the window never trip it.
        state = default;
        policy.OnGranted(ref state, Target, 0);
        policy.OnGranted(ref state, Target, 10);
        policy.OnGranted(ref state, Target, 20);
        Assert.True(policy.MayPropose(in state, Target, 20));
    }

    [Fact]
    public void A_lost_target_lifts_the_commitment_so_the_agent_can_look_again()
    {
        var policy = ClaimPolicy.Default;
        var state = default(ClaimAgentState);
        policy.OnGranted(ref state, Target, 0);
        Assert.False(policy.MayPropose(in state, Other, 1));
        policy.OnLost(ref state, Target, 1);
        Assert.True(policy.MayPropose(in state, Other, 1));
    }

    private sealed class EvenTicksOnly : ClaimPolicy
    {
        public override bool MayPropose(in ClaimAgentState state, EntityId target, long tick) => tick % 2 == 0;

        public override void OnGranted(ref ClaimAgentState state, EntityId target, long tick) => state = state with { RecentStarts = state.RecentStarts + 1 };

        public override void OnDenied(ref ClaimAgentState state, EntityId target, long tick)
        {
        }

        public override void OnReleased(ref ClaimAgentState state, EntityId target, long tick)
        {
        }

        public override void OnLost(ref ClaimAgentState state, EntityId target, long tick)
        {
        }
    }

    [Fact]
    public void A_game_policy_replaces_the_defaults_end_to_end()
    {
        using var world = ClaimTestWorld.Create(out var claims, new ClaimOptions { Policy = new EvenTicksOnly() });
        var agent = ClaimTestWorld.SpawnAgents(world, claims, 1)[0];
        var target = ClaimTestWorld.SpawnTargets(world, claims, 1)[0];
        var proposal = default(ClaimProposal);
        var grant = default(ClaimGrant);
        var state = default(ClaimAgentState);

        Assert.False(claims.TryPropose(ref proposal, in grant, in state, target, 0, 0, tick: 1));
        Assert.True(claims.TryPropose(ref proposal, in grant, in state, target, 0, 0, tick: 2));
        world.Get<ClaimProposal>(agent, claims.Proposal) = proposal;
        world.Tick();

        Assert.Equal(target, world.TargetOf(agent, claims.Relation));
        Assert.Equal(1, world.Get<ClaimAgentState>(agent, claims.Agent).RecentStarts);
        Assert.IsType<EvenTicksOnly>(claims.Policy);
    }
}
