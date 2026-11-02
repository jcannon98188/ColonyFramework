namespace Colony.Jobs.Tests;

/// <summary>AC #1: resolution grants by (priority key, Entity id) within capacity and amount; denied agents are told why.</summary>
public sealed class ClaimResolutionTests
{
    [Fact]
    public void Within_capacity_the_smallest_priority_key_wins_and_ties_break_by_entity_id()
    {
        using var world = ClaimTestWorld.Create(out var claims, out var log);
        var agents = ClaimTestWorld.SpawnAgents(world, claims, 5);
        var target = ClaimTestWorld.SpawnTargets(world, claims, 1, slots: 2)[0];

        ClaimTestWorld.Propose(world, claims, agents[4], target, priority: 5);
        ClaimTestWorld.Propose(world, claims, agents[3], target, priority: 1);
        ClaimTestWorld.Propose(world, claims, agents[1], target, priority: 5);
        ClaimTestWorld.Propose(world, claims, agents[0], target, priority: 9);
        world.Tick();

        // priority 1 (agent 3) first, then the priority-5 tie goes to the smaller Entity id (agent 1).
        Assert.Equal(new[] { agents[1], agents[3] }, ClaimTestWorld.Sources(world, target, claims.Relation));
        Assert.Equal(0, world.Get<ClaimGrant>(agents[3], claims.Grant).Slot);
        Assert.Equal(1, world.Get<ClaimGrant>(agents[1], claims.Grant).Slot);
        Assert.False(world.Get<ClaimGrant>(agents[4], claims.Grant).IsHeld);

        Assert.Equal(new[] { agents[3], agents[1] }, log.Granted.Select(g => g.Claimant).ToArray());
        Assert.Equal(new[] { agents[4], agents[0] }, log.Denied.Select(d => d.Claimant).ToArray());
        Assert.All(log.Denied, d => Assert.Equal(ClaimDenialReason.Capacity, d.Reason));
        Assert.Equal(new ClaimStats(4, 2, 2, 0), world.Resource<ClaimStats>(claims.Stats));
    }

    [Fact]
    public void Existing_holders_count_against_capacity_and_freed_slots_are_reused_lowest_first()
    {
        using var world = ClaimTestWorld.Create(out var claims, out var log, new ClaimOptions { Policy = ClaimPolicy.None });
        var agents = ClaimTestWorld.SpawnAgents(world, claims, 4);
        var target = ClaimTestWorld.SpawnTargets(world, claims, 1, slots: 2)[0];

        ClaimTestWorld.Propose(world, claims, agents[0], target);
        ClaimTestWorld.Propose(world, claims, agents[1], target);
        world.Tick();
        ClaimTestWorld.Propose(world, claims, agents[2], target);
        world.Tick();
        Assert.Equal(ClaimDenialReason.Capacity, Assert.Single(log.Denied).Reason);

        // Agent 0 ends its job (slot 0 frees); agent 2 gets slot 0 on the next round.
        var buffer = world.CreateCommandBuffer();
        claims.Release(buffer, agents[0], ref world.Get<ClaimGrant>(agents[0], claims.Grant), ref world.Get<ClaimAgentState>(agents[0], claims.Agent), world.CurrentTick);
        world.Playback(buffer);
        Assert.Equal(EntityId.Null, world.TargetOf(agents[0], claims.Relation));

        ClaimTestWorld.Propose(world, claims, agents[2], target);
        world.Tick();
        Assert.Equal(new[] { agents[1], agents[2] }, ClaimTestWorld.Sources(world, target, claims.Relation));
        Assert.Equal(0, world.Get<ClaimGrant>(agents[2], claims.Grant).Slot);
        Assert.Equal(1, world.Get<ClaimGrant>(agents[1], claims.Grant).Slot);
    }

