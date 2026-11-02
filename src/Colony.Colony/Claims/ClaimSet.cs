using Colony.Diagnostics;

namespace Colony.Jobs;

/// <summary>Registration options for <see cref="ClaimsBuilderExtensions.AddClaims"/>.</summary>
public sealed class ClaimOptions
{
    /// <summary>The anti-thrash policy (spec section 9.2); <see cref="ClaimPolicy.Default"/> unless replaced.</summary>
    public ClaimPolicy Policy { get; init; } = ClaimPolicy.Default;

    /// <summary>Proposals the resolve System can hold before growing its scratch arrays (grow-only; size it for the load so steady state allocates nothing).</summary>
    public int InitialProposalCapacity { get; init; } = 1024;

    /// <summary>
    /// Proposals are resolved per target bucket, in parallel across buckets: a bucket is a block of
    /// 2^<see cref="BucketShift"/> consecutive target Entity slots (the stand-in for "target chunk": both are
    /// deterministic functions of the target alone). Lower means more, smaller buckets.
    /// </summary>
    public int BucketShift { get; init; } = 8;
}

/// <summary>
/// The handles of the built-in Claim machinery (spec section 9.2) registered by
/// <see cref="ClaimsBuilderExtensions.AddClaims"/>: the Claim <see cref="Relation"/> from claimant to
/// target, the three agent Components (<see cref="Proposal"/>, <see cref="Grant"/>, <see cref="Agent"/>; give
/// every agent <see cref="AgentComponents"/>), the per-target <see cref="Capacity"/>, the resolve events and
/// the running <see cref="Stats"/>. Agents propose from their own Simulate jobs with <see cref="TryPropose"/>
/// and end jobs with <see cref="Release"/>; the <see cref="ResolveSystemName"/> System grants or denies in the
/// Begin Phase of the next Tick. Everything a save needs is in these Components and the Relation.
/// </summary>
public sealed class ClaimSet
{
    public const string RelationName = "colony.claim";
    public const string ProposalName = "colony.claim.proposal";
    public const string GrantName = "colony.claim.grant";
    public const string AgentName = "colony.claim.agent";
    public const string CapacityName = "colony.claim.capacity";
    public const string GrantedEventName = "colony.claim.granted";
    public const string DeniedEventName = "colony.claim.denied";
    public const string LostEventName = "colony.claim.lost";
    public const string StatsName = "colony.claim.stats";
    public const string ResolveSystemName = "colony.claims.resolve";
    public const string GrantedCounterName = "colony.claims.granted";
    public const string DeniedCounterName = "colony.claims.denied";
    public const string LostCounterName = "colony.claims.lost";

    internal ClaimSet(
        RelationType relation,
        ComponentType proposal,
        ComponentType grant,
        ComponentType agent,
        ComponentType capacity,
        ResourceType granted,
        ResourceType denied,
        ResourceType lost,
        ResourceType stats,
        CounterId grantedCounter,
        CounterId deniedCounter,
        CounterId lostCounter,
        ClaimPolicy policy)
    {
        Relation = relation;
        Proposal = proposal;
        Grant = grant;
        Agent = agent;
        Capacity = capacity;
        Granted = granted;
        Denied = denied;
        Lost = lost;
        Stats = stats;
        GrantedCounter = grantedCounter;
        DeniedCounter = deniedCounter;
        LostCounter = lostCounter;
        Policy = policy;
        AgentComponents = ComponentMask.Of(proposal, grant, agent);
    }

    /// <summary>The Claim Relation: source = claimant, target = the claimed Entity. Reverse lookup with <see cref="World.SourcesOf"/>.</summary>
    public RelationType Relation { get; }

    /// <summary><see cref="ClaimProposal"/> on every agent.</summary>
    public ComponentType Proposal { get; }

    /// <summary><see cref="ClaimGrant"/> on every agent.</summary>
    public ComponentType Grant { get; }

    /// <summary><see cref="ClaimAgentState"/> on every agent.</summary>
    public ComponentType Agent { get; }

    /// <summary><see cref="ClaimCapacity"/> on targets that allow more than one claimant or meter an amount.</summary>
    public ComponentType Capacity { get; }

    /// <summary>The three agent Components; add them to every Entity that proposes claims.</summary>
    public ComponentMask AgentComponents { get; }

    /// <summary><see cref="ClaimGranted"/> queue, readable from Simulate onwards in the Tick of the grant.</summary>
    public ResourceType Granted { get; }

    /// <summary><see cref="ClaimDenied"/> queue.</summary>
    public ResourceType Denied { get; }

