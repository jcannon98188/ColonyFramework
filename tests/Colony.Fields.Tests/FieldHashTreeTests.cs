using Colony.Fields;

namespace Colony.Fields.Tests;

/// <summary>TASK-32: Fields contribute one hash tree item per Field with one leaf per chunk, so a perturbed cell is localised to its chunk coordinate.</summary>
public sealed class FieldHashTreeTests
{
    private static (WorldSchema Schema, Field<int> Heat, Field<byte> Soil) BuildSchema()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var fields = builder.AddFields(new WorldSize(48, 32, 16)); // 3 x 2 x 1 chunks
        var heat = fields.AddField<int>("heat");
        var soil = fields.AddField<byte>("soil", 3);
        return (builder.Build(), heat, soil);
    }

    [Fact]
    public void The_fields_group_has_one_item_per_field_and_one_leaf_per_chunk_in_morton_order()
    {
        var (schema, _, _) = BuildSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });

        var tree = world.HashTree();

        Assert.Equal(world.Hash(), tree.WorldHash);
        var fields = tree.Group("fields");
        Assert.NotNull(fields);
        Assert.Equal(["heat", "soil"], fields.Children.Select(c => c.Name));
        var heat = fields.Child("heat")!;
        Assert.Equal(HashTreeLeafKind.FieldChunk, heat.LeafKind);
        Assert.Equal(6, heat.Leaves.Count);
        var set = world.Fields();
        for (int ordinal = 0; ordinal < 6; ordinal++)
        {
            var coord = set.ChunkAt(ordinal);
            Assert.Equal(new HashTreeLeafKey(ordinal, coord.X, coord.Y, coord.Z), heat.Leaves[ordinal].Key);
        }

        // Every untouched chunk of one Field hashes alike (all uniform with the same value).
        Assert.All(heat.Leaves, l => Assert.Equal(heat.Leaves[0].Hash, l.Hash));
    }

    [Fact]
    public void A_perturbed_cell_is_localised_to_its_field_and_chunk_coordinate_and_nothing_else()
    {
        var (schema, heat, soil) = BuildSchema();
        using var a = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        a.Fields().Accessor(soil)[5, 5, 5] = 9;
        b.Fields().Accessor(soil)[5, 5, 5] = 9;
        Assert.True(WorldHashTree.Compare(a.HashTree(), b.HashTree()).Identical);

        b.Fields().Accessor(heat)[40, 20, 3] = 7; // chunk (2, 1, 0)

        var comparison = WorldHashTree.Compare(a.HashTree(), b.HashTree());

        Assert.False(comparison.Identical);
        var difference = Assert.Single(comparison.Differences);
        Assert.Equal("fields", difference.Group);
        Assert.Equal("heat", difference.Item);
        Assert.Equal(HashTreeLeafKind.FieldChunk, difference.LeafKind);
        Assert.Equal((2, 1, 0), (difference.Left!.Value.X, difference.Left.Value.Y, difference.Left.Value.Z));
        Assert.Equal(b.Fields().OrdinalOf(new ChunkCoord(2, 1, 0)), difference.Left.Value.Ordinal);
        Assert.Equal("fields/heat chunk (2, 1, 0)", difference.Describe());
    }

    [Fact]
    public void Uniform_and_dense_chunks_with_the_same_cells_have_the_same_leaf_hash()
    {
        var (schema, heat, _) = BuildSchema();
        using var uniform = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var dense = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        dense.Fields().Accessor(heat).WriteChunk(3);

        Assert.True(WorldHashTree.Compare(uniform.HashTree(), dense.HashTree()).Identical);
    }
}
