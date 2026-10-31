namespace Colony.Jobs.Tests;

public struct AgentTag
{
    public int Kind;
}

public struct TargetTag
{
    public int Kind;
}

/// <summary>Shared fixture: a schema with agents, targets and Claims, plus host-side helpers to spawn and propose between Ticks.</summary>
internal static class ClaimTestWorld
{
    public static WorldBuilder Builder(out ClaimSet claims, ClaimOptions? options = null)
    {
        var builder = new WorldBuilder()
            .AddComponent<AgentTag>("test.agent")
            .AddComponent<TargetTag>("test.target")
            .DebugValidation(false);
        claims = builder.AddClaims(options);
        return builder;
    }

    public static World Create(out ClaimSet claims, ClaimOptions? options = null, int workers = 1)
    {
        var schema = Builder(out claims, options).Build();
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