    [Fact]
    public void Metered_amounts_are_granted_until_the_target_runs_out()
    {
        using var world = ClaimTestWorld.Create(out var claims, out var log);
        var agents = ClaimTestWorld.SpawnAgents(world, claims, 3);
        var target = ClaimTestWorld.SpawnTargets(world, claims, 1, slots: 8, amount: 10)[0];

        ClaimTestWorld.Propose(world, claims, agents[0], target, priority: 0, amount: 6);
        ClaimTestWorld.Propose(world, claims, agents[1], target, priority: 1, amount: 6);
        ClaimTestWorld.Propose(world, claims, agents[2], target, priority: 2, amount: 4);
        world.Tick();

        Assert.Equal(new[] { agents[0], agents[2] }, ClaimTestWorld.Sources(world, target, claims.Relation));
        var denied = Assert.Single(log.Denied);
        Assert.Equal(agents[1], denied.Claimant);
        Assert.Equal(ClaimDenialReason.Amount, denied.Reason);
        Assert.Equal(6, world.Get<ClaimGrant>(agents[0], claims.Grant).Amount);
    }

    [Fact]
    public void A_target_that_died_before_the_round_denies_every_proposal_and_a_holder_cannot_propose_again()
    {
        using var world = ClaimTestWorld.Create(out var claims, out var log);
        var agents = ClaimTestWorld.SpawnAgents(world, claims, 2);
        var targets = ClaimTestWorld.SpawnTargets(world, claims, 2, slots: 4);

        ClaimTestWorld.Propose(world, claims, agents[0], targets[0]);
        world.Tick();
        Assert.Equal(targets[0], world.TargetOf(agents[0], claims.Relation));

        var buffer = world.CreateCommandBuffer();
        buffer.Destroy(targets[1]);
        world.Playback(buffer);
        ClaimTestWorld.Propose(world, claims, agents[1], targets[1]);
        ClaimTestWorld.Propose(world, claims, agents[0], targets[0]);
        world.Tick();

        Assert.Equal(2, log.Denied.Count);
        Assert.Equal(ClaimDenialReason.Holding, log.Denied.Single(d => d.Claimant == agents[0]).Reason);
        Assert.Equal(ClaimDenialReason.TargetGone, log.Denied.Single(d => d.Claimant == agents[1]).Reason);
        Assert.Equal(targets[0], world.TargetOf(agents[0], claims.Relation));
    }

    [Fact]
    public void Targets_without_a_capacity_component_take_one_claimant()
    {
        using var world = ClaimTestWorld.Create(out var claims);
        var agents = ClaimTestWorld.SpawnAgents(world, claims, 2);
        var target = ClaimTestWorld.Spawn(world, ComponentMask.Of(world.Schema.Component<TargetTag>()), 1)[0];

        ClaimTestWorld.Propose(world, claims, agents[0], target, priority: 2);
        ClaimTestWorld.Propose(world, claims, agents[1], target, priority: 1);
        world.Tick();

        Assert.Equal(new[] { agents[1] }, ClaimTestWorld.Sources(world, target, claims.Relation));
    }

    [Fact]
    public void A_slot_reused_in_the_round_keeps_one_group_per_target_generation_and_never_grants_past_capacity()
    {
        using var world = ClaimTestWorld.Create(out var claims, out var log, new ClaimOptions { Policy = ClaimPolicy.None });
        var agents = ClaimTestWorld.SpawnAgents(world, claims, 3);
        var old = ClaimTestWorld.SpawnTargets(world, claims, 1, slots: 1)[0];
        var buffer = world.CreateCommandBuffer();
        buffer.Destroy(old);
        world.Playback(buffer);
        var reused = ClaimTestWorld.SpawnTargets(world, claims, 1, slots: 1)[0];
        Assert.Equal(old.Index, reused.Index);
        Assert.NotEqual(old, reused);

        // Sorted on the slot alone these interleave as reused, old, reused: the second reused group recounted held = 0.
        ClaimTestWorld.Propose(world, claims, agents[0], reused, priority: 1);
        ClaimTestWorld.Propose(world, claims, agents[1], old, priority: 2);
        ClaimTestWorld.Propose(world, claims, agents[2], reused, priority: 3);
        world.Tick();

        Assert.Equal(new[] { agents[0] }, ClaimTestWorld.Sources(world, reused, claims.Relation));
        Assert.Equal(new[] { agents[0] }, log.Granted.Select(g => g.Claimant).ToArray());
        Assert.Equal(ClaimDenialReason.TargetGone, log.Denied.Single(d => d.Claimant == agents[1]).Reason);
        Assert.Equal(ClaimDenialReason.Capacity, log.Denied.Single(d => d.Claimant == agents[2]).Reason);
    }
}