    /// <summary><see cref="ClaimLost"/> queue.</summary>
    public ResourceType Lost { get; }

    /// <summary><see cref="ClaimStats"/> value Resource.</summary>
    public ResourceType Stats { get; }

    public CounterId GrantedCounter { get; }

    public CounterId DeniedCounter { get; }

    public CounterId LostCounter { get; }

    public ClaimPolicy Policy { get; }

    /// <summary>
    /// Records a proposal into the agent's own <see cref="ClaimProposal"/> row (call it from the agent's chunk
    /// job with the row's refs; declare <see cref="Proposal"/> in Writes and <see cref="Grant"/>/<see cref="Agent"/>
    /// in Reads). Refused when the agent already holds a claim or the policy says no (commitment, cooldown,
    /// breaker); a later call in the same round replaces the earlier proposal. Resolved next Begin Phase.
    /// </summary>
    public bool TryPropose(ref ClaimProposal proposal, in ClaimGrant grant, in ClaimAgentState state, EntityId target, long priority, int amount, long tick)
    {
        if (target.IsNull)
        {
            throw new ArgumentException("A proposal needs a target.", nameof(target));
        }

        if (grant.IsHeld || !Policy.MayPropose(in state, target, tick))
        {
            return false;
        }

        proposal = new ClaimProposal(target, priority, amount);
        return true;
    }

    /// <summary>
    /// Ends the agent's job: drops the Claim Relation (through <paramref name="commands"/>, applied at the Phase
    /// boundary), clears the agent's <see cref="ClaimGrant"/> row and informs the policy. Call it from the
    /// agent's chunk job with the row's refs (declare <see cref="Grant"/> and <see cref="Agent"/> in Writes).
    /// A no-op when nothing is held.
    /// </summary>
    public void Release(CommandBuffer commands, EntityId claimant, ref ClaimGrant grant, ref ClaimAgentState state, long tick)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (!grant.IsHeld)
        {
            return;
        }

        commands.Unrelate(claimant, Relation);
        Policy.OnReleased(ref state, grant.Target, tick);
        grant = default;
    }
}

public static class ClaimsBuilderExtensions
{
    /// <summary>
    /// Registers the built-in Claim Relation, its Components, events, stats and the Begin-Phase resolve
    /// System (spec section 9.2). Call it once per builder; the returned handles are what agents' Systems
    /// and the host use.
    /// </summary>
    public static ClaimSet AddClaims(this WorldBuilder builder, ClaimOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new ClaimOptions();
        ArgumentNullException.ThrowIfNull(options.Policy, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BucketShift, 0, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.BucketShift, 30, nameof(options));

        var relation = builder.AddRelation(ClaimSet.RelationName);
        builder
            .AddComponent<ClaimProposal>(ClaimSet.ProposalName)
            .AddComponent<ClaimGrant>(ClaimSet.GrantName)
            .AddComponent<ClaimAgentState>(ClaimSet.AgentName)
            .AddComponent<ClaimCapacity>(ClaimSet.CapacityName)
            .AddEvent<ClaimGranted>(ClaimSet.GrantedEventName)
            .AddEvent<ClaimDenied>(ClaimSet.DeniedEventName)
            .AddEvent<ClaimLost>(ClaimSet.LostEventName)
            .AddResource<ClaimStats>(ClaimSet.StatsName);

        var set = new ClaimSet(
            relation,
            builder.Component<ClaimProposal>(),
            builder.Component<ClaimGrant>(),
            builder.Component<ClaimAgentState>(),
            builder.Component<ClaimCapacity>(),
            builder.Resource<ClaimGranted>(),
            builder.Resource<ClaimDenied>(),
            builder.Resource<ClaimLost>(),
            builder.Resource<ClaimStats>(),
            builder.AddCounter(ClaimSet.GrantedCounterName),
            builder.AddCounter(ClaimSet.DeniedCounterName),
            builder.AddCounter(ClaimSet.LostCounterName),
            options.Policy);

        var resolver = new ClaimResolver(set, options);
        builder.AddSystem(new SystemDefinition
        {
            Name = ClaimSet.ResolveSystemName,
            Phase = Phase.Begin,
            All = set.AgentComponents,
            Reads = ComponentMask.Of(relation.Component, set.Capacity),
            Writes = set.AgentComponents,
            WritesResources = ResourceMask.Of(set.Granted, set.Denied, set.Lost, set.Stats),
            IgnoreEnableBits = true,
            Begin = resolver.Begin,
            ForEachChunk = resolver.Gather,
            End = resolver.Resolve,
        });

        return set;
    }
}
