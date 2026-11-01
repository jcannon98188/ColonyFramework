using Colony.Fields;

namespace Colony.Fields.Tests;

/// <summary>TASK-26: the residency manager (spec section 5.3). Focus points arrive as Input Commands; transitions happen in the Streaming Phase in canonical chunk order.</summary>
public sealed class ResidencyTests
{
    // 6 x 4 x 1 chunks (96 x 64 x 16 tiles).
    private static readonly WorldSize Size = new(96, 64, 16);

    private static (WorldSchema Schema, Field<int> Height, ComponentType TilePos) BuildSchema(Action<WorldBuilder>? extra = null)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        builder.AddComponent<TilePos>("tile.pos");
        var fields = builder.AddFields(Size);
        var height = fields.AddField<int>("height");
        builder.AddResidency(fields, builder.Component<TilePos>());
        extra?.Invoke(builder);
        var schema = builder.Build();
        return (schema, height, schema.Component<TilePos>());
    }

    [Fact]
    public void A_focus_point_makes_chunks_within_its_radius_Active_a_ring_Hibernating_and_the_rest_Unloaded_after_the_next_Streaming_Phase()
    {
        var (schema, _, _) = BuildSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var residency = world.Residency();

        // Without focus points everything is resident and simulated: the never-unload policy.
        Assert.All(Enumerable.Range(0, fields.ChunkCount), o => Assert.Equal(ChunkResidency.Active, residency.ResidencyOf(o)));

        // Focus on the chunk (1, 1, 0) with radius 1 chunk: Active 0..2 x 0..2, Hibernating ring up to 3, Unloaded beyond.
        world.Inputs.Submit(new InputSource(1), new FocusPoint { Id = 7, Center = new TilePos(20, 20, 3), Radius = 1 });
        world.Tick(); // Input Phase stores the focus; the Streaming Phase of this Tick ran before it.
        Assert.All(Enumerable.Range(0, fields.ChunkCount), o => Assert.Equal(ChunkResidency.Active, residency.ResidencyOf(o)));
        world.Tick(); // The Streaming Phase applies the transitions.

        for (int ordinal = 0; ordinal < fields.ChunkCount; ordinal++)
        {
            var c = fields.ChunkAt(ordinal);
            int distance = Math.Max(Math.Abs(c.X - 1), Math.Abs(c.Y - 1));
            var expected = distance <= 1 ? ChunkResidency.Active : distance == 2 ? ChunkResidency.Hibernating : ChunkResidency.Unloaded;
            Assert.Equal(expected, residency.ResidencyOf(ordinal));
            Assert.Equal(expected, fields.ResidencyOf(ordinal));
        }

        Assert.Equal(9, residency.Counts.Active);
        Assert.Equal(7, residency.Counts.Hibernating);
        Assert.Equal(24 - 16, residency.Counts.Unloaded);
        Assert.True(residency.RingInvariantHolds());
    }
}
