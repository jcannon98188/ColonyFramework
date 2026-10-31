namespace Colony.Jobs.Tests;

/// <summary>AC #2 (release clauses) and AC #5 (resolution runs in the Begin Phase of the Tick after the proposal).</summary>
public sealed class ClaimLifecycleTests
{
    [Fact]
    public void Destroying_the_claimant_drops_the_claim_from_the_target()
    {
        using var world = ClaimTestWorld.Create(out var claims);
        var agents = ClaimTestWorld.SpawnAgents(world, claims, 2);
        var target = ClaimTestWorld.SpawnTargets(world, claims, 1, slots: 2)[0];
        ClaimTestWorld.Propose(world, claims, agents[0], target);
        ClaimTestWorld.Propose(world, claims, agents[1], target);
        world.Tick();
        Assert.Equal(2, world.SourcesOf(target, claims.Relation).Count);

        var buffer = world.CreateCommandBuffer();
        buffer.Destroy(agents[0]);
        world.Playback(buffer);

        Assert.Equal(new[] { agents[1] }, ClaimTestWorld.Sources(world, target, claims.Relation));
        world.Tick();
        Assert.Equal(new[] { agents[1] }, ClaimTestWorld.Sources(world, target, claims.Relation));
    }

    [Fact]
    public void Destroying_the_target_releases_the_claimant_and_reports_the_loss_next_resolve()
    {
        using var world = ClaimTestWorld.Create(out var claims, out var log);
        var agent = ClaimTestWorld.SpawnAgents(world, claims, 1)[0];
        var target = ClaimTestWorld.SpawnTargets(world, claims, 1)[0];
        ClaimTestWorld.Propose(world, claims, agent, target);
        world.Tick();
        Assert.True(world.Get<ClaimGrant>(agent, claims.Grant).IsHeld);

        var buffer = world.CreateCommandBuffer();
        buffer.Destroy(target);
        world.Playback(buffer);
        Assert.Equal(EntityId.Null, world.TargetOf(agent, claims.Relation));

        world.Tick();
        Assert.False(world.Get<ClaimGrant>(agent, claims.Grant).IsHeld);
        var lost = Assert.Single(log.Lost);
        Assert.Equal(agent, lost.Claimant);
        Assert.Equal(target, lost.Target);
        Assert.Equal(1, world.Resource<ClaimStats>(claims.Stats).Lost);

        // Freed for good: a new proposal from the same agent is granted.
        var other = ClaimTestWorld.SpawnTargets(world, claims, 1)[0];
        ClaimTestWorld.Propose(world, claims, agent, other);
        world.Tick();
        Assert.Equal(other, world.TargetOf(agent, claims.Relation));
    }

    [Fact]
    public void Agents_propose_and_release_from_their_own_simulate_jobs_and_resolution_lands_in_the_next_begin_phase()
    {
        // A Game-style Simulate System: at Tick 0 every agent proposes the target; at Tick 2 it ends its job.
        // Probes record what the Begin Phase (after the resolver) and the Simulate Phase see each Tick.
        var builder = ClaimTestWorld.Builder(out var claims);
        var beginSeen = new List<(long Tick, int Holders)>();
        var simulateSeen = new List<(long Tick, int Holders)>();
        var set = claims;
        var targetTag = builder.Component<TargetTag>();
        EntityId theTarget = default;
        builder.AddSystem(new SystemDefinition
        {
            Name = "test.agents",
            Phase = Phase.Simulate,
            All = set.AgentComponents,
            Reads = ComponentMask.Of(set.Agent),
            Writes = ComponentMask.Of(set.Proposal, set.Grant, set.Agent),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var proposals = chunk.Column<ClaimProposal>(set.Proposal);
                var grants = chunk.Column<ClaimGrant>(set.Grant);
                var states = chunk.Column<ClaimAgentState>(set.Agent);
                var entities = chunk.Entities;
                for (int i = 0; i < entities.Length; i++)
                {
                    if (context.Tick == 0)
                    {
                        Assert.True(set.TryPropose(ref proposals[i], in grants[i], in states[i], theTarget, priority: entities[i].Index, amount: 0, context.Tick));
                    }
                    else if (context.Tick == 2)
                    {
                        set.Release(context.Commands, entities[i], ref grants[i], ref states[i], context.Tick);
                    }
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "test.begin-probe",
            Phase = Phase.Begin,
            Reads = ComponentMask.Of(set.Relation.Component, targetTag),
            After = [ClaimSet.ResolveSystemName],
            Begin = (in SystemContext context) => beginSeen.Add((context.Tick, context.World.SourcesOf(theTarget, set.Relation).Count)),
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "test.simulate-probe",
            Phase = Phase.Simulate,
            Reads = ComponentMask.Of(set.Relation.Component, targetTag),
            After = ["test.agents"],
            Begin = (in SystemContext context) => simulateSeen.Add((context.Tick, context.World.SourcesOf(theTarget, set.Relation).Count)),
        });

        using var world = builder.Build().CreateWorld(new WorldOptions { Seed = 1 });
        ClaimTestWorld.SpawnAgents(world, claims, 3);
        theTarget = ClaimTestWorld.SpawnTargets(world, claims, 1, slots: 2)[0];

        for (int t = 0; t < 4; t++)
        {
            world.Tick();
        }

        // Tick 0: proposals written in Simulate; nothing granted yet.
        // Tick 1: the Begin Phase resolves them (2 of 3 fit); the Relate merges at the Begin boundary, so the
        //         grants are visible to Simulate of the same Tick (and to Begin Systems from the next Tick on).
        // Tick 2: Simulate releases; the Unrelate merges at the Simulate boundary, so Begin of Tick 3 sees 0.
        Assert.Equal([(0, 0), (1, 0), (2, 2), (3, 0)], beginSeen);
        Assert.Equal([(0, 0), (1, 2), (2, 2), (3, 0)], simulateSeen);
        Assert.Equal(Phase.Begin, world.Schema.System(ClaimSet.ResolveSystemName).Phase);
    }
}
