namespace Colony.Jobs.Tests;

/// <summary>AC #2 (storage clause) and AC #5: Claims are an ordinary Relation plus plain Components, resolved by a Begin-Phase System.</summary>
public sealed class ClaimRegistrationTests
{
    [Fact]
    public void AddClaims_registers_the_claim_relation_its_components_and_a_begin_phase_resolve_system()
    {
        var schema = ClaimTestWorld.Builder(out var claims).Build();

        var relation = schema.Relation(ClaimSet.RelationName);
        Assert.Equal(claims.Relation, relation.Type);
        Assert.True(schema.Describe(claims.Relation.Component).IsRelation);
        Assert.Equal("colony.claim.proposal", schema.Describe(claims.Proposal).Name);
        Assert.Equal("colony.claim.grant", schema.Describe(claims.Grant).Name);
        Assert.Equal("colony.claim.agent", schema.Describe(claims.Agent).Name);
        Assert.Equal("colony.claim.capacity", schema.Describe(claims.Capacity).Name);
        Assert.True(claims.AgentComponents.Contains(claims.Proposal));
        Assert.True(claims.AgentComponents.Contains(claims.Grant));
        Assert.True(claims.AgentComponents.Contains(claims.Agent));
        Assert.Equal(Phase.Begin, schema.System(ClaimSet.ResolveSystemName).Phase);
    }

    [Fact]
    public void A_granted_claim_is_a_plain_relation_from_claimant_to_target_that_the_hash_covers_and_the_index_rebuild_reproduces()
    {
        using var world = ClaimTestWorld.Create(out var claims);
        var agents = ClaimTestWorld.SpawnAgents(world, claims, 3);
        var targets = ClaimTestWorld.SpawnTargets(world, claims, 1, slots: 3);
        ulong before = world.Hash();

        ClaimTestWorld.Propose(world, claims, agents[2], targets[0]);
        ClaimTestWorld.Propose(world, claims, agents[0], targets[0]);
        world.Tick();

        Assert.Equal(targets[0], world.TargetOf(agents[0], claims.Relation));
        Assert.Equal(targets[0], world.TargetOf(agents[2], claims.Relation));
        Assert.Equal(EntityId.Null, world.TargetOf(agents[1], claims.Relation));
        Assert.Equal(new[] { agents[0], agents[2] }, ClaimTestWorld.Sources(world, targets[0], claims.Relation));
        Assert.Equal(targets[0], world.Get<ClaimGrant>(agents[0], claims.Grant).Target);
        Assert.Equal(EntityId.Null, world.Get<ClaimProposal>(agents[0], claims.Proposal).Target);

        ulong granted = world.Hash();
        Assert.NotEqual(before, granted);

        // No side table: the forward links and the Grant Components are the whole saved state, so rebuilding
        // the reverse index from the Components reproduces the same lookups and leaves the hash unchanged.
        world.RebuildRelationIndex();
        Assert.Equal(new[] { agents[0], agents[2] }, ClaimTestWorld.Sources(world, targets[0], claims.Relation));
        Assert.Equal(granted, world.Hash());
    }
}
