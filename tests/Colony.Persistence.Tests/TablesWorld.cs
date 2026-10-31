namespace Colony.Persistence.Tests;

public struct Label
{
    public StringId Name;
    public BlobRef<int> Data;
}

public struct Weight
{
    public int Value;
}

public struct Score
{
    public long Value;
}

/// <summary>
/// A hand-built World exercising every store the container carries beyond plain columns: interned strings,
/// blobs, buffer Components inline and overflowing to the heap, a non-fragmenting Component, a Relation and
/// the hierarchy, disabled Components and destroyed Entities (a non-trivial free list).
/// </summary>
internal sealed class TablesWorld
{
    public const ulong Seed = 99;
    public const int Entities = 100;

    private TablesWorld(WorldSchema schema, ComponentType label, ComponentType weight, ComponentType score, ComponentType items, RelationType likes)
    {
        Schema = schema;
        Label = label;
        Weight = weight;
        Score = score;
        Items = items;
        Likes = likes;
    }

    public WorldSchema Schema { get; }

    public ComponentType Label { get; }

    public ComponentType Weight { get; }

    public ComponentType Score { get; }

    public ComponentType Items { get; }

    public RelationType Likes { get; }

    public static TablesWorld Compose()
    {
        var builder = new WorldBuilder()
            .AddComponent<Label>("t.label")
            .AddComponent<Weight>("t.weight")
            .AddComponent<Score>("t.score", ComponentOptions.NonFragmenting)
            .AddBufferComponent<int, Inline4<int>>("t.items")
            .DebugValidation(false);
        var likes = builder.AddRelation("t.likes");
        builder.AddHierarchy();
        var label = builder.Component<Label>();
        var weight = builder.Component<Weight>();
        var score = builder.Component<Score>();
        var items = builder.Component("t.items");
        return new TablesWorld(builder.Build(), label, weight, score, items, likes);
    }

    public World Create(int workers = 1)
    {
        var world = Schema.CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = workers, InitialEntityCapacity = 64 });
        Populate(world);
        return world;
    }

    /// <summary>The Entities with a label, in chunk order.</summary>
    public List<EntityId> Labelled(World world)
    {
        var ids = new List<EntityId>();
        foreach (var chunk in world.Query(ComponentMask.Of(Label), ignoreEnableBits: true))
        {
            foreach (var e in chunk.Entities)
            {
                ids.Add(e);
            }
        }

        return ids;
    }

    private void Populate(World world)
    {
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Entities; i++)
        {
            var mask = i % 3 == 0 ? ComponentMask.Of(Label, Weight, Items) : ComponentMask.Of(Label, Weight);
            buffer.Create(mask);
        }

        world.Playback(buffer);

        var ids = Labelled(world);
        var scratch = new int[40];
        for (int i = 0; i < ids.Count; i++)
        {
            var e = ids[i];
            ref var label = ref world.Get<Label>(e, Label);
            label.Name = world.Strings.Intern($"label-{i % 17}");
            for (int k = 0; k < i % 7; k++)
            {
                scratch[k] = (i * 31) + k;
            }

            label.Data = i % 5 == 0 ? world.Blobs.Store<int>(scratch.AsSpan(0, i % 7)) : default;
            world.Get<Weight>(e, Weight).Value = i * 11;
            if (world.Has(e, Items))
            {
                var items = world.GetBuffer<int>(e, Items);
                int count = i % 2 == 0 ? 3 : 20; // inline, or overflowing to the heap
                for (int k = 0; k < count; k++)
                {
                    items.Add(i + k);
                }
            }

            if (i % 4 == 1)
            {
                world.SetEnabled(e, Weight, false);
            }
        }

        buffer.Clear();
        for (int i = 0; i < ids.Count; i++)
        {
            if (i % 6 == 2)
            {
                buffer.Add(ids[i], Score, new Score { Value = i * 1000L });
            }

            if (i % 2 == 1)
            {
                buffer.Relate(ids[i], Likes, ids[(i + 7) % ids.Count]);
            }

            if (i >= 10 && i % 3 != 0)
            {
                buffer.SetParent(ids[i], ids[i % 10]);
            }
        }

        world.Playback(buffer);

        buffer.Clear();
        for (int i = 0; i < ids.Count; i++)
        {
            if (i % 9 == 4)
            {
                buffer.Destroy(ids[i]);
            }
        }

        world.Playback(buffer);
        world.ResetScratch();
    }
}
