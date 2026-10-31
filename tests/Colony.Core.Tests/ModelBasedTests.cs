namespace Colony.Core.Tests;

/// <summary>
/// Correctness oracle for the storage (AC #3): random sequences of structural operations are applied
/// through a command buffer and, after every playback, to a dictionary-based reference ECS with the
/// same semantics (stale targets skipped, Add overwrites, Remove of an absent Component is a no-op).
/// Observable state is compared after every playback, both through the Entity API and through chunk
/// iteration, so swap-back moves and archetype transitions are checked against the reference.
/// </summary>
public sealed class ModelBasedTests
{
    private const int Position = 0;
    private const int Hunger = 1;
    private const int Health = 2;
    private const int Marked = 3;

    [Theory]
    [InlineData(1, 300)]
    [InlineData(2, 300)]
    [InlineData(3, 300)]
    [InlineData(4, 600)]
    [InlineData(5, 600)]
    public void Random_operation_sequences_match_the_dictionary_reference(int seed, int batches)
    {
        using var world = TestSchemas.ColonistSchema(debug: true).CreateWorld();
        var schema = world.Schema;
        var types = new[]
        {
            schema.Component<Position>(), schema.Component<Hunger>(), schema.Component<Health>(), schema.Component<Marked>(),
        };
        var model = new ReferenceEcs();
        var random = new Random(seed);
        var buffer = world.CreateCommandBuffer();
        var pending = new List<Op>();

        for (int batch = 0; batch < batches; batch++)
        {
            pending.Clear();
            int count = random.Next(1, 40);
            int deferredCount = 0;
            for (int i = 0; i < count; i++)
            {
                var op = NextOp(random, model, deferredCount);
                if (op.Kind == OpKind.Create)
                {
                    deferredCount++;
                }

                pending.Add(op);
                Record(buffer, types, op);
            }

            world.Playback(buffer);
            model.Apply(pending, buffer);
            Verify(world, types, model);

            if (batch % 50 == 49)
            {
                world.ResetScratch();
            }
        }
    }

    private static Op NextOp(Random random, ReferenceEcs model, int deferredCount)
    {
        int roll = random.Next(100);
        if (roll < 25 || (model.Alive.Count == 0 && deferredCount == 0))
        {
            // Create, with a random set of fragmenting Components in the signature.
            int signature = random.Next(8);
            return new Op(OpKind.Create, default, -1, signature, 0);
        }

        var target = PickTarget(random, model, deferredCount);
        if (roll < 40)
        {
            return new Op(OpKind.Destroy, target, -1, 0, 0);
        }

        int component = random.Next(4);
        return roll < 75
            ? new Op(OpKind.Add, target, component, 0, random.Next(1, 1_000_000))
            : new Op(OpKind.Remove, target, component, 0, 0);
    }

    private static Target PickTarget(Random random, ReferenceEcs model, int deferredCount)
    {
        int roll = random.Next(10);
        if (roll == 0 && model.Dead.Count > 0)
        {
            return Target.Real(model.Dead[random.Next(model.Dead.Count)]);
        }

        if (roll <= 2 && deferredCount > 0)
        {
            return Target.Deferred(random.Next(deferredCount));
        }

        if (model.Alive.Count > 0)
        {
            return Target.Real(model.AliveIds[random.Next(model.AliveIds.Count)]);
        }

        return Target.Deferred(random.Next(deferredCount));
    }

    private static void Record(CommandBuffer buffer, ComponentType[] types, Op op)
    {
        switch (op.Kind)
        {
            case OpKind.Create:
                var mask = ComponentMask.Empty;
                for (int c = 0; c < 3; c++)
                {
                    if ((op.Signature & (1 << c)) != 0)
                    {
                        mask = mask.With(types[c]);
                    }
                }

                buffer.Create(mask);
                break;
            case OpKind.Destroy:
                buffer.Destroy(op.Target.ToEntityId());
                break;
            case OpKind.Add:
                var type = types[op.Component];
                var id = op.Target.ToEntityId();
                switch (op.Component)
                {
                    case Position:
                        buffer.Add(id, type, new Position { X = op.Value, Y = -op.Value });
                        break;
                    case Hunger:
                        buffer.Add(id, type, new Hunger { Value = op.Value });
                        break;
                    case Health:
                        buffer.Add(id, type, new Health { Value = op.Value });
                        break;
                    default:
                        buffer.Add(id, type, new Marked { Stamp = op.Value });
                        break;
                }

                break;
            default:
                buffer.Remove(op.Target.ToEntityId(), types[op.Component]);
                break;
        }
    }

