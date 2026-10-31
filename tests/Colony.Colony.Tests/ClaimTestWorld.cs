namespace Colony.Jobs.Tests;

public struct AgentTag
{
    public int Kind;
}

public struct TargetTag
{
    public int Kind;
}

/// <summary>
/// Records the resolve events of the latest Tick: queues are cleared at the End boundary, so a host cannot read
/// Begin-Phase events after <see cref="World.Tick"/>; a Simulate-Phase probe System copies them here instead.
/// </summary>
internal sealed class ClaimEventLog
{
    public List<ClaimGranted> Granted { get; } = [];

    public List<ClaimDenied> Denied { get; } = [];

    public List<ClaimLost> Lost { get; } = [];

    public void Record(in SystemContext context, ClaimSet claims)
    {
        Granted.Clear();
        Denied.Clear();
        Lost.Clear();
        Granted.AddRange(context.Events<ClaimGranted>(claims.Granted));
        Denied.AddRange(context.Events<ClaimDenied>(claims.Denied));
        Lost.AddRange(context.Events<ClaimLost>(claims.Lost));
    }
}

/// <summary>Shared fixture: a schema with agents, targets and Claims, plus host-side helpers to spawn and propose between Ticks.</summary>
internal static class ClaimTestWorld
{
    public static WorldBuilder Builder(out ClaimSet claims, ClaimOptions? options = null, ClaimEventLog? log = null)
    {
        var builder = new WorldBuilder()
            .AddComponent<AgentTag>("test.agent")
            .AddComponent<TargetTag>("test.target")
            .DebugValidation(false);
        claims = builder.AddClaims(options);
        if (log is not null)
        {
            var set = claims;
            builder.AddSystem(new SystemDefinition
            {
                Name = "test.probe",
                Phase = Phase.Simulate,
                ReadsResources = ResourceMask.Of(set.Granted, set.Denied, set.Lost),
                Begin = (in SystemContext context) => log.Record(in context, set),
            });
        }

        return builder;
    }

    public static World Create(out ClaimSet claims, ClaimOptions? options = null, int workers = 1)
        => Create(out claims, out _, options, workers);

    public static World Create(out ClaimSet claims, out ClaimEventLog log, ClaimOptions? options = null, int workers = 1)
    {
        log = new ClaimEventLog();
        var schema = Builder(out claims, options, log).Build();
        return schema.CreateWorld(new WorldOptions { WorkerThreads = workers, Seed = 7 });
    }

    public static EntityId[] SpawnAgents(World world, ClaimSet claims, int count)
    {
        var mask = ComponentMask.Of(world.Schema.Component<AgentTag>()).Union(claims.AgentComponents);
        return Spawn(world, mask, count);
    }

    public static EntityId[] SpawnTargets(World world, ClaimSet claims, int count, int slots = 1, int amount = 0)
    {
        var buffer = world.CreateCommandBuffer();
        var deferred = new EntityId[count];
        var mask = ComponentMask.Of(world.Schema.Component<TargetTag>(), claims.Capacity);
        for (int i = 0; i < count; i++)
        {
            deferred[i] = buffer.Create(mask);
            buffer.Add(deferred[i], claims.Capacity, new ClaimCapacity { Slots = slots, Amount = amount });
        }

        return Resolve(world, buffer, deferred);
    }

    public static EntityId[] Spawn(World world, ComponentMask mask, int count)
    {
        var buffer = world.CreateCommandBuffer();
        var deferred = new EntityId[count];
        for (int i = 0; i < count; i++)
        {
            deferred[i] = buffer.Create(mask);
        }

        return Resolve(world, buffer, deferred);
    }

    private static EntityId[] Resolve(World world, CommandBuffer buffer, EntityId[] deferred)
    {
        world.Playback(buffer);
        var ids = new EntityId[deferred.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = buffer.Resolve(deferred[i]);
        }

        world.ResetScratch();
        return ids;
    }

    /// <summary>Host-side proposal: what an agent's Simulate job would write into its own ClaimProposal row.</summary>
    public static void Propose(World world, ClaimSet claims, EntityId agent, EntityId target, long priority = 0, int amount = 0)
        => world.Get<ClaimProposal>(agent, claims.Proposal) = new ClaimProposal { Target = target, Priority = priority, Amount = amount };

    public static EntityId[] Sources(World world, EntityId target, RelationType relation)
    {
        var result = new List<EntityId>();
        foreach (var source in world.SourcesOf(target, relation))
        {
            result.Add(source);
        }

        return [.. result];
    }
}
