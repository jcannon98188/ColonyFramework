using Colony.Persistence;

namespace Colony.Samples.Lockstep;

/// <summary>
/// The game both peers run: colonists that get hungrier every Tick, a shared Pantry, and two Input Commands. Every
/// seat is one Input source; its script is a pure function of (seat, Tick), so a peer needs nothing from the
/// others but their frames.
/// </summary>
public static class LockstepGame
{
    public const ulong Seed = 0x4C4F_434B_5354_4550UL;

    public struct Colonist
    {
        public int Hunger;
    }

    public struct Pantry
    {
        public long Food;
        public int Colonists;
        public int Feedings;
    }

    public struct Spawn
    {
        public int Count;
    }

    public struct Feed
    {
        public int Amount;
    }

    /// <summary>Every peer must build the identical schema (the handshake compares its hash).</summary>
    public static WorldSchema BuildSchema()
    {
        var builder = new WorldBuilder();
        builder.AddComponent<Colonist>("colonist");
        builder.AddResource<Pantry>("pantry");
        var spawn = builder.AddInputCommand<Spawn>("spawn");
        var feed = builder.AddInputCommand<Feed>("feed");
        var colonist = builder.Component<Colonist>();
        var pantry = builder.Resource<Pantry>();

        builder.AddSystem(new SystemDefinition
        {
            Name = "game.spawn",
            Phase = Phase.Input,
            WritesResources = ResourceMask.Of(pantry),
            Begin = (in SystemContext context) =>
            {
                foreach (var entry in context.Inputs<Spawn>(spawn))
                {
                    for (int i = 0; i < entry.Command.Count; i++)
                    {
                        var id = context.Commands.Create(ComponentMask.Of(colonist));
                        context.Commands.Add(id, colonist, new Colonist { Hunger = entry.Random.Int32At((ulong)i, 0, 20) });
                    }

                    context.Resource<Pantry>(pantry).Colonists += entry.Command.Count;
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "game.feed",
            Phase = Phase.Input,
            WritesResources = ResourceMask.Of(pantry),
            After = ["game.spawn"],
            Begin = (in SystemContext context) =>
            {
                foreach (var entry in context.Inputs<Feed>(feed))
                {
                    ref var p = ref context.Resource<Pantry>(pantry);
                    p.Food += entry.Command.Amount;
                    p.Feedings++;
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "game.hunger",
            Phase = Phase.Simulate,
            Writes = ComponentMask.Of(colonist),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var column = chunk.Column<Colonist>(colonist);
                var entities = chunk.Entities;
                for (int row = 0; row < column.Length; row++)
                {
                    column[row].Hunger += 1 + context.Random.Int32At(entities[row].Value, 0, 2);
                    if (column[row].Hunger > 400)
                    {
                        column[row].Hunger = 0;
                    }
                }
            },
        });
        return builder.Build();
    }

    /// <summary>This seat's commands for the open input Tick: a feeding every other Tick, a spawn on a seat-specific cycle.</summary>
    public static void Script(LockstepSession session, long tick)
    {
        ArgumentNullException.ThrowIfNull(session);
        int seat = session.LocalPeer;
        if (tick % 2 == 0)
        {
            session.Submit(new Feed { Amount = (int)(tick % 17) + seat });
        }

        if (tick % 7 == seat % 7)
        {
            session.Submit(new Spawn { Count = 1 + seat });
        }
    }
}