    private static void Verify(World world, ComponentType[] types, ReferenceEcs model)
    {
        Assert.Equal(model.Alive.Count, world.EntityCount);
        foreach (var (id, components) in model.Alive)
        {
            Assert.True(world.IsAlive(id), $"{id} should be alive");
            for (int c = 0; c < 4; c++)
            {
                bool expected = components.TryGetValue(c, out int value);
                Assert.Equal(expected, world.Has(id, types[c]));
                if (expected)
                {
                    Assert.Equal(value, ReadValue(world, types, id, c));
                }
            }
        }

        foreach (var id in model.Dead)
        {
            Assert.False(world.IsAlive(id), $"{id} should be dead");
        }

        // Chunk iteration must see exactly the alive Entities, with the same column values.
        int seen = 0;
        foreach (var chunk in world.Query(ComponentMask.Empty))
        {
            var entities = chunk.Entities;
            seen += entities.Length;
            for (int row = 0; row < entities.Length; row++)
            {
                var id = entities[row];
                Assert.True(model.Alive.TryGetValue(id, out var components), $"chunk holds unknown {id}");
                for (int c = 0; c < 3; c++)
                {
                    Assert.Equal(components.ContainsKey(c), chunk.Has(types[c]));
                }

                if (chunk.Has(types[Position]))
                {
                    Assert.Equal(components[Position], chunk.Column<Position>(types[Position])[row].X);
                    Assert.Equal(-components[Position], chunk.Column<Position>(types[Position])[row].Y);
                }

                if (chunk.Has(types[Hunger]))
                {
                    Assert.Equal((float)components[Hunger], chunk.Column<Hunger>(types[Hunger])[row].Value);
                }

                if (chunk.Has(types[Health]))
                {
                    Assert.Equal(components[Health], chunk.Column<Health>(types[Health])[row].Value);
                }
            }
        }

        Assert.Equal(model.Alive.Count, seen);
    }

    private static int ReadValue(World world, ComponentType[] types, EntityId id, int component) => component switch
    {
        Position => world.Get<Position>(id, types[Position]).X,
        Hunger => (int)world.Get<Hunger>(id, types[Hunger]).Value,
        Health => world.Get<Health>(id, types[Health]).Value,
        _ => world.Get<Marked>(id, types[Marked]).Stamp,
    };

    private enum OpKind
    {
        Create,
        Destroy,
        Add,
        Remove,
    }

    private readonly record struct Target(EntityId Id, int DeferredOrdinal)
    {
        public static Target Real(EntityId id) => new(id, -1);

        public static Target Deferred(int ordinal) => new(default, ordinal);

        public bool IsDeferred => DeferredOrdinal >= 0;

        public EntityId ToEntityId() => IsDeferred ? EntityId.Deferred(DeferredOrdinal) : Id;
    }

    private readonly record struct Op(OpKind Kind, Target Target, int Component, int Signature, int Value);

    /// <summary>Dictionary ECS with the command-buffer semantics, applied after the real playback so deferred handles resolve.</summary>
    private sealed class ReferenceEcs
    {
        public Dictionary<EntityId, Dictionary<int, int>> Alive { get; } = [];

        public List<EntityId> AliveIds { get; } = [];

        public List<EntityId> Dead { get; } = [];

        public void Apply(List<Op> ops, CommandBuffer buffer)
        {
            var created = new List<EntityId>();
            foreach (var op in ops)
            {
                switch (op.Kind)
                {
                    case OpKind.Create:
                        var id = buffer.Resolve(EntityId.Deferred(created.Count));
                        created.Add(id);
                        var components = new Dictionary<int, int>();
                        for (int c = 0; c < 3; c++)
                        {
                            if ((op.Signature & (1 << c)) != 0)
                            {
                                components[c] = 0;
                            }
                        }

                        Alive.Add(id, components);
                        AliveIds.Add(id);
                        break;
                    case OpKind.Destroy:
                        var victim = Resolve(op.Target, created);
                        if (Alive.Remove(victim))
                        {
                            AliveIds.Remove(victim);
                            Dead.Add(victim);
                        }

                        break;
                    case OpKind.Add:
                        if (Alive.TryGetValue(Resolve(op.Target, created), out var adding))
                        {
                            adding[op.Component] = op.Value;
                        }

                        break;
                    default:
                        if (Alive.TryGetValue(Resolve(op.Target, created), out var removing))
                        {
                            removing.Remove(op.Component);
                        }

                        break;
                }
            }
        }

        private static EntityId Resolve(Target target, List<EntityId> created) => target.IsDeferred ? created[target.DeferredOrdinal] : target.Id;
    }
}
